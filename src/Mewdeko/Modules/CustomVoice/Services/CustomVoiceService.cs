using System.Text.Json;
using System.Threading;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Services.Analytics;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.CustomVoice.Services;

/// <summary>
///     Where a new temporary channel copies its permission overwrites from.
/// </summary>
public enum CustomVoiceSyncMode
{
    /// <summary>
    ///     Nothing is copied.
    /// </summary>
    None = 0,

    /// <summary>
    ///     The overwrites of the hub channel the member joined.
    /// </summary>
    Hub = 1,

    /// <summary>
    ///     The overwrites of the category the channel is created in.
    /// </summary>
    Category = 2
}

/// <summary>
///     Service for managing custom voice channels.
/// </summary>
public class CustomVoiceService : INService, IUnloadableService, IReadyExecutor
{
    private static readonly OverwritePermissions TextMember = new OverwritePermissions().Modify(
        viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, readMessageHistory: PermValue.Allow);

    private readonly ConcurrentDictionary<ulong, ulong> textChannels = new();
    private readonly ConcurrentDictionary<ulong, HashSet<ulong>> activeChannels = new();
    private readonly FluxerClient client;
    private readonly IAnalyticsCollector collector;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ConcurrentDictionary<ulong, Timer> emptyChannelTimers = new();
    private readonly ConcurrentDictionary<ulong, DateTime> emptyChannels = new();
    private readonly EventHandler eventHandler;
    private readonly GuildSettingsService guildSettings;
    private readonly SemaphoreSlim @lock = new(1, 1);
    private readonly ILogger<CustomVoiceService> logger;
    private readonly GeneratedBotStrings strings;


    /// <summary>
    ///     Initializes a new instance of the <see cref="CustomVoiceService" /> class.
    /// </summary>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="client">The Discord client instance.</param>
    /// <param name="eventHandler">The event handler service.</param>
    /// <param name="strings">The localized strings service.</param>
    /// <param name="logger">The logger instance for structured logging.</param>
    /// <param name="collector">The analytics collector.</param>
    /// <param name="guildSettings">The guild settings service, used for the command prefix.</param>
    public CustomVoiceService(
        IDataConnectionFactory dbFactory,
        FluxerClient client,
        EventHandler eventHandler,
        GeneratedBotStrings strings, ILogger<CustomVoiceService> logger, IAnalyticsCollector collector,
        GuildSettingsService guildSettings)
    {
        this.collector = collector;
        this.guildSettings = guildSettings;
        this.dbFactory = dbFactory;
        this.client = client;
        this.eventHandler = eventHandler;
        this.strings = strings;
        this.logger = logger;

        this.eventHandler.UserVoiceStateUpdated.Subscribe("CustomVoiceService", OnUserVoiceStateUpdated);
        this.eventHandler.ChannelDestroyed.Subscribe("CustomVoiceService", OnChannelDestroyed);
        this.eventHandler.JoinedGuild.Subscribe("CustomVoiceService", OnJoinedGuild);
        this.eventHandler.LeftGuild.Subscribe("CustomVoiceService", OnLeftGuild);

        // Start cleaning task for empty channels
        _ = StartEmptyChannelCleanupTask();

        // Load all active custom voice channels on startup
        _ = LoadActiveChannelsOnStartup();
    }

