using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Modules.Administration.Services;
using Mewdeko.Modules.ServerStats.Common;
using Mewdeko.Modules.ServerStats.Services;
using Mewdeko.Modules.StatChannels.Common;
using Mewdeko.Modules.Twitch.Services;
using Mewdeko.Services.Analytics;
using Microsoft.Extensions.Caching.Memory;

namespace Mewdeko.Modules.StatChannels.Services;

/// <summary>
///     Service for managing stat channels that display live server statistics in voice channel names.
/// </summary>
public class StatChannelService : INService, IReadyExecutor, IDisposable
{
    private const string CacheKey = "stat_channels_{0}";
    private const string SettingsCacheKey = "stat_channel_settings_{0}";

    /// <summary>
    ///     Fluxer permits two channel name edits per ten minutes per channel.
    /// </summary>
    private const int RenamesPerWindow = 2;

    /// <summary>
    ///     The shortest interval a rename based channel can sustain without burning its budget.
    /// </summary>
    public const int MinimumRenameIntervalMinutes = 5;

    /// <summary>
    ///     The shortest interval any stat channel may be configured with.
    /// </summary>
    public const int MinimumIntervalMinutes = 1;

    private static readonly TimeSpan RenameWindow = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache cache;
    private readonly FluxerClient client;
    private readonly IAnalyticsCollector collector;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ILogger<StatChannelService> logger;

    private readonly ConcurrentDictionary<ulong, List<DateTime>> renameHistory = new();
    private readonly ServerStatsService serverStats;
    private readonly GuildTimezoneService timezones;
    private readonly TwitchService twitchService;
    private readonly SemaphoreSlim updateSemaphore = new(1, 1);
    private bool isDisposed;
    private Timer? updateTimer;

    /// <summary>
    ///     Creates a new stat channel service.
    /// </summary>
    public StatChannelService(
        IDataConnectionFactory dbFactory,
        FluxerClient client,
        IMemoryCache cache,
        TwitchService twitchService,
        ILogger<StatChannelService> logger,
        IAnalyticsCollector collector,
        ServerStatsService serverStats,
        GuildTimezoneService timezones)
    {
        this.collector = collector;
        this.dbFactory = dbFactory;
        this.client = client;
        this.cache = cache;
        this.twitchService = twitchService;
        this.logger = logger;
        this.serverStats = serverStats;
        this.timezones = timezones;
    }

