using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Modules.Moderation.Services;
using Mewdeko.Services.Analytics;
using Mewdeko.Services.Strings;

// ReSharper disable GrammarMistakeInComment

namespace Mewdeko.Modules.Administration.Services;

/// <summary>
///     Service for managing log commands.
/// </summary>
/// <param name="dbFactory">The database service.</param>
/// <param name="client">The Fluxer client.</param>
/// <param name="handler">The event handler.</param>
/// <param name="muteService">The mute service.</param>
/// <param name="strings">The localization service.</param>
/// <param name="logger">The logger instance for structured logging.</param>
/// <param name="collector">The analytics collector.</param>
public class LogCommandService(
    IDataConnectionFactory dbFactory,
    FluxerClient client,
    EventHandler handler,
    MuteService muteService,
    GeneratedBotStrings strings,
    ILogger<LogCommandService> logger,
    IAnalyticsCollector collector) : INService, IReadyExecutor
{
    /// <summary>
    ///     Result of toggling a channel's ignore status
    /// </summary>
    public enum IgnoreResult
    {
        /// <summary>Channel was added to ignore list</summary>
        Added,

        /// <summary>Channel was removed from ignore list</summary>
        Removed,

        /// <summary>An error occurred</summary>
        Error
    }

    /// <summary>
    ///     Log category types.
    /// </summary>
    public enum LogCategoryTypes
    {
        /// <summary> All available log event types. </summary>
        All,

        /// <summary> Events related to users (join, leave, roles, name changes, voice). </summary>
        Users,

        /// <summary> Events related to thread creation, deletion, updates. </summary>
        Threads,

        /// <summary> Events related to role creation, deletion, updates. </summary>
        Roles,

        /// <summary> Events related to server-wide settings changes and server events. </summary>
        Server,

        /// <summary> Events related to channel creation, deletion, updates. </summary>
        Channel,

        /// <summary> Events related to message updates and deletions. </summary>
        Messages,

        /// <summary> Events related to moderation actions (bans, unbans, mutes). </summary>
        Moderation,

        /// <summary> Disables logging for all event types. </summary>
        None
    }

    /// <summary>
    ///     Dictionary of log types.
    /// </summary>
    public enum LogType
    {
        /// <summary> Miscellaneous or custom log events not covered by other types. </summary>
        Other,

        /// <summary> A role's properties (name, color, perms, etc.) were updated. </summary>
        RoleUpdated,

        /// <summary> A new role was created. </summary>
        RoleCreated,

        /// <summary> A role was deleted. </summary>
        RoleDeleted,

        /// <summary> Server settings (name, icon, owner, etc.) were updated. </summary>
        ServerUpdated,

        /// <summary> A new thread was created. </summary>
        ThreadCreated,

        /// <summary> One or more roles were added to a user. </summary>
        UserRoleAdded,

        /// <summary> One or more roles were removed from a user. </summary>
        UserRoleRemoved,

        /// <summary> A user's global username changed. </summary>
        UsernameUpdated,

        /// <summary> A user's server nickname changed. </summary>
        NicknameUpdated,

        /// <summary> A user's avatar changed. </summary>
        AvatarUpdated,

        /// <summary> A thread was deleted. </summary>
        ThreadDeleted,

        /// <summary> A thread's properties (name, archive status, etc.) were updated. </summary>
        ThreadUpdated,

        /// <summary> A message's content was edited. </summary>
        MessageUpdated,

        /// <summary> A message was deleted. </summary>
        MessageDeleted,

        /// <summary> A user joined the server. </summary>
        UserJoined,

        /// <summary> A user left (or was kicked from) the server. </summary>
        UserLeft,

        /// <summary> A user was banned from the server. </summary>
        UserBanned,

        /// <summary> A user was unbanned from the server. </summary>
        UserUnbanned,

        /// <summary> A user's profile properties (avatar, global name) changed. </summary>
        UserUpdated,

        /// <summary> A new channel was created. </summary>
        ChannelCreated,

        /// <summary> A channel was deleted. </summary>
        ChannelDestroyed,

        /// <summary> A channel's properties (name, topic, permissions, etc.) were updated. </summary>
        ChannelUpdated,

        /// <summary> A user's voice state changed (join, leave, move, mute, deafen, stream). </summary>
        VoicePresence,

        /// <summary> A user potentially used Text-to-Speech in a voice channel (Detection not guaranteed). </summary>
        VoicePresenceTts,

        /// <summary> A user was muted (voice or text). </summary>
        UserMuted,

        /// <summary> An invite was created. </summary>
        InviteCreated,

        /// <summary> An invite was deleted. </summary>
        InviteDeleted,

        /// <summary> Multiple messages were bulk deleted. </summary>
        MessagesBulkDeleted,

        /// <summary> Reactions were added or removed from messages. </summary>
        ReactionEvents,

        /// <summary> A member was timed out. </summary>
        UserTimedOut,

        /// <summary> A member's timeout was removed. </summary>
        UserTimeoutRemoved,

        /// <summary> A message contained an invite link. </summary>
        InviteLinkPosted,

        /// <summary> A custom emoji was created. </summary>
        EmojiCreated,

        /// <summary> A custom emoji was renamed. </summary>
        EmojiUpdated,

        /// <summary> A custom emoji was deleted. </summary>
        EmojiDeleted,

        /// <summary> A custom sticker was created. </summary>
        StickerCreated,

        /// <summary> A custom sticker was edited. </summary>
        StickerUpdated,

        /// <summary> A custom sticker was deleted. </summary>
        StickerDeleted,

        /// <summary> A channel's permission overwrites changed. </summary>
        ChannelPermissionsUpdated,

        /// <summary> A channel's webhooks changed. </summary>
        WebhookChanged
    }

    /// <summary>
    ///     Result of importing a log configuration.
    /// </summary>
    /// <param name="Applied">The log types that were mapped to a channel.</param>
    /// <param name="Unmapped">Descriptions of channels that could not be mapped.</param>
    /// <param name="Valid">Whether the code could be read.</param>
    public record LogImportResult(int Applied, List<string> Unmapped, bool Valid);

    private sealed class LogConfigExport
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, ExportedChannel> Types { get; set; } = new();
        public List<ExportedChannel> Ignored { get; set; } = [];
        public List<string> Prefixes { get; set; } = [];
    }

    private sealed class ExportedChannel
    {
        public ulong Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static readonly Regex InviteRegex = new(
        @"(?:https?://)?(?:www\.)?(?:fluxer\.gg|fluxer\.app/invite)/[A-Za-z0-9-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    ///     Cache of ignored channels per guild
    /// </summary>
    private readonly ConcurrentDictionary<ulong, HashSet<ulong>> ignoredChannelsCache = new();

    /// <summary>
    ///     Reaction events batching system to prevent spam in high-activity servers
    /// </summary>
    private readonly ConcurrentDictionary<ulong, MessageReactionBatch> reactionBatches = new();

    private Timer reactionBatchTimer;

    /// <summary>
    ///     Dictionary of log settings for each guild.
    /// </summary>
    public ConcurrentDictionary<ulong, LoggingV2> GuildLogSettings { get; set; }


    private const int MaxEmbedTitleLength = 256;
    private const int MaxEmbedDescriptionLength = 4096;
    private const int MaxEmbedFieldNameLength = 256;
    private const int MaxEmbedFieldValueLength = 1024;
    private const int MaxEmbedFields = 25;
    private const int MaxEmbedLength = 6000;

    /// <summary>
    ///     Creates a log embed with the given title, description and color, stamped with the current time.
    /// </summary>
    /// <param name="title">The embed title.</param>
    /// <param name="description">The embed description.</param>
    /// <param name="color">The embed color.</param>
    /// <returns>The embed builder.</returns>
    private static EmbedBuilder LogEmbed(string title, string description, Color color)
    {
        return new EmbedBuilder()
            .WithTitle(title.TrimTo(MaxEmbedTitleLength))
            .WithDescription(description.TrimTo(MaxEmbedDescriptionLength))
            .WithColor(color)
            .WithCurrentTimestamp();
    }

    /// <summary>
    ///     Adds a field to a log embed, skipping empty values and respecting the field count and length limits.
    /// </summary>
    /// <param name="eb">The embed to add the field to.</param>
    /// <param name="name">The field name.</param>
    /// <param name="value">The field value.</param>
    private static void AddSection(EmbedBuilder eb, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || eb.Fields.Count >= MaxEmbedFields)
            return;

        eb.AddField(name.TrimTo(MaxEmbedFieldNameLength), value.TrimTo(MaxEmbedFieldValueLength));
    }

    /// <summary>
    ///     Drops trailing fields and then trims the description until the embed fits the total size limit.
    /// </summary>
    /// <param name="eb">The embed to shrink.</param>
    private static void FitEmbed(EmbedBuilder eb)
    {
        while (eb.Length > MaxEmbedLength && eb.Fields.Count > 0)
            eb.Fields.RemoveAt(eb.Fields.Count - 1);

        var excess = eb.Length - MaxEmbedLength;
        if (excess > 0 && eb.Description is { Length: > 0 } description)
            eb.WithDescription(description.TrimTo(Math.Max(0, description.Length - excess)));
    }

    /// <summary>
    ///     Sends a log embed to a channel without pinging anyone, optionally uploading files with it.
    /// </summary>
    /// <param name="channel">The log channel.</param>
    /// <param name="eb">The embed to send.</param>
    /// <param name="files">Files to upload alongside the embed.</param>
    private async Task SendLogAsync(Channel channel, EmbedBuilder eb, List<AttachmentRequest>? files = null)
    {
        FitEmbed(eb);

        if (files is { Count: > 0 })
            await channel.SendAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None, attachments: files);
        else
            await channel.SendAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);

        collector.Feature("logging", channel.Guild.Id);
    }

    /// <inheritdoc />
    public async Task OnReadyAsync()
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        try
        {
            var logSettingsList = await db.LoggingV2.ToListAsync().ConfigureAwait(false);
            var dict = logSettingsList.ToDictionary(g => g.GuildId, g => g);
            GuildLogSettings = new ConcurrentDictionary<ulong, LoggingV2>(dict);

            // Load ignored channels
            var allIgnoredChannels = await db.GetTable<LogIgnoredChannel>()
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var group in allIgnoredChannels.GroupBy(ic => ic.GuildId))
            {
                ignoredChannelsCache[group.Key] = group.Select(ic => ic.ChannelId).ToHashSet();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load LoggingV2 settings on ready.");
            GuildLogSettings = new ConcurrentDictionary<ulong, LoggingV2>();
        }

        // yeaaaa would be a good idea to add events AFTER we get all guild settings.
        handler.RoleUpdated.Subscribe("LogCommandService", OnRoleUpdated);
        handler.RoleCreated.Subscribe("LogCommandService", OnRoleCreated);
        handler.RoleDeleted.Subscribe("LogCommandService", OnRoleDeleted);
        handler.GuildUpdated.Subscribe("LogCommandService", OnGuildUpdated);
        handler.ThreadCreated.Subscribe("LogCommandService", OnThreadCreated);
        handler.GuildMemberUpdated.Subscribe("LogCommandService", OnUserRoleAdded);
        handler.GuildMemberUpdated.Subscribe("LogCommandService", OnUserRoleRemoved);
        handler.UserUpdated.Subscribe("LogCommandService", OnUsernameUpdated);
        handler.UserUpdated.Subscribe("LogCommandService", OnUserUpdated);
        handler.GuildMemberUpdated.Subscribe("LogCommandService", OnNicknameUpdated);
        handler.ThreadDeleted.Subscribe("LogCommandService", OnThreadDeleted);
        handler.ThreadUpdated.Subscribe("LogCommandService", OnThreadUpdated);
        handler.MessageUpdated.Subscribe("LogCommandService", OnMessageUpdated);
        handler.MessageDeleted.Subscribe("LogCommandService", OnMessageDeleted);
        handler.UserJoined.Subscribe("LogCommandService", OnUserJoined);
        handler.UserLeft.Subscribe("LogCommandService", OnUserLeft);
        handler.ChannelCreated.Subscribe("LogCommandService", OnChannelCreated);
        handler.ChannelDestroyed.Subscribe("LogCommandService", OnChannelDestroyed);
        handler.ChannelUpdated.Subscribe("LogCommandService", OnChannelUpdated);
        handler.UserVoiceStateUpdated.Subscribe("LogCommandService", OnVoicePresence);
        handler.UserVoiceStateUpdated.Subscribe("LogCommandService", OnVoicePresenceTts);
        handler.AuditLogCreated.Subscribe("LogCommandService", OnAuditLogCreated);
        handler.InviteCreated.Subscribe("LogCommandService", OnInviteCreated);
        handler.InviteDeleted.Subscribe("LogCommandService", OnInviteDeleted);
        handler.MessagesBulkDeleted.Subscribe("LogCommandService", OnMessagesBulkDeleted);
        handler.ReactionAdded.Subscribe("LogCommandService", OnReactionAdded);
        handler.ReactionRemoved.Subscribe("LogCommandService", OnReactionRemoved);
        handler.GuildMemberUpdated.Subscribe("LogCommandService", OnUserTimeoutChanged);
        handler.MessageReceived.Subscribe("LogCommandService", OnInviteLinkPosted);
        handler.EmojiCreated.Subscribe("LogCommandService", OnEmojiCreated);
        handler.EmojiUpdated.Subscribe("LogCommandService", OnEmojiUpdated);
        handler.EmojiDeleted.Subscribe("LogCommandService", OnEmojiDeleted);
        handler.StickerCreated.Subscribe("LogCommandService", OnStickerCreated);
        handler.StickerUpdated.Subscribe("LogCommandService", OnStickerUpdated);
        handler.StickerDeleted.Subscribe("LogCommandService", OnStickerDeleted);
        handler.WebhooksUpdated.Subscribe("LogCommandService", OnWebhooksUpdated);
        handler.ChannelUpdated.Subscribe("LogCommandService", OnChannelPermissionsChanged);
        muteService.UserMuted += OnUserMuted;
        muteService.UserUnmuted += OnUserUnmuted;

        // Initialize reaction batching timer
        reactionBatchTimer = new Timer(ProcessReactionBatches, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    ///     Handles the creation of audit logs.
    /// </summary>
    /// <param name="args">The audit log entry.</param>
    /// <param name="arsg2">The guild where the audit log was created.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task OnAuditLogCreated(SocketAuditLog args, SocketGuild arsg2)
    {
        switch (args.Action)
        {
            case ActionType.Ban:
            {
                if (args.TargetId is { } targetId && await client.GetUserAsync(targetId) is { } target)
                    await OnUserBanned(target, arsg2, args.User, args.Reason);
                break;
            }
            case ActionType.Unban:
            {
                if (args.TargetId is { } targetId && await client.GetUserAsync(targetId) is { } target)
                    await OnUserUnbanned(target, arsg2, args.User, args.Reason);
                break;
            }
        }
    }

    /// <summary>
    ///     Handles the creation of a role.
    /// </summary>
    /// <param name="args">The role that was created.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task OnRoleCreated(SocketRole args)
    {
        logger.LogDebug("LogCommandService.OnRoleCreated called for role {RoleName} in guild {GuildId}", args.Name,
            args.Guild.Id);
        if (GuildLogSettings.TryGetValue(args.Guild.Id, out var logSetting))
        {
            if (logSetting.RoleCreatedId is null or 0)
                return;

            var channel = args.Guild.GetTextChannel(logSetting.RoleCreatedId.Value);
            if (channel is null)
                return;

            await Task.Delay(500);
            var auditLogs = await GetAuditLogsSafeAsync(args.Guild, ActionType.RoleCreated);
            var auditLog = auditLogs.FirstOrDefault();
            if (auditLog == null) return;

            var eb = LogEmbed(strings.RoleCreated(args.Guild.Id),
                strings.NameField(args.Guild.Id, args.Name) +
                strings.IdField(args.Guild.Id, args.Id) +
                strings.ColorField(args.Guild.Id, args.Colour) +
                strings.HoistedField(args.Guild.Id, args.IsHoisted) +
                strings.MentionableField(args.Guild.Id, args.IsMentionable) +
                strings.PositionField(args.Guild.Id, args.Position) +
                strings.PermissionsField(args.Guild.Id, string.Join(", ", args.Permissions.ToList())) +
                strings.CreatedByField(args.Guild.Id, auditLog.User.Mention, auditLog.User.Id),
                Mewdeko.OkColor);

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     An audit log entry reduced to who did what to which target.
    /// </summary>
    /// <param name="Action">The kind of action.</param>
    /// <param name="User">The user who performed the action.</param>
    /// <param name="TargetId">The ID of the affected entity, when there is one.</param>
    /// <param name="Reason">The reason given for the action.</param>
    /// <param name="CreatedAt">When the action happened.</param>
    private sealed record AuditEntry(ActionType Action, User User, ulong? TargetId, string? Reason,
        DateTimeOffset CreatedAt);

    /// <summary>
    ///     Fetches audit log entries without throwing when the bot lacks the View Audit Log permission. Guilds where the
    ///     permission is missing simply return no entries, so logging falls back to whatever it can report on its own.
    /// </summary>
    /// <param name="guild">The guild to fetch audit logs for.</param>
    /// <param name="actionType">The action type to filter by.</param>
    /// <param name="limit">The maximum number of entries to fetch.</param>
    /// <returns>The matching audit log entries, or an empty collection if they are unavailable.</returns>
    private async Task<IReadOnlyCollection<AuditEntry>> GetAuditLogsSafeAsync(SocketGuild? guild, ActionType actionType,
        int limit = 1)
    {
        if (guild is null)
            return [];

        var botUser = client.GetGuild(guild.Id)?.CurrentUser;
        if (botUser is null || !botUser.GuildPermissions.ViewAuditLog)
            return [];

        try
        {
            var list = await guild.GetAuditLogsAsync(new GuildAuditLogListRequest
            {
                Limit = limit, ActionType = actionType
            }).ConfigureAwait(false);

            var entries = new List<AuditEntry>();
            foreach (var entry in list.Entries)
            {
                if (entry.UserId is not { } userId)
                    continue;

                var user = client.GetUser(userId);
                if (user is null && list.Users.FirstOrDefault(x => x.Id == userId) is { } userJson)
                    user = User.Create(client, userJson);

                if (user is not null)
                    entries.Add(new AuditEntry(entry.Action, user, entry.TargetId, entry.Reason,
                        SnowflakeUtils.FromSnowflake(entry.Id)));
            }

            return entries;
        }
        catch (FluxerApiException ex)
        {
            logger.LogDebug(ex, "Could not fetch {ActionType} audit logs for guild {GuildId}", actionType, guild.Id);
            return [];
        }
    }

    /// <summary>
    ///     Handles the event when a guild is updated.
    /// </summary>
    /// <param name="args">The original guild before the update.</param>
    /// <param name="arsg2">The updated guild.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task OnGuildUpdated(SocketGuild args, SocketGuild arsg2)
    {
        if (GuildLogSettings.TryGetValue(args.Id, out var logSetting))
        {
            if (logSetting.ServerUpdatedId is null or 0)
                return;

            var channel = args.GetTextChannel(logSetting.ServerUpdatedId.Value);
            if (channel is null)
                return;

            await Task.Delay(500);
            var auditLogs = await GetAuditLogsSafeAsync(args, ActionType.GuildUpdated);
            var auditLog = auditLogs.FirstOrDefault();

            var updatedByStr = auditLog is null
                ? "`Updated By:` Unknown"
                : $"`Updated By:` {auditLog.User.Mention} | {auditLog.User.Id}";
            var guildId = args.Id;
            string title;
            string description;
            string? beforeImage = null;
            string? afterImage = null;

            if (args.Name != arsg2.Name)
            {
                title = strings.ServerNameUpdated(guildId);
                description = strings.ServerNameChangeDescription(guildId, arsg2.Name, args.Name, updatedByStr);
            }
            else if (args.IconUrl != arsg2.IconUrl)
            {
                title = strings.ServerIconUpdated(guildId);
                description = updatedByStr;
                beforeImage = args.IconUrl;
                afterImage = arsg2.IconUrl;
            }
            else if (args.BannerUrl != arsg2.BannerUrl)
            {
                title = strings.ServerBannerUpdated(guildId);
                description = updatedByStr;
                beforeImage = args.BannerUrl;
                afterImage = arsg2.BannerUrl;
            }
            else if (args.VanityUrlCode != arsg2.VanityUrlCode)
            {
                title = strings.ServerVanityUrlUpdated(guildId);
                description = strings.ServerVanityUrlChange(guildId, arsg2.VanityUrlCode ?? strings.None(guildId),
                    args.VanityUrlCode ?? strings.None(guildId), updatedByStr);
            }
            else if (args.OwnerId != arsg2.OwnerId)
            {
                title = strings.ServerOwnerUpdated(guildId);
                description = strings.ServerOwnerChange(guildId, MentionUtils.MentionUser(arsg2.OwnerId),
                    arsg2.OwnerId, MentionUtils.MentionUser(args.OwnerId), args.OwnerId, updatedByStr);
            }
            else if (args.AFKChannel?.Id != arsg2.AFKChannel?.Id)
            {
                title = strings.ServerAfkChannelUpdated(guildId);
                description = strings.ServerAfkChannelChange(guildId,
                    arsg2.AFKChannel?.Mention ?? strings.None(guildId), arsg2.AFKChannel?.Id.ToString() ?? "N/A",
                    args.AFKChannel?.Mention ?? strings.None(guildId), args.AFKChannel?.Id.ToString() ?? "N/A",
                    updatedByStr);
            }
            else if (args.AfkTimeout != arsg2.AfkTimeout)
            {
                title = strings.ServerAfkTimeoutUpdated(guildId);
                description = strings.ServerAfkTimeoutChange(guildId, arsg2.AfkTimeout, args.AfkTimeout, updatedByStr);
            }
            else if (args.DefaultMessageNotifications != arsg2.DefaultMessageNotifications)
            {
                title = strings.ServerDefaultNotificationsUpdated(guildId);
                description = strings.ServerDefaultNotificationsChange(guildId, arsg2.DefaultMessageNotifications,
                    args.DefaultMessageNotifications, updatedByStr);
            }
            else if (args.ExplicitContentFilter != arsg2.ExplicitContentFilter)
            {
                title = strings.ServerExplicitContentFilterUpdated(guildId);
                description = strings.ServerExplicitContentFilterChange(guildId, arsg2.ExplicitContentFilter,
                    args.ExplicitContentFilter, updatedByStr);
            }
            else if (args.MfaLevel != arsg2.MfaLevel)
            {
                title = strings.ServerMfaLevelUpdated(guildId);
                description = strings.ServerMfaLevelChange(guildId, arsg2.MfaLevel, args.MfaLevel, updatedByStr);
            }
            else if (args.VerificationLevel != arsg2.VerificationLevel)
            {
                title = strings.ServerVerificationLevelUpdated(guildId);
                description = strings.ServerVerificationLevelChange(guildId, arsg2.VerificationLevel,
                    args.VerificationLevel, updatedByStr);
            }
            else if (args.SystemChannel?.Id != arsg2.SystemChannel?.Id)
            {
                title = strings.ServerSystemChannelUpdated(guildId);
                description = strings.ServerSystemChannelChange(guildId,
                    arsg2.SystemChannel?.Mention ?? strings.None(guildId),
                    arsg2.SystemChannel?.Id.ToString() ?? "N/A",
                    args.SystemChannel?.Mention ?? strings.None(guildId),
                    args.SystemChannel?.Id.ToString() ?? "N/A", updatedByStr);
            }
            else if (args.RulesChannel?.Id != arsg2.RulesChannel?.Id)
            {
                title = strings.ServerRulesChannelUpdated(guildId);
                description = strings.ServerRulesChannelChange(guildId,
                    arsg2.RulesChannel?.Mention ?? strings.None(guildId),
                    arsg2.RulesChannel?.Id.ToString() ?? "N/A",
                    args.RulesChannel?.Mention ?? strings.None(guildId),
                    args.RulesChannel?.Id.ToString() ?? "N/A", updatedByStr);
            }
            else
                return;

            var eb = LogEmbed(title, description, Mewdeko.OkColor);
            if (!string.IsNullOrEmpty(beforeImage))
            {
                eb.WithThumbnailUrl(beforeImage);
                AddSection(eb, strings.LogSectionBefore(guildId), beforeImage);
            }

            if (!string.IsNullOrEmpty(afterImage))
            {
                eb.WithImageUrl(afterImage);
                AddSection(eb, strings.LogSectionAfter(guildId), afterImage);
            }

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a role is deleted in a guild.
    /// </summary>
    /// <param name="args">The role that was deleted.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task OnRoleDeleted(SocketRole args)
    {
        if (GuildLogSettings.TryGetValue(args.Guild.Id, out var logSetting))
        {
            if (logSetting.RoleDeletedId is null or 0)
                return;

            var channel = args.Guild.GetTextChannel(logSetting.RoleDeletedId.Value);
            if (channel is null)
                return;

            await Task.Delay(500);
            var auditLogs = await GetAuditLogsSafeAsync(args.Guild, ActionType.RoleDeleted);
            var auditLog = auditLogs.FirstOrDefault();
            if (auditLog == null) return;

            var eb = LogEmbed(strings.RoleDeleted(args.Guild.Id),
                strings.RoleField(args.Guild.Id, args.Name) +
                strings.IdField(args.Guild.Id, args.Id) +
                strings.DeletedByField(args.Guild.Id, auditLog.User.Mention, auditLog.User.Id) +
                strings.DeletedAtField(args.Guild.Id, DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss")),
                Mewdeko.OkColor);

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a role is updated in a guild.
    /// </summary>
    /// <param name="args">The original role before the update.</param>
    /// <param name="arsg2">The updated role.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task OnRoleUpdated(SocketRole args, SocketRole arsg2)
    {
        if (GuildLogSettings.TryGetValue(args.Guild.Id, out var logSetting))
        {
            if (logSetting.RoleUpdatedId is null or 0)
                return;

            var channel = args.Guild.GetTextChannel(logSetting.RoleUpdatedId.Value);
            if (channel is null)
                return;

            await Task.Delay(500);
            var auditLogs = await GetAuditLogsSafeAsync(args.Guild, ActionType.RoleUpdated);
            var auditLog = auditLogs.FirstOrDefault();
            if (auditLog == null) return;

            var updatedByStr = $"`Updated By:` {auditLog.User.Mention} | {auditLog.User.Id}";
            var roleStr = $"`Role:` {args.Mention} | {args.Id}";
            var guildId = args.Guild.Id;
            var color = Mewdeko.OkColor;
            string title;
            string description;

            if (args.Name != arsg2.Name)
            {
                title = strings.RoleNameUpdated(guildId);
                description = strings.RoleNameChange(guildId, arsg2.Name, args.Name, updatedByStr);
            }
            else if (args.Color != arsg2.Color)
            {
                title = strings.RoleColorUpdated(guildId);
                description = strings.RoleColorChange(guildId, roleStr, arsg2.Colour, args.Colour, updatedByStr);
                color = arsg2.Colour;
            }
            else if (args.IsHoisted != arsg2.IsHoisted)
            {
                title = strings.RoleHoistedUpdated(guildId);
                description = strings.RoleHoistedChange(guildId, roleStr, arsg2.IsHoisted, args.IsHoisted,
                    updatedByStr);
            }
            else if (args.IsMentionable != arsg2.IsMentionable)
            {
                title = strings.RoleMentionableUpdated(guildId);
                description = strings.RoleMentionableChange(guildId, roleStr, arsg2.IsMentionable,
                    args.IsMentionable, updatedByStr);
            }
            else if (args.Position != arsg2.Position)
            {
                title = strings.RolePositionUpdated(guildId);
                description = strings.RolePositionChange(guildId, roleStr, arsg2.Position, args.Position,
                    updatedByStr);
            }
            else if (!arsg2.Permissions.Equals(args.Permissions))
            {
                title = strings.RolePermissionsUpdated(guildId);
                description = strings.RolePermissionsChange(guildId, roleStr, arsg2.Permissions, args.Permissions,
                    updatedByStr);
            }
            else if (args.UnicodeEmoji != arsg2.UnicodeEmoji)
            {
                title = strings.RoleIconUpdated(guildId);
                description = strings.RoleIconChange(guildId, roleStr, updatedByStr);
            }
            else
                return;

            await SendLogAsync(channel, LogEmbed(title, description, color));
        }
    }

    /// <summary>
    ///     Handles logging for when a thread is created.
    /// </summary>
    /// <param name="socketThreadChannel">The created thread channel.</param>
    private async Task OnThreadCreated(ThreadChannel socketThreadChannel)
    {
        // Check if thread or its parent channel is ignored
        if (IsChannelIgnored(socketThreadChannel.Guild.Id, socketThreadChannel.Id) ||
            (socketThreadChannel.ParentChannel != null &&
             IsChannelIgnored(socketThreadChannel.Guild.Id, socketThreadChannel.ParentChannel.Id)))
            return;

        if (GuildLogSettings.TryGetValue(socketThreadChannel.Guild.Id, out var logSetting))
        {
            if (logSetting.ThreadCreatedId is null or 0)
                return;

            var channel = socketThreadChannel.Guild.GetTextChannel(logSetting.ThreadCreatedId.Value);
            if (channel is null)
                return;

            var eb = LogEmbed(strings.ThreadCreated(socketThreadChannel.Guild.Id),
                strings.ThreadName(socketThreadChannel.Guild.Id, socketThreadChannel.Name) +
                $"`Created By:` {(socketThreadChannel.OwnerId is { } ownerId ? MentionUtils.MentionUser(ownerId) : "N/A")} | {socketThreadChannel.OwnerId?.ToString() ?? "N/A"}\n" +
                $"`Created At:` {socketThreadChannel.CreatedAt:dd/MM/yyyy HH:mm:ss}\n" +
                $"`Thread Type:` {socketThreadChannel.Type}\n" +
                $"`Thread Tags:` {string.Join(", ", socketThreadChannel.AppliedTags ?? [])}", Mewdeko.OkColor);

            await SendLogAsync(channel, eb);
            collector.Feature("logging", channel.Guild.Id);
        }
    }

    /// <summary>
    ///     Handles the event when a user has a role added to them.
    /// </summary>
    /// <param name="before">The member before the event fired</param>
    /// <param name="arsg2">The member after the event was fired</param>
    private async Task OnUserRoleAdded(MemberSnapshot? before, SocketGuildMember arsg2)
    {
        if (!GuildLogSettings.TryGetValue(arsg2.Guild.Id, out var logSetting) || before is null)
            return;

        if (logSetting.UserRoleAddedId is null or 0)
            return;

        var addedRoles = arsg2.RoleIds.Except(before.RoleIds).ToList();
        if (!addedRoles.Any()) return;

        var channel = arsg2.Guild.GetTextChannel(logSetting.UserRoleAddedId.Value);
        if (channel is null) return;

        await Task.Delay(500);
        var auditLogs = await GetAuditLogsSafeAsync(arsg2.Guild, ActionType.MemberRoleUpdated);
        var auditLog = auditLogs.LastOrDefault();
        if (auditLog?.User == null) return;

        var eb = LogEmbed(strings.UserRolesAdded(arsg2.Guild.Id),
            strings.RolesField(arsg2.Guild.Id,
                string.Join(strings.CommaSeparator(arsg2.Guild.Id), addedRoles.Select(MentionUtils.MentionRole))) +
            strings.AddedByField(arsg2.Guild.Id, auditLog.User.Mention, auditLog.User.Id) +
            strings.AddedToField(arsg2.Guild.Id, arsg2.Mention, arsg2.Id), Mewdeko.OkColor);

        await SendLogAsync(channel, eb);
    }


    /// <summary>
    ///     Handles the event where a user has a role removed.
    /// </summary>
    /// <param name="before">The member before the removal</param>
    /// <param name="arsg2">The member after the removal</param>
    private async Task OnUserRoleRemoved(MemberSnapshot? before, SocketGuildMember arsg2)
    {
        if (!GuildLogSettings.TryGetValue(arsg2.Guild.Id, out var logSetting) || before is null)
            return;

        if (logSetting.UserRoleRemovedId is null or 0)
            return;

        var removedRoles = before.RoleIds.Except(arsg2.RoleIds).ToList();
        if (!removedRoles.Any()) return;

        var channel = arsg2.Guild.GetTextChannel(logSetting.UserRoleRemovedId.Value);
        if (channel is null) return;

        await Task.Delay(500);
        var auditLogs = await GetAuditLogsSafeAsync(arsg2.Guild, ActionType.MemberRoleUpdated);
        var auditLog = auditLogs.LastOrDefault();
        if (auditLog?.User == null) return;

        var eb = LogEmbed(strings.UserRolesRemoved(arsg2.Guild.Id),
            strings.RolesField(arsg2.Guild.Id,
                string.Join(strings.CommaSeparator(arsg2.Guild.Id), removedRoles.Select(MentionUtils.MentionRole))) +
            strings.RemovedByField(arsg2.Guild.Id, auditLog.User.Mention, auditLog.User.Id) +
            strings.RemovedFromField(arsg2.Guild.Id, arsg2.Mention, arsg2.Id), Mewdeko.OkColor);

        await SendLogAsync(channel, eb);
    }


    /// <summary>
    ///     Handles the event when a user updates their username.
    /// </summary>
    /// <param name="args">The user before they updated their username.</param>
    /// <param name="user">The member after they updated their username.</param>
    private async Task OnUsernameUpdated(UserSnapshot args, SocketGuildMember user)
    {
        if (string.Equals(args.Username, user.Username))
            return;

        var guild = user.Guild;
        if (!GuildLogSettings.TryGetValue(guild.Id, out var logSetting))
            return;

        if (logSetting.UsernameUpdatedId is null or 0)
            return;

        var channel = guild.GetTextChannel(logSetting.UsernameUpdatedId.Value);
        if (channel is null)
            return;

        var eb = LogEmbed(strings.UsernameUpdated(guild.Id),
            strings.UserField(guild.Id, user.Mention, user.Id) +
            strings.OldUsernameField(guild.Id, args.Username) +
            strings.NewUsernameField(guild.Id, user.Username), Mewdeko.OkColor);

        await SendLogAsync(channel, eb);
    }

    /// <summary>
    ///     Handles the event when a user updates their nickname.
    /// </summary>
    /// <param name="before">The member before they updated their nickname</param>
    /// <param name="arsg2">The member after they updated their nickname</param>
    private async Task OnNicknameUpdated(MemberSnapshot? before, SocketGuildMember arsg2)
    {
        if (before is null || before.Nickname == arsg2.Nickname) return;

        if (GuildLogSettings.TryGetValue(arsg2.Guild.Id, out var logSetting))
        {
            if (logSetting.NicknameUpdatedId is null or 0)
                return;

            var channel = arsg2.Guild.GetTextChannel(logSetting.NicknameUpdatedId.Value);
            if (channel is null)
                return;

            await Task.Delay(500);
            var auditLogs = await GetAuditLogsSafeAsync(arsg2.Guild, ActionType.MemberUpdated);
            var entry = auditLogs.FirstOrDefault();
            if (entry == null) return; // Cannot determine who changed it without audit log

            var eb = LogEmbed(strings.NicknameUpdated(arsg2.Guild.Id),
                strings.UserField(arsg2.Guild.Id, arsg2.Mention, arsg2.Id) +
                strings.OldNicknameField(arsg2.Guild.Id, before.Nickname ?? before.User.Username) +
                strings.NewNicknameField(arsg2.Guild.Id, arsg2.Nickname ?? arsg2.Username) +
                strings.UpdatedByField(arsg2.Guild.Id, entry.User.Mention, entry.User.Id), Mewdeko.OkColor);

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a thread gets deleted.
    /// </summary>
    /// <param name="args">The cached thread. May return null.</param>
    private async Task OnThreadDeleted(Cacheable<ThreadChannel> args)
    {
        if (!args.HasValue) return;
        var deletedThread = args.Value;

        // Check if thread or its parent channel is ignored
        if (IsChannelIgnored(deletedThread.Guild.Id, deletedThread.Id) ||
            (deletedThread.ParentChannel != null &&
             IsChannelIgnored(deletedThread.Guild.Id, deletedThread.ParentChannel.Id)))
            return;

        if (GuildLogSettings.TryGetValue(deletedThread.Guild.Id, out var logSetting))
        {
            if (logSetting.ThreadDeletedId is null or 0)
                return;

            var channel = deletedThread.Guild.GetTextChannel(logSetting.ThreadDeletedId.Value);
            if (channel is null)
                return;

            var guildId = deletedThread.Guild.Id;
            var eb = LogEmbed(strings.ThreadDeleted(guildId),
                strings.ThreadNameField(guildId, deletedThread.Name) +
                strings.ThreadIdField(guildId, deletedThread.Id), Mewdeko.OkColor);

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a thread is updated.
    /// </summary>
    /// <param name="cacheable">The cached thread. May return null.</param>
    /// <param name="arsg2">The updated thread.</param>
    private async Task OnThreadUpdated(Cacheable<ThreadChannel> cacheable, ThreadChannel arsg2)
    {
        if (!cacheable.HasValue) return;
        var oldThread = cacheable.Value;

        // Check if thread or its parent channel is ignored
        if (IsChannelIgnored(arsg2.Guild.Id, arsg2.Id) ||
            (arsg2.ParentChannel != null && IsChannelIgnored(arsg2.Guild.Id, arsg2.ParentChannel.Id)))
            return;

        if (GuildLogSettings.TryGetValue(arsg2.Guild.Id, out var logSetting))
        {
            if (logSetting.ThreadUpdatedId is null or 0)
                return;

            var channel = arsg2.Guild.GetTextChannel(logSetting.ThreadUpdatedId.Value);
            if (channel is null)
                return;

            var updatedByStr = string.Empty;
            var threadIdStr = $"`Thread:` {arsg2.Mention} | {arsg2.Id}";
            var guildId = arsg2.Guild.Id;
            string title;
            string description;

            if (oldThread.Name != arsg2.Name)
            {
                title = strings.ThreadNameUpdated(guildId);
                description = strings.ThreadNameChange(guildId, threadIdStr, oldThread.Name, arsg2.Name, updatedByStr);
            }
            else if (oldThread.ThreadMetadata?.IsArchived != arsg2.ThreadMetadata?.IsArchived)
            {
                title = strings.ThreadArchiveStatusUpdated(guildId);
                description = strings.ThreadArchiveStatusChange(guildId, threadIdStr,
                    oldThread.ThreadMetadata?.IsArchived ?? false, arsg2.ThreadMetadata?.IsArchived ?? false,
                    updatedByStr);
            }
            else if (oldThread.ThreadMetadata?.IsLocked != arsg2.ThreadMetadata?.IsLocked)
            {
                title = strings.ThreadLockStatusUpdated(guildId);
                description = strings.ThreadLockStatusChange(guildId, threadIdStr,
                    oldThread.ThreadMetadata?.IsLocked ?? false, arsg2.ThreadMetadata?.IsLocked ?? false,
                    updatedByStr);
            }
            else if (oldThread.SlowModeInterval != arsg2.SlowModeInterval)
            {
                title = strings.ThreadSlowModeUpdated(guildId);
                description = strings.ThreadSlowModeChange(guildId, threadIdStr, oldThread.SlowModeInterval,
                    arsg2.SlowModeInterval, updatedByStr);
            }
            else if (oldThread.ThreadMetadata?.AutoArchiveDuration != arsg2.ThreadMetadata?.AutoArchiveDuration)
            {
                title = strings.ThreadAutoArchiveUpdated(guildId);
                description = strings.ThreadAutoArchiveChange(guildId, threadIdStr,
                    oldThread.ThreadMetadata?.AutoArchiveDuration, arsg2.ThreadMetadata?.AutoArchiveDuration,
                    updatedByStr);
            }
            else
                return;

            await SendLogAsync(channel, LogEmbed(title, description, Mewdeko.OkColor));
        }
    }

    /// <summary>
    ///     Handles the event when a message is updated.
    /// </summary>
    /// <param name="oldMessage">The cached message from before the edit. Null when it was not cached.</param>
    /// <param name="args2">The new message</param>
    private async Task OnMessageUpdated(Message? oldMessage, SocketMessage args2)
    {
        if (oldMessage is null || args2.Author is null) return;
        if (args2.Channel is not { } guildChannel) return;
        if (IgnoresBot(guildChannel.Guild.Id, args2.Author.IsBot)) return;
        if (oldMessage.Content == args2.Content) return; // Compare content directly

        // Check if channel is ignored
        if (IsChannelIgnored(guildChannel.Guild.Id, guildChannel.Id))
            return;

        if (GuildLogSettings.TryGetValue(guildChannel.Guild.Id, out var logSetting))
        {
            if (logSetting.MessageUpdatedId is null or 0)
                return;

            if (StartsWithIgnoredPrefix(logSetting, oldMessage.Content) ||
                StartsWithIgnoredPrefix(logSetting, args2.Content))
                return;

            var channel = guildChannel.Guild.GetTextChannel(logSetting.MessageUpdatedId.Value);
            if (channel is null)
                return;

            var guildId = guildChannel.Guild.Id;
            var eb = LogEmbed(strings.MessageUpdated(guildId),
                    strings.MessageAuthorField(guildId, oldMessage.Author.Mention, oldMessage.Author.Id) +
                    strings.MessageChannelField(guildId, guildChannel.Mention, guildChannel.Id) +
                    strings.MessageIdField(guildId, oldMessage.Id) +
                    strings.LogJumpToMessage(guildId, oldMessage.GetJumpUrl()), Mewdeko.OkColor)
                .WithThumbnailUrl(oldMessage.Author.RealAvatarUrl().ToString());

            AddSection(eb, strings.LogSectionBefore(guildId), oldMessage.Content.TrimTo(1000));
            AddSection(eb, strings.LogSectionAfter(guildId), args2.Content.TrimTo(1000));

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a message is deleted.
    /// </summary>
    /// <param name="args">The cached message. May return null.</param>
    /// <param name="guildChannel">The channel where the message was deleted</param>
    private async Task OnMessageDeleted(Cacheable<Message> args, Channel guildChannel)
    {
        // Get message from cache if available
        var message = args.HasValue ? args.Value : null;
        // If message not in cache, we cannot proceed as we don't have content/author
        if (message == null) return;
        if (guildChannel.Guild is null) return; // Ensure it's a guild channel
        if (IgnoresBot(guildChannel.Guild.Id, message.Author.IsBot)) return;

        // Check if channel is ignored
        if (IsChannelIgnored(guildChannel.Guild.Id, guildChannel.Id))
            return;

        if (GuildLogSettings.TryGetValue(guildChannel.Guild.Id, out var logSetting))
        {
            if (logSetting.MessageDeletedId is null or 0)
                return;

            if (StartsWithIgnoredPrefix(logSetting, message.Content))
                return;

            var logChannel = guildChannel.Guild.GetTextChannel(logSetting.MessageDeletedId.Value);
            if (logChannel is null)
                return;

            await Task.Delay(1000); // Delay slightly to increase chance of audit log availability

            var auditLogs = await GetAuditLogsSafeAsync(guildChannel.Guild, ActionType.MessageDeleted, 5);

            // Try to find who deleted the message
            var deleteUser = message.Author; // Assume self-delete initially
            var entry = auditLogs.FirstOrDefault(e => e.TargetId == message.Author.Id);

            if (entry != null &&
                (DateTimeOffset.UtcNow - entry.CreatedAt).TotalSeconds < 10) // Check if recent audit log matches
            {
                deleteUser = entry.User;
            }


            var guildId = guildChannel.Guild.Id;
            var tempFiles = new List<AttachmentRequest>();
            var tempStreams = new List<MemoryStream>();

            var eb = LogEmbed(strings.MessageDeleted(guildId),
                    strings.MessageAuthorField(guildId, message.Author.Mention, message.Author.Id) +
                    strings.MessageChannelField(guildId, guildChannel.Mention, guildChannel.Id) +
                    strings.DeletedByField(guildId, deleteUser?.Mention ?? strings.Unknown(guildId),
                        deleteUser?.Id.ToString() ?? "N/A"), Mewdeko.OkColor)
                .WithThumbnailUrl(message.Author.RealAvatarUrl().ToString());

            if (!string.IsNullOrWhiteSpace(message.Content))
                AddSection(eb, strings.LogSectionMessageContent(guildId), message.Content.TrimTo(1000));

            if (message.Attachments is { Length: > 0 } messageAttachments)
            {
                var attachmentLines = new List<string>();

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                foreach (var att in messageAttachments.Take(10))
                {
                    var preserved = false;
                    try
                    {
                        var response = await httpClient.GetAsync(att.Url);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsByteArrayAsync();
                            var contentStream = new MemoryStream(content);
                            tempStreams.Add(contentStream);
                            tempFiles.Add(new AttachmentRequest(contentStream, att.Filename));
                            preserved = true;

                            if (string.IsNullOrEmpty(eb.ImageUrl) && IsImageAttachment(att.Filename))
                                eb.WithImageUrl($"attachment://{Uri.EscapeDataString(att.Filename)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not download attachment {Filename} for the deleted message log",
                            att.Filename);
                    }

                    var sizeText = strings.AttachmentSize(guildId, att.Size);
                    attachmentLines.Add(preserved
                        ? strings.LogAttachmentPreserved(guildId, att.Filename, sizeText)
                        : strings.LogAttachmentNotPreserved(guildId, att.Filename, sizeText));
                }

                AddSection(eb, $"{strings.Attachments(guildId)} ({messageAttachments.Length})",
                    string.Join("\n", attachmentLines));
            }

            try
            {
                await SendLogAsync(logChannel, eb, tempFiles);
            }
            finally
            {
                foreach (var tempStream in tempStreams)
                {
                    await tempStream.DisposeAsync();
                }
            }
        }
    }

    private static bool IsImageAttachment(string filename)
    {
        var imageExtensions = new[]
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
        }; // Added webp
        return imageExtensions.Any(ext => filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Handles the event when a user joins a guild.
    /// </summary>
    /// <param name="guildUser">The user that joined the guild.</param>
    private async Task OnUserJoined(SocketGuildMember guildUser)
    {
        logger.LogDebug("LogCommandService.OnUserJoined called for user {UserId} in guild {GuildId}", guildUser.Id,
            guildUser.Guild.Id);
        if (GuildLogSettings.TryGetValue(guildUser.Guild.Id, out var logSetting))
        {
            if (logSetting.UserJoinedId is null or 0)
                return;

            var channel = await guildUser.Guild.GetTextChannelAsync(logSetting.UserJoinedId.Value);
            if (channel is null)
                return;

            var guildId = guildUser.Guild.Id;
            var eb = LogEmbed(strings.UserJoined(guildId),
                    strings.UserField(guildId, guildUser.Mention, guildUser.Id) +
                    strings.UserGlobalNameField(guildId, guildUser.GlobalName ?? guildUser.Username), Mewdeko.OkColor)
                .WithThumbnailUrl(guildUser.RealAvatarUrl().ToString());

            AddSection(eb, strings.LogSectionAccountDetails(guildId),
                strings.AccountCreatedField(guildId, guildUser.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss")) + "\n" +
                strings.JoinedServerField(guildId, guildUser.JoinedAt.ToString("dd/MM/yyyy HH:mm:ss")) +
                "\n" + strings.UserStatusField(guildId, guildUser.Status));

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a user leaves a guild.
    /// </summary>
    /// <param name="guild">The guild the user left.</param>
    /// <param name="user">The user that left the guild.</param>
    /// <param name="member">The member as last seen before they left, if known.</param>
    private async Task OnUserLeft(SocketGuild guild, User user, MemberSnapshot? member)
    {
        if (GuildLogSettings.TryGetValue(guild.Id, out var logSetting))
        {
            if (logSetting.UserLeftId is null or 0)
                return;

            var channel = await guild.GetTextChannelAsync(logSetting.UserLeftId.Value);
            if (channel is null)
                return;

            // Check if it was a kick or ban via audit log
            var title = strings.UserLeft(guild.Id);
            string? footer = null;
            await Task.Delay(1000); // Delay for audit log
            var auditLogsKick = await GetAuditLogsSafeAsync(guild, ActionType.Kick);
            var kickLog = auditLogsKick.FirstOrDefault(e =>
                e.TargetId == user.Id && (DateTimeOffset.UtcNow - e.CreatedAt).TotalSeconds < 10);

            var auditLogsBan = await GetAuditLogsSafeAsync(guild, ActionType.Ban);
            var banLog = auditLogsBan.FirstOrDefault(e =>
                e.TargetId == user.Id && (DateTimeOffset.UtcNow - e.CreatedAt).TotalSeconds < 10);

            if (kickLog != null)
            {
                title = strings.LogUserKickedTitle(guild.Id);
                footer = strings.LogKickedByFooter(guild.Id, kickLog.User.Username, kickLog.User.Id);
            }
            else if (banLog != null)
            {
                // Ban event is handled by OnUserBanned, so we might not want to log here again.
                // However, if the user left *before* the ban event fired, this might catch it.
                // Let's just stick to "User Left" unless kicked.
            }

            var eb = LogEmbed(title,
                    strings.UserField(guild.Id, user.Mention, user.Id) +
                    strings.UserGlobalNameField(guild.Id, user.GlobalName ?? user.Username) + "\n" +
                    strings.AccountCreatedField(guild.Id, user.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss")),
                    Mewdeko.ErrorColor)
                .WithThumbnailUrl(user.RealAvatarUrl().ToString());

            if (footer != null)
                eb.WithFooter(footer);

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a user is banned from a guild.
    /// </summary>
    /// <param name="user">The user that was banned.</param>
    /// <param name="guild">The guild the user was banned from.</param>
    /// <param name="bannedBy">The user that banned the user.</param>
    /// <param name="reason">The reason for the ban.</param>
    private async Task OnUserBanned(User user, SocketGuild guild, User? bannedBy, string? reason = null)
    {
        if (GuildLogSettings.TryGetValue(guild.Id, out var logSetting))
        {
            if (logSetting.UserBannedId is null or 0)
                return;

            var channel = guild.GetTextChannel(logSetting.UserBannedId.Value);
            if (channel is null)
                return;

            var eb = LogEmbed(strings.UserBanned(guild.Id),
                    strings.UserInfoLine(guild.Id, user.Mention, user.Id) +
                    $"`User Global Name:` {user.GlobalName ?? user.Username}\n" +
                    $"`Account Created:` {user.CreatedAt:dd/MM/yyyy HH:mm:ss}\n" +
                    $"`Banned By:` {bannedBy?.Mention ?? "Unknown"} | {bannedBy?.Id.ToString() ?? "N/A"}\n" +
                    $"`Reason:` {reason ?? "No reason provided"}", Mewdeko.ErrorColor)
                .WithThumbnailUrl(user.RealAvatarUrl().ToString());

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a user is unbanned from a guild.
    /// </summary>
    /// <param name="user">The user that was unbanned.</param>
    /// <param name="guild">The guild the user was unbanned from.</param>
    /// <param name="unbannedBy">The user that unbanned the user.</param>
    /// <param name="reason">The reason for the unban, if provided.</param>
    private async Task OnUserUnbanned(User user, SocketGuild guild, User? unbannedBy, string? reason)
    {
        if (GuildLogSettings.TryGetValue(guild.Id, out var logSetting))
        {
            if (logSetting.UserUnbannedId is null or 0)
                return;

            var channel = guild.GetTextChannel(logSetting.UserUnbannedId.Value);
            if (channel is null)
                return;

            var eb = LogEmbed(strings.UserUnbanned(guild.Id),
                    strings.UserInfoLine(guild.Id, user.Mention, user.Id) +
                    $"`User Global Name:` {user.GlobalName ?? user.Username}\n" +
                    $"`Account Created:` {user.CreatedAt:dd/MM/yyyy HH:mm:ss}\n" +
                    $"`Unbanned By:` {unbannedBy?.Mention ?? "Unknown"} | {unbannedBy?.Id.ToString() ?? "N/A"}\n" +
                    $"`Reason:` {reason ?? "No reason provided"}", Mewdeko.OkColor)
                .WithThumbnailUrl(user.RealAvatarUrl().ToString());

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a user is updated.
    /// </summary>
    /// <param name="args">The user before the update.</param>
    /// <param name="userInGuild">The member after the update.</param>
    private async Task OnUserUpdated(UserSnapshot args, SocketGuildMember userInGuild)
    {
        if (IgnoresBot(userInGuild.Guild.Id, args.IsBot)) return;

        var after = userInGuild.User;
        var hasAvatarChanged = args.AvatarHash != after.AvatarHash;
        var hasGlobalNameChanged = args.DisplayName != after.DisplayName;

        if (!hasAvatarChanged && !hasGlobalNameChanged) return;

        var guild = userInGuild.Guild;
        if (!GuildLogSettings.TryGetValue(guild.Id, out var logSetting)) return;
        if (logSetting.UserUpdatedId is null or 0)
            return;

        var logChannel = guild.GetTextChannel(logSetting.UserUpdatedId.Value);
        if (logChannel is null)
            return;

        var before = args.ToUser();

        if (hasAvatarChanged)
        {
            if (logSetting.AvatarUpdatedId != null)
                logChannel = guild.GetTextChannel(logSetting.AvatarUpdatedId.Value);
            if (logChannel is null)
                return;

            var avatarEmbed = LogEmbed(strings.UserAvatarUpdated(guild.Id),
                    strings.UserLogEntry(guild.Id, userInGuild.Mention, userInGuild.Id), Mewdeko.OkColor)
                .WithThumbnailUrl(before.GetAvatarOrDefaultUrl(2048))
                .WithImageUrl(after.GetAvatarOrDefaultUrl(2048));

            await SendLogAsync(logChannel, avatarEmbed);
        }

        if (!hasGlobalNameChanged) return;
        if (logSetting.UsernameUpdatedId != null)
            logChannel = guild.GetTextChannel(logSetting.UsernameUpdatedId.Value);
        if (logChannel is null)
            return;
        var globalNameEmbed = LogEmbed(strings.UserGlobalNameUpdated(guild.Id),
                strings.UserLogEntry(guild.Id, userInGuild.Mention, userInGuild.Id) + "\n" +
                $"`Old Global Name:` {args.DisplayName ?? args.Username}\n" +
                $"`New Global Name:` {after.DisplayName ?? after.Username}", Mewdeko.OkColor)
            .WithThumbnailUrl(after.GetAvatarOrDefaultUrl(2048));

        await SendLogAsync(logChannel, globalNameEmbed);
    }

    /// <summary>
    ///     Gets the name of a channel's kind for log messages.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The name of the channel kind.</returns>
    private static string ChannelTypeName(Channel channel)
    {
        return channel.Type switch
        {
            ChannelType.GuildVoice => "Voice",
            ChannelType.GuildNews => "News",
            ChannelType.GuildText => "Text",
            ChannelType.GuildCategory => "Category",
            ChannelType.GuildForum => "Forum",
            ChannelType.GuildMedia => "Media",
            ChannelType.GuildLink => "Link",
            _ => "Unknown"
        };
    }

    /// <summary>
    ///     Handles the event when a channel is created in a guild.
    /// </summary>
    /// <param name="args">The channel that was created.</param>
    private async Task OnChannelCreated(Channel args)
    {
        if (args is not Channel { GuildId: not null } channel) return;

        // Check if channel is ignored
        if (IsChannelIgnored(channel.Guild.Id, channel.Id))
            return;

        if (GuildLogSettings.TryGetValue(channel.Guild.Id, out var logSetting))
        {
            if (logSetting.ChannelCreatedId is null or 0)
                return;

            var logChannel = channel.Guild.GetTextChannel(logSetting.ChannelCreatedId.Value);
            if (logChannel is null)
                return;

            var createdType = ChannelTypeName(channel);

            await Task.Delay(500);
            var auditLogs = await GetAuditLogsSafeAsync(channel.Guild, ActionType.ChannelCreated);
            var entry = auditLogs.FirstOrDefault();

            var guildId = channel.Guild.Id;
            var eb = LogEmbed(strings.ChannelCreated(guildId),
                $"`Channel:` <#{channel.Id}> ({channel.Name}) | {channel.Id}\n" +
                $"`Channel Type:` {createdType}", Mewdeko.OkColor);

            AddSection(eb, strings.LogSectionCreationInformation(guildId),
                $"`Created By:` {entry?.User.Mention ?? "Unknown"} | {entry?.User.Id.ToString() ?? "N/A"}\n" +
                $"`Created At:` {channel.CreatedAt:dd/MM/yyyy HH:mm:ss}");

            await SendLogAsync(logChannel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a channel is destroyed/deleted in a guild.
    /// </summary>
    /// <param name="args">The channel that was destroyed.</param>
    private async Task OnChannelDestroyed(Channel args)
    {
        if (args is not Channel { GuildId: not null } channel) return;

        // Check if channel is ignored
        if (IsChannelIgnored(channel.Guild.Id, channel.Id))
            return;

        if (GuildLogSettings.TryGetValue(channel.Guild.Id, out var logSetting))
        {
            if (logSetting.ChannelDestroyedId is null or 0)
                return;

            var logChannel = channel.Guild.GetTextChannel(logSetting.ChannelDestroyedId.Value);
            if (logChannel is null)
                return;

            var createdType = ChannelTypeName(channel);

            await Task.Delay(500);
            var auditLogs = await GetAuditLogsSafeAsync(channel.Guild, ActionType.ChannelDeleted);
            var entry = auditLogs.FirstOrDefault();

            var guildId = channel.Guild.Id;
            var eb = LogEmbed(strings.ChannelDestroyed(guildId),
                $"{strings.ChannelLogEntry(guildId, channel.Name, channel.Id)}\n" +
                $"`Channel Type:` {createdType}", Mewdeko.ErrorColor);

            AddSection(eb, strings.LogSectionDeletionInformation(guildId),
                $"`Destroyed By:` {entry?.User.Mention ?? "Unknown"} | {entry?.User.Id.ToString() ?? "N/A"}\n" +
                $"`Destroyed At:` {DateTime.UtcNow:dd/MM/yyyy HH:mm:ss}");

            await SendLogAsync(logChannel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a channel is updated in a guild.
    /// </summary>
    /// <param name="args">The channel before the update.</param>
    /// <param name="arsg2">The channel after the update.</param>
    private async Task OnChannelUpdated(Channel args, Channel arsg2)
    {
        if (args is not Channel { GuildId: not null } channel || arsg2 is not Channel { GuildId: not null } channel2) return;
        if (channel.Id != channel2.Id) return; // Should be the same channel

        // Check if channel is ignored
        if (IsChannelIgnored(channel.Guild.Id, channel.Id))
            return;

        if (GuildLogSettings.TryGetValue(channel.Guild.Id, out var logSetting))
        {
            if (logSetting.ChannelUpdatedId is null or 0)
                return;

            var logChannel = channel.Guild.GetTextChannel(logSetting.ChannelUpdatedId.Value);
            if (logChannel is null)
                return;

            await Task.Delay(500);
            var audit = await GetAuditLogsSafeAsync(channel.Guild, ActionType.ChannelUpdated);
            var entry = audit.FirstOrDefault();
            if (entry == null) return;

            var updatedByStr = $"`Updated By:` {entry.User.Mention} | {entry.User.Id}";
            var channelIdStr =
                $"`Channel:` <#{channel2.Id}> ({channel2.Name}) | {channel2.Id}"; // Mention current state
            var changed = false;
            var title = "";
            var description = "";

            if (channel.Name != channel2.Name)
            {
                title = strings.ChannelNameUpdated(channel.Guild.Id);
                description = strings.ChannelNameChange(channel.Guild.Id, channelIdStr, channel.Name, channel2.Name,
                    updatedByStr);
                changed = true;
            }
            else if (channel.Position != channel2.Position)
            {
                // Position changes are often noisy due to category changes, maybe ignore?
                // eb.WithTitle(strings.ChannelPositionUpdated(channel.Guild.Id)).WithDescription(strings.ChannelPositionChange(channel.Guild.Id, channelIdStr, channel.Position, channel2.Position, updatedByStr));
                // changed = true;
            }

            switch (channel.Type)
            {
                // Text Channel specific changes
                case ChannelType.GuildText or ChannelType.GuildNews:
                {
                    var textChannel = channel;
                    var textChannel2 = channel2;
                    if (textChannel.Topic != textChannel2.Topic)
                    {
                        title = strings.ChannelTopicUpdated(channel.Guild.Id);
                        description = strings.ChannelTopicChange(channel.Guild.Id, channelIdStr,
                            textChannel.Topic?.TrimTo(100) ?? strings.None(channel.Guild.Id),
                            textChannel2.Topic?.TrimTo(100) ?? strings.None(channel.Guild.Id), updatedByStr);
                        changed = true;
                    }
                    else if (textChannel.IsNsfw != textChannel2.IsNsfw)
                    {
                        title = strings.ChannelNsfwUpdated(channel.Guild.Id);
                        description = strings.ChannelNsfwChange(channel.Guild.Id, channelIdStr, textChannel.IsNsfw,
                            textChannel2.IsNsfw, updatedByStr);
                        changed = true;
                    }
                    else if (textChannel.SlowModeInterval != textChannel2.SlowModeInterval)
                    {
                        title = strings.ChannelSlowmodeUpdated(channel.Guild.Id);
                        description = strings.ChannelSlowmodeChange(channel.Guild.Id, channelIdStr,
                            textChannel.SlowModeInterval,
                            textChannel2.SlowModeInterval, updatedByStr);
                        changed = true;
                    }
                    else if (textChannel.CategoryId != textChannel2.CategoryId)
                    {
                        title = strings.ChannelCategoryUpdated(channel.Guild.Id);
                        description = strings.ChannelCategoryChange(channel.Guild.Id, channelIdStr,
                            textChannel.Category?.Name ?? strings.None(channel.Guild.Id),
                            textChannel2.Category?.Name ?? strings.None(channel.Guild.Id), updatedByStr);
                        changed = true;
                    }

                    break;
                }
                // Voice Channel specific changes
                case ChannelType.GuildVoice:
                {
                    var voiceChannel = channel;
                    var voiceChannel2 = channel2;
                    if (voiceChannel.Bitrate != voiceChannel2.Bitrate)
                    {
                        title = strings.ChannelBitrateUpdated(channel.Guild.Id);
                        description = strings.ChannelBitrateChange(channel.Guild.Id, channelIdStr,
                            voiceChannel.Bitrate / 1000,
                            voiceChannel2.Bitrate / 1000, updatedByStr);
                        changed = true;
                    }
                    else if (voiceChannel.UserLimit != voiceChannel2.UserLimit)
                    {
                        title = strings.ChannelUserLimitUpdated(channel.Guild.Id);
                        description = strings.ChannelUserLimitChange(channel.Guild.Id, channelIdStr,
                            voiceChannel.UserLimit?.ToString() ?? strings.Unlimited(channel.Guild.Id),
                            voiceChannel2.UserLimit?.ToString() ?? strings.Unlimited(channel.Guild.Id), updatedByStr);
                        changed = true;
                    }
                    else if (voiceChannel.CategoryId != voiceChannel2.CategoryId)
                    {
                        title = strings.ChannelCategoryUpdated(channel.Guild.Id);
                        description =
                            $"{channelIdStr}\n`Old Category:` {voiceChannel.Category?.Name ?? strings.None(channel.Guild.Id)}\n`New Category:` {voiceChannel2.Category?.Name ?? strings.None(channel.Guild.Id)}\n{updatedByStr}";
                        changed = true;
                    }

                    break;
                }
            }

            if (changed && !string.IsNullOrEmpty(title))
            {
                var eb = LogEmbed(title, description, Mewdeko.OkColor);

                await SendLogAsync(logChannel, eb);
            }
        }
    }

    /// <summary>
    ///     Handles the event when voice state is updated.
    /// </summary>
    /// <param name="guildUser">The user that had their voice state updated.</param>
    /// <param name="oldState">The voice state before the update.</param>
    /// <param name="newState">The voice state after the update.</param>
    private async Task OnVoicePresence(SocketGuildMember guildUser, VoiceStateSnapshot oldState,
        VoiceStateSnapshot newState)
    {
        if (IgnoresBot(guildUser.Guild.Id, guildUser.IsBot))
            return;

        // Check if old or new channel is ignored
        if ((oldState.VoiceChannel != null && IsChannelIgnored(guildUser.Guild.Id, oldState.VoiceChannel.Id)) ||
            (newState.VoiceChannel != null && IsChannelIgnored(guildUser.Guild.Id, newState.VoiceChannel.Id)))
            return;

        if (GuildLogSettings.TryGetValue(guildUser.Guild.Id, out var logSetting))
        {
            if (logSetting.LogVoicePresenceId is null or 0)
                return;

            var logChannel = await guildUser.Guild.GetTextChannelAsync(logSetting.LogVoicePresenceId.Value);
            if (logChannel is null)
                return;

            var stateChanged = false;
            var userInfo = strings.UserInfoLine(guildUser.Guild.Id, guildUser.Mention, guildUser.Id).TrimEnd('\n');
            var title = "";
            var description = "";

            // Channel Changes
            if (oldState.VoiceChannel?.Id != newState.VoiceChannel?.Id)
            {
                if (oldState.VoiceChannel != null && newState.VoiceChannel != null)
                {
                    title = strings.UserMovedVoiceChannels(guildUser.Guild.Id);
                    description = strings.UserMovedVoiceChannelsDesc(guildUser.Guild.Id, userInfo,
                        oldState.VoiceChannel.Name,
                        newState.VoiceChannel.Name);
                }
                else if (oldState.VoiceChannel == null && newState.VoiceChannel != null)
                {
                    title = strings.UserJoinedVoiceChannel(guildUser.Guild.Id);
                    description = strings.UserJoinedVoiceChannelDesc(guildUser.Guild.Id, userInfo,
                        newState.VoiceChannel.Name);
                }
                else if (oldState.VoiceChannel != null && newState.VoiceChannel == null)
                {
                    title = strings.UserLeftVoiceChannel(guildUser.Guild.Id);
                    description = strings.UserLeftVoiceChannelDesc(guildUser.Guild.Id, userInfo,
                        oldState.VoiceChannel.Name);
                }

                stateChanged = true;
            }
            // Mute/Deafen Status Changes (only log if channel didn't change, to avoid spam)
            else if (newState.VoiceChannel != null) // Only log these if user is still in a channel
            {
                if (oldState.IsDeafened != newState.IsDeafened)
                {
                    title = newState.IsDeafened
                        ? strings.UserVoiceDeafened(guildUser.Guild.Id,
                            !newState.IsSelfDeafened
                                ? strings.Server(guildUser.Guild.Id)
                                : strings.Self(guildUser.Guild.Id))
                        : strings.UserVoiceUndeafened(guildUser.Guild.Id);
                    description = strings.UserVoiceStateDesc(guildUser.Guild.Id, userInfo,
                        newState.VoiceChannel.Name);
                    stateChanged = true;
                }
                else if (oldState.IsMuted != newState.IsMuted)
                {
                    title = newState.IsMuted
                        ? strings.UserVoiceMuted(guildUser.Guild.Id,
                            !newState.IsSelfMuted
                                ? strings.Server(guildUser.Guild.Id)
                                : strings.Self(guildUser.Guild.Id))
                        : strings.UserVoiceUnmuted(guildUser.Guild.Id, guildUser.Username);
                    description = strings.UserVoiceStateDesc(guildUser.Guild.Id, userInfo,
                        newState.VoiceChannel.Name);
                    stateChanged = true;
                }
                else if (oldState.IsStreaming != newState.IsStreaming)
                {
                    title = newState.IsStreaming
                        ? strings.UserStartedStreaming(guildUser.Guild.Id)
                        : strings.UserStoppedStreaming(guildUser.Guild.Id);
                    description = strings.UserVoiceStateDesc(guildUser.Guild.Id, userInfo,
                        newState.VoiceChannel.Name);
                    stateChanged = true;
                }
                else if (oldState.IsVideoing != newState.IsVideoing)
                {
                    title = newState.IsVideoing
                        ? strings.UserStartedVideo(guildUser.Guild.Id)
                        : strings.UserStoppedVideo(guildUser.Guild.Id);
                    description = strings.UserVoiceStateDesc(guildUser.Guild.Id, userInfo,
                        newState.VoiceChannel.Name);
                    stateChanged = true;
                }
            }

            if (stateChanged && !string.IsNullOrEmpty(title))
            {
                var eb = LogEmbed(title, description, Mewdeko.OkColor);

                await SendLogAsync(logChannel, eb);
            }
        }
    }

    /// <summary>
    ///     Handles the event when a user says something using tts in a channel.
    /// </summary>
    /// <param name="guildUser">The user that used tts.</param>
    /// <param name="oldState">The voice state before the update.</param>
    /// <param name="newState">The voice state after the update.</param>
    private async Task OnVoicePresenceTts(SocketGuildMember guildUser, VoiceStateSnapshot oldState,
        VoiceStateSnapshot newState)
    {
        if (IgnoresBot(guildUser.Guild.Id, guildUser.IsBot))
            return;

        // Check if old or new channel is ignored
        if ((oldState.VoiceChannel != null && IsChannelIgnored(guildUser.Guild.Id, oldState.VoiceChannel.Id)) ||
            (newState.VoiceChannel != null && IsChannelIgnored(guildUser.Guild.Id, newState.VoiceChannel.Id)))
            return;

        if (GuildLogSettings.TryGetValue(guildUser.Guild.Id, out var logSetting))
        {
            if (logSetting.LogVoicePresenceTtsId is null or 0)
                return;

            var logChannel = await guildUser.Guild.GetTextChannelAsync(logSetting.LogVoicePresenceTtsId.Value);
            if (logChannel is null)
                return;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Handles the event when a user is muted in a guild.
    /// </summary>
    /// <param name="guildUser">The user that was muted.</param>
    /// <param name="muter">The user that muted the user.</param>
    /// <param name="muteType">Type of mute. <see cref="MuteType" /></param>
    /// <param name="reason">The reason the user was muted.</param>
    private async Task OnUserMuted(SocketGuildMember guildUser, IUser muter, MuteType muteType, string reason)
    {
        if (GuildLogSettings.TryGetValue(guildUser.Guild.Id, out var logSetting))
        {
            if (logSetting.UserMutedId is null or 0)
                return;

            var logChannel = await guildUser.Guild.GetTextChannelAsync(logSetting.UserMutedId.Value);
            if (logChannel is null)
                return;

            var eb = LogEmbed(strings.UserMutedTitle(guildUser.GuildId, muteType),
                strings.UserMutedDesc(guildUser.GuildId, guildUser.Mention, guildUser.Id), Mewdeko.ErrorColor)
                .WithThumbnailUrl(guildUser.RealAvatarUrl().ToString());

            AddSection(eb, strings.LogSectionModerationInformation(guildUser.GuildId),
                strings.MutedByDesc(guildUser.GuildId, muter.Mention, muter.Id) +
                strings.MuteReasonDesc(guildUser.GuildId, reason ?? "N/A"));

            await SendLogAsync(logChannel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a user is unmuted in a guild.
    /// </summary>
    /// <param name="guildUser">The user that was unmuted.</param>
    /// <param name="unmuter">The user that unmuted the user.</param>
    /// <param name="muteType">Type of mute. <see cref="MuteType" /></param>
    /// <param name="reason">The reason the user was unmuted.</param>
    private async Task OnUserUnmuted(SocketGuildMember guildUser, IUser unmuter, MuteType muteType, string reason)
    {
        if (GuildLogSettings.TryGetValue(guildUser.Guild.Id, out var logSetting))
        {
            if (logSetting.UserMutedId is null or 0)
                return;

            var logChannel = await guildUser.Guild.GetTextChannelAsync(logSetting.UserMutedId.Value);
            if (logChannel is null)
                return;

            var eb = LogEmbed(strings.UserUnmutedTitle(guildUser.GuildId, muteType),
                strings.UserUnmutedDesc(guildUser.GuildId, guildUser.Mention, guildUser.Id), Mewdeko.OkColor)
                .WithThumbnailUrl(guildUser.RealAvatarUrl().ToString());

            AddSection(eb, strings.LogSectionModerationInformation(guildUser.GuildId),
                strings.UnmutedByDesc(guildUser.GuildId, unmuter.Mention, unmuter.Id) +
                strings.MuteReasonDesc(guildUser.GuildId, reason ?? "N/A"));

            await SendLogAsync(logChannel, eb);
        }
    }

    /// <summary>
    ///     Sets the log channel for a specific type of log.
    /// </summary>
    /// <param name="guildId">The guildId to set the log setting for.</param>
    /// <param name="channelId">The channelId to set the log channel to.</param>
    /// <param name="type">The type of log to set the channel for.</param>
    public async Task SetLogChannel(ulong guildId, ulong channelId, LogType type)
    {
        LoggingV2 logSetting;
        var isNew = false;
        await using var db = await dbFactory.CreateConnectionAsync();

        logSetting = await db.LoggingV2.FirstOrDefaultAsync(l => l.GuildId == guildId).ConfigureAwait(false);

        if (logSetting == null)
        {
            isNew = true;
            logSetting = new LoggingV2
            {
                GuildId = guildId
            };
        }

        switch (type)
        {
            case LogType.Other: logSetting.LogOtherId = channelId; break;
            case LogType.RoleUpdated: logSetting.RoleUpdatedId = channelId; break;
            case LogType.RoleCreated: logSetting.RoleCreatedId = channelId; break;
            case LogType.ServerUpdated: logSetting.ServerUpdatedId = channelId; break;
            case LogType.ThreadCreated: logSetting.ThreadCreatedId = channelId; break;
            case LogType.UserRoleAdded: logSetting.UserRoleAddedId = channelId; break;
            case LogType.UserRoleRemoved: logSetting.UserRoleRemovedId = channelId; break;
            case LogType.UsernameUpdated: logSetting.UsernameUpdatedId = channelId; break;
            case LogType.NicknameUpdated: logSetting.NicknameUpdatedId = channelId; break;
            case LogType.AvatarUpdated: logSetting.AvatarUpdatedId = channelId; break;
            case LogType.ThreadDeleted: logSetting.ThreadDeletedId = channelId; break;
            case LogType.ThreadUpdated: logSetting.ThreadUpdatedId = channelId; break;
            case LogType.MessageUpdated: logSetting.MessageUpdatedId = channelId; break;
            case LogType.MessageDeleted: logSetting.MessageDeletedId = channelId; break;
            case LogType.UserJoined: logSetting.UserJoinedId = channelId; break;
            case LogType.UserLeft: logSetting.UserLeftId = channelId; break;
            case LogType.UserBanned: logSetting.UserBannedId = channelId; break;
            case LogType.UserUnbanned: logSetting.UserUnbannedId = channelId; break;
            case LogType.UserUpdated: logSetting.UserUpdatedId = channelId; break;
            case LogType.ChannelCreated: logSetting.ChannelCreatedId = channelId; break;
            case LogType.ChannelDestroyed: logSetting.ChannelDestroyedId = channelId; break;
            case LogType.ChannelUpdated: logSetting.ChannelUpdatedId = channelId; break;
            case LogType.VoicePresence: logSetting.LogVoicePresenceId = channelId; break;
            case LogType.VoicePresenceTts: logSetting.LogVoicePresenceTtsId = channelId; break;
            case LogType.UserMuted: logSetting.UserMutedId = channelId; break;
            case LogType.RoleDeleted: logSetting.RoleDeletedId = channelId; break;
            case LogType.InviteCreated: logSetting.InviteCreatedId = channelId; break;
            case LogType.InviteDeleted: logSetting.InviteDeletedId = channelId; break;
            case LogType.MessagesBulkDeleted: logSetting.MessagesBulkDeletedId = channelId; break;
            case LogType.ReactionEvents: logSetting.ReactionEventsId = channelId; break;
            default: SetLogChannelId(logSetting, type, channelId); break;
        }

        try
        {
            if (isNew)
                await db.InsertAsync(logSetting).ConfigureAwait(false);
            else
                await db.UpdateAsync(logSetting).ConfigureAwait(false);

            GuildLogSettings.AddOrUpdate(guildId, logSetting, (_, _) => logSetting);
        }
        catch (Exception e)
        {
            logger.LogError(e, "There was an issue setting log settings for Guild {GuildId}", guildId);
        }
    }

    /// <summary>
    ///     Allows you to set the log channel for a specific category of logs.
    /// </summary>
    /// <param name="guildId">The guildId to set the logs for.</param>
    /// <param name="channelId">The channelId to set the logs to.</param>
    /// <param name="categoryTypes">The category of logs to set the channel for.</param>
    public async Task LogSetByType(ulong guildId, ulong channelId, LogCategoryTypes categoryTypes)
    {
        LoggingV2 logSetting;
        var isNew = false;
        await using var db = await dbFactory.CreateConnectionAsync();

        logSetting = await db.LoggingV2.FirstOrDefaultAsync(l => l.GuildId == guildId).ConfigureAwait(false);

        if (logSetting == null)
        {
            isNew = true;
            logSetting = new LoggingV2
            {
                GuildId = guildId
            };
        }

        switch (categoryTypes)
        {
            case LogCategoryTypes.All:
                logSetting.AvatarUpdatedId = channelId;
                logSetting.ChannelCreatedId = channelId;
                logSetting.ChannelDestroyedId = channelId;
                logSetting.ChannelUpdatedId = channelId;
                logSetting.LogOtherId = channelId;
                logSetting.MessageDeletedId = channelId;
                logSetting.MessageUpdatedId = channelId;
                logSetting.NicknameUpdatedId = channelId;
                logSetting.RoleCreatedId = channelId;
                logSetting.RoleDeletedId = channelId;
                logSetting.RoleUpdatedId = channelId;
                logSetting.ServerUpdatedId = channelId;
                logSetting.ThreadCreatedId = channelId;
                logSetting.ThreadDeletedId = channelId;
                logSetting.ThreadUpdatedId = channelId;
                logSetting.UserBannedId = channelId;
                logSetting.UserJoinedId = channelId;
                logSetting.UserLeftId = channelId;
                logSetting.UserMutedId = channelId;
                logSetting.UsernameUpdatedId = channelId;
                logSetting.UserUnbannedId = channelId;
                logSetting.UserUpdatedId = channelId;
                logSetting.LogUserPresenceId = channelId;
                logSetting.LogVoicePresenceId = channelId;
                logSetting.UserRoleAddedId = channelId;
                logSetting.UserRoleRemovedId = channelId;
                logSetting.LogVoicePresenceTtsId = channelId;
                logSetting.InviteCreatedId = channelId;
                logSetting.InviteDeletedId = channelId;
                logSetting.MessagesBulkDeletedId = channelId;
                logSetting.ReactionEventsId = channelId;
                logSetting.EmojiCreatedId = channelId;
                logSetting.EmojiUpdatedId = channelId;
                logSetting.EmojiDeletedId = channelId;
                logSetting.StickerCreatedId = channelId;
                logSetting.StickerUpdatedId = channelId;
                logSetting.StickerDeletedId = channelId;
                logSetting.UserTimedOutId = channelId;
                logSetting.UserTimeoutRemovedId = channelId;
                logSetting.InviteLinkPostedId = channelId;
                logSetting.ChannelPermissionsUpdatedId = channelId;
                logSetting.WebhookChangedId = channelId;
                break;
            case LogCategoryTypes.Users:
                logSetting.NicknameUpdatedId = channelId;
                logSetting.AvatarUpdatedId = channelId;
                logSetting.UsernameUpdatedId = channelId;
                logSetting.UserRoleAddedId = channelId;
                logSetting.UserRoleRemovedId = channelId;
                logSetting.LogVoicePresenceId = channelId;
                logSetting.UserJoinedId = channelId;
                logSetting.UserLeftId = channelId;
                logSetting.UserUpdatedId = channelId;
                break;
            case LogCategoryTypes.Threads:
                logSetting.ThreadCreatedId = channelId;
                logSetting.ThreadDeletedId = channelId;
                logSetting.ThreadUpdatedId = channelId;
                break;
            case LogCategoryTypes.Roles:
                logSetting.RoleCreatedId = channelId;
                logSetting.RoleDeletedId = channelId;
                logSetting.RoleUpdatedId = channelId;
                break;
            case LogCategoryTypes.Server:
                logSetting.ServerUpdatedId = channelId;
                logSetting.InviteCreatedId = channelId;
                logSetting.InviteDeletedId = channelId;
                logSetting.EmojiCreatedId = channelId;
                logSetting.EmojiUpdatedId = channelId;
                logSetting.EmojiDeletedId = channelId;
                logSetting.StickerCreatedId = channelId;
                logSetting.StickerUpdatedId = channelId;
                logSetting.StickerDeletedId = channelId;
                logSetting.WebhookChangedId = channelId;
                break;
            case LogCategoryTypes.Channel:
                logSetting.ChannelUpdatedId = channelId;
                logSetting.ChannelCreatedId = channelId;
                logSetting.ChannelDestroyedId = channelId;
                logSetting.ChannelPermissionsUpdatedId = channelId;
                break;
            case LogCategoryTypes.Messages:
                logSetting.MessageDeletedId = channelId;
                logSetting.MessageUpdatedId = channelId;
                logSetting.MessagesBulkDeletedId = channelId;
                logSetting.ReactionEventsId = channelId;
                logSetting.InviteLinkPostedId = channelId;
                break;
            case LogCategoryTypes.Moderation:
                logSetting.UserTimedOutId = channelId;
                logSetting.UserTimeoutRemovedId = channelId;
                logSetting.UserMutedId = channelId;
                logSetting.UserBannedId = channelId;
                logSetting.UserUnbannedId = channelId;
                logSetting.LogOtherId = channelId;
                break;
            case LogCategoryTypes.None:
                ulong? noneValue = null;
                logSetting.AvatarUpdatedId = noneValue;
                logSetting.ChannelCreatedId = noneValue;
                logSetting.ChannelDestroyedId = noneValue;
                logSetting.ChannelUpdatedId = noneValue;
                logSetting.LogOtherId = noneValue;
                logSetting.MessageDeletedId = noneValue;
                logSetting.MessageUpdatedId = noneValue;
                logSetting.NicknameUpdatedId = noneValue;
                logSetting.RoleCreatedId = noneValue;
                logSetting.RoleDeletedId = noneValue;
                logSetting.RoleUpdatedId = noneValue;
                logSetting.ServerUpdatedId = noneValue;
                logSetting.ThreadCreatedId = noneValue;
                logSetting.ThreadDeletedId = noneValue;
                logSetting.ThreadUpdatedId = noneValue;
                logSetting.UserBannedId = noneValue;
                logSetting.UserJoinedId = noneValue;
                logSetting.UserLeftId = noneValue;
                logSetting.UserMutedId = noneValue;
                logSetting.UsernameUpdatedId = noneValue;
                logSetting.UserUnbannedId = noneValue;
                logSetting.UserUpdatedId = noneValue;
                logSetting.LogUserPresenceId = noneValue;
                logSetting.LogVoicePresenceId = noneValue;
                logSetting.UserRoleAddedId = noneValue;
                logSetting.UserRoleRemovedId = noneValue;
                logSetting.LogVoicePresenceTtsId = noneValue;
                logSetting.InviteCreatedId = noneValue;
                logSetting.InviteDeletedId = noneValue;
                logSetting.MessagesBulkDeletedId = noneValue;
                logSetting.ReactionEventsId = noneValue;
                logSetting.EmojiCreatedId = noneValue;
                logSetting.EmojiUpdatedId = noneValue;
                logSetting.EmojiDeletedId = noneValue;
                logSetting.StickerCreatedId = noneValue;
                logSetting.StickerUpdatedId = noneValue;
                logSetting.StickerDeletedId = noneValue;
                logSetting.UserTimedOutId = noneValue;
                logSetting.UserTimeoutRemovedId = noneValue;
                logSetting.InviteLinkPostedId = noneValue;
                logSetting.ChannelPermissionsUpdatedId = noneValue;
                logSetting.WebhookChangedId = noneValue;
                break;
        }

        try
        {
            if (isNew)
                await db.InsertAsync(logSetting).ConfigureAwait(false);
            else
                await db.UpdateAsync(logSetting).ConfigureAwait(false);

            GuildLogSettings.AddOrUpdate(guildId, logSetting, (_, _) => logSetting);
        }
        catch (Exception e)
        {
            logger.LogError(e, "There was an issue setting log settings by type for Guild {GuildId}", guildId);
        }
    }

    private static readonly (string Name, LogType[] Types)[] SetupGroups =
    [
        ("message-logs",
            [
                LogType.MessageUpdated, LogType.MessageDeleted, LogType.MessagesBulkDeleted, LogType.ReactionEvents,
                LogType.InviteLinkPosted
            ]),
        ("member-logs",
            [
                LogType.UserJoined, LogType.UserLeft, LogType.UsernameUpdated, LogType.NicknameUpdated,
                LogType.AvatarUpdated, LogType.UserUpdated, LogType.UserRoleAdded, LogType.UserRoleRemoved
            ]),
        ("moderation-logs",
            [
                LogType.UserBanned, LogType.UserUnbanned, LogType.UserMuted, LogType.UserTimedOut,
                LogType.UserTimeoutRemoved, LogType.Other
            ]),
        ("server-logs",
            [
                LogType.ServerUpdated, LogType.InviteCreated, LogType.InviteDeleted,
                LogType.RoleCreated, LogType.RoleUpdated, LogType.RoleDeleted, LogType.ChannelCreated,
                LogType.ChannelUpdated, LogType.ChannelDestroyed, LogType.ThreadCreated, LogType.ThreadUpdated,
                LogType.ThreadDeleted, LogType.EmojiCreated, LogType.EmojiUpdated, LogType.EmojiDeleted,
                LogType.StickerCreated, LogType.StickerUpdated, LogType.StickerDeleted,
                LogType.ChannelPermissionsUpdated, LogType.WebhookChanged
            ]),
        ("voice-logs", [LogType.VoicePresence, LogType.VoicePresenceTts])
    ];

    /// <summary>
    ///     Gets the channel configured for a log type.
    /// </summary>
    /// <param name="l">The log settings.</param>
    /// <param name="type">The log type.</param>
    /// <returns>The channel id, or null if none is set.</returns>
    public static ulong? GetLogChannelId(LoggingV2 l, LogType type)
    {
        return type switch
        {
            LogType.Other => l.LogOtherId,
            LogType.MessageUpdated => l.MessageUpdatedId,
            LogType.UserUpdated => l.UserUpdatedId,
            LogType.MessageDeleted => l.MessageDeletedId,
            LogType.UserJoined => l.UserJoinedId,
            LogType.UserLeft => l.UserLeftId,
            LogType.UserBanned => l.UserBannedId,
            LogType.UserUnbanned => l.UserUnbannedId,
            LogType.ChannelCreated => l.ChannelCreatedId,
            LogType.ChannelDestroyed => l.ChannelDestroyedId,
            LogType.ChannelUpdated => l.ChannelUpdatedId,
            LogType.VoicePresence => l.LogVoicePresenceId,
            LogType.VoicePresenceTts => l.LogVoicePresenceTtsId,
            LogType.UserMuted => l.UserMutedId,
            LogType.ThreadCreated => l.ThreadCreatedId,
            LogType.ThreadDeleted => l.ThreadDeletedId,
            LogType.ThreadUpdated => l.ThreadUpdatedId,
            LogType.NicknameUpdated => l.NicknameUpdatedId,
            LogType.RoleCreated => l.RoleCreatedId,
            LogType.RoleDeleted => l.RoleDeletedId,
            LogType.RoleUpdated => l.RoleUpdatedId,
            LogType.ServerUpdated => l.ServerUpdatedId,
            LogType.UserRoleAdded => l.UserRoleAddedId,
            LogType.UserRoleRemoved => l.UserRoleRemovedId,
            LogType.UsernameUpdated => l.UsernameUpdatedId,
            LogType.AvatarUpdated => l.AvatarUpdatedId,
            LogType.InviteCreated => l.InviteCreatedId,
            LogType.InviteDeleted => l.InviteDeletedId,
            LogType.MessagesBulkDeleted => l.MessagesBulkDeletedId,
            LogType.ReactionEvents => l.ReactionEventsId,
            LogType.EmojiCreated => l.EmojiCreatedId,
            LogType.EmojiUpdated => l.EmojiUpdatedId,
            LogType.EmojiDeleted => l.EmojiDeletedId,
            LogType.StickerCreated => l.StickerCreatedId,
            LogType.StickerUpdated => l.StickerUpdatedId,
            LogType.StickerDeleted => l.StickerDeletedId,
            LogType.UserTimedOut => l.UserTimedOutId,
            LogType.UserTimeoutRemoved => l.UserTimeoutRemovedId,
            LogType.InviteLinkPosted => l.InviteLinkPostedId,
            LogType.ChannelPermissionsUpdated => l.ChannelPermissionsUpdatedId,
            LogType.WebhookChanged => l.WebhookChangedId,
            _ => null
        };
    }

    private static void SetLogChannelId(LoggingV2 l, LogType type, ulong? channelId)
    {
        switch (type)
        {
            case LogType.Other: l.LogOtherId = channelId; break;
            case LogType.RoleUpdated: l.RoleUpdatedId = channelId; break;
            case LogType.RoleCreated: l.RoleCreatedId = channelId; break;
            case LogType.ServerUpdated: l.ServerUpdatedId = channelId; break;
            case LogType.ThreadCreated: l.ThreadCreatedId = channelId; break;
            case LogType.UserRoleAdded: l.UserRoleAddedId = channelId; break;
            case LogType.UserRoleRemoved: l.UserRoleRemovedId = channelId; break;
            case LogType.UsernameUpdated: l.UsernameUpdatedId = channelId; break;
            case LogType.NicknameUpdated: l.NicknameUpdatedId = channelId; break;
            case LogType.AvatarUpdated: l.AvatarUpdatedId = channelId; break;
            case LogType.ThreadDeleted: l.ThreadDeletedId = channelId; break;
            case LogType.ThreadUpdated: l.ThreadUpdatedId = channelId; break;
            case LogType.MessageUpdated: l.MessageUpdatedId = channelId; break;
            case LogType.MessageDeleted: l.MessageDeletedId = channelId; break;
            case LogType.UserJoined: l.UserJoinedId = channelId; break;
            case LogType.UserLeft: l.UserLeftId = channelId; break;
            case LogType.UserBanned: l.UserBannedId = channelId; break;
            case LogType.UserUnbanned: l.UserUnbannedId = channelId; break;
            case LogType.UserUpdated: l.UserUpdatedId = channelId; break;
            case LogType.ChannelCreated: l.ChannelCreatedId = channelId; break;
            case LogType.ChannelDestroyed: l.ChannelDestroyedId = channelId; break;
            case LogType.ChannelUpdated: l.ChannelUpdatedId = channelId; break;
            case LogType.VoicePresence: l.LogVoicePresenceId = channelId; break;
            case LogType.VoicePresenceTts: l.LogVoicePresenceTtsId = channelId; break;
            case LogType.UserMuted: l.UserMutedId = channelId; break;
            case LogType.RoleDeleted: l.RoleDeletedId = channelId; break;
            case LogType.InviteCreated: l.InviteCreatedId = channelId; break;
            case LogType.InviteDeleted: l.InviteDeletedId = channelId; break;
            case LogType.MessagesBulkDeleted: l.MessagesBulkDeletedId = channelId; break;
            case LogType.ReactionEvents: l.ReactionEventsId = channelId; break;
            case LogType.EmojiCreated: l.EmojiCreatedId = channelId; break;
            case LogType.EmojiUpdated: l.EmojiUpdatedId = channelId; break;
            case LogType.EmojiDeleted: l.EmojiDeletedId = channelId; break;
            case LogType.StickerCreated: l.StickerCreatedId = channelId; break;
            case LogType.StickerUpdated: l.StickerUpdatedId = channelId; break;
            case LogType.StickerDeleted: l.StickerDeletedId = channelId; break;
            case LogType.UserTimedOut: l.UserTimedOutId = channelId; break;
            case LogType.UserTimeoutRemoved: l.UserTimeoutRemovedId = channelId; break;
            case LogType.InviteLinkPosted: l.InviteLinkPostedId = channelId; break;
            case LogType.ChannelPermissionsUpdated: l.ChannelPermissionsUpdatedId = channelId; break;
            case LogType.WebhookChanged: l.WebhookChangedId = channelId; break;
        }
    }

    private async Task SaveLogSettingsAsync(ulong guildId, Action<LoggingV2> mutate)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var logSetting = await db.LoggingV2.FirstOrDefaultAsync(l => l.GuildId == guildId).ConfigureAwait(false);
        var isNew = logSetting == null;
        logSetting ??= new LoggingV2
        {
            GuildId = guildId
        };

        mutate(logSetting);

        try
        {
            if (isNew)
                await db.InsertAsync(logSetting).ConfigureAwait(false);
            else
                await db.UpdateAsync(logSetting).ConfigureAwait(false);

            GuildLogSettings.AddOrUpdate(guildId, logSetting, (_, _) => logSetting);
        }
        catch (Exception e)
        {
            logger.LogError(e, "There was an issue saving log settings for Guild {GuildId}", guildId);
        }
    }

    /// <summary>
    ///     Creates a private "Logs" category with a channel per log group and routes every log type to its channel.
    /// </summary>
    /// <param name="guild">The guild to set logging up in.</param>
    /// <returns>The channels used, keyed by name, or null if they could not be created.</returns>
    public async Task<Dictionary<string, Channel>?> SetupLogChannels(SocketGuild guild)
    {
        try
        {
            var overwrites = new[]
            {
                OverwritePermissions.ToRequest(guild.Id, OverwriteRequestType.Role,
                    new OverwritePermissions(0, ChannelPermission.ViewChannel)),
                OverwritePermissions.ToRequest(client.CurrentUser.Id, OverwriteRequestType.Member,
                    new OverwritePermissions(
                        ChannelPermission.ViewChannel | ChannelPermission.SendMessages | ChannelPermission.EmbedLinks |
                        ChannelPermission.AttachFiles, 0))
            };

            var category = guild.CategoryChannels
                               .FirstOrDefault(c => c.Name.Equals("Logs", StringComparison.OrdinalIgnoreCase))
                           ?? await guild.CreateCategoryChannelAsync(new CreateCategoryChannelRequest
                           {
                               Name = "Logs", PermissionOverwrites = overwrites
                           }).ConfigureAwait(false);

            var channels = new Dictionary<string, Channel>();
            var routes = new Dictionary<LogType, ulong>();
            foreach (var (name, types) in SetupGroups)
            {
                var channel = guild.TextChannels
                                  .FirstOrDefault(c => c.ParentId == category.Id &&
                                                       c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                              ?? await guild.CreateTextChannelAsync(new CreateTextChannelRequest
                              {
                                  Name = name, ParentCategoryId = category.Id, PermissionOverwrites = overwrites
                              }).ConfigureAwait(false);

                channels[name] = channel;
                foreach (var type in types)
                    routes[type] = channel.Id;
            }

            await ApplyLogChannels(guild.Id, routes).ConfigureAwait(false);
            return channels;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set up log channels for guild {GuildId}", guild.Id);
            return null;
        }
    }

    /// <summary>
    ///     Sets the channel for several log types in one save.
    /// </summary>
    /// <param name="guildId">The guild to update.</param>
    /// <param name="routes">The channel to use for each log type.</param>
    public Task ApplyLogChannels(ulong guildId, IReadOnlyDictionary<LogType, ulong> routes)
    {
        return SaveLogSettingsAsync(guildId, l =>
        {
            foreach (var (type, channelId) in routes)
                SetLogChannelId(l, type, channelId);
        });
    }

    private static List<string> ParsePrefixes(string? stored)
    {
        return string.IsNullOrEmpty(stored)
            ? []
            : stored.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>
    ///     Gets the prefixes whose messages are skipped by message edit and delete logs.
    /// </summary>
    /// <param name="guildId">The guild to get prefixes for.</param>
    /// <returns>The configured prefixes.</returns>
    public List<string> GetMessageIgnorePrefixes(ulong guildId)
    {
        return GuildLogSettings.TryGetValue(guildId, out var l) ? ParsePrefixes(l.MessageIgnorePrefixes) : [];
    }

    /// <summary>
    ///     Sets the prefixes whose messages are skipped by message edit and delete logs.
    /// </summary>
    /// <param name="guildId">The guild to update.</param>
    /// <param name="prefixes">The prefixes to skip. An empty collection clears them.</param>
    public Task SetMessageIgnorePrefixes(ulong guildId, IEnumerable<string> prefixes)
    {
        var cleaned = prefixes.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        return SaveLogSettingsAsync(guildId,
            l => l.MessageIgnorePrefixes = cleaned.Count == 0 ? null : string.Join('\n', cleaned));
    }

    /// <summary>
    ///     Gets whether a guild's logs skip actions by bots.
    /// </summary>
    /// <param name="guildId">The guild to check.</param>
    /// <returns>True when bots are skipped, which is the default.</returns>
    public bool GetIgnoreBots(ulong guildId)
    {
        return !GuildLogSettings.TryGetValue(guildId, out var l) || l.IgnoreBots;
    }

    /// <summary>
    ///     Sets whether a guild's logs skip actions by bots.
    /// </summary>
    /// <param name="guildId">The guild to update.</param>
    /// <param name="ignore">Whether to skip bots.</param>
    public Task SetIgnoreBots(ulong guildId, bool ignore)
    {
        return SaveLogSettingsAsync(guildId, l => l.IgnoreBots = ignore);
    }

    private bool IgnoresBot(ulong guildId, bool isBot)
    {
        return isBot && GetIgnoreBots(guildId);
    }

    private static bool StartsWithIgnoredPrefix(LoggingV2 l, string? content)
    {
        if (string.IsNullOrEmpty(l.MessageIgnorePrefixes) || string.IsNullOrEmpty(content))
            return false;

        return ParsePrefixes(l.MessageIgnorePrefixes)
            .Any(p => content.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Exports a guild's log configuration.
    /// </summary>
    /// <param name="guild">The guild to export.</param>
    /// <returns>The configuration as indented JSON and as a short code, or null if nothing is configured.</returns>
    public async Task<(string Json, string Code)?> ExportConfig(SocketGuild guild)
    {
        if (!GuildLogSettings.TryGetValue(guild.Id, out var l))
            return null;

        var channels = guild.TextChannels.ToDictionary(c => c.Id);
        var export = new LogConfigExport
        {
            Prefixes = ParsePrefixes(l.MessageIgnorePrefixes)
        };

        foreach (var type in Enum.GetValues<LogType>())
        {
            var id = GetLogChannelId(l, type);
            if (id is null or 0 || !channels.TryGetValue(id.Value, out var channel))
                continue;

            export.Types[type.ToString()] = new ExportedChannel
            {
                Id = channel.Id, Name = channel.Name
            };
        }

        foreach (var id in await GetIgnoredChannels(guild.Id).ConfigureAwait(false))
        {
            if (channels.TryGetValue(id, out var channel))
                export.Ignored.Add(new ExportedChannel
                {
                    Id = channel.Id, Name = channel.Name
                });
        }

        if (export.Types.Count == 0 && export.Ignored.Count == 0 && export.Prefixes.Count == 0)
            return null;

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
        {
            await gzip.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(export)).ConfigureAwait(false);
        }

        return (json, Convert.ToBase64String(output.ToArray()));
    }

    /// <summary>
    ///     Imports a log configuration from a code or JSON, mapping channels by id and then by name.
    /// </summary>
    /// <param name="guild">The guild to import into.</param>
    /// <param name="input">The export code or JSON.</param>
    /// <returns>What was applied and which channels could not be mapped.</returns>
    public async Task<LogImportResult> ImportConfig(SocketGuild guild, string input)
    {
        LogConfigExport? export;
        try
        {
            input = input.Trim().Trim('`');
            if (input.StartsWith('{'))
            {
                export = JsonSerializer.Deserialize<LogConfigExport>(input);
            }
            else
            {
                await using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(input)),
                    CompressionMode.Decompress);
                export = await JsonSerializer.DeserializeAsync<LogConfigExport>(gzip).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return new LogImportResult(0, [], false);
        }

        if (export is null)
            return new LogImportResult(0, [], false);

        var channels = guild.TextChannels.ToList();
        var unmapped = new HashSet<string>();

        Channel? Resolve(ExportedChannel exported)
        {
            var found = channels.FirstOrDefault(c => c.Id == exported.Id)
                        ?? channels.FirstOrDefault(c => c.Name.Equals(exported.Name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                unmapped.Add($"#{exported.Name}");
            return found;
        }

        var routes = new Dictionary<LogType, ulong>();
        foreach (var (typeName, exported) in export.Types)
        {
            if (!Enum.TryParse<LogType>(typeName, true, out var type))
                continue;

            var channel = Resolve(exported);
            if (channel is not null)
                routes[type] = channel.Id;
        }

        var ignored = export.Ignored.Select(Resolve).OfType<Channel>().Select(c => c.Id).ToHashSet();

        await SaveLogSettingsAsync(guild.Id, l =>
        {
            foreach (var (type, channelId) in routes)
                SetLogChannelId(l, type, channelId);

            var prefixes = export.Prefixes.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
            l.MessageIgnorePrefixes = prefixes.Count == 0 ? null : string.Join('\n', prefixes);
        }).ConfigureAwait(false);

        await UpdateIgnoredChannelsAsync(guild.Id, ignored).ConfigureAwait(false);
        return new LogImportResult(routes.Count, unmapped.ToList(), true);
    }

    private static string? EmojiUrl(GuildEmoji emoji)
    {
        return emoji.GetEmojiUrl();
    }

    private async Task SendEmojiLogAsync(SocketGuild? guild, ulong? channelId, string title, string text, Color color,
        string? imageUrl)
    {
        if (guild is null || channelId is null or 0 || guild.GetTextChannel(channelId.Value) is not { } channel)
            return;

        var eb = LogEmbed(title, text, color);
        if (imageUrl is not null)
            eb.WithThumbnailUrl(imageUrl);

        await SendLogAsync(channel, eb);
    }

    /// <summary>
    ///     Handles a custom emoji being created.
    /// </summary>
    /// <param name="emoji">The created emoji.</param>
    private async Task OnEmojiCreated(GuildEmoji emoji)
    {
        if (!GuildLogSettings.TryGetValue(emoji.GuildId, out var logSetting) || logSetting.EmojiCreatedId is null or 0)
            return;

        var guild = client.GetGuild(emoji.GuildId);
        var gid = emoji.GuildId;
        var by = (await GetAuditLogsSafeAsync(guild, ActionType.EmojiCreated)).FirstOrDefault()?.User;
        await SendEmojiLogAsync(guild, logSetting.EmojiCreatedId, strings.LogEmojiCreated(gid),
            strings.NameField(gid, emoji.Name) + strings.IdField(gid, emoji.Id) +
            (by is null ? "" : strings.CreatedByField(gid, by.Mention, by.Id)), Mewdeko.OkColor, EmojiUrl(emoji));
    }

    /// <summary>
    ///     Handles a custom emoji being renamed.
    /// </summary>
    /// <param name="before">The emoji before the edit.</param>
    /// <param name="after">The emoji after the edit.</param>
    private async Task OnEmojiUpdated(GuildEmoji before, GuildEmoji after)
    {
        if (before.Name == after.Name ||
            !GuildLogSettings.TryGetValue(after.GuildId, out var logSetting) || logSetting.EmojiUpdatedId is null or 0)
            return;

        var guild = client.GetGuild(after.GuildId);
        var gid = after.GuildId;
        var by = (await GetAuditLogsSafeAsync(guild, ActionType.EmojiUpdated)).FirstOrDefault()?.User;
        await SendEmojiLogAsync(guild, logSetting.EmojiUpdatedId, strings.LogEmojiUpdated(gid),
            strings.LogNameChangeField(gid, before.Name, after.Name) + strings.IdField(gid, after.Id) +
            (by is null ? "" : strings.UpdatedByField(gid, by.Mention, by.Id)), Mewdeko.OkColor, EmojiUrl(after));
    }

    /// <summary>
    ///     Handles a custom emoji being deleted.
    /// </summary>
    /// <param name="emoji">The deleted emoji.</param>
    private async Task OnEmojiDeleted(GuildEmoji emoji)
    {
        if (!GuildLogSettings.TryGetValue(emoji.GuildId, out var logSetting) || logSetting.EmojiDeletedId is null or 0)
            return;

        var guild = client.GetGuild(emoji.GuildId);
        var gid = emoji.GuildId;
        var by = (await GetAuditLogsSafeAsync(guild, ActionType.EmojiDeleted)).FirstOrDefault()?.User;
        await SendEmojiLogAsync(guild, logSetting.EmojiDeletedId, strings.LogEmojiDeleted(gid),
            strings.NameField(gid, emoji.Name) + strings.IdField(gid, emoji.Id) +
            (by is null ? "" : strings.DeletedByField(gid, by.Mention, by.Id)), Mewdeko.ErrorColor, null);
    }

    /// <summary>
    ///     Handles a custom sticker being created.
    /// </summary>
    /// <param name="sticker">The created sticker.</param>
    private async Task OnStickerCreated(GuildSticker sticker)
    {
        if (!GuildLogSettings.TryGetValue(sticker.GuildId, out var logSetting) ||
            logSetting.StickerCreatedId is null or 0)
            return;

        var gid = sticker.GuildId;
        var by = sticker.Creator;
        await SendEmojiLogAsync(client.GetGuild(gid), logSetting.StickerCreatedId, strings.LogStickerCreated(gid),
            strings.NameField(gid, sticker.Name) + strings.IdField(gid, sticker.Id) +
            (by is null ? "" : strings.CreatedByField(gid, by.Mention, by.Id)), Mewdeko.OkColor,
            sticker.GetStickerUrl());
    }

    /// <summary>
    ///     Handles a custom sticker being edited.
    /// </summary>
    /// <param name="before">The sticker before the edit.</param>
    /// <param name="after">The sticker after the edit.</param>
    private async Task OnStickerUpdated(GuildSticker before, GuildSticker after)
    {
        var oldTags = before.Tags ?? [];
        var newTags = after.Tags ?? [];
        if (before.Name == after.Name && before.Description == after.Description && oldTags.SequenceEqual(newTags))
            return;

        if (!GuildLogSettings.TryGetValue(after.GuildId, out var logSetting) ||
            logSetting.StickerUpdatedId is null or 0)
            return;

        var gid = after.GuildId;
        var text = strings.LogNameChangeField(gid, before.Name, after.Name) + strings.IdField(gid, after.Id);
        if (before.Description != after.Description)
            text += $"`Description:` {before.Description ?? "N/A"} to {after.Description ?? "N/A"}\n";
        if (!oldTags.SequenceEqual(newTags))
            text += $"`Tags:` {string.Join(", ", oldTags)} to {string.Join(", ", newTags)}\n";

        await SendEmojiLogAsync(client.GetGuild(gid), logSetting.StickerUpdatedId, strings.LogStickerUpdated(gid), text,
            Mewdeko.OkColor, after.GetStickerUrl());
    }

    /// <summary>
    ///     Handles a custom sticker being deleted.
    /// </summary>
    /// <param name="sticker">The deleted sticker.</param>
    private async Task OnStickerDeleted(GuildSticker sticker)
    {
        if (!GuildLogSettings.TryGetValue(sticker.GuildId, out var logSetting) ||
            logSetting.StickerDeletedId is null or 0)
            return;

        var gid = sticker.GuildId;
        var guild = client.GetGuild(gid);
        var by = (await GetAuditLogsSafeAsync(guild, ActionType.StickerDeleted)).FirstOrDefault()?.User;
        await SendEmojiLogAsync(guild, logSetting.StickerDeletedId, strings.LogStickerDeleted(gid),
            strings.NameField(gid, sticker.Name) + strings.IdField(gid, sticker.Id) +
            (by is null ? "" : strings.DeletedByField(gid, by.Mention, by.Id)), Mewdeko.ErrorColor, null);
    }

    /// <summary>
    ///     Handles the webhooks of a channel changing.
    /// </summary>
    /// <param name="channel">The channel whose webhooks changed.</param>
    private async Task OnWebhooksUpdated(Channel channel)
    {
        if (channel.Guild is not { } guild || !GuildLogSettings.TryGetValue(guild.Id, out var logSetting) ||
            logSetting.WebhookChangedId is null or 0 ||
            guild.GetTextChannel(logSetting.WebhookChangedId.Value) is not { } logChannel)
            return;

        await Task.Delay(500);
        var gid = guild.Id;
        var entry = (await GetAuditLogsSafeAsync(guild, ActionType.WebhookUpdated)).FirstOrDefault();
        var text = strings.LogChannelField(gid, channel.Id, channel.Name);
        if (entry is not null)
            text += strings.UpdatedByField(gid, entry.User.Mention, entry.User.Id);

        await SendLogAsync(logChannel, LogEmbed(strings.LogWebhookChanged(gid), text, Mewdeko.OkColor));
    }

    /// <summary>
    ///     Handles the permission overwrites of a channel changing.
    /// </summary>
    /// <param name="before">The channel before the update.</param>
    /// <param name="after">The channel after the update.</param>
    private async Task OnChannelPermissionsChanged(Channel before, Channel after)
    {
        if (after.Guild is not { } guild || IsChannelIgnored(guild.Id, after.Id) ||
            !GuildLogSettings.TryGetValue(guild.Id, out var logSetting) ||
            logSetting.ChannelPermissionsUpdatedId is null or 0 ||
            guild.GetTextChannel(logSetting.ChannelPermissionsUpdatedId.Value) is not { } logChannel)
            return;

        var oldOverwrites = (before.PermissionOverwrites ?? []).ToDictionary(x => x.Id);
        var newOverwrites = (after.PermissionOverwrites ?? []).ToDictionary(x => x.Id);
        var gid = guild.Id;

        string Describe(ulong id, PermissionOverwriteType type)
        {
            return type == PermissionOverwriteType.Role ? $"<@&{id}>" : $"<@{id}>";
        }

        var changes = new List<string>();
        foreach (var (id, added) in newOverwrites.Where(x => !oldOverwrites.ContainsKey(x.Key)))
            changes.Add(strings.LogPermissionOverwriteAdded(gid, Describe(id, added.Type)));
        foreach (var (id, removed) in oldOverwrites.Where(x => !newOverwrites.ContainsKey(x.Key)))
            changes.Add(strings.LogPermissionOverwriteRemoved(gid, Describe(id, removed.Type)));
        foreach (var (id, current) in newOverwrites.Where(x => oldOverwrites.ContainsKey(x.Key)))
        {
            var old = oldOverwrites[id];
            if (old.Allow.RawValue != current.Allow.RawValue || old.Deny.RawValue != current.Deny.RawValue)
                changes.Add(strings.LogPermissionOverwriteChanged(gid, Describe(id, current.Type)));
        }

        if (changes.Count == 0)
            return;

        await Task.Delay(500);
        var entry = (await GetAuditLogsSafeAsync(guild, ActionType.OverwriteUpdated)).FirstOrDefault();
        var text = strings.LogChannelField(gid, after.Id, after.Name) + string.Join("\n", changes) + "\n";
        if (entry is not null)
            text += strings.UpdatedByField(gid, entry.User.Mention, entry.User.Id);

        await SendLogAsync(logChannel, LogEmbed(strings.LogChannelPermissionsChanged(gid), text, Mewdeko.OkColor));
    }

    /// <summary>
    ///     Handles a member being timed out or having a timeout removed.
    /// </summary>
    /// <param name="before">The member before the update.</param>
    /// <param name="after">The member after the update.</param>
    private async Task OnUserTimeoutChanged(MemberSnapshot? before, SocketGuildMember after)
    {
        if (before is null || !GuildLogSettings.TryGetValue(after.Guild.Id, out var logSetting))
            return;

        var now = DateTimeOffset.UtcNow;
        var oldUntil = before.TimedOutUntil is { } o && o > now ? o : (DateTimeOffset?)null;
        var newUntil = after.CommunicationDisabledUntil is { } n && n > now ? n : (DateTimeOffset?)null;
        if (oldUntil == newUntil || IgnoresBot(after.Guild.Id, after.User.IsBot))
            return;

        var gid = after.Guild.Id;
        var logId = newUntil is not null ? logSetting.UserTimedOutId : logSetting.UserTimeoutRemovedId;
        if (logId is null or 0 || after.Guild.GetTextChannel(logId.Value) is not { } channel)
            return;

        await Task.Delay(500);
        var by = (await GetAuditLogsSafeAsync(after.Guild, ActionType.MemberUpdated)).FirstOrDefault()?.User;
        var text = strings.UserField(gid, after.Mention, after.Id);
        if (newUntil is not null)
            text += strings.LogTimeoutUntilField(gid, $"<t:{newUntil.Value.ToUnixTimeSeconds()}:f>");
        if (by is not null)
            text += strings.UpdatedByField(gid, by.Mention, by.Id);

        await SendLogAsync(channel,
            newUntil is not null
                ? LogEmbed(strings.LogUserTimedOut(gid), text, Mewdeko.ErrorColor)
                : LogEmbed(strings.LogUserTimeoutRemoved(gid), text, Mewdeko.OkColor));
    }

    /// <summary>
    ///     Handles a message containing a Fluxer invite link.
    /// </summary>
    /// <param name="message">The received message.</param>
    private async Task OnInviteLinkPosted(SocketMessage message)
    {
        if (message.Channel is not { Guild: { } guild } guildChannel || message.Author is null ||
            IgnoresBot(guild.Id, message.Author.IsBot) || message.Content.Length == 0)
            return;

        var matches = InviteRegex.Matches(message.Content);
        if (matches.Count == 0)
            return;

        if (!GuildLogSettings.TryGetValue(guild.Id, out var logSetting) ||
            logSetting.InviteLinkPostedId is null or 0 ||
            IsChannelIgnored(guild.Id, guildChannel.Id) ||
            guild.GetTextChannel(logSetting.InviteLinkPostedId.Value) is not { } channel)
            return;

        var gid = guild.Id;
        var invites = string.Join(", ", matches.Select(m => $"`{m.Value}`").Distinct());
        var eb = LogEmbed(strings.LogInviteLinkPosted(gid),
            strings.MessageAuthorField(gid, message.Author.Mention, message.Author.Id) +
            strings.MessageChannelField(gid, $"<#{guildChannel.Id}>", guildChannel.Id) +
            strings.LogInvitesField(gid, invites) + message.GetJumpUrl(), Mewdeko.ErrorColor);

        await SendLogAsync(channel, eb);
    }

    /// <summary>
    ///     Toggles whether a channel is ignored for logging
    /// </summary>
    public async Task<IgnoreResult> LogIgnore(ulong guildId, ulong channelId)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            // Get or create the ignored channels set for this guild
            var ignoredChannels = ignoredChannelsCache.GetOrAdd(guildId, _ => new HashSet<ulong>());

            // Check if channel is already ignored
            var existing = await db.GetTable<LogIgnoredChannel>()
                .FirstOrDefaultAsync(lic => lic.GuildId == guildId && lic.ChannelId == channelId);

            if (existing != null)
            {
                // Remove from database
                await db.DeleteAsync(existing);

                // Remove from cache
                ignoredChannels.Remove(channelId);

                return IgnoreResult.Removed;
            }
            else
            {
                // Add to database
                await db.InsertAsync(new LogIgnoredChannel
                {
                    GuildId = guildId, ChannelId = channelId, DateAdded = DateTime.UtcNow
                });

                // Add to cache
                ignoredChannels.Add(channelId);

                return IgnoreResult.Added;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error toggling ignore status for channel {ChannelId} in guild {GuildId}",
                channelId, guildId);
            return IgnoreResult.Error;
        }
    }

    /// <summary>
    ///     Gets all ignored channels for a guild
    /// </summary>
    public async Task<HashSet<ulong>> GetIgnoredChannels(ulong guildId)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            var ignoredChannels = await db.GetTable<LogIgnoredChannel>()
                .Where(lic => lic.GuildId == guildId)
                .Select(lic => lic.ChannelId)
                .ToListAsync();

            return ignoredChannels.ToHashSet();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting ignored channels for guild {GuildId}", guildId);
            return new HashSet<ulong>();
        }
    }

    /// <summary>
    ///     Checks if a channel is ignored for logging
    /// </summary>
    /// <param name="guildId">The guild identifier.</param>
    /// <param name="channelId">The channelid identifier.</param>
    public bool IsChannelIgnored(ulong guildId, ulong channelId)
    {
        if (ignoredChannelsCache.TryGetValue(guildId, out var ignoredChannels))
            return ignoredChannels.Contains(channelId);

        return false;
    }

    /// <summary>
    ///     Drops the cached log settings and ignored channels of a guild and reloads them from the database.
    /// </summary>
    /// <param name="guildId">The guild identifier.</param>
    public async Task ReloadGuildAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        var settings = await db.LoggingV2.FirstOrDefaultAsync(x => x.GuildId == guildId).ConfigureAwait(false);
        switch (settings)
        {
            case null:
                GuildLogSettings?.TryRemove(guildId, out _);
                break;
            default:
                GuildLogSettings ??= new ConcurrentDictionary<ulong, LoggingV2>();
                GuildLogSettings[guildId] = settings;
                break;
        }

        var ignored = await db.GetTable<LogIgnoredChannel>()
            .Where(x => x.GuildId == guildId)
            .Select(x => x.ChannelId)
            .ToListAsync().ConfigureAwait(false);

        switch (ignored.Count)
        {
            case 0:
                ignoredChannelsCache.TryRemove(guildId, out _);
                break;
            default:
                ignoredChannelsCache[guildId] = ignored.ToHashSet();
                break;
        }
    }

    /// <summary>
    ///     Updates the ignored channels for a guild and fires events
    /// </summary>
    /// <param name="guildId">The guild identifier.</param>
    /// <param name="channels">The channels identifier.</param>
    public async Task UpdateIgnoredChannelsAsync(ulong guildId, HashSet<ulong> channels)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            // Remove existing entries
            await db.GetTable<LogIgnoredChannel>()
                .Where(lic => lic.GuildId == guildId)
                .DeleteAsync();

            // Add new entries
            foreach (var channelId in channels)
            {
                await db.InsertAsync(new LogIgnoredChannel
                {
                    GuildId = guildId, ChannelId = channelId, DateAdded = DateTime.UtcNow
                }).ConfigureAwait(false);
            }

            // Update cache
            ignoredChannelsCache[guildId] = new HashSet<ulong>(channels);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating ignored channels for guild {GuildId}", guildId);
        }
    }

    /// <summary>
    ///     Handles the event when an invite is created.
    /// </summary>
    /// <param name="invite">The invite that was created.</param>
    private async Task OnInviteCreated(Invite invite)
    {
        if (invite.Guild == null) return;

        if (GuildLogSettings.TryGetValue(invite.Guild.Id, out var logSetting))
        {
            if (logSetting.InviteCreatedId is null or 0)
                return;

            var channel = client.GetGuild(invite.Guild.Id)?.GetTextChannel(logSetting.InviteCreatedId.Value);
            if (channel is null)
                return;

            var eb = LogEmbed(strings.InviteCreated(invite.Guild.Id),
                $"`Invite Code:` {invite.Code}\n" +
                $"`Channel:` <#{invite.Channel.Id}> ({invite.Channel.Name}) | {invite.Channel.Id}\n" +
                $"`Created By:` {invite.Inviter?.Mention ?? "Unknown"} | {invite.Inviter?.Id.ToString() ?? "N/A"}\n" +
                $"`Max Uses:` {(invite.MaxUses == 0 ? "Unlimited" : invite.MaxUses.ToString())}\n" +
                $"`Max Age:` {(invite.MaxAge == 0 ? "Never" : TimeSpan.FromSeconds(invite.MaxAge).ToString(@"dd\.hh\:mm\:ss"))}\n" +
                $"`Temporary:` {invite.IsTemporary}\n" +
                $"`Created At:` {invite.CreatedAt:dd/MM/yyyy HH:mm:ss}\n" +
                strings.LogInviteLink(invite.Guild.Id, $"{client.Config.InviteUrl}/{invite.Code}"), Mewdeko.OkColor);

            await SendLogAsync(channel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when an invite is deleted.
    /// </summary>
    /// <param name="channel">The channel where the invite was deleted.</param>
    /// <param name="inviteCode">The code of the deleted invite.</param>
    private async Task OnInviteDeleted(Channel channel, string inviteCode)
    {
        if (GuildLogSettings.TryGetValue(channel.Guild.Id, out var logSetting))
        {
            if (logSetting.InviteDeletedId is null or 0)
                return;

            var logChannel = channel.Guild.GetTextChannel(logSetting.InviteDeletedId.Value);
            if (logChannel is null)
                return;

            var eb = LogEmbed(strings.InviteDeletedTitle(channel.Guild.Id),
                $"`Invite Code:` {inviteCode}\n" +
                $"`Channel:` <#{channel.Id}> ({channel.Name}) | {channel.Id}\n" +
                $"`Deleted At:` {DateTime.UtcNow:dd/MM/yyyy HH:mm:ss}", Mewdeko.ErrorColor);

            await SendLogAsync(logChannel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when messages are bulk deleted.
    /// </summary>
    /// <param name="messages">The collection of deleted messages.</param>
    /// <param name="guildChannel">The channel where messages were deleted.</param>
    private async Task OnMessagesBulkDeleted(IReadOnlyCollection<Cacheable<Message>> messages,
        Channel guildChannel)
    {
        if (guildChannel.Guild is null) return;

        // Check if channel is ignored
        if (IsChannelIgnored(guildChannel.Guild.Id, guildChannel.Id))
            return;

        if (GuildLogSettings.TryGetValue(guildChannel.Guild.Id, out var logSetting))
        {
            if (logSetting.MessagesBulkDeletedId is null or 0)
                return;

            var logChannel = guildChannel.Guild.GetTextChannel(logSetting.MessagesBulkDeletedId.Value);
            if (logChannel is null)
                return;

            var guildId = guildChannel.Guild.Id;
            var cachedMessages = messages.Where(m => m.HasValue).ToList();
            var uncachedCount = messages.Count - cachedMessages.Count;

            var eb = LogEmbed(strings.MessagesBulkDeleted(guildId),
                $"`Messages Deleted:` {messages.Count}\n" +
                $"`Channel:` <#{guildChannel.Id}> ({guildChannel.Name}) | {guildChannel.Id}\n" +
                $"`Deleted At:` {DateTime.UtcNow:dd/MM/yyyy HH:mm:ss}", Mewdeko.ErrorColor);

            AddSection(eb, strings.LogSectionMessageInformation(guildId),
                $"`Cached Messages:` {cachedMessages.Count}\n" +
                $"`Uncached Messages:` {uncachedCount}\n" +
                $"`Oldest Cached Message:` {(cachedMessages.Any() ? cachedMessages.Min(m => m.Value.Timestamp).ToString("dd/MM/yyyy HH:mm:ss") : "N/A")}\n" +
                $"`Newest Cached Message:` {(cachedMessages.Any() ? cachedMessages.Max(m => m.Value.Timestamp).ToString("dd/MM/yyyy HH:mm:ss") : "N/A")}");

            await SendLogAsync(logChannel, eb);
        }
    }

    /// <summary>
    ///     Handles the event when a reaction is added to a message.
    /// </summary>
    /// <param name="message">The cached message.</param>
    /// <param name="guildChannel">The channel containing the message.</param>
    /// <param name="reaction">The reaction that was added.</param>
    private Task OnReactionAdded(Cacheable<Message> message, Channel guildChannel,
        SocketReaction reaction)
    {
        if (guildChannel.Guild is null) return Task.CompletedTask;
        if (IgnoresBot(guildChannel.Guild.Id, reaction.Member?.IsBot == true)) return Task.CompletedTask;

        // Check if channel is ignored
        if (IsChannelIgnored(guildChannel.Guild.Id, guildChannel.Id))
            return Task.CompletedTask;

        AddReactionToBatch(message.Id, guildChannel.Id, guildChannel.Guild.Id, guildChannel.Name,
            reaction.UserId, Emoji.From(reaction.Emoji), true);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Handles the event when a reaction is removed from a message.
    /// </summary>
    /// <param name="message">The cached message.</param>
    /// <param name="guildChannel">The channel containing the message.</param>
    /// <param name="reaction">The reaction that was removed.</param>
    private Task OnReactionRemoved(Cacheable<Message> message, Channel guildChannel,
        SocketReaction reaction)
    {
        if (guildChannel.Guild is null) return Task.CompletedTask;
        if (IgnoresBot(guildChannel.Guild.Id, reaction.Member?.IsBot == true)) return Task.CompletedTask;

        // Check if channel is ignored
        if (IsChannelIgnored(guildChannel.Guild.Id, guildChannel.Id))
            return Task.CompletedTask;

        AddReactionToBatch(message.Id, guildChannel.Id, guildChannel.Guild.Id, guildChannel.Name,
            reaction.UserId, Emoji.From(reaction.Emoji), false);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Adds a reaction event to the batching system.
    /// </summary>
    private void AddReactionToBatch(ulong messageId, ulong channelId, ulong guildId, string channelName, ulong userId,
        Emoji emote, bool isAdded)
    {
        if (userId == 0) return; // Invalid user

        var batch = reactionBatches.GetOrAdd(messageId, _ => new MessageReactionBatch
        {
            MessageId = messageId, ChannelId = channelId, GuildId = guildId, ChannelName = channelName
        });

        var emoteKey = emote.ToString() ?? "Unknown";

        lock (batch)
        {
            if (isAdded)
            {
                if (!batch.ReactionsAdded.ContainsKey(emoteKey))
                    batch.ReactionsAdded[emoteKey] = new List<ulong>();
                if (!batch.ReactionsAdded[emoteKey].Contains(userId))
                    batch.ReactionsAdded[emoteKey].Add(userId);
            }
            else
            {
                if (!batch.ReactionsRemoved.ContainsKey(emoteKey))
                    batch.ReactionsRemoved[emoteKey] = new List<ulong>();
                if (!batch.ReactionsRemoved[emoteKey].Contains(userId))
                    batch.ReactionsRemoved[emoteKey].Add(userId);
            }
        }
    }

    /// <summary>
    ///     Timer callback to process batched reaction events.
    /// </summary>
    private void ProcessReactionBatches(object? state)
    {
        var batchesToProcess = new List<MessageReactionBatch>();

        // Extract batches older than 5 seconds
        var cutoffTime = DateTime.UtcNow.AddSeconds(-5);
        var keysToRemove = new List<ulong>();

        foreach (var kvp in reactionBatches)
        {
            if (kvp.Value.FirstEventTime <= cutoffTime && kvp.Value.HasChanges)
            {
                batchesToProcess.Add(kvp.Value);
                keysToRemove.Add(kvp.Key);
            }
        }

        // Remove processed batches
        foreach (var key in keysToRemove)
        {
            reactionBatches.TryRemove(key, out _);
        }

        // Process each batch
        foreach (var batch in batchesToProcess)
        {
            _ = ProcessReactionBatch(batch);
        }
    }

    /// <summary>
    ///     Processes a single reaction batch and sends the log message.
    /// </summary>
    private async Task ProcessReactionBatch(MessageReactionBatch batch)
    {
        if (!GuildLogSettings.TryGetValue(batch.GuildId, out var logSetting))
            return;

        if (logSetting.ReactionEventsId is null or 0)
            return;

        var logChannel = client.GetGuild(batch.GuildId)?.GetTextChannel(logSetting.ReactionEventsId.Value);
        if (logChannel is null)
            return;

        var guildId = batch.GuildId;
        var summary =
            $"`Message:` <#{batch.ChannelId}>: [Jump to Message]({FluxerContext.WebUrl}/channels/{batch.GuildId}/{batch.ChannelId}/{batch.MessageId})\n" +
            $"`Channel:` <#{batch.ChannelId}> ({batch.ChannelName}) | {batch.ChannelId}\n" +
            $"`Total Added:` {batch.TotalAdded}\n" +
            $"`Total Removed:` {batch.TotalRemoved}";

        var eb = LogEmbed(strings.LogReactionEventsTitle(guildId), summary, Mewdeko.OkColor);

        if (batch.ReactionsAdded.Any())
        {
            var addedDetails = string.Join("\n", batch.ReactionsAdded.Select(kvp =>
                $"`{kvp.Key}`: {kvp.Value.Count} users ({string.Join(", ", kvp.Value.Select(u => $"<@{u}>"))})"));
            AddSection(eb, strings.LogSectionReactionsAdded(guildId), addedDetails);
        }

        if (batch.ReactionsRemoved.Any())
        {
            var removedDetails = string.Join("\n", batch.ReactionsRemoved.Select(kvp =>
                $"`{kvp.Key}`: {kvp.Value.Count} users ({string.Join(", ", kvp.Value.Select(u => $"<@{u}>"))})"));
            AddSection(eb, strings.LogSectionReactionsRemoved(guildId), removedDetails);
        }

        try
        {
            await SendLogAsync(logChannel, eb);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending reaction batch log for message {MessageId} in guild {GuildId}",
                batch.MessageId, batch.GuildId);
        }
    }

    /// <summary>
    ///     Represents a batched collection of reaction events for a specific message
    /// </summary>
    public class MessageReactionBatch
    {
        /// <summary>
        ///     The ID of the message that had reaction changes
        /// </summary>
        public ulong MessageId { get; set; }

        /// <summary>
        ///     The ID of the channel containing the message
        /// </summary>
        public ulong ChannelId { get; set; }

        /// <summary>
        ///     The ID of the guild containing the message
        /// </summary>
        public ulong GuildId { get; set; }

        /// <summary>
        ///     The name of the channel containing the message
        /// </summary>
        public string ChannelName { get; set; } = "";

        /// <summary>
        ///     When the first reaction event in this batch occurred
        /// </summary>
        public DateTime FirstEventTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        ///     Track reactions added by emote -> list of users who added them
        /// </summary>
        public Dictionary<string, List<ulong>> ReactionsAdded { get; set; } = new();

        /// <summary>
        ///     Track reactions removed by emote -> list of users who removed them
        /// </summary>
        public Dictionary<string, List<ulong>> ReactionsRemoved { get; set; } = new();

        /// <summary>
        ///     Gets total count of reaction additions in this batch
        /// </summary>
        public int TotalAdded
        {
            get
            {
                return ReactionsAdded.Values.Sum(users => users.Count);
            }
        }

        /// <summary>
        ///     Gets total count of reaction removals in this batch
        /// </summary>
        public int TotalRemoved
        {
            get
            {
                return ReactionsRemoved.Values.Sum(users => users.Count);
            }
        }

        /// <summary>
        ///     Gets whether this batch has any reaction changes
        /// </summary>
        public bool HasChanges
        {
            get
            {
                return TotalAdded > 0 || TotalRemoved > 0;
            }
        }
    }
}