    /// <summary>
    ///     Unloads the service and unsubscribes from events.
    /// </summary>
    public Task Unload()
    {
        eventHandler.UserVoiceStateUpdated.Unsubscribe("CustomVoiceService", OnUserVoiceStateUpdated);
        eventHandler.ChannelDestroyed.Unsubscribe("CustomVoiceService", OnChannelDestroyed);
        eventHandler.JoinedGuild.Unsubscribe("CustomVoiceService", OnJoinedGuild);
        eventHandler.LeftGuild.Unsubscribe("CustomVoiceService", OnLeftGuild);

        // Dispose all empty channel timers
        foreach (var timer in emptyChannelTimers.Values)
        {
            timer.Dispose();
        }

        emptyChannelTimers.Clear();

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Rebuilds the access of temporary text channels and deletes the ones whose voice channel is gone.
    /// </summary>
    public async Task OnReadyAsync()
    {
        try
        {
            List<CustomVoiceChannel> rows;
            await using (var db = await dbFactory.CreateConnectionAsync())
                rows = await db.CustomVoiceChannels.Where(x => x.TextChannelId != null).ToListAsync();

            foreach (var row in rows)
            {
                try
                {
                    await ReconcileTextChannelAsync(row);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not reconcile the text channel of voice channel {ChannelId}",
                        row.ChannelId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reconciling temporary text channels on startup");
        }
    }

    private async Task ReconcileTextChannelAsync(CustomVoiceChannel row)
    {
        var guild = client.GetGuild(row.GuildId);
        if (guild is null || row.TextChannelId is not { } textId)
            return;

        var voice = guild.GetVoiceChannel(row.ChannelId) as Channel;
        var text = guild.GetTextChannel(textId) as Channel;
        if (voice is null || text is null)
        {
            if (text is not null)
                await text.DeleteAsync();

            await ClearTextChannelAsync(row.ChannelId);
            return;
        }

        textChannels[row.ChannelId] = textId;
        var inside = voice.ConnectedUsers.Where(x => !x.IsBot).ToList();
        var insideIds = inside.Select(x => x.Id).ToHashSet();

        foreach (var overwrite in (text.PermissionOverwrites ?? []).ToList())
        {
            if (overwrite.Type == PermissionOverwriteType.Member && !insideIds.Contains(overwrite.Id)
                                                                  && overwrite.Id != row.OwnerId
                                                                  && IsTextMember(OverwritePermissions.From(overwrite)))
                await text.RemovePermissionOverwriteAsync(overwrite.Id);
        }

        foreach (var user in inside.Where(user => text.GetPermissionOverwrite(user) is null))
            await text.AddPermissionOverwriteAsync(user, TextMember);
    }

    private static bool IsTextMember(OverwritePermissions perms) =>
        perms.AllowValue == TextMember.AllowValue && perms.DenyValue == 0;

    private async Task ClearTextChannelAsync(ulong voiceChannelId)
    {
        textChannels.TryRemove(voiceChannelId, out _);
        await using var db = await dbFactory.CreateConnectionAsync();
        await db.CustomVoiceChannels.Where(x => x.ChannelId == voiceChannelId)
            .Set(x => x.TextChannelId, (ulong?)null).UpdateAsync();
    }

    private async Task DeleteTextChannelAsync(ulong guildId, ulong voiceChannelId)
    {
        if (!textChannels.TryRemove(voiceChannelId, out var textId))
            return;

        try
        {
            if (client.GetGuild(guildId)?.GetTextChannel(textId) is Channel text)
                await text.DeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the text channel of voice channel {ChannelId}", voiceChannelId);
        }
    }

    private async Task<ulong?> CreateTextChannelAsync(SocketGuild guild, CustomVoiceConfig config, string name,
        Channel? category, ulong ownerId, List<ulong> allowedUsers, List<ulong> deniedUsers)
    {
        try
        {
            var overwrites = new List<ChannelOverwriteRequest>
            {
                OverwritePermissions.ToRequest(guild.EveryoneRole.Id, OverwriteRequestType.Role,
                    Overwrite(viewChannel: PermValue.Deny)),
                OverwritePermissions.ToRequest(client.CurrentUser.Id, OverwriteRequestType.Member,
                    Overwrite(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow,
                        manageChannel: PermValue.Allow, manageMessages: PermValue.Allow)),
                OverwritePermissions.ToRequest(ownerId, OverwriteRequestType.Member,
                    Overwrite(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow,
                        readMessageHistory: PermValue.Allow, manageMessages: PermValue.Allow))
            };

            if (config.CustomVoiceAdminRoleId is { } roleId && guild.GetRole(roleId) is not null)
            {
                overwrites.Add(OverwritePermissions.ToRequest(roleId, OverwriteRequestType.Role,
                    Overwrite(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow,
                        readMessageHistory: PermValue.Allow, manageMessages: PermValue.Allow)));
            }

            foreach (var moderatorRoleId in ParseRoleIds(config.ModeratorRoleIds).Where(x => guild.GetRole(x) is not null))
            {
                overwrites.Add(OverwritePermissions.ToRequest(moderatorRoleId, OverwriteRequestType.Role,
                    Overwrite(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow,
                        readMessageHistory: PermValue.Allow, manageMessages: PermValue.Allow)));
            }

            foreach (var allowedId in allowedUsers.Where(x => x != ownerId && !deniedUsers.Contains(x)))
            {
                overwrites.Add(OverwritePermissions.ToRequest(allowedId, OverwriteRequestType.Member, TextMember));
            }

            var text = await guild.CreateTextChannelAsync(new CreateTextChannelRequest
            {
                Name = name,
                ParentCategoryId = category?.Id,
                PermissionOverwrites = overwrites.ToArray()
            });
            return text.Id;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating the text channel for a custom voice channel in guild {GuildId}",
                guild.Id);
            return null;
        }
    }

    private async Task UpdateTextAccessAsync(SocketGuildMember user, VoiceStateSnapshot oldState,
        VoiceStateSnapshot newState)
    {
        if (user.IsBot || oldState.ChannelId == newState.ChannelId)
            return;

        var guild = user.Guild;
        if (oldState.ChannelId is { } before && textChannels.TryGetValue(before, out var oldText)
                                              && guild.GetTextChannel(oldText) is Channel oldChannel
                                              && oldChannel.GetPermissionOverwrite(user) is { } current
                                              && IsTextMember(current))
            await oldChannel.RemovePermissionOverwriteAsync(user);

        if (newState.ChannelId is { } after && textChannels.TryGetValue(after, out var newText)
                                             && guild.GetTextChannel(newText) is Channel newChannel
                                             && newChannel.GetPermissionOverwrite(user) is null)
            await newChannel.AddPermissionOverwriteAsync(user, TextMember);
    }

    /// <summary>
    ///     Loads all active custom voice channels on startup.
    /// </summary>
    private async Task LoadActiveChannelsOnStartup()
    {
        try
        {
            await using var dbContext = await dbFactory.CreateConnectionAsync();
            var allChannels = await dbContext.CustomVoiceChannels.ToListAsync();

            foreach (var channel in allChannels)
            {
                var guild = client.GetGuild(channel.GuildId);
                if (guild == null)
                    continue;

                var voiceChannel = guild.GetVoiceChannel(channel.ChannelId) as Channel;
                if (voiceChannel == null)
                {
                    // Channel no longer exists, remove from database
                    await dbContext.DeleteAsync(channel);
                    if (channel.TextChannelId is { } orphanText && guild.GetTextChannel(orphanText) is Channel orphan)
                        await orphan.DeleteAsync();

                    continue;
                }

                // Add to active channels
                activeChannels.AddOrUpdate(channel.GuildId,
                    [channel.ChannelId],
                    (_, set) =>
                    {
                        set.Add(channel.ChannelId);
                        return set;
                    });

                // Check if it's empty and should be tracked for deletion - filter for actually connected non-bot users
                var connectedUsersCount =
                    voiceChannel.ConnectedUsers.Count(u => u.VoiceChannel?.Id == channel.ChannelId && !u.IsBot);
                if (connectedUsersCount == 0 && !channel.KeepAlive)
                {
                    var config = await GetConfigForChannelAsync(channel.GuildId, channel.ChannelId);
                    if (config.DeleteWhenEmpty)
                    {
                        emptyChannels.TryAdd(channel.ChannelId, DateTime.UtcNow);

                        // Schedule deletion after timeout
                        if (config.EmptyChannelTimeout > 0)
                        {
                            var timer = new Timer(async _ =>
                            {
                                await DeleteEmptyChannelAsync(channel.GuildId, channel.ChannelId);
                            }, null, TimeSpan.FromMinutes(config.EmptyChannelTimeout), Timeout.InfiniteTimeSpan);

                            emptyChannelTimers.TryAdd(channel.ChannelId, timer);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading active voice channels on startup");
        }
    }

    /// <summary>
    ///     Gets or creates the custom voice configuration for a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The custom voice configuration.</returns>
    public async Task<CustomVoiceConfig> GetOrCreateConfigAsync(ulong guildId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var config = PickPrimaryConfig(await dbContext.CustomVoiceConfigs.Where(c => c.GuildId == guildId)
            .ToListAsync());

        if (config == null)
        {
            config = new CustomVoiceConfig
            {
                GuildId = guildId,
                DefaultNameFormat = "{0}'s Channel",
                DefaultUserLimit = 0,
                DefaultBitrate = 64,
                DeleteWhenEmpty = true,
                EmptyChannelTimeout = 1,
                AllowMultipleChannels = false,
                AllowNameCustomization = true,
                AllowUserLimitCustomization = true,
                AllowBitrateCustomization = true,
                AllowLocking = true,
                AllowUserManagement = true,
                MaxUserLimit = 99,
                MaxBitrate = 96,
                PersistUserPreferences = true,
                AutoPermission = true
            };

            await dbContext.InsertAsync(config);
        }
        else
        {
            // Auto-migrate bad bitrate values (if stored in bps instead of kbps)
            var needsUpdate = false;

            if (config.DefaultBitrate > 1000)
            {
                logger.LogWarning("Guild {GuildId} has bad DefaultBitrate {OldValue}, converting from bps to kbps",
                    guildId, config.DefaultBitrate);
                config.DefaultBitrate = config.DefaultBitrate / 1000;
                needsUpdate = true;
            }

            if (config.MaxBitrate > 1000)
            {
                logger.LogWarning("Guild {GuildId} has bad MaxBitrate {OldValue}, converting from bps to kbps",
                    guildId, config.MaxBitrate);
                config.MaxBitrate = config.MaxBitrate / 1000;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                await dbContext.UpdateAsync(config);
            }
        }

        return config;
    }

    /// <summary>
    ///     Picks the first hub's configuration, falling back to an unconfigured one.
    /// </summary>
    /// <param name="configs">A guild's configurations.</param>
    /// <returns>The primary configuration, or null when there are none.</returns>
    private static CustomVoiceConfig? PickPrimaryConfig(IEnumerable<CustomVoiceConfig> configs)
    {
        return configs.OrderBy(c => c.HubVoiceChannelId == 0).ThenBy(c => c.Id).FirstOrDefault();
    }

    /// <summary>
    ///     Gets the settings of every join-to-create hub in a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The hubs in the order they were added.</returns>
    public async Task<List<CustomVoiceConfig>> GetHubConfigsAsync(ulong guildId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        return await dbContext.CustomVoiceConfigs
            .Where(c => c.GuildId == guildId && c.HubVoiceChannelId != 0)
            .OrderBy(c => c.Id)
            .ToListAsync();
    }

    /// <summary>
    ///     Gets the settings of one hub.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="hubChannelId">The hub voice channel ID.</param>
    /// <returns>The hub's settings, or null when that channel is not a hub.</returns>
    public async Task<CustomVoiceConfig?> GetHubConfigAsync(ulong guildId, ulong hubChannelId)
    {
        if (hubChannelId == 0)
            return null;

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        return await dbContext.CustomVoiceConfigs
            .FirstOrDefaultAsync(c => c.GuildId == guildId && c.HubVoiceChannelId == hubChannelId);
    }

    /// <summary>
    ///     Gets the settings of the hub a temporary channel was created from.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The temporary voice channel ID.</param>
    /// <returns>The hub's settings, or the first hub's when the channel does not remember its hub.</returns>
    public async Task<CustomVoiceConfig> GetConfigForChannelAsync(ulong guildId, ulong channelId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var hubId = await dbContext.CustomVoiceChannels.Where(c => c.ChannelId == channelId)
            .Select(c => c.HubVoiceChannelId).FirstOrDefaultAsync();

        if (hubId is { } id and not 0 && await GetHubConfigAsync(guildId, id) is { } hub)
            return hub;

        return await GetOrCreateConfigAsync(guildId);
    }

    /// <summary>
    ///     Gets the first hub's settings, which is what community-wide configuration commands edit when no hub is given.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The first hub's settings.</returns>
    public Task<CustomVoiceConfig> GetPrimaryConfigAsync(ulong guildId)
    {
        return GetOrCreateConfigAsync(guildId);
    }

    /// <summary>
    ///     Adds another join-to-create hub with its own settings, or updates the category of an existing hub.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="hubChannelId">The hub voice channel ID.</param>
    /// <param name="categoryId">The category for created channels.</param>
    /// <returns>The hub's settings.</returns>
    public async Task<CustomVoiceConfig> AddHubAsync(ulong guildId, ulong hubChannelId, ulong? categoryId = null)
    {
        var guild = client.GetGuild(guildId) ??
                    throw new ArgumentException($"Guild with ID {guildId} not found", nameof(guildId));
        if (hubChannelId == 0 || guild.GetVoiceChannel(hubChannelId) == null)
            throw new ArgumentException($"Voice channel with ID {hubChannelId} not found", nameof(hubChannelId));
        if (categoryId.HasValue && guild.GetCategoryChannel(categoryId.Value) == null)
            throw new ArgumentException($"Category with ID {categoryId} not found", nameof(categoryId));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var configs = await dbContext.CustomVoiceConfigs.Where(c => c.GuildId == guildId).ToListAsync();
        var existing = configs.FirstOrDefault(c => c.HubVoiceChannelId == hubChannelId);
        if (existing != null)
        {
            if (categoryId.HasValue)
            {
                existing.ChannelCategoryId = categoryId;
                await dbContext.UpdateAsync(existing);
            }

            return existing;
        }

        var hubCount = configs.Count(c => c.HubVoiceChannelId != 0);
        var unconfigured = configs.OrderBy(c => c.Id).FirstOrDefault(c => c.HubVoiceChannelId == 0);
        var config = unconfigured ?? new CustomVoiceConfig
        {
            GuildId = guildId,
            DefaultNameFormat = "{0}'s Channel",
            DefaultBitrate = 64,
            DeleteWhenEmpty = true,
            EmptyChannelTimeout = 1,
            AllowNameCustomization = true,
            AllowUserLimitCustomization = true,
            AllowBitrateCustomization = true,
            AllowLocking = true,
            AllowUserManagement = true,
            MaxUserLimit = 99,
            MaxBitrate = 96,
            PersistUserPreferences = true,
            AutoPermission = true
        };

        config.HubVoiceChannelId = hubChannelId;
        config.ChannelCategoryId = categoryId;
        config.HubName = $"Hub {hubCount + 1}";

        if (unconfigured != null)
            await dbContext.UpdateAsync(config);
        else
            config.Id = await dbContext.InsertWithInt32IdentityAsync(config);

        return config;
    }

    /// <summary>
    ///     Removes a hub and its settings. Channels it already created are left alone.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="hubChannelId">The hub voice channel ID.</param>
    /// <returns>True when the hub existed.</returns>
    public async Task<bool> RemoveHubAsync(ulong guildId, ulong hubChannelId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        return await dbContext.CustomVoiceConfigs
            .Where(c => c.GuildId == guildId && c.HubVoiceChannelId == hubChannelId && hubChannelId != 0)
            .DeleteAsync() > 0;
    }

    /// <summary>
    ///     Updates the custom voice configuration for a guild.
    /// </summary>
    /// <param name="config">The updated configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateConfigAsync(CustomVoiceConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (config.GuildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(config));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        await dbContext.UpdateAsync(config);
    }

    /// <summary>
    ///     Sets up a voice channel as a hub for creating custom voice channels.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="hubChannelId">The hub channel ID.</param>
    /// <param name="categoryId">The optional category ID for created channels.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetupHubAsync(ulong guildId, ulong hubChannelId, ulong? categoryId = null)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (hubChannelId == 0)
            throw new ArgumentException("Hub channel ID cannot be 0", nameof(hubChannelId));

        // Validate that the channel exists
        var guild = client.GetGuild(guildId);
        if (guild == null)
            throw new ArgumentException($"Guild with ID {guildId} not found", nameof(guildId));

        var hubChannel = guild.GetVoiceChannel(hubChannelId);
        if (hubChannel == null)
            throw new ArgumentException($"Voice channel with ID {hubChannelId} not found", nameof(hubChannelId));

        // Validate category if provided
        if (categoryId.HasValue)
        {
            var category = guild.GetCategoryChannel(categoryId.Value);
            if (category == null)
                throw new ArgumentException($"Category with ID {categoryId} not found", nameof(categoryId));
        }

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var guildConfigs = await dbContext.CustomVoiceConfigs.Where(c => c.GuildId == guildId).ToListAsync();
        var config = guildConfigs.FirstOrDefault(c => c.HubVoiceChannelId == hubChannelId) ??
                     PickPrimaryConfig(guildConfigs);

        if (config == null)
        {
            config = new CustomVoiceConfig
            {
                GuildId = guildId,
                HubName = "Hub 1",
                HubVoiceChannelId = hubChannelId,
                ChannelCategoryId = categoryId,
                DefaultNameFormat = "{0}'s Channel",
                DefaultUserLimit = 0,
                DefaultBitrate = 64,
                DeleteWhenEmpty = true,
                EmptyChannelTimeout = 1,
                AllowMultipleChannels = false,
                AllowNameCustomization = true,
                AllowUserLimitCustomization = true,
                AllowBitrateCustomization = true,
                AllowLocking = true,
                AllowUserManagement = true,
                MaxUserLimit = 99,
                MaxBitrate = 96,
                PersistUserPreferences = true,
                AutoPermission = true
            };

            await dbContext.InsertAsync(config);
        }
        else
        {
            config.HubVoiceChannelId = hubChannelId;
            if (categoryId.HasValue)
                config.ChannelCategoryId = categoryId;

            await dbContext.UpdateAsync(config);
        }
    }

    /// <summary>
    ///     Gets the active custom voice channels for a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>A list of active custom voice channels.</returns>
    public async Task<List<CustomVoiceChannel>> GetActiveChannelsAsync(ulong guildId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        return await dbContext.CustomVoiceChannels.Where(c => c.GuildId == guildId).ToListAsync();
    }

    /// <summary>
    ///     Gets a custom voice channel by its ID.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <returns>The custom voice channel if found, null otherwise.</returns>
    public async Task<CustomVoiceChannel?> GetChannelAsync(ulong guildId, ulong channelId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (channelId == 0)
            throw new ArgumentException("Channel ID cannot be 0", nameof(channelId));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        return await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c =>
            c.GuildId == guildId && c.ChannelId == channelId);
    }

    /// <summary>
    ///     Gets all custom voice channels owned by a user.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <returns>A list of custom voice channels owned by the user.</returns>
    public async Task<List<CustomVoiceChannel>> GetUserChannelsAsync(ulong guildId, ulong userId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (userId == 0)
            throw new ArgumentException("User ID cannot be 0", nameof(userId));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        return await dbContext.CustomVoiceChannels.Where(c => c.GuildId == guildId && c.OwnerId == userId)
            .ToListAsync();
    }

    /// <summary>
    ///     Gets a user's voice preferences.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <returns>The user's voice preferences if found, null otherwise.</returns>
    public async Task<UserVoicePreference?> GetUserPreferencesAsync(ulong guildId, ulong userId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (userId == 0)
            throw new ArgumentException("User ID cannot be 0", nameof(userId));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        return await dbContext.UserVoicePreferences.FirstOrDefaultAsync(p =>
            p.GuildId == guildId && p.UserId == userId);
    }

    /// <summary>
    ///     Sets a user's voice preferences.
    /// </summary>
    /// <param name="prefs">The user's voice preferences.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetUserPreferencesAsync(UserVoicePreference prefs)
    {
        if (prefs == null)
            throw new ArgumentNullException(nameof(prefs));

        if (prefs.GuildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(prefs));

        if (prefs.UserId == 0)
            throw new ArgumentException("User ID cannot be 0", nameof(prefs));

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var existingPrefs = await dbContext.UserVoicePreferences.FirstOrDefaultAsync(p =>
            p.GuildId == prefs.GuildId && p.UserId == prefs.UserId);

        if (existingPrefs == null)
        {
            await dbContext.InsertAsync(prefs);
        }
        else
        {
            existingPrefs.NameFormat = prefs.NameFormat;
            existingPrefs.UserLimit = prefs.UserLimit;
            existingPrefs.Bitrate = prefs.Bitrate;
            existingPrefs.PreferLocked = prefs.PreferLocked;
            existingPrefs.KeepAlive = prefs.KeepAlive;
            existingPrefs.WhitelistJson = prefs.WhitelistJson;
            existingPrefs.BlacklistJson = prefs.BlacklistJson;
            await dbContext.UpdateAsync(existingPrefs);
        }
    }

    /// <summary>
    ///     Creates a custom voice channel for a user.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="user">The user.</param>
    /// <param name="hubConfig">The settings of the hub the user joined, or null for the first hub.</param>
    /// <returns>The created voice channel if successful, null otherwise.</returns>
    public async Task<Channel> CreateVoiceChannelAsync(SocketGuild guild, SocketGuildMember user,
        CustomVoiceConfig? hubConfig = null)
    {
        if (guild == null)
            throw new ArgumentNullException(nameof(guild));

        if (user == null)
            throw new ArgumentNullException(nameof(user));

        var config = hubConfig ?? await GetOrCreateConfigAsync(guild.Id);

        await using var dbContext = await dbFactory.CreateConnectionAsync();

        // Check if the user already has a channel and multiple channels are not allowed
        if (!config.AllowMultipleChannels)
        {
            var existingChannel = await dbContext.CustomVoiceChannels
                .FirstOrDefaultAsync(c => c.GuildId == guild.Id && c.OwnerId == user.Id);

            if (existingChannel != null)
            {
                // Delete the old channel and create a new one
                await DeleteVoiceChannelAsync(guild.Id, existingChannel.ChannelId);
            }
        }

        // Get user preferences if they exist and persistence is enabled
        UserVoicePreference prefs = null;
        if (config.PersistUserPreferences)
        {
            prefs = await GetUserPreferencesAsync(guild.Id, user.Id);
        }

        // Determine channel name
        var channelName = FormatChannelName(config.DefaultNameFormat, user, guild);
        if (prefs?.NameFormat != null && config.AllowNameCustomization)
        {
            channelName = FormatChannelName(prefs.NameFormat, user, guild);
        }

        // Sanitize channel name
        channelName = SanitizeChannelName(channelName);

        // Determine channel category
        Channel category = null;
        if (config.ChannelCategoryId.HasValue)
        {
            category = await guild.GetCategoryChannelAsync(config.ChannelCategoryId.Value);
        }

        // Determine user limit
        var userLimit = config.DefaultUserLimit;
        if (prefs?.UserLimit.HasValue == true && config.AllowUserLimitCustomization)
        {
            userLimit = Math.Min(prefs.UserLimit.Value, config.MaxUserLimit);
        }

        // Determine bitrate
        var bitrate = config.DefaultBitrate;
        if (prefs?.Bitrate.HasValue == true && config.AllowBitrateCustomization)
        {
            bitrate = Math.Min(prefs.Bitrate.Value, config.MaxBitrate);
        }

        // Create the channel
        var inherited = GetInheritedOverwrites(guild, config, category);
        Channel voiceChannel = await guild.CreateVoiceChannelAsync(new CreateVoiceChannelRequest
        {
            Name = channelName,
            ParentCategoryId = category?.Id,
            UserLimit = userLimit > 0 ? userLimit : null,
            Bitrate = bitrate * 1000,
            PermissionOverwrites = inherited
        });

        // Create the channel entry in the database
        var customChannel = new CustomVoiceChannel
        {
            GuildId = guild.Id,
            ChannelId = voiceChannel.Id,
            OwnerId = user.Id,
            CreatedAt = DateTime.UtcNow,
            LastActive = DateTime.UtcNow,
            IsLocked = prefs?.PreferLocked == true && config.AllowLocking,
            KeepAlive = prefs?.KeepAlive == true,
            HubVoiceChannelId = config.HubVoiceChannelId == 0 ? null : config.HubVoiceChannelId
        };
        var allowedUsers = new List<ulong>();
        var deniedUsers = new List<ulong>();

        // Add allowed/denied users if they exist in preferences
        if (prefs?.WhitelistJson != null)
        {
            try
            {
                allowedUsers = JsonSerializer.Deserialize<List<ulong>>(prefs.WhitelistJson) ?? [];
                customChannel.AllowedUsersJson = prefs.WhitelistJson;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error deserializing whitelist JSON for user {UserId}", user.Id);
            }
        }

        if (prefs?.BlacklistJson != null)
        {
            try
            {
                deniedUsers = JsonSerializer.Deserialize<List<ulong>>(prefs.BlacklistJson) ?? [];
                customChannel.DeniedUsersJson = prefs.BlacklistJson;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error deserializing blacklist JSON for user {UserId}", user.Id);
            }
        }

        if (config.CreateTextChannel)
        {
            customChannel.TextChannelId = await CreateTextChannelAsync(guild, config, channelName, category,
                user.Id, allowedUsers, deniedUsers);
            if (customChannel.TextChannelId is { } createdText)
                textChannels[voiceChannel.Id] = createdText;
        }

        await dbContext.InsertAsync(customChannel);

        // Keep track of the channel immediately after database insert
        activeChannels.AddOrUpdate(guild.Id,
            [voiceChannel.Id],
            (_, set) =>
            {
                set.Add(voiceChannel.Id);
                return set;
            });

        // Set permissions for the channel
        if (config.AutoPermission)
        {
            try
            {
                // Give the creator management permissions
                await voiceChannel.AddPermissionOverwriteAsync(user, BuildOwnerPermissions(config));

                // If the channel should be locked, deny everyone else connect permission
                if (customChannel.IsLocked)
                {
                    var everyoneRole = guild.EveryoneRole;
                    await voiceChannel.AddPermissionOverwriteAsync(everyoneRole, Overwrite(
                        connect: PermValue.Deny
                    ));

                    // Add allowed users if any
                    foreach (var allowedUserId in allowedUsers)
                    {
                        try
                        {
                            var allowedUser = await guild.GetUserAsync(allowedUserId);
                            if (allowedUser != null)
                            {
                                await voiceChannel.AddPermissionOverwriteAsync(allowedUser, Overwrite(
                                    connect: PermValue.Allow
                                ));
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex,
                                "Error setting permissions for allowed user {UserId} in custom voice channel {ChannelId}",
                                allowedUserId, voiceChannel.Id);
                        }
                    }

                    // Deny specific users if any
                    foreach (var deniedUserId in deniedUsers)
                    {
                        try
                        {
                            var deniedUser = await guild.GetUserAsync(deniedUserId);
                            if (deniedUser != null)
                            {
                                await voiceChannel.AddPermissionOverwriteAsync(deniedUser, Overwrite(
                                    connect: PermValue.Deny
                                ));
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex,
                                "Error setting permissions for denied user {UserId} in custom voice channel {ChannelId}",
                                deniedUserId, voiceChannel.Id);
                        }
                    }
                }

                // If a custom voice admin role is set, give it full permissions
                if (config.CustomVoiceAdminRoleId.HasValue)
                {
                    var adminRole = guild.GetRole(config.CustomVoiceAdminRoleId.Value);
                    if (adminRole != null)
                    {
                        await voiceChannel.AddPermissionOverwriteAsync(adminRole, Overwrite(
                            connect: PermValue.Allow,
                            speak: PermValue.Allow,
                            manageChannel: PermValue.Allow,
                            moveMembers: PermValue.Allow,
                            muteMembers: PermValue.Allow,
                            deafenMembers: PermValue.Allow
                        ));
                    }
                }

                foreach (var moderatorRole in ParseRoleIds(config.ModeratorRoleIds)
                             .Select(guild.GetRole).Where(role => role != null))
                {
                    await voiceChannel.AddPermissionOverwriteAsync(moderatorRole, Overwrite(
                        connect: PermValue.Allow,
                        speak: PermValue.Allow,
                        manageChannel: PermValue.Allow,
                        moveMembers: PermValue.Allow,
                        muteMembers: PermValue.Allow,
                        deafenMembers: PermValue.Allow
                    ));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error setting permissions for custom voice channel {ChannelId}", voiceChannel.Id);
            }
        }

        // Also set permissions for the associated text channel
        try
        {
            Channel textChannel = voiceChannel;
            if (textChannel != null && config.AutoPermission)
            {
                // Give the creator management permissions for the text channel
                await textChannel.AddPermissionOverwriteAsync(user, Overwrite(
                    viewChannel: PermValue.Allow,
                    sendMessages: PermValue.Allow,
                    readMessageHistory: PermValue.Allow,
                    manageChannel: PermValue.Allow,
                    manageMessages: PermValue.Allow
                ));

                // If the channel should be locked, restrict who can see the text channel
                if (customChannel.IsLocked)
                {
                    var everyoneRole = guild.EveryoneRole;
                    await textChannel.AddPermissionOverwriteAsync(everyoneRole, Overwrite(
                        viewChannel: PermValue.Deny
                    ));

                    // Add allowed users if any
                    foreach (var allowedUserId in allowedUsers)
                    {
                        try
                        {
                            var allowedUser = await guild.GetUserAsync(allowedUserId);
                            if (allowedUser != null)
                            {
                                await textChannel.AddPermissionOverwriteAsync(allowedUser, Overwrite(
                                    viewChannel: PermValue.Allow,
                                    sendMessages: PermValue.Allow,
                                    readMessageHistory: PermValue.Allow
                                ));
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex,
                                "Error setting text channel permissions for allowed user {UserId} in custom voice channel {ChannelId}",
                                allowedUserId, voiceChannel.Id);
                        }
                    }
                }

                // If a custom voice admin role is set, give it full permissions
                if (config.CustomVoiceAdminRoleId.HasValue)
                {
                    var adminRole = guild.GetRole(config.CustomVoiceAdminRoleId.Value);
                    if (adminRole != null)
                    {
                        await textChannel.AddPermissionOverwriteAsync(adminRole, Overwrite(
                            viewChannel: PermValue.Allow,
                            sendMessages: PermValue.Allow,
                            readMessageHistory: PermValue.Allow,
                            manageChannel: PermValue.Allow,
                            manageMessages: PermValue.Allow
                        ));
                    }
                }

                // Send a welcome message listing the control commands to the text channel
                var embed = new EmbedBuilder()
                    .WithOkColor(voiceChannel.Guild.Id)
                    .WithTitle(strings.CustomVoiceDefaultControlsTitle(voiceChannel.Guild.Id))
                    .WithDescription(strings.CustomVoiceDescription(voiceChannel.Guild.Id) ??
                                     strings.CustomVoiceDefaultDescription(voiceChannel.Guild.Id))
                    .AddField(strings.CustomVoiceControlsCommandsTitle(voiceChannel.Guild.Id),
                        await GetControlsHelpAsync(voiceChannel.Guild))
                    .Build();

                await textChannel.SendAsync(embed: embed);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting up text channel for voice channel {ChannelId}", voiceChannel.Id);
        }

        // Move the user to the new channel
        try
        {
            await user.ModifyAsync(props => props.ChannelId = voiceChannel.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error moving user {UserId} to custom voice channel {ChannelId}", user.Id,
                voiceChannel.Id);
        }

        collector.Feature("custom_voice", guild.Id);
        return voiceChannel;
    }

    /// <summary>
    ///     Deletes a custom voice channel.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <returns>True if deleted successfully, false otherwise.</returns>
    public async Task<bool> DeleteVoiceChannelAsync(ulong guildId, ulong channelId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (channelId == 0)
            throw new ArgumentException("Channel ID cannot be 0", nameof(channelId));

        try
        {
            var guild = client.GetGuild(guildId);
            if (guild == null)
                return false;

            var channel = guild.GetVoiceChannel(channelId) as Channel;
            if (channel == null)
                return false;

            await using var dbContext = await dbFactory.CreateConnectionAsync();

            // Delete from database
            var cvc = await dbContext.CustomVoiceChannels
                .FirstOrDefaultAsync(c => c.ChannelId == channelId);

            if (cvc != null)
            {
                await dbContext.DeleteAsync(cvc);
                if (cvc.TextChannelId is { } storedText)
                    textChannels.TryAdd(channelId, storedText);
            }

            await channel.DeleteAsync();
            await DeleteTextChannelAsync(guildId, channelId);

            // Remove from tracking collections
            if (activeChannels.TryGetValue(guildId, out var channels))
            {
                channels.Remove(channelId);
            }

            emptyChannels.TryRemove(channelId, out _);
            if (emptyChannelTimers.TryRemove(channelId, out var timer))
            {
                await timer.DisposeAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting custom voice channel {ChannelId}", channelId);
            return false;
        }
    }

    /// <summary>
    ///     Updates a custom voice channel's settings.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="name">The new name (optional).</param>
    /// <param name="userLimit">The new user limit (optional).</param>
    /// <param name="bitrate">The new bitrate (optional).</param>
    /// <param name="isLocked">The new locked status (optional).</param>
    /// <returns>True if updated successfully, false otherwise.</returns>
    public async Task<bool> UpdateVoiceChannelAsync(ulong guildId, ulong channelId, string name = null,
        int? userLimit = null,
        int? bitrate = null, bool? isLocked = null)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (channelId == 0)
            throw new ArgumentException("Channel ID cannot be 0", nameof(channelId));

        try
        {
            if (client.GetGuild(guildId) is not SocketGuild guild)
                return false;

            var channel = await guild.GetVoiceChannelAsync(channelId);
            if (channel == null)
                return false;

            // Get the config for permission checks
            var config = await GetConfigForChannelAsync(guildId, channelId);

            // Get the channel from database
            await using var dbContext = await dbFactory.CreateConnectionAsync();
            var customChannel = await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
            if (customChannel == null)
                return false;

            var allowedUsers = new List<ulong>();
            var deniedUsers = new List<ulong>();

            // Update channel settings
            if (!string.IsNullOrEmpty(name) && config.AllowNameCustomization)
            {
                // Sanitize the name
                name = SanitizeChannelName(name);
                await channel.ModifyAsync(new UpdateChannelRequest { Name = name });
            }

            if (userLimit.HasValue && config.AllowUserLimitCustomization)
            {
                var limit = Math.Min(userLimit.Value, config.MaxUserLimit);
                await channel.ModifyAsync(new UpdateChannelRequest { UserLimit = limit > 0 ? limit : (int?)null });
            }

            if (bitrate.HasValue && config.AllowBitrateCustomization)
            {
                var rate = Math.Min(bitrate.Value, config.MaxBitrate);

                await channel.ModifyAsync(new UpdateChannelRequest { Bitrate = rate * 1000 });
            }

            if (isLocked.HasValue && config.AllowLocking)
            {
                customChannel.IsLocked = isLocked.Value;

                // Update permissions based on lock status
                if (isLocked.Value)
                {
                    // Lock both voice and text channel

                    // Voice channel
                    await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, Overwrite(
                        connect: PermValue.Deny
                    ));

                    // Text channel
                    try
                    {
                        Channel textChannel = channel;
                        if (textChannel != null)
                        {
                            await textChannel.AddPermissionOverwriteAsync(guild.EveryoneRole, Overwrite(
                                viewChannel: PermValue.Deny
                            ));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error updating text channel permissions for channel {ChannelId}",
                            channelId);
                    }

                    if (string.IsNullOrEmpty(customChannel.AllowedUsersJson))
                    {
                        allowedUsers = [];
                    }
                    else
                    {
                        try
                        {
                            allowedUsers = JsonSerializer.Deserialize<List<ulong>>(customChannel.AllowedUsersJson) ??
                                           [];
                        }
                        catch (Exception)
                        {
                            allowedUsers = [];
                        }
                    }

                    foreach (var allowedUserId in allowedUsers)
                    {
                        var user = await guild.GetUserAsync(allowedUserId);
                        if (user != null)
                        {
                            // Voice channel
                            await channel.AddPermissionOverwriteAsync(user, Overwrite(
                                connect: PermValue.Allow
                            ));

                            // Text channel
                            try
                            {
                                Channel textChannel = channel;
                                if (textChannel != null)
                                {
                                    await textChannel.AddPermissionOverwriteAsync(user, Overwrite(
                                        viewChannel: PermValue.Allow,
                                        sendMessages: PermValue.Allow,
                                        readMessageHistory: PermValue.Allow
                                    ));
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex,
                                    "Error updating text channel permissions for user {UserId} in channel {ChannelId}",
                                    allowedUserId, channelId);
                            }
                        }
                    }
                }
                else
                {
                    // Unlock both voice and text channel

                    // Voice channel
                    var everyoneOverwrite = channel.GetPermissionOverwrite(guild.EveryoneRole);
                    if (everyoneOverwrite is { } permissions)
                    {
                        if (permissions.Connect == PermValue.Deny)
                        {
                            await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, Overwrite(
                                connect: PermValue.Inherit
                            ));
                        }
                    }

                    // Text channel
                    try
                    {
                        Channel textChannel = channel;
                        if (textChannel != null)
                        {
                            var textEveryoneOverwrite = textChannel.GetPermissionOverwrite(guild.EveryoneRole);
                            if (textEveryoneOverwrite is { } textPermissions)
                            {
                                if (textPermissions.ViewChannel == PermValue.Deny)
                                {
                                    await textChannel.AddPermissionOverwriteAsync(guild.EveryoneRole,
                                        Overwrite(
                                            viewChannel: PermValue.Inherit
                                        ));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error updating text channel permissions for channel {ChannelId}",
                            channelId);
                    }
                }

                // Update the database
                await dbContext.UpdateAsync(customChannel);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating custom voice channel {ChannelId}", channelId);
            return false;
        }
    }

    /// <summary>
    ///     Adds a user to the allowed list for a locked channel.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="userId">The user ID to allow.</param>
    /// <returns>True if added successfully, false otherwise.</returns>
    public async Task<bool> AllowUserAsync(ulong guildId, ulong channelId, ulong userId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (channelId == 0)
            throw new ArgumentException("Channel ID cannot be 0", nameof(channelId));

        if (userId == 0)
            throw new ArgumentException("User ID cannot be 0", nameof(userId));

        if (client.GetGuild(guildId) is not SocketGuild guild)
            return false;

        var channel = await guild.GetVoiceChannelAsync(channelId);
        if (channel == null)
            return false;

        // Get the config for permission checks
        var config = await GetConfigForChannelAsync(guildId, channelId);
        if (!config.AllowUserManagement)
            return false;

        // Get the channel from database
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var customChannel = await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (customChannel == null)
            return false;

        // Parse allowed users
        var allowedUsers = new List<ulong>();
        if (!string.IsNullOrEmpty(customChannel.AllowedUsersJson))
        {
            try
            {
                allowedUsers = JsonSerializer.Deserialize<List<ulong>>(customChannel.AllowedUsersJson) ?? [];
            }
            catch (Exception)
            {
                allowedUsers = [];
            }
        }

        // Add the user if not already in the list
        if (!allowedUsers.Contains(userId))
        {
            allowedUsers.Add(userId);
            customChannel.AllowedUsersJson = JsonSerializer.Serialize(allowedUsers);
            await dbContext.UpdateAsync(customChannel);
        }

        // Update channel permissions
        if (customChannel.IsLocked)
        {
            var user = await guild.GetUserAsync(userId);
            if (user != null)
            {
                // Voice channel
                await channel.AddPermissionOverwriteAsync(user, Overwrite(
                    connect: PermValue.Allow
                ));

                // Text channel
                try
                {
                    Channel textChannel = channel;
                    if (textChannel != null)
                    {
                        await textChannel.AddPermissionOverwriteAsync(user, Overwrite(
                            viewChannel: PermValue.Allow,
                            sendMessages: PermValue.Allow,
                            readMessageHistory: PermValue.Allow
                        ));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Error updating text channel permissions for user {UserId} in channel {ChannelId}",
                        userId, channelId);
                }
            }
        }

        // Remove from denied users if present
        var deniedUsers = new List<ulong>();
        if (!string.IsNullOrEmpty(customChannel.DeniedUsersJson))
        {
            try
            {
                deniedUsers = JsonSerializer.Deserialize<List<ulong>>(customChannel.DeniedUsersJson) ?? [];
            }
            catch (Exception)
            {
                deniedUsers = [];
            }
        }

        if (deniedUsers.Contains(userId))
        {
            deniedUsers.Remove(userId);
            customChannel.DeniedUsersJson = JsonSerializer.Serialize(deniedUsers);
            await dbContext.UpdateAsync(customChannel);
        }

        return true;
    }

    /// <summary>
    ///     Removes a user from the allowed list for a locked channel.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="userId">The user ID to disallow.</param>
    /// <returns>True if removed successfully, false otherwise.</returns>
    public async Task<bool> DenyUserAsync(ulong guildId, ulong channelId, ulong userId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (channelId == 0)
            throw new ArgumentException("Channel ID cannot be 0", nameof(channelId));

        if (userId == 0)
            throw new ArgumentException("User ID cannot be 0", nameof(userId));

        if (client.GetGuild(guildId) is not SocketGuild guild)
            return false;

        var channel = await guild.GetVoiceChannelAsync(channelId);
        if (channel == null)
            return false;

        // Get the config for permission checks
        var config = await GetConfigForChannelAsync(guildId, channelId);
        if (!config.AllowUserManagement)
            return false;

        // Get the channel from database
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var customChannel = await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (customChannel == null)
            return false;

        // Don't allow denying the channel owner
        if (customChannel.OwnerId == userId)
            return false;

        // Parse denied users
        var deniedUsers = new List<ulong>();
        if (!string.IsNullOrEmpty(customChannel.DeniedUsersJson))
        {
            try
            {
                deniedUsers = JsonSerializer.Deserialize<List<ulong>>(customChannel.DeniedUsersJson) ?? [];
            }
            catch (Exception)
            {
                deniedUsers = [];
            }
        }

        // Add the user if not already in the list
        if (!deniedUsers.Contains(userId))
        {
            deniedUsers.Add(userId);
            customChannel.DeniedUsersJson = JsonSerializer.Serialize(deniedUsers);
            await dbContext.UpdateAsync(customChannel);
        }

        // Update channel permissions
        var user = await guild.GetUserAsync(userId);
        if (user != null)
        {
            // Voice channel
            await channel.AddPermissionOverwriteAsync(user, Overwrite(
                connect: PermValue.Deny
            ));

            // Text channel
            try
            {
                Channel textChannel = channel;
                if (textChannel != null)
                {
                    await textChannel.AddPermissionOverwriteAsync(user, Overwrite(
                        viewChannel: PermValue.Deny
                    ));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating text channel permissions for user {UserId} in channel {ChannelId}",
                    userId, channelId);
            }

            // If the user is in the channel, kick them
            if (user.VoiceChannel?.Id == channelId)
            {
                try
                {
                    await user.ModifyAsync(props => props.ChannelId = null);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error disconnecting denied user {UserId} from voice channel {ChannelId}",
                        userId,
                        channelId);
                }
            }
        }

        // Remove from allowed users if present
        var allowedUsers = new List<ulong>();
        if (!string.IsNullOrEmpty(customChannel.AllowedUsersJson))
        {
            try
            {
                allowedUsers = JsonSerializer.Deserialize<List<ulong>>(customChannel.AllowedUsersJson) ?? [];
            }
            catch (Exception)
            {
                allowedUsers = [];
            }
        }

        if (allowedUsers.Contains(userId))
        {
            allowedUsers.Remove(userId);
            customChannel.AllowedUsersJson = JsonSerializer.Serialize(allowedUsers);
            await dbContext.UpdateAsync(customChannel);
        }

        return true;
    }

    /// <summary>
    ///     Transfers ownership of a custom voice channel to another user.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="newOwnerId">The new owner's user ID.</param>
    /// <returns>True if ownership was transferred successfully, false otherwise.</returns>
    public async Task<bool> TransferOwnershipAsync(ulong guildId, ulong channelId, ulong newOwnerId)
    {
        if (guildId == 0)
            throw new ArgumentException("Guild ID cannot be 0", nameof(guildId));

        if (channelId == 0)
            throw new ArgumentException("Channel ID cannot be 0", nameof(channelId));

        if (newOwnerId == 0)
            throw new ArgumentException("New owner ID cannot be 0", nameof(newOwnerId));

        if (client.GetGuild(guildId) is not SocketGuild guild)
            return false;

        var channel = await guild.GetVoiceChannelAsync(channelId);
        if (channel == null)
            return false;

        var newOwner = await guild.GetUserAsync(newOwnerId);
        if (newOwner == null)
            return false;

        // Get the channel from database
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var customChannel = await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (customChannel == null)
            return false;

        // Update ownership
        var oldOwnerId = customChannel.OwnerId;
        customChannel.OwnerId = newOwnerId;
        customChannel.OwnerLeftAt = null;

        // Update permissions
        try
        {
            // Remove admin permissions from old owner
            var oldOwner = await guild.GetUserAsync(oldOwnerId);
            if (oldOwner != null)
            {
                // Voice channel
                await channel.AddPermissionOverwriteAsync(oldOwner, Overwrite(
                    connect: PermValue.Allow,
                    speak: PermValue.Allow
                ));

                // Text channel
                try
                {
                    Channel textChannel = channel;
                    if (textChannel != null)
                    {
                        await textChannel.AddPermissionOverwriteAsync(oldOwner, Overwrite(
                            viewChannel: PermValue.Allow,
                            sendMessages: PermValue.Allow,
                            readMessageHistory: PermValue.Allow
                        ));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Error updating text channel permissions for old owner {UserId} in channel {ChannelId}",
                        oldOwnerId, channelId);
                }
            }

            // Add admin permissions to new owner
            // Voice channel
            await channel.AddPermissionOverwriteAsync(newOwner, Overwrite(
                connect: PermValue.Allow,
                speak: PermValue.Allow,
                manageChannel: PermValue.Allow,
                moveMembers: PermValue.Allow,
                muteMembers: PermValue.Allow,
                deafenMembers: PermValue.Allow
            ));

            // Text channel
            try
            {
                Channel textChannel = channel;
                if (textChannel != null)
                {
                    await textChannel.AddPermissionOverwriteAsync(newOwner, Overwrite(
                        viewChannel: PermValue.Allow,
                        sendMessages: PermValue.Allow,
                        readMessageHistory: PermValue.Allow,
                        manageChannel: PermValue.Allow,
                        manageMessages: PermValue.Allow
                    ));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error updating text channel permissions for new owner {UserId} in channel {ChannelId}",
                    newOwnerId, channelId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating permissions during ownership transfer for channel {ChannelId}",
                channelId);
        }

        // Save changes
        await dbContext.UpdateAsync(customChannel);

        if (customChannel.TextChannelId is { } textId && guild.GetTextChannel(textId) is Channel pairedText)
        {
            try
            {
                if (await guild.GetUserAsync(oldOwnerId) is { } previousOwner)
                    await pairedText.AddPermissionOverwriteAsync(previousOwner, TextMember);

                await pairedText.AddPermissionOverwriteAsync(newOwner, Overwrite(
                    viewChannel: PermValue.Allow, sendMessages: PermValue.Allow,
                    readMessageHistory: PermValue.Allow, manageMessages: PermValue.Allow));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error moving text channel ownership for channel {ChannelId}", channelId);
            }
        }

        return true;
    }

    /// <summary>
    ///     Gets the permission overwrites a new channel starts with when the hub syncs them from the hub or category.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="config">The hub's settings.</param>
    /// <param name="category">The category the channel is created in.</param>
    /// <returns>The overwrites to copy, or null when nothing is synced.</returns>
    private static ChannelOverwriteRequest[]? GetInheritedOverwrites(SocketGuild guild, CustomVoiceConfig config,
        Channel? category)
    {
        Channel? source = (CustomVoiceSyncMode)config.SyncPermissions switch
        {
            CustomVoiceSyncMode.Hub => guild.GetVoiceChannel(config.HubVoiceChannelId) as Channel,
            CustomVoiceSyncMode.Category => category,
            _ => null
        };

        return source?.PermissionOverwrites?.Select(OverwritePermissions.ToRequest).ToArray();
    }

    /// <summary>
    ///     Gets how long a member has to wait before they can claim a channel whose owner left.
    /// </summary>
    /// <param name="config">The hub's settings.</param>
    /// <param name="channel">The temporary channel.</param>
    /// <returns>The time left, or zero when the channel can be claimed now.</returns>
    public static TimeSpan GetClaimWait(CustomVoiceConfig config, CustomVoiceChannel channel)
    {
        if (config.OwnerLockMinutes <= 0 || channel.OwnerLeftAt is not { } leftAt)
            return TimeSpan.Zero;

        var remaining = leftAt.AddMinutes(config.OwnerLockMinutes) - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    ///     Builds the permissions a channel owner gets in their channel, following the hub's owner toggles.
    /// </summary>
    /// <param name="config">The hub's settings.</param>
    /// <returns>The owner's permission overwrite.</returns>
    public static OverwritePermissions BuildOwnerPermissions(CustomVoiceConfig config)
    {
        return Overwrite(
                connect: PermValue.Allow,
                speak: PermValue.Allow,
                manageChannel: config.OwnerManageChannel ? PermValue.Allow : PermValue.Inherit,
                moveMembers: config.OwnerMoveMembers ? PermValue.Allow : PermValue.Inherit,
                muteMembers: PermValue.Allow,
                deafenMembers: PermValue.Allow)
            .With(ChannelPermission.ManagePermissions, config.OwnerManageRoles ? PermValue.Allow : PermValue.Inherit)
            .With(ChannelPermission.PrioritySpeaker,
                config.OwnerPrioritySpeaker ? PermValue.Allow : PermValue.Inherit);
    }

    /// <summary>
    ///     Parses stored comma separated role IDs.
    /// </summary>
    /// <param name="stored">The stored text.</param>
    /// <returns>The role IDs.</returns>
    public static List<ulong> ParseRoleIds(string? stored)
    {
        return (stored ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => ulong.TryParse(x, out var id) ? id : 0)
            .Where(x => x != 0)
            .ToList();
    }

    /// <summary>
    ///     Hides a temporary channel from everyone who can't already see it, or shows it again.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="hidden">True to hide the channel, false to reveal it.</param>
    /// <returns>True when the channel was updated.</returns>
    public async Task<bool> SetHiddenAsync(ulong guildId, ulong channelId, bool hidden)
    {
        try
        {
            if (client.GetGuild(guildId) is not { } guild || guild.GetVoiceChannel(channelId) is not Channel channel)
                return false;

            await using var dbContext = await dbFactory.CreateConnectionAsync();
            var customChannel = await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
            if (customChannel == null)
                return false;

            var everyone = channel.GetPermissionOverwrite(guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
            await channel.AddPermissionOverwriteAsync(guild.EveryoneRole,
                everyone.Modify(viewChannel: hidden ? PermValue.Deny : PermValue.Inherit));

            if (hidden)
            {
                foreach (var member in channel.ConnectedUsers.Where(x => !x.IsBot))
                {
                    var current = channel.GetPermissionOverwrite(member) ?? OverwritePermissions.InheritAll;
                    await channel.AddPermissionOverwriteAsync(member, current.Modify(viewChannel: PermValue.Allow));
                }
            }

            customChannel.IsHidden = hidden;
            await dbContext.UpdateAsync(customChannel);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error changing whether custom voice channel {ChannelId} is hidden", channelId);
            return false;
        }
    }

    /// <summary>
    ///     Disconnects a member from a temporary channel without banning them from it.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="userId">The member to disconnect.</param>
    /// <returns>True when the member was in the channel and was disconnected.</returns>
    public async Task<bool> KickUserAsync(ulong guildId, ulong channelId, ulong userId)
    {
        try
        {
            if (client.GetGuild(guildId) is not { } guild)
                return false;

            await using var dbContext = await dbFactory.CreateConnectionAsync();
            var customChannel = await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
            if (customChannel == null || customChannel.OwnerId == userId)
                return false;

            var user = guild.GetUser(userId);
            if (user?.VoiceChannel?.Id != channelId)
                return false;

            await user.ModifyAsync(props => props.ChannelId = null);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error disconnecting {UserId} from custom voice channel {ChannelId}", userId,
                channelId);
            return false;
        }
    }

    private static OverwritePermissions Overwrite(PermValue? viewChannel = null, PermValue? sendMessages = null,
        PermValue? connect = null, PermValue? speak = null, PermValue? readMessageHistory = null,
        PermValue? manageMessages = null, PermValue? manageChannel = null, PermValue? muteMembers = null,
        PermValue? deafenMembers = null, PermValue? moveMembers = null)
    {
        return OverwritePermissions.InheritAll
            .Modify(viewChannel, sendMessages, null, connect, speak, null, null, null, readMessageHistory,
                manageMessages, manageChannel)
            .With(ChannelPermission.MuteMembers, muteMembers ?? PermValue.Inherit)
            .With(ChannelPermission.DeafenMembers, deafenMembers ?? PermValue.Inherit)
            .With(ChannelPermission.MoveMembers, moveMembers ?? PermValue.Inherit);
    }

    /// <summary>
    ///     Formats a channel name using placeholders.
    /// </summary>
    private string FormatChannelName(string format, SocketGuildMember user, SocketGuild guild)
    {
        return format
            .Replace("{0}", user.Username)
            .Replace("{1}", user.Discriminator)
            .Replace("{2}", guild.Name);
    }

    /// <summary>
    ///     Sanitizes a channel name to comply with Discord's restrictions.
    /// </summary>
    private string SanitizeChannelName(string name)
    {
        // Remove illegal characters
        name = string.Join("",
            name.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '-' || c == '_'));

        // Ensure it's not empty
        if (string.IsNullOrWhiteSpace(name))
            name = "voice-channel";

        // Trim to Discord's maximum length
        if (name.Length > 100)
            name = name[..100];

        // Remove starting/ending spaces
        return name.Trim();
    }

    /// <summary>
    ///     Builds the text listing the commands that manage a custom voice channel, using the guild's prefix.
    /// </summary>
    /// <param name="guild">The guild the commands will be used in.</param>
    /// <returns>The localized command list.</returns>
    public async Task<string> GetControlsHelpAsync(SocketGuild guild)
    {
        return strings.CustomVoiceControlsCommands(guild.Id, await guildSettings.GetPrefix(guild));
    }

    /// <summary>
    ///     Handles voice state updates to create, manage, or clean up custom voice channels.
    /// </summary>
    private async Task OnUserVoiceStateUpdated(SocketGuildMember guildUser, VoiceStateSnapshot oldState,
        VoiceStateSnapshot newState)
    {

        var guild = guildUser.Guild;
        if (guild == null)
            return;

        try
        {
            await UpdateTextAccessAsync(guildUser, oldState, newState);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update the text access of {UserId}", guildUser.Id);
        }

        try
        {
            if (newState.VoiceChannel != null &&
                await GetHubConfigAsync(guild.Id, newState.VoiceChannel.Id) is { } hubConfig)
            {
                // Create a custom voice channel for the user
                await CreateVoiceChannelAsync(guild, guildUser, hubConfig);
                return;
            }

            // Check if the user joined a custom voice channel
            if (newState.VoiceChannel != null && activeChannels.TryGetValue(guild.Id, out var channels) &&
                channels.Contains(newState.VoiceChannel.Id))
            {
                // Update last active time
                await using var dbContext = await dbFactory.CreateConnectionAsync();
                var customChannel =
                    await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c =>
                        c.ChannelId == newState.ChannelId);
                if (customChannel != null)
                {
                    customChannel.LastActive = DateTime.UtcNow;
                    if (customChannel.OwnerId == guildUser.Id)
                        customChannel.OwnerLeftAt = null;

                    await dbContext.UpdateAsync(customChannel);
                }

                // Remove from empty channels if it was there
                emptyChannels.TryRemove(newState.VoiceChannel.Id, out _);
                if (emptyChannelTimers.TryRemove(newState.VoiceChannel.Id, out var timer))
                {
                    await timer.DisposeAsync();
                }
            }

            activeChannels.TryGetValue(guild.Id, out var oldChannels);
            // Check if the user left a custom voice channel
            if (oldState.VoiceChannel != null &&
                oldChannels != null && oldChannels.Contains(oldState.VoiceChannel.Id))
            {
                if (oldState.VoiceChannel.Id != newState.VoiceChannel?.Id)
                {
                    await using var ownerDb = await dbFactory.CreateConnectionAsync();
                    await ownerDb.CustomVoiceChannels
                        .Where(c => c.ChannelId == oldState.VoiceChannel.Id && c.OwnerId == guildUser.Id)
                        .Set(c => c.OwnerLeftAt, DateTime.UtcNow)
                        .UpdateAsync();
                }

                var vc = guild.GetVoiceChannel(oldState.VoiceChannel.Id) as Channel;

                // Add null check and use consistent property
                if (vc != null)
                {
                    // Add a small delay to ensure user count is updated
                    await Task.Delay(100);

                    // Refresh the channel reference to get updated user count
                    vc = guild.GetVoiceChannel(oldState.VoiceChannel.Id) as Channel;

                    // Check if the channel is now empty - filter for actually connected non-bot users
                    if (vc != null)
                    {
                        var connectedUsersCount = vc.ConnectedUsers.Count(u => u.VoiceChannel?.Id == vc.Id && !u.IsBot);
                        if (connectedUsersCount == 0)
                        {
                            // Get the channel to check if it should be kept
                            await using var dbContext = await dbFactory.CreateConnectionAsync();
                            var customChannel =
                                await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c =>
                                    c.ChannelId == oldState.ChannelId);

                            var config = await GetConfigForChannelAsync(guild.Id, oldState.VoiceChannel.Id);
                            if (customChannel is { KeepAlive: false } && config.DeleteWhenEmpty)
                            {
                                // Mark the channel as empty
                                emptyChannels.TryAdd(oldState.VoiceChannel.Id, DateTime.UtcNow);

                                // Schedule deletion after timeout
                                if (config.EmptyChannelTimeout > 0)
                                {
                                    var timer = new Timer(_ =>
                                        {
                                            Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    await DeleteEmptyChannelAsync(guild.Id, oldState.VoiceChannel.Id);
                                                }
                                                catch (Exception ex)
                                                {
                                                    logger.LogError(ex, "Timer callback failed for channel {ChannelId}",
                                                        oldState.VoiceChannel.Id);
                                                }
                                            });
                                        }, null, TimeSpan.FromMinutes(config.EmptyChannelTimeout),
                                        Timeout.InfiniteTimeSpan);

                                    emptyChannelTimers.TryAdd(oldState.VoiceChannel.Id, timer);
                                }
                                else
                                {
                                    // Delete immediately
                                    await DeleteEmptyChannelAsync(guild.Id, oldState.VoiceChannel.Id);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling voice state update for user {UserId} in guild {GuildId}", guildUser.Id,
                guild.Id);
        }
    }

    /// <summary>
    ///     Handles channel deletion to clean up database entries.
    /// </summary>
    private async Task OnChannelDestroyed(Channel channel)
    {
        if (channel is not { Type: ChannelType.GuildVoice } voiceChannel)
            return;

        var guild = (voiceChannel as Channel)?.Guild;
        if (guild == null)
            return;

        try
        {
            // Check if this was a custom voice channel
            await using var dbContext = await dbFactory.CreateConnectionAsync();
            var customChannel = await dbContext.CustomVoiceChannels
                .FirstOrDefaultAsync(c => c.ChannelId == voiceChannel.Id);

            if (customChannel != null)
            {
                // Delete the channel from the database
                await dbContext.DeleteAsync(customChannel);
                if (customChannel.TextChannelId is { } storedText)
                    textChannels.TryAdd(voiceChannel.Id, storedText);

                await DeleteTextChannelAsync(guild.Id, voiceChannel.Id);

                // Remove from tracking collections
                if (activeChannels.TryGetValue(guild.Id, out var channels))
                {
                    channels.Remove(voiceChannel.Id);
                }

                emptyChannels.TryRemove(voiceChannel.Id, out _);
                if (emptyChannelTimers.TryRemove(voiceChannel.Id, out var timer))
                {
                    await timer.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling channel deletion for channel {ChannelId} in guild {GuildId}",
                channel.Id,
                guild.Id);
        }
    }

    /// <summary>
    ///     Handles guild join to set up tracking.
    /// </summary>
    private async Task OnJoinedGuild(SocketGuild guild)
    {
        try
        {
            // Load active channels for this guild
            await using var dbContext = await dbFactory.CreateConnectionAsync();
            var channels = await dbContext.CustomVoiceChannels.Where(c => c.GuildId == guild.Id).ToListAsync();

            if (channels.Any())
            {
                var channelIds = channels.Select(c => c.ChannelId).ToHashSet();
                activeChannels.TryAdd(guild.Id, channelIds);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling guild join for guild {GuildId}", guild.Id);
        }
    }

    /// <summary>
    ///     Handles guild leave to clean up tracking.
    /// </summary>
    private Task OnLeftGuild(SocketGuild guild)
    {
        try
        {
            // Remove tracking for this guild
            activeChannels.TryRemove(guild.Id, out _);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling guild leave for guild {GuildId}", guild.Id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Deletes an empty channel after the timeout period.
    /// </summary>
    private async Task DeleteEmptyChannelAsync(ulong guildId, ulong channelId)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return;

        var channel = guild.GetVoiceChannel(channelId) as Channel;

        // Double-check that it's still empty before deleting - check actually connected non-bot users
        if (channel != null)
        {
            var connectedUsersCount = channel.ConnectedUsers.Count(u => u.VoiceChannel?.Id == channelId && !u.IsBot);
            if (connectedUsersCount == 0)
            {
                await DeleteVoiceChannelAsync(guildId, channelId);
            }
            else
            {
                // Channel has users again, remove from empty tracking
                emptyChannels.TryRemove(channelId, out _);
                if (emptyChannelTimers.TryRemove(channelId, out var timer))
                {
                    await timer.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    ///     Starts a background task to clean up empty channels.
    /// </summary>
    private async Task StartEmptyChannelCleanupTask()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5));

                await @lock.WaitAsync();
                try
                {
                    // Get all potential empty channels
                    var emptyChannels = new Dictionary<ulong, DateTime>(this.emptyChannels);

                    foreach (var (channelId, emptyTime) in emptyChannels)
                    {
                        try
                        {
                            // Find the guild for this channel
                            await using var dbContext = await dbFactory.CreateConnectionAsync();
                            var customChannel =
                                await dbContext.CustomVoiceChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
                            if (customChannel == null)
                            {
                                this.emptyChannels.TryRemove(channelId, out _);
                                continue;
                            }

                            var guild = client.GetGuild(customChannel.GuildId);
                            if (guild == null)
                            {
                                this.emptyChannels.TryRemove(channelId, out _);
                                continue;
                            }

                            var config = await GetConfigForChannelAsync(customChannel.GuildId, customChannel.ChannelId);

                            // Check if the timeout has passed
                            if ((DateTime.UtcNow - emptyTime).TotalMinutes >= config.EmptyChannelTimeout &&
                                !customChannel.KeepAlive &&
                                config.DeleteWhenEmpty)
                            {
                                // Delete the channel
                                await DeleteVoiceChannelAsync(customChannel.GuildId, channelId);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error cleaning up empty channel {ChannelId}", channelId);
                        }
                    }

                    await SweepLeftoverChannelsAsync();
                }
                finally
                {
                    @lock.Release();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in empty channel cleanup task");
            }
        }
    }

    /// <summary>
    ///     Deletes temporary channels left empty past the hub's empty timeout without a running timer.
    /// </summary>
    private async Task SweepLeftoverChannelsAsync()
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var rows = await dbContext.CustomVoiceChannels.ToListAsync();
        var configs = (await dbContext.CustomVoiceConfigs.ToListAsync()).GroupBy(c => c.GuildId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var row in rows)
        {
            try
            {
                if (row.KeepAlive || client.GetGuild(row.GuildId) is not { } guild ||
                    !configs.TryGetValue(row.GuildId, out var guildConfigs))
                    continue;

                var config = guildConfigs.FirstOrDefault(c => c.HubVoiceChannelId == row.HubVoiceChannelId) ??
                             PickPrimaryConfig(guildConfigs);
                if (config is not { CleanupLeftovers: true, DeleteWhenEmpty: true } ||
                    guild.GetVoiceChannel(row.ChannelId) is not Channel voice ||
                    voice.ConnectedUsers.Any(u => !u.IsBot))
                    continue;

                if ((DateTime.UtcNow - row.LastActive).TotalMinutes < Math.Max(config.EmptyChannelTimeout, 1))
                    continue;

                await DeleteVoiceChannelAsync(row.GuildId, row.ChannelId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error cleaning up leftover channel {ChannelId}", row.ChannelId);
            }
        }
    }
}