    /// <summary>
    ///     Disposes the update timer.
    /// </summary>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        updateTimer?.Dispose();
        updateSemaphore.Dispose();
    }

    /// <summary>
    ///     Initializes the update timer when the bot is ready. The tick is deliberately short: each stat channel carries
    ///     its own interval and is only pushed when it is actually due.
    /// </summary>
    public Task OnReadyAsync()
    {
        updateTimer = new Timer(_ => _ = UpdateAllStatChannelsAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        logger.LogInformation("Stat Channel Service Ready");
        return Task.CompletedTask;
    }

    #region Settings

    /// <summary>
    ///     Gets the guild wide stat channel defaults, creating them on first read.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The guild's defaults.</returns>
    public async Task<StatChannelSetting> GetSettingsAsync(ulong guildId)
    {
        var key = string.Format(SettingsCacheKey, guildId);
        if (cache.TryGetValue(key, out StatChannelSetting? cached) && cached != null)
            return cached;

        await using var db = await dbFactory.CreateConnectionAsync();
        var settings = await db.StatChannelSettings.FirstOrDefaultAsync(s => s.GuildId == guildId);

        if (settings == null)
        {
            settings = new StatChannelSetting
            {
                GuildId = guildId, DateAdded = DateTime.UtcNow
            };
            settings.Id = await db.InsertWithInt32IdentityAsync(settings);
        }

        cache.Set(key, settings, TimeSpan.FromMinutes(10));
        return settings;
    }

    /// <summary>
    ///     Updates the guild wide stat channel defaults.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="mechanism">The default update mechanism.</param>
    /// <param name="intervalMinutes">The default refresh interval in minutes.</param>
    /// <param name="displayStyle">The default display style.</param>
    /// <returns>The updated settings.</returns>
    public async Task<StatChannelSetting> UpdateSettingsAsync(ulong guildId,
        StatChannelUpdateMechanism? mechanism = null, int? intervalMinutes = null,
        StatChannelDisplayStyle? displayStyle = null)
    {
        var settings = await GetSettingsAsync(guildId);

        await using var db = await dbFactory.CreateConnectionAsync();
        if (mechanism.HasValue) settings.DefaultMechanism = (int)mechanism.Value;
        if (intervalMinutes.HasValue)
            settings.DefaultIntervalMinutes = ClampInterval(intervalMinutes.Value,
                (StatChannelUpdateMechanism)settings.DefaultMechanism);
        if (displayStyle.HasValue) settings.DefaultDisplayStyle = (int)displayStyle.Value;

        await db.UpdateAsync(settings);
        cache.Remove(string.Format(SettingsCacheKey, guildId));
        cache.Set(string.Format(SettingsCacheKey, guildId), settings, TimeSpan.FromMinutes(10));
        return settings;
    }

    #endregion

    #region CRUD

    /// <summary>
    ///     Creates a new locked voice channel and registers it as a stat channel.
    /// </summary>
    /// <param name="guild">The Fluxer guild.</param>
    /// <param name="statType">The stat type to display.</param>
    /// <param name="template">The display template.</param>
    /// <param name="categoryId">The category to create the channel in, or null for no category.</param>
    /// <param name="roleId">The role ID for role member counts.</param>
    /// <param name="countdownDate">The countdown target date.</param>
    /// <param name="goalTarget">The member goal target.</param>
    /// <param name="options">Optional presentation and scheduling overrides.</param>
    /// <returns>The created stat channel and the new voice channel.</returns>
    public async Task<(StatChannel StatChannel, Channel VoiceChannel)> CreateStatChannelAsync(
        SocketGuild guild, StatChannelType statType, string template, ulong? categoryId = null,
        ulong? roleId = null, DateTime? countdownDate = null, int goalTarget = 0,
        StatChannelOptions? options = null)
    {
        var initialName = StripPlaceholders(template);

        var voiceChannel = await guild.CreateVoiceChannelAsync(new CreateVoiceChannelRequest
        {
            Name = initialName,
            ParentCategoryId = categoryId
        });

        await voiceChannel.AddPermissionOverwriteAsync(guild.EveryoneRole,
            OverwritePermissions.InheritAll.Modify(connect: PermValue.Deny));

        var statChannel = await AddStatChannelAsync(guild.Id, voiceChannel.Id, statType, template,
            roleId, countdownDate, goalTarget, options);

        return (statChannel, voiceChannel);
    }

    /// <summary>
    ///     Adds a stat channel to a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The voice channel ID.</param>
    /// <param name="statType">The stat type to display.</param>
    /// <param name="template">The display template.</param>
    /// <param name="roleId">The role ID for role member counts.</param>
    /// <param name="countdownDate">The countdown target date.</param>
    /// <param name="goalTarget">The member goal target.</param>
    /// <param name="options">Optional presentation and scheduling overrides.</param>
    /// <returns>The created stat channel.</returns>
    public async Task<StatChannel> AddStatChannelAsync(ulong guildId, ulong channelId, StatChannelType statType,
        string template, ulong? roleId = null, DateTime? countdownDate = null, int goalTarget = 0,
        StatChannelOptions? options = null)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        var existing = await db.StatChannels.FirstOrDefaultAsync(s => s.ChannelId == channelId);
        if (existing != null)
            throw new InvalidOperationException("This channel is already a stat channel.");

        var settings = await GetSettingsAsync(guildId);
        var mechanism = options?.Mechanism ?? (StatChannelUpdateMechanism)settings.DefaultMechanism;
        var style = options?.DisplayStyle ?? (StatChannelDisplayStyle)settings.DefaultDisplayStyle;
        var interval = ClampInterval(options?.UpdateIntervalMinutes ?? settings.DefaultIntervalMinutes, mechanism);

        var statChannel = new StatChannel
        {
            GuildId = guildId,
            ChannelId = channelId,
            StatType = (int)statType,
            Template = template,
            RoleId = roleId,
            CountdownDate = countdownDate,
            GoalTarget = goalTarget,
            DisplayStyle = (int)style,
            StyleOptions = options?.StyleOptions?.Serialize(),
            UpdateMechanism = (int)mechanism,
            UpdateIntervalMinutes = interval,
            TargetId = options?.TargetId,
            TargetName = options?.TargetName,
            DateAdded = DateTime.UtcNow
        };

        CaptureChannelLayout(statChannel);

        statChannel.Id = await db.InsertWithInt32IdentityAsync(statChannel);
        InvalidateCache(guildId);

        _ = UpdateStatChannelAsync(statChannel);

        return statChannel;
    }

    /// <summary>
    ///     Removes a stat channel.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID to remove.</param>
    /// <returns>True if removed.</returns>
    public async Task<bool> RemoveStatChannelAsync(ulong guildId, ulong channelId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var deleted = await db.StatChannels
            .Where(s => s.GuildId == guildId && s.ChannelId == channelId)
            .DeleteAsync();

        if (deleted > 0)
        {
            InvalidateCache(guildId);
            renameHistory.TryRemove(channelId, out _);
        }

        return deleted > 0;
    }

    /// <summary>
    ///     Gets all stat channels for a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The list of stat channels.</returns>
    public async Task<List<StatChannel>> GetStatChannelsAsync(ulong guildId)
    {
        var cacheKey = string.Format(CacheKey, guildId);
        if (cache.TryGetValue(cacheKey, out List<StatChannel>? cached) && cached != null)
            return cached;

        await using var db = await dbFactory.CreateConnectionAsync();
        var channels = await db.StatChannels
            .Where(s => s.GuildId == guildId)
            .ToListAsync();

        cache.Set(cacheKey, channels, TimeSpan.FromMinutes(10));
        return channels;
    }

    /// <summary>
    ///     Updates the template for a stat channel.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="template">The new template.</param>
    /// <returns>The updated stat channel, or null if not found.</returns>
    public async Task<StatChannel?> UpdateTemplateAsync(ulong guildId, ulong channelId, string template)
    {
        return await UpdateStatChannelConfigAsync(guildId, channelId, new StatChannelOptions
        {
            Template = template
        });
    }

    /// <summary>
    ///     Applies a partial configuration update to a stat channel and pushes the result immediately.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="options">The fields to change. Null fields are left untouched.</param>
    /// <returns>The updated stat channel, or null if not found.</returns>
    public async Task<StatChannel?> UpdateStatChannelConfigAsync(ulong guildId, ulong channelId,
        StatChannelOptions options)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var sc = await db.StatChannels
            .FirstOrDefaultAsync(s => s.GuildId == guildId && s.ChannelId == channelId);

        if (sc == null) return null;

        if (options.Template != null) sc.Template = options.Template;
        if (options.StatType.HasValue) sc.StatType = (int)options.StatType.Value;
        if (options.DisplayStyle.HasValue) sc.DisplayStyle = (int)options.DisplayStyle.Value;
        if (options.StyleOptions != null) sc.StyleOptions = options.StyleOptions.Serialize();
        if (options.Mechanism.HasValue) sc.UpdateMechanism = (int)options.Mechanism.Value;
        if (options.RoleId.HasValue) sc.RoleId = options.RoleId;
        if (options.CountdownDate.HasValue) sc.CountdownDate = options.CountdownDate;
        if (options.GoalTarget.HasValue) sc.GoalTarget = options.GoalTarget.Value;
        if (options.TargetId.HasValue) sc.TargetId = options.TargetId;
        if (options.TargetName != null) sc.TargetName = options.TargetName;

        if (options.UpdateIntervalMinutes.HasValue)
            sc.UpdateIntervalMinutes = options.UpdateIntervalMinutes.Value;

        sc.UpdateIntervalMinutes = ClampInterval(sc.UpdateIntervalMinutes,
            (StatChannelUpdateMechanism)sc.UpdateMechanism);

        await db.UpdateAsync(sc);
        InvalidateCache(guildId);

        _ = UpdateStatChannelAsync(sc, force: true);

        return sc;
    }

    #endregion

    #region Resolution

    /// <summary>
    ///     Resolves the current display name for a stat channel.
    /// </summary>
    /// <param name="sc">The stat channel config.</param>
    /// <param name="guild">The Fluxer guild.</param>
    /// <returns>The resolved channel name, capped at Fluxer's 100 character limit.</returns>
    public async Task<string> ResolveStatValueAsync(StatChannel sc, SocketGuild guild)
    {
        var context = new StatResolutionContext(guild);
        return await ResolveStatValueAsync(sc, guild, context);
    }

    /// <summary>
    ///     Renders a template against a stat type without persisting anything, for dashboard previews.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="statType">The stat type to render.</param>
    /// <param name="template">The template to render.</param>
    /// <param name="options">The presentation options to render with.</param>
    /// <returns>The rendered name, or null when the guild is not available.</returns>
    public async Task<string?> PreviewAsync(ulong guildId, StatChannelType statType, string template,
        StatChannelOptions? options = null)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null) return null;

        var probe = new StatChannel
        {
            GuildId = guildId,
            StatType = (int)statType,
            Template = template,
            DisplayStyle = (int)(options?.DisplayStyle ?? StatChannelDisplayStyle.Comma),
            StyleOptions = options?.StyleOptions?.Serialize(),
            RoleId = options?.RoleId,
            CountdownDate = options?.CountdownDate,
            GoalTarget = options?.GoalTarget ?? 0,
            TargetId = options?.TargetId,
            TargetName = options?.TargetName
        };

        return await ResolveStatValueAsync(probe, guild, new StatResolutionContext(guild));
    }

    private async Task<string> ResolveStatValueAsync(StatChannel sc, SocketGuild guild, StatResolutionContext context)
    {
        var type = (StatChannelType)sc.StatType;
        var style = (StatChannelDisplayStyle)sc.DisplayStyle;
        var styleOptions = StatChannelStyleOptions.Parse(sc.StyleOptions);

        var resolved = await ResolveRawAsync(sc, type, guild, context);
        var target = type == StatChannelType.MemberGoal && sc.GoalTarget > 0 ? sc.GoalTarget : resolved.Target;

        var display = resolved.Kind switch
        {
            StatChannelValueKind.Boolean => StatChannelFormatter.FormatBoolean(resolved.Flag, styleOptions),
            StatChannelValueKind.Text => resolved.Text,
            _ => StatChannelFormatter.Format(resolved.Number, style, styleOptions, target)
        };

        var rendered = sc.Template
            .Replace("%count%", display)
            .Replace("%count.raw%", resolved.Number.ToString(CultureInfo.InvariantCulture))
            .Replace("%server.name%", guild.Name)
            .Replace("%server.id%", guild.Id.ToString())
            .Replace("%server.members%", guild.TotalMembers.ToString("N0"));

        if (resolved.Kind == StatChannelValueKind.Boolean)
            rendered = rendered.Replace("%status%", display);

        foreach (var (token, value) in resolved.Extras)
            rendered = rendered.Replace(token, value);

        return rendered.Length > 100 ? rendered[..100] : rendered;
    }

    private async Task<ResolvedStat> ResolveRawAsync(StatChannel sc, StatChannelType type, SocketGuild guild,
        StatResolutionContext context)
    {
        switch (type)
        {
            case StatChannelType.TotalMembers:
                return ResolvedStat.Num(guild.TotalMembers);
            case StatChannelType.HumanMembers:
                return ResolvedStat.Num(guild.Users.Count(u => !u.IsBot));
            case StatChannelType.BotCount:
                return ResolvedStat.Num(guild.Users.Count(u => u.IsBot));
            case StatChannelType.OnlineMembers:
                return ResolvedStat.Num(guild.Users.Count(u => u.Status != UserStatus.Offline));
            case StatChannelType.IdleMembers:
                return ResolvedStat.Num(guild.Users.Count(u => u.Status == UserStatus.Idle));
            case StatChannelType.DndMembers:
                return ResolvedStat.Num(guild.Users.Count(u => u.Status == UserStatus.DoNotDisturb));
            case StatChannelType.StreamingMembers:
                return ResolvedStat.Num(guild.Users.Count(u =>
                    u.VoiceStates.Values.Any(v => v.IsSelfStream)));
            case StatChannelType.InVoiceMembers:
                return ResolvedStat.Num(guild.VoiceChannels.Sum(vc => vc.ConnectedUsers.Count()));

            case StatChannelType.RoleMembers:
            {
                var role = sc.RoleId.HasValue ? guild.GetRole(sc.RoleId.Value) : null;
                return ResolvedStat.Num(GetRoleMembers(guild, role).Count)
                    .With("%role.name%", role?.Name ?? "Unknown Role")
                    .With("%role.id%", role?.Id.ToString() ?? "0")
                    .With("%role.color%", role?.Color.ToString("X6") ?? "000000");
            }

            case StatChannelType.NewestMember:
            {
                var newest = guild.Users
                    .Where(u => !u.IsBot)
                    .MaxBy(u => u.JoinedAt);
                return ResolvedStat.Str(newest?.DisplayName ?? "Unknown")
                    .With("%member.name%", newest?.DisplayName ?? "Unknown")
                    .With("%member.id%", newest?.Id.ToString() ?? "0");
            }

            case StatChannelType.MembersJoinedToday:
                return ResolvedStat.Num(CountJoinedSince(guild, TimeSpan.FromDays(1)));
            case StatChannelType.MembersJoinedWeek:
                return ResolvedStat.Num(CountJoinedSince(guild, TimeSpan.FromDays(7)));

            case StatChannelType.MemberGoal:
            {
                var goal = Math.Max(0, sc.GoalTarget);
                var current = guild.TotalMembers;
                var styleOptions = StatChannelStyleOptions.Parse(sc.StyleOptions);
                var percent = goal > 0 ? (double)current / goal * 100 : 0;
                return ResolvedStat.Num(current, goal)
                    .With("%goal%", goal.ToString("N0"))
                    .With("%goal.raw%", goal.ToString(CultureInfo.InvariantCulture))
                    .With("%goal.percent%", $"{percent:F0}%")
                    .With("%goal.remaining%", Math.Max(0, goal - current).ToString("N0"))
                    .With("%goal.bar%", StatChannelFormatter.Format(current,
                        StatChannelDisplayStyle.ProgressBar, styleOptions, goal));
            }

            case StatChannelType.Countdown:
            {
                var remaining = sc.CountdownDate.HasValue
                    ? sc.CountdownDate.Value - DateTime.UtcNow
                    : TimeSpan.Zero;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                return ResolvedStat.Num((long)remaining.TotalDays)
                    .With("%days%", ((int)remaining.TotalDays).ToString())
                    .With("%hours%", remaining.Hours.ToString())
                    .With("%minutes%", remaining.Minutes.ToString())
                    .With("%total.hours%", ((int)remaining.TotalHours).ToString());
            }

            case StatChannelType.ChannelCount:
                return ResolvedStat.Num(guild.Channels.Count);
            case StatChannelType.TextChannelCount:
                return ResolvedStat.Num(guild.TextChannels.Count);
            case StatChannelType.VoiceChannelCount:
                return ResolvedStat.Num(guild.VoiceChannels.Count);
            case StatChannelType.CategoryCount:
                return ResolvedStat.Num(guild.CategoryChannels.Count);
            case StatChannelType.ForumChannelCount:
                return ResolvedStat.Num(guild.Channels.Values.Count(c => c.Type == ChannelType.GuildForum));
            case StatChannelType.ThreadCount:
                return ResolvedStat.Num(guild.ThreadChannels.Count);
            case StatChannelType.RoleCount:
                return ResolvedStat.Num(guild.Roles.Count);
            case StatChannelType.EmojiCount:
                return ResolvedStat.Num(guild.Emotes.Count);
            case StatChannelType.AnimatedEmojiCount:
                return ResolvedStat.Num(guild.Emotes.Count(e => e.IsAnimated));
            case StatChannelType.StaticEmojiCount:
                return ResolvedStat.Num(guild.Emotes.Count(e => !e.IsAnimated));
            case StatChannelType.StickerCount:
                return ResolvedStat.Num(guild.Stickers.Count);

            case StatChannelType.ServerAge:
            {
                var age = DateTime.UtcNow - guild.CreatedAt.UtcDateTime;
                return ResolvedStat.Num((long)age.TotalDays)
                    .With("%years%", ((int)(age.TotalDays / 365)).ToString())
                    .With("%created%", guild.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd"));
            }

            case StatChannelType.BotUptime:
            {
                var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
                return ResolvedStat.Num((long)uptime.TotalHours)
                    .With("%days%", ((int)uptime.TotalDays).ToString())
                    .With("%hours%", uptime.Hours.ToString())
                    .With("%minutes%", uptime.Minutes.ToString());
            }

            case StatChannelType.TwitchLiveStatus:
            {
                var snapshot = await context.GetTwitchSnapshotAsync(twitchService);
                return ResolvedStat.Bool(snapshot?.IsLive ?? false)
                    .With("%twitch.name%", snapshot?.DisplayName ?? "Twitch");
            }

            case StatChannelType.TwitchViewers:
            {
                var snapshot = await context.GetTwitchSnapshotAsync(twitchService);
                return ResolvedStat.Num(snapshot?.Viewers ?? 0)
                    .With("%twitch.name%", snapshot?.DisplayName ?? "Twitch");
            }

            case StatChannelType.TwitchGame:
            {
                var snapshot = await context.GetTwitchSnapshotAsync(twitchService);
                var game = string.IsNullOrWhiteSpace(snapshot?.Game) ? "Offline" : snapshot!.Game;
                return ResolvedStat.Str(game)
                    .With("%game%", game)
                    .With("%title%", snapshot?.Title ?? "")
                    .With("%twitch.name%", snapshot?.DisplayName ?? "Twitch");
            }

            case StatChannelType.TwitchUptime:
            {
                var snapshot = await context.GetTwitchSnapshotAsync(twitchService);
                var live = snapshot?.StartedAt.HasValue == true
                    ? DateTime.UtcNow - snapshot.StartedAt!.Value.ToUniversalTime()
                    : TimeSpan.Zero;
                if (live < TimeSpan.Zero) live = TimeSpan.Zero;
                return ResolvedStat.Num((long)live.TotalHours)
                    .With("%hours%", ((int)live.TotalHours).ToString())
                    .With("%minutes%", live.Minutes.ToString());
            }

            case StatChannelType.TwitchFollowers:
            {
                var snapshot = await context.GetTwitchSnapshotAsync(twitchService);
                return ResolvedStat.Num(snapshot?.Followers ?? 0)
                    .With("%twitch.name%", snapshot?.DisplayName ?? "Twitch");
            }

            case StatChannelType.TwitchSubs:
            {
                var snapshot = await context.GetTwitchSnapshotAsync(twitchService);
                return ResolvedStat.Num(snapshot?.Subscribers ?? 0)
                    .With("%twitch.name%", snapshot?.DisplayName ?? "Twitch");
            }

            case StatChannelType.TwitchCounter:
            {
                var name = sc.TargetName ?? "";
                var value = string.IsNullOrWhiteSpace(name)
                    ? 0
                    : await twitchService.GetCounterAsync(guild.Id, name) ?? 0;
                return ResolvedStat.Num(value).With("%counter.name%", name);
            }

            case StatChannelType.TwitchLastRaider:
            {
                var raid = await context.GetLastRaidAsync(dbFactory);
                return ResolvedStat.Str(raid.Raider)
                    .With("%raider%", raid.Raider)
                    .With("%raid.viewers%", raid.Viewers.ToString("N0"));
            }

            case StatChannelType.TwitchRecentStreams:
            {
                var since = DateTime.UtcNow.AddDays(-14);
                await using var db = await dbFactory.CreateConnectionAsync();
                var count = await db.TwitchEventHistory.CountAsync(e =>
                    e.GuildId == guild.Id && e.EventType == "stream.online" && e.DateAdded >= since);
                return ResolvedStat.Num(count);
            }

            case StatChannelType.MinecraftPlayers:
            case StatChannelType.MinecraftStatus:
            {
                var snapshot = await context.GetMinecraftSnapshotAsync(dbFactory, sc.TargetId);
                if (snapshot == null)
                    return type == StatChannelType.MinecraftStatus
                        ? ResolvedStat.Bool(false).With("%latency%", "0ms")
                        : ResolvedStat.Num(0).With("%players.max%", "0").With("%server.version%", "Unknown");

                if (type == StatChannelType.MinecraftStatus)
                    return ResolvedStat.Bool(snapshot.IsOnline)
                        .With("%latency%", $"{snapshot.Latency}ms");

                return ResolvedStat.Num(snapshot.PlayersOnline, snapshot.PlayersMax)
                    .With("%players.max%", snapshot.PlayersMax.ToString("N0"))
                    .With("%server.version%", snapshot.Version ?? "Unknown");
            }

            case StatChannelType.CountingCurrent:
            case StatChannelType.CountingRecord:
            {
                var counting = await context.GetCountingChannelAsync(dbFactory, sc.TargetId);
                if (counting == null) return ResolvedStat.Num(0);
                return ResolvedStat.Num(type == StatChannelType.CountingCurrent
                    ? counting.CurrentNumber
                    : counting.HighestNumber);
            }

            case StatChannelType.OpenTickets:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                return ResolvedStat.Num(await db.Tickets.CountAsync(t =>
                    t.GuildId == guild.Id && t.ClosedAt == null && !t.IsArchived));
            }

            case StatChannelType.TicketsToday:
            {
                var since = DateTime.UtcNow.AddDays(-1);
                await using var db = await dbFactory.CreateConnectionAsync();
                return ResolvedStat.Num(await db.Tickets.CountAsync(t =>
                    t.GuildId == guild.Id && t.CreatedAt >= since));
            }

            case StatChannelType.PendingSuggestions:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                return ResolvedStat.Num(await db.Suggestions.CountAsync(s =>
                    s.GuildId == guild.Id && s.CurrentState == 0));
            }

            case StatChannelType.ActiveGiveaways:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                return ResolvedStat.Num(await db.Giveaways.CountAsync(g =>
                    g.ServerId == guild.Id && g.Ended == 0));
            }

            case StatChannelType.StarboardPosts:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var count = await db.StarboardPosts
                    .Join(db.Starboards, p => p.StarboardConfigId, s => s.Id, (p, s) => s.GuildId)
                    .CountAsync(g => g == guild.Id);
                return ResolvedStat.Num(count);
            }

            case StatChannelType.TopXpUser:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var top = await db.GuildUserXps
                    .Where(x => x.GuildId == guild.Id)
                    .OrderByDescending(x => x.TotalXp)
                    .FirstOrDefaultAsync();
                var name = top == null ? "Nobody" : guild.GetUser(top.UserId)?.DisplayName ?? "Unknown";
                return ResolvedStat.Str(name)
                    .With("%member.name%", name)
                    .With("%member.id%", top?.UserId.ToString() ?? "0")
                    .With("%member.xp%", (top?.TotalXp ?? 0).ToString("N0"));
            }

            case StatChannelType.TotalGuildXp:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var total = await db.GuildUserXps
                    .Where(x => x.GuildId == guild.Id)
                    .SumAsync(x => (long?)x.TotalXp) ?? 0;
                return ResolvedStat.Num(total);
            }

            case StatChannelType.AfkCount:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                return ResolvedStat.Num(await db.Afks.CountAsync(a => a.GuildId == guild.Id));
            }

            case StatChannelType.TotalCurrency:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var total = await db.GuildUserBalances
                    .Where(b => b.GuildId == guild.Id)
                    .SumAsync(b => (long?)b.Balance) ?? 0;
                return ResolvedStat.Num(total);
            }

            case StatChannelType.ActivePolls:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                return ResolvedStat.Num(await db.Polls.CountAsync(p => p.GuildId == guild.Id && p.IsActive));
            }

            case StatChannelType.InviteCount:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var total = await db.InviteCounts
                    .Where(i => i.GuildId == guild.Id)
                    .SumAsync(i => (long?)i.Count) ?? 0;
                return ResolvedStat.Num(total);
            }

            case StatChannelType.Clock:
            {
                var tz = timezones.GetTimeZoneOrDefault(guild.Id) ?? TimeZoneInfo.Utc;
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                var abbreviation = tz.Id == "UTC" || tz.Id == "Etc/UTC" ? "UTC" : tz.StandardName.GetInitials();
                return ResolvedStat.Str(now.ToString("HH:mm"))
                    .With("%time%", now.ToString("HH:mm"))
                    .With("%date%", now.ToString("dd MMM"))
                    .With("%datetime%", now.ToString("dd MMM HH:mm"))
                    .With("%weekday%", now.ToString("dddd"))
                    .With("%tz%", abbreviation);
            }

            case StatChannelType.MessagesToday:
                return ResolvedStat.Num(await serverStats.CountMessagesSinceAsync(guild.Id,
                    DateTime.UtcNow.AddDays(-1)));
            case StatChannelType.MessagesWeek:
                return ResolvedStat.Num(await serverStats.CountMessagesSinceAsync(guild.Id,
                    DateTime.UtcNow.AddDays(-7)));
            case StatChannelType.VoiceHoursToday:
                return ResolvedStat.Num(await serverStats.CountVoiceSecondsSinceAsync(guild.Id,
                    DateTime.UtcNow.AddDays(-1)) / 3600);
            case StatChannelType.VoiceHoursWeek:
                return ResolvedStat.Num(await serverStats.CountVoiceSecondsSinceAsync(guild.Id,
                    DateTime.UtcNow.AddDays(-7)) / 3600);

            case StatChannelType.TopChatter:
            {
                var top = (await serverStats.GetTopUsersAsync(guild.Id, StatKind.Messages, 7, 1)).FirstOrDefault();
                var name = top == null ? "Nobody" : guild.GetUser(top.Id)?.DisplayName ?? "Unknown";
                return ResolvedStat.Str(name)
                    .With("%member.name%", name)
                    .With("%member.id%", top?.Id.ToString() ?? "0")
                    .With("%member.messages%", (top?.Value ?? 0).ToString("N0"));
            }

            case StatChannelType.TopVoiceMember:
            {
                var top = (await serverStats.GetTopUsersAsync(guild.Id, StatKind.Voice, 7, 1)).FirstOrDefault();
                var name = top == null ? "Nobody" : guild.GetUser(top.Id)?.DisplayName ?? "Unknown";
                return ResolvedStat.Str(name)
                    .With("%member.name%", name)
                    .With("%member.id%", top?.Id.ToString() ?? "0")
                    .With("%member.hours%", ((top?.Value ?? 0) / 3600.0).ToString("N1"));
            }

            case StatChannelType.RoleMembersOnline:
            {
                var role = sc.RoleId.HasValue ? guild.GetRole(sc.RoleId.Value) : null;
                var members = GetRoleMembers(guild, role).ToList();
                return ResolvedStat.Num(members.Count(m => m.Status != UserStatus.Offline))
                    .With("%role.name%", role?.Name ?? "Unknown Role")
                    .With("%role.id%", role?.Id.ToString() ?? "0")
                    .With("%role.total%", members.Count.ToString("N0"));
            }

            case StatChannelType.TopInviter:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var top = await db.InviteCounts
                    .Where(i => i.GuildId == guild.Id)
                    .OrderByDescending(i => i.Count)
                    .FirstOrDefaultAsync();
                var name = top == null ? "Nobody" : guild.GetUser(top.UserId)?.DisplayName ?? "Unknown";
                return ResolvedStat.Str(name)
                    .With("%member.name%", name)
                    .With("%member.id%", top?.UserId.ToString() ?? "0")
                    .With("%member.invites%", (top?.Count ?? 0).ToString("N0"));
            }

            case StatChannelType.InvitesWeek:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var since = DateTime.UtcNow.AddDays(-7);
                return ResolvedStat.Num(await db.InvitedBies.CountAsync(i =>
                    i.GuildId == guild.Id && i.InviterId != 0 && !i.IsFake && i.DateAdded >= since));
            }

            case StatChannelType.MembersLeftToday:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var since = DateTime.UtcNow.AddDays(-1);
                return ResolvedStat.Num(await db.JoinLeaveLogs.CountAsync(x =>
                    x.GuildId == guild.Id && !x.IsJoin && x.DateAdded >= since));
            }

            case StatChannelType.MembersLeftWeek:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var since = DateTime.UtcNow.AddDays(-7);
                return ResolvedStat.Num(await db.JoinLeaveLogs.CountAsync(x =>
                    x.GuildId == guild.Id && !x.IsJoin && x.DateAdded >= since));
            }

            case StatChannelType.NetGrowthWeek:
            {
                await using var db = await dbFactory.CreateConnectionAsync();
                var since = DateTime.UtcNow.AddDays(-7);
                var joins = await db.JoinLeaveLogs.CountAsync(x =>
                    x.GuildId == guild.Id && x.IsJoin && x.DateAdded >= since);
                var leaves = await db.JoinLeaveLogs.CountAsync(x =>
                    x.GuildId == guild.Id && !x.IsJoin && x.DateAdded >= since);
                return ResolvedStat.Num(joins - leaves)
                    .With("%joins%", joins.ToString("N0"))
                    .With("%leaves%", leaves.ToString("N0"));
            }

            case StatChannelType.TopGame:
            {
                var top = (await serverStats.GetTopActivitiesAsync(guild.Id, 7, 1)).FirstOrDefault();
                return ResolvedStat.Str(top?.Name ?? "Nothing yet")
                    .With("%game%", top?.Name ?? "Nothing yet")
                    .With("%hours%", ((top?.Seconds ?? 0) / 3600.0).ToString("N1"))
                    .With("%players%", (top?.Players ?? 0).ToString("N0"));
            }

            case StatChannelType.PlayingNow:
                return ResolvedStat.Num(serverStats.CountActiveNow(guild.Id));

            default:
                return ResolvedStat.Num(0);
        }
    }

    private static int CountJoinedSince(SocketGuild guild, TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        return guild.Users.Count(u => u.JoinedAt >= cutoff);
    }

    private static IReadOnlyCollection<SocketGuildMember> GetRoleMembers(SocketGuild guild, SocketRole? role)
    {
        return role is null ? [] : guild.Users.Where(m => m.RoleIds.Contains(role.Id)).ToList();
    }

    #endregion

    #region Update pipeline

    private async Task UpdateAllStatChannelsAsync()
    {
        if (!await updateSemaphore.WaitAsync(0))
            return;

        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            var allStatChannels = await db.StatChannels.ToListAsync();

            var now = DateTime.UtcNow;
            var due = allStatChannels
                .Where(sc => sc.LastUpdateAt == null ||
                             now - sc.LastUpdateAt.Value >= TimeSpan.FromMinutes(
                                 Math.Max(MinimumIntervalMinutes, sc.UpdateIntervalMinutes)))
                .GroupBy(s => s.GuildId);

            foreach (var group in due)
            {
                var guild = client.GetGuild(group.Key);
                if (guild == null) continue;

                var context = new StatResolutionContext(guild);

                foreach (var sc in group)
                {
                    try
                    {
                        await UpdateStatChannelAsync(sc, guild, context);
                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Failed to update stat channel {ChannelId} in guild {GuildId}",
                            sc.ChannelId, sc.GuildId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating stat channels");
        }
        finally
        {
            updateSemaphore.Release();
        }
    }

    private async Task UpdateStatChannelAsync(StatChannel sc, SocketGuild? guild = null,
        StatResolutionContext? context = null, bool force = false)
    {
        guild ??= client.GetGuild(sc.GuildId);
        if (guild == null) return;

        var channel = guild.GetVoiceChannel(sc.ChannelId);
        if (channel == null) return;

        context ??= new StatResolutionContext(guild);
        var newName = await ResolveStatValueAsync(sc, guild, context);

        if (!force && channel.Name == newName)
        {
            await MarkUpdatedAsync(sc, newName);
            return;
        }

        var mechanism = (StatChannelUpdateMechanism)sc.UpdateMechanism;
        var pushed = mechanism switch
        {
            StatChannelUpdateMechanism.Recreate => await RecreateAsync(sc, guild, channel, newName),
            StatChannelUpdateMechanism.Rename => await RenameAsync(sc, channel, newName),
            _ => await AutoAsync(sc, guild, channel, newName)
        };

        if (pushed)
            await MarkUpdatedAsync(sc, newName);
    }

    /// <summary>
    ///     Renames while the per-channel rename budget holds. Once that bucket is spent, the recreate cost is only
    ///     worth paying when the configured cadence is faster than a rename can deliver; otherwise this waits for the
    ///     bucket to refill rather than churning the channel ID.
    /// </summary>
    /// <param name="sc">The stat channel config.</param>
    /// <param name="guild">The Fluxer guild.</param>
    /// <param name="channel">The voice channel being updated.</param>
    /// <param name="newName">The name to push.</param>
    /// <returns>True when the new name reached Fluxer.</returns>
    private async Task<bool> AutoAsync(StatChannel sc, SocketGuild guild, Channel channel, string newName)
    {
        if (HasRenameBudget(sc.ChannelId))
            return await RenameAsync(sc, channel, newName);

        if (sc.UpdateIntervalMinutes >= MinimumRenameIntervalMinutes)
            return false;

        return await RecreateAsync(sc, guild, channel, newName);
    }

    private async Task<bool> RenameAsync(StatChannel sc, Channel channel, string newName)
    {
        try
        {
            await channel.ModifyAsync(new UpdateChannelRequest { Name = newName });
            RecordRename(sc.ChannelId);
            collector.Feature("stat_channel", sc.GuildId);
            return true;
        }
        catch (Exception ex)
        {
            collector.Feature("stat_channel", sc.GuildId, false, ex.GetType().Name);
            logger.LogDebug(ex, "Failed to rename stat channel {ChannelId}", sc.ChannelId);
            return false;
        }
    }

    private async Task<bool> RecreateAsync(StatChannel sc, SocketGuild guild, Channel channel,
        string newName)
    {
        try
        {
            var categoryId = sc.CategoryId ?? channel.CategoryId;
            var position = sc.Position ?? channel.Position;
            var overwrites = ReadOverwrites(sc) ?? ToOverwriteRequests(channel.PermissionOverwrites);

            var created = await guild.CreateVoiceChannelAsync(new CreateVoiceChannelRequest
            {
                Name = newName,
                ParentCategoryId = categoryId,
                PermissionOverwrites = overwrites.ToArray()
            });

            if (position.HasValue)
            {
                await guild.UpdateChannelPositionsAsync([
                    new ChannelPositionUpdateRequestItem
                    {
                        Id = created.Id, ParentId = categoryId, Position = position
                    }
                ]);
            }

            await channel.DeleteAsync();

            await using var db = await dbFactory.CreateConnectionAsync();
            var oldChannelId = sc.ChannelId;
            sc.ChannelId = created.Id;
            sc.CategoryId = categoryId;
            sc.Position = position;
            sc.PermissionOverwrites = SerializeOverwrites(overwrites);
            await db.UpdateAsync(sc);

            renameHistory.TryRemove(oldChannelId, out _);
            InvalidateCache(sc.GuildId);
            collector.Feature("stat_channel", sc.GuildId);
            return true;
        }
        catch (Exception ex)
        {
            collector.Feature("stat_channel", sc.GuildId, false, ex.GetType().Name);
            logger.LogWarning(ex, "Failed to recreate stat channel {ChannelId} in guild {GuildId}",
                sc.ChannelId, sc.GuildId);
            return false;
        }
    }

    private async Task MarkUpdatedAsync(StatChannel sc, string newName)
    {
        sc.LastValue = newName;
        sc.LastUpdateAt = DateTime.UtcNow;

        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            await db.StatChannels
                .Where(s => s.Id == sc.Id)
                .Set(s => s.LastValue, newName)
                .Set(s => s.LastUpdateAt, sc.LastUpdateAt)
                .UpdateAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to persist stat channel update state for {ChannelId}", sc.ChannelId);
        }
    }

    private bool HasRenameBudget(ulong channelId)
    {
        var cutoff = DateTime.UtcNow - RenameWindow;
        var history = renameHistory.GetOrAdd(channelId, _ => []);

        lock (history)
        {
            history.RemoveAll(t => t < cutoff);
            return history.Count < RenamesPerWindow;
        }
    }

    private void RecordRename(ulong channelId)
    {
        var history = renameHistory.GetOrAdd(channelId, _ => []);
        lock (history)
        {
            history.Add(DateTime.UtcNow);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    ///     Clamps a requested interval to what the chosen mechanism can actually sustain.
    /// </summary>
    /// <param name="minutes">The requested interval.</param>
    /// <param name="mechanism">The chosen mechanism.</param>
    /// <returns>The usable interval.</returns>
    public static int ClampInterval(int minutes, StatChannelUpdateMechanism mechanism)
    {
        var floor = mechanism == StatChannelUpdateMechanism.Rename
            ? MinimumRenameIntervalMinutes
            : MinimumIntervalMinutes;
        return Math.Clamp(minutes, floor, 1440);
    }

    private static string StripPlaceholders(string template)
    {
        var name = template;
        var start = name.IndexOf('%');
        while (start >= 0)
        {
            var end = name.IndexOf('%', start + 1);
            if (end < 0) break;
            name = name.Remove(start, end - start + 1).Insert(start, "...");
            start = name.IndexOf('%', start + 3);
        }

        return name.Length > 100 ? name[..100] : name;
    }

    private void CaptureChannelLayout(StatChannel sc)
    {
        var guild = client.GetGuild(sc.GuildId);
        var channel = guild?.GetVoiceChannel(sc.ChannelId);
        if (channel == null) return;

        sc.CategoryId = channel.CategoryId;
        sc.Position = channel.Position;
        sc.PermissionOverwrites = SerializeOverwrites(ToOverwriteRequests(channel.PermissionOverwrites));
    }

    private static List<ChannelOverwriteRequest> ToOverwriteRequests(IEnumerable<PermissionOverwrite>? overwrites)
    {
        return overwrites?.Select(o => new ChannelOverwriteRequest
        {
            Id = o.Id,
            Type = (OverwriteRequestType)(int)o.Type,
            Allow = (ulong)o.Allow.RawValue,
            Deny = (ulong)o.Deny.RawValue
        }).ToList() ?? [];
    }

    private static string SerializeOverwrites(IEnumerable<ChannelOverwriteRequest> overwrites)
    {
        return JsonSerializer.Serialize(overwrites.Select(o => new StoredOverwrite
        {
            TargetId = o.Id,
            TargetType = (int)o.Type,
            Allow = o.Allow,
            Deny = o.Deny
        }));
    }

    private static List<ChannelOverwriteRequest>? ReadOverwrites(StatChannel sc)
    {
        if (string.IsNullOrWhiteSpace(sc.PermissionOverwrites))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredOverwrite>>(sc.PermissionOverwrites);
            return stored?.Select(o => new ChannelOverwriteRequest
            {
                Id = o.TargetId,
                Type = (OverwriteRequestType)o.TargetType,
                Allow = o.Allow,
                Deny = o.Deny
            }).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void InvalidateCache(ulong guildId)
    {
        cache.Remove(string.Format(CacheKey, guildId));
    }

    private class StoredOverwrite
    {
        public ulong TargetId { get; set; }
        public int TargetType { get; set; }
        public ulong Allow { get; set; }
        public ulong Deny { get; set; }
    }

    #endregion
}