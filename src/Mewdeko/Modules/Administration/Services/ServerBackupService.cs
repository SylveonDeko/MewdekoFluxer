using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DataModel;
using Humanizer;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Modules.Administration.Common;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.Administration.Services;

/// <summary>
///     Creates, stores and restores backups of a community's settings, roles, channels, emojis and messages.
/// </summary>
public class ServerBackupService : INService
{
    /// <summary>
    ///     The most backups one user can keep.
    /// </summary>
    public const int MaxBackupsPerUser = 5;

    /// <summary>
    ///     The most messages saved from one channel.
    /// </summary>
    public const int MaxMessagesPerChannel = 100;

    /// <summary>
    ///     The most channels messages can be saved from.
    /// </summary>
    public const int MaxMessageChannels = 10;

    /// <summary>
    ///     How long a user has to wait between creating backups.
    /// </summary>
    public static readonly TimeSpan CreateCooldown = TimeSpan.FromMinutes(30);

    private const int MaxCompressedBytes = 25 * 1024 * 1024;
    private const int RequestDelayMs = 300;
    private const int EmojiFailureLimit = 3;

    private static readonly Regex SnowflakePattern = new(@"\d{15,21}", RegexOptions.Compiled);

    private static readonly Regex WebhookName = new("discord|fluxer|clyde", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<ServerBackupService> logger;
    private readonly ConcurrentDictionary<ulong, byte> running = new();
    private readonly ConcurrentDictionary<ulong, BackupRestoreJob> jobs = new();
    private readonly GeneratedBotStrings strings;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ServerBackupService" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="httpFactory">The HTTP client factory, for downloading images.</param>
    /// <param name="strings">The generated bot strings.</param>
    /// <param name="logger">The logger instance.</param>
    public ServerBackupService(FluxerClient client, IDataConnectionFactory dbFactory,
        IHttpClientFactory httpFactory, GeneratedBotStrings strings, ILogger<ServerBackupService> logger)
    {
        this.client = client;
        this.dbFactory = dbFactory;
        this.httpFactory = httpFactory;
        this.strings = strings;
        this.logger = logger;
    }

    /// <summary>
    ///     Checks whether a member owns the community or has the administrator permission.
    /// </summary>
    /// <param name="guild">The community.</param>
    /// <param name="user">The member.</param>
    /// <returns>True if the member is the owner or an administrator.</returns>
    public static bool IsOwnerOrAdmin(SocketGuild guild, SocketGuildMember user)
    {
        return guild.OwnerId == user.Id || user.GuildPermissions.Administrator;
    }

    /// <summary>
    ///     Lists a user's backups without their data.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The backups, newest first.</returns>
    public async Task<List<ServerBackup>> ListAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.ServerBackups
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.DateAdded)
            .Select(x => new ServerBackup
            {
                Id = x.Id,
                UserId = x.UserId,
                SourceGuildId = x.SourceGuildId,
                SourceGuildName = x.SourceGuildName,
                SizeBytes = x.SizeBytes,
                RoleCount = x.RoleCount,
                ChannelCount = x.ChannelCount,
                EmojiCount = x.EmojiCount,
                MessageCount = x.MessageCount,
                DateAdded = x.DateAdded
            })
            .ToListAsync();
    }

    /// <summary>
    ///     Gets one of a user's backups including its data.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="id">The backup ID.</param>
    /// <returns>The backup, or null if it does not exist or belongs to someone else.</returns>
    public async Task<ServerBackup?> GetAsync(ulong userId, int id)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.ServerBackups.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
    }

    /// <summary>
    ///     Deletes one of a user's backups.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="id">The backup ID.</param>
    /// <returns>True if a backup was deleted.</returns>
    public async Task<bool> DeleteAsync(ulong userId, int id)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.ServerBackups.Where(x => x.Id == id && x.UserId == userId).DeleteAsync() > 0;
    }

    /// <summary>
    ///     Reads the data out of a backup.
    /// </summary>
    /// <param name="backup">The backup row.</param>
    /// <returns>The data, or null if it could not be read.</returns>
    public static ServerBackupData? ReadData(ServerBackup backup)
    {
        return ReadData(backup.Data);
    }

    /// <summary>
    ///     Reads backup data from its compressed form.
    /// </summary>
    /// <param name="compressed">The gzip compressed JSON.</param>
    /// <returns>The data, or null if it could not be read.</returns>
    public static ServerBackupData? ReadData(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<ServerBackupData>(gzip);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Takes a backup of a community.
    /// </summary>
    /// <param name="guild">The community to back up.</param>
    /// <param name="userId">The user the backup belongs to.</param>
    /// <param name="messageCount">How many recent messages to save from each chosen channel.</param>
    /// <param name="messageChannelIds">The channels to save messages from.</param>
    /// <returns>The error, if any, the new backup, and how long to wait when on cooldown.</returns>
    public async Task<(BackupError Error, ServerBackup? Backup, TimeSpan Wait)> CreateAsync(SocketGuild guild,
        ulong userId, int messageCount, IReadOnlyCollection<ulong> messageChannelIds)
    {
        var existing = await ListAsync(userId);
        if (existing.Count >= MaxBackupsPerUser)
            return (BackupError.LimitReached, null, TimeSpan.Zero);

        var last = existing.Count == 0 ? (DateTime?)null : existing.Max(x => x.DateAdded);
        if (last is { } lastAt && DateTime.UtcNow - lastAt < CreateCooldown)
            return (BackupError.Cooldown, null, CreateCooldown - (DateTime.UtcNow - lastAt));

        var data = new ServerBackupData();
        using var http = httpFactory.CreateClient();

        data.Settings = new BackupSettings
        {
            Name = guild.Name,
            Icon = await DownloadAsync(http, guild.IconUrl),
            VerificationLevel = (int)guild.VerificationLevel,
            DefaultMessageNotifications = (int)guild.DefaultMessageNotifications,
            ExplicitContentFilter = (int)guild.ExplicitContentFilter,
            AfkTimeout = guild.AfkTimeout,
            AfkChannelId = guild.AfkChannelId,
            SystemChannelId = guild.SystemChannelId,
            SystemChannelFlags = (int)guild.SystemChannelFlags
        };

        foreach (var role in guild.Roles.Values.OrderBy(x => x.Position))
        {
            data.Roles.Add(CaptureRole(guild, role,
                role.Id == guild.Id ? null : await DownloadAsync(http, role.GetIconUrl())));
        }

        var channels = (await guild.GetChannelsAsync()).Where(x => x.ThreadMetadata is null)
            .OrderBy(x => x.Position ?? 0).ToList();
        foreach (var channel in channels)
        {
            if (CaptureChannel(channel) is { } entry)
                data.Channels.Add(entry);
        }

        var gate = new SemaphoreSlim(6);
        var emojiTasks = guild.Emotes.Select(async emote =>
        {
            await gate.WaitAsync();
            try
            {
                var image = await DownloadAsync(http, emote.GetEmojiUrl());
                return image is null
                    ? null
                    : new BackupEmoji
                    {
                        Name = emote.Name, Animated = emote.IsAnimated, Image = image
                    };
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        data.Emojis = (await Task.WhenAll(emojiTasks)).Where(x => x is not null).Select(x => x!).ToList();

        if (messageCount > 0)
        {
            var wanted = messageChannelIds.Take(MaxMessageChannels).ToHashSet();
            foreach (var text in channels.Where(x => x.Type == ChannelType.GuildText && wanted.Contains(x.Id)))
            {
                try
                {
                    var fetched = await text.GetMessagesAsync(Math.Min(messageCount, MaxMessagesPerChannel));
                    var saved = new BackupChannelMessages
                    {
                        ChannelId = text.Id
                    };
                    foreach (var message in fetched.OrderBy(x => x.CreatedAt))
                    {
                        if (message.Type is not (MessageType.Default or MessageType.Reply))
                            continue;

                        var content = message.Content ?? "";
                        foreach (var attachment in message.Attachments ?? [])
                        {
                            if (attachment.Url is { } url)
                                content += $"\n{url}";
                        }

                        content = content.Trim();
                        if (content.Length == 0)
                            continue;

                        saved.Messages.Add(new BackupMessage
                        {
                            Author = guild.GetUser(message.Author.Id)?.DisplayName ??
                                     (string.IsNullOrWhiteSpace(message.Author.DisplayName)
                                         ? message.Author.Username
                                         : message.Author.DisplayName),
                            AvatarUrl = message.Author.GetAvatarOrDefaultUrl(),
                            Content = content.Length > 2000 ? content[..2000] : content
                        });
                    }

                    if (saved.Messages.Count > 0)
                        data.Messages.Add(saved);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not read messages from {Channel} for a backup", text.Id);
                }
            }
        }

        var compressed = await CompressAsync(data);

        if (compressed.Length > MaxCompressedBytes)
            return (BackupError.TooLarge, null, TimeSpan.Zero);

        var backup = new ServerBackup
        {
            UserId = userId,
            SourceGuildId = guild.Id,
            SourceGuildName = guild.Name,
            Data = compressed,
            SizeBytes = compressed.Length,
            RoleCount = data.Roles.Count,
            ChannelCount = data.Channels.Count,
            EmojiCount = data.Emojis.Count,
            MessageCount = data.Messages.Sum(x => x.Messages.Count),
            DateAdded = DateTime.UtcNow
        };

        await using var db = await dbFactory.CreateConnectionAsync();
        backup.Id = await db.InsertWithInt32IdentityAsync(backup);
        return (BackupError.None, backup, TimeSpan.Zero);
    }

    /// <summary>
    ///     Compresses backup data into the form stored in the database.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <returns>The gzip compressed JSON.</returns>
    public static async Task<byte[]> CompressAsync(ServerBackupData data)
    {
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
            await JsonSerializer.SerializeAsync(gzip, data);
        return output.ToArray();
    }

    /// <summary>
    ///     Reads a role into its backup form.
    /// </summary>
    /// <param name="guild">The community the role belongs to.</param>
    /// <param name="role">The role.</param>
    /// <param name="icon">The role icon image, if it should be kept.</param>
    /// <returns>The role.</returns>
    public static BackupRole CaptureRole(SocketGuild guild, Role role, byte[]? icon)
    {
        return new BackupRole
        {
            Id = role.Id,
            IsEveryone = role.Id == guild.Id,
            Name = role.Name,
            Color = (uint)role.Color,
            Permissions = (ulong)role.Permissions.RawValue,
            Hoist = role.IsHoisted,
            Mentionable = role.IsMentionable,
            Position = role.Position,
            Icon = icon,
            UnicodeEmoji = role.UnicodeEmoji
        };
    }

    /// <summary>
    ///     Reads a category or channel into its backup form.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The channel, or null if it is not a kind that is saved.</returns>
    public static BackupChannel? CaptureChannel(Channel channel)
    {
        var type = ChannelKind(channel);
        if (type is null)
            return null;

        var entry = new BackupChannel
        {
            Id = channel.Id,
            Type = type,
            Name = channel.Name ?? "",
            Position = channel.Position ?? 0,
            ParentId = channel.ParentId,
            Overwrites = (channel.PermissionOverwrites ?? []).Select(x => new BackupOverwrite
            {
                IsRole = x.Type == PermissionOverwriteType.Role,
                TargetId = x.Id,
                Allow = (ulong)x.Allow.RawValue,
                Deny = (ulong)x.Deny.RawValue
            }).ToList()
        };

        switch (type)
        {
            case "voice" or "stage":
                entry.Bitrate = channel.Bitrate ?? 0;
                entry.UserLimit = channel.UserLimit;
                break;
            case "text":
                entry.Topic = channel.Topic;
                entry.SlowMode = channel.RateLimitPerUser ?? 0;
                entry.Nsfw = channel.IsNsfw;
                break;
            case "forum" or "media":
                entry.Topic = channel.Topic;
                entry.Nsfw = channel.IsNsfw;
                break;
        }

        return entry;
    }

    /// <summary>
    ///     Creates a category or channel from its backup form. Stage channels come back as voice channels.
    /// </summary>
    /// <param name="guild">The community to create it in.</param>
    /// <param name="backed">The channel to create.</param>
    /// <param name="parent">The ID of the category to put it in, or null.</param>
    /// <param name="overwrites">The permission overwrites to give it.</param>
    /// <returns>The new channel.</returns>
    public static async Task<Channel> CreateChannelAsync(SocketGuild guild, BackupChannel backed, ulong? parent,
        ChannelOverwriteRequest[] overwrites)
    {
        T Fill<T>(T request) where T : CreateGuildChannelRequest
        {
            request.Name = backed.Name;
            request.PermissionOverwrites = overwrites;
            if (backed.Type != "category")
                request.ParentCategoryId = parent;
            return request;
        }

        switch (backed.Type)
        {
            case "category":
                return await guild.CreateCategoryChannelAsync(Fill(new CreateCategoryChannelRequest()));
            case "voice" or "stage":
                var voice = Fill(new CreateVoiceChannelRequest());
                voice.Bitrate = backed.Bitrate >= 8000 ? backed.Bitrate : null;
                voice.UserLimit = backed.UserLimit;
                return await guild.CreateVoiceChannelAsync(voice);
            case "forum":
                var forum = Fill(new CreateForumChannelRequest());
                forum.Topic = backed.Topic;
                forum.Nsfw = backed.Nsfw;
                return await guild.CreateForumChannelAsync(forum);
            case "media":
                var media = Fill(new CreateMediaChannelRequest());
                media.Topic = backed.Topic;
                media.Nsfw = backed.Nsfw;
                return await guild.CreateMediaChannelAsync(media);
            default:
                var text = Fill(new CreateTextChannelRequest());
                text.Topic = backed.Topic;
                text.RatelimitPerUser = backed.SlowMode;
                text.Nsfw = backed.Nsfw;
                return await guild.CreateTextChannelAsync(text);
        }
    }

    /// <summary>
    ///     Checks whether a backup can be restored into a community by a member.
    /// </summary>
    /// <param name="guild">The community to restore into.</param>
    /// <param name="user">The member running the restore.</param>
    /// <param name="backup">The backup.</param>
    /// <param name="hasMessages">Whether the backup holds messages that need webhooks.</param>
    /// <returns>The error, if any, and the permissions the bot is missing.</returns>
    public async Task<(BackupError Error, string Detail)> CheckRestoreAsync(SocketGuild guild, SocketGuildMember user,
        ServerBackup backup, bool hasMessages)
    {
        if (running.ContainsKey(guild.Id))
            return (BackupError.RestoreRunning, "");

        if (!IsOwnerOrAdmin(guild, user))
            return (BackupError.NotAdmin, "");

        if (backup.SourceGuildId != guild.Id && client.GetGuild(backup.SourceGuildId) is { } source)
        {
            var inSource = await source.TryGetUserAsync(user.Id);
            if (inSource is null || !IsOwnerOrAdmin(source, inSource))
                return (BackupError.NotAdminInSource, source.Name);
        }

        var me = guild.CurrentMember;
        if (!me.GuildPermissions.Administrator)
        {
            var needed = new List<GuildPermission>
            {
                GuildPermission.ManageRoles,
                GuildPermission.ManageChannels,
                GuildPermission.ManageGuild,
                GuildPermission.ManageExpressions
            };
            if (hasMessages)
                needed.Add(GuildPermission.ManageWebhooks);

            var missing = needed.Where(x => !me.GuildPermissions.Has(x)).ToList();
            if (missing.Count > 0)
                return (BackupError.BotMissingPermissions, string.Join(", ", missing));
        }

        if (me.Hierarchy < 2)
            return (BackupError.BotRoleTooLow, "");

        return (BackupError.None, "");
    }

    /// <summary>
    ///     Pulls the IDs out of text such as channel mentions.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The IDs found.</returns>
    public static List<ulong> ParseIds(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? []
            : SnowflakePattern.Matches(text).Select(x => ulong.Parse(x.Value)).Distinct().ToList();
    }

    /// <summary>
    ///     Gets the message that explains why a backup could not be created or restored.
    /// </summary>
    /// <param name="guildId">The community ID, for the language.</param>
    /// <param name="error">The error.</param>
    /// <param name="detail">Extra detail from the check, such as the missing permissions.</param>
    /// <param name="wait">How long to wait when on cooldown.</param>
    /// <returns>The message.</returns>
    public string ErrorText(ulong guildId, BackupError error, string detail = "", TimeSpan wait = default)
    {
        return error switch
        {
            BackupError.LimitReached => strings.BackupLimit(guildId, MaxBackupsPerUser),
            BackupError.Cooldown => strings.BackupCooldown(guildId, wait.Humanize(2)),
            BackupError.TooLarge => strings.BackupTooLarge(guildId),
            BackupError.NotFound => strings.BackupNotFound(guildId),
            BackupError.NotAdmin => strings.BackupNotAdmin(guildId),
            BackupError.NotAdminInSource => strings.BackupNotAdminSource(guildId, detail),
            BackupError.BotMissingPermissions => strings.BackupBotPerms(guildId, detail),
            BackupError.BotRoleTooLow => strings.BackupBotRole(guildId),
            BackupError.RestoreRunning => strings.BackupRestoreRunning(guildId),
            _ => strings.BackupCorrupt(guildId)
        };
    }

    /// <summary>
    ///     Builds the embed that shows how far a restore has got.
    /// </summary>
    /// <param name="guildId">The community ID, for the language.</param>
    /// <param name="progress">The progress.</param>
    /// <param name="finished">Whether the restore is over.</param>
    /// <returns>The embed.</returns>
    public EmbedBuilder ProgressEmbed(ulong guildId, BackupRestoreProgress progress, bool finished)
    {
        var embed = new EmbedBuilder().WithOkColor(guildId);
        if (finished)
        {
            embed.WithTitle(strings.BackupDoneTitle(guildId))
                .WithDescription(strings.BackupDoneSummary(guildId, progress.RolesCreated, progress.ChannelsCreated,
                    progress.EmojisCreated, progress.MessagesRestored));
        }
        else
        {
            embed.WithDescription(strings.BackupProgress(guildId, progress.Step, progress.RolesCreated,
                progress.ChannelsCreated, progress.EmojisCreated, progress.MessagesRestored));
        }

        var failures = progress.Failures.ToList();
        if (failures.Count > 0)
        {
            var text = string.Join("\n", failures.Take(10));
            if (failures.Count > 10)
                text += "\n" + strings.BackupFailuresMore(guildId, failures.Count - 10);
            embed.AddField(strings.BackupFailures(guildId), text);
        }

        return embed;
    }

    /// <summary>
    ///     Gets the latest restore started for a community with <see cref="StartRestore" />.
    /// </summary>
    /// <param name="guildId">The community ID.</param>
    /// <returns>The job, or null if none was started since the bot started.</returns>
    public BackupRestoreJob? GetJob(ulong guildId)
    {
        return jobs.GetValueOrDefault(guildId);
    }

    /// <summary>
    ///     Starts a restore in the background after <see cref="CheckRestoreAsync" /> passes.
    /// </summary>
    /// <param name="guild">The community to restore into.</param>
    /// <param name="user">The person who started the restore.</param>
    /// <param name="data">The backup data.</param>
    /// <param name="mode">Whether to replace everything or only add what is missing.</param>
    /// <returns>The job.</returns>
    public BackupRestoreJob StartRestore(SocketGuild guild, SocketGuildMember user, ServerBackupData data,
        BackupRestoreMode mode)
    {
        var job = new BackupRestoreJob();
        jobs[guild.Id] = job;
        _ = Task.Run(async () =>
        {
            ulong? reportChannel = null;
            try
            {
                reportChannel = await RestoreAsync(guild, data, mode, 0, job.Progress);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup restore failed in {Guild}", guild.Id);
                job.Progress.Failures.Add(ex.Message);
            }

            job.Finished = true;
            var final = ProgressEmbed(guild.Id, job.Progress, true).Build();
            try
            {
                await user.SendAsync(embed: final);
                return;
            }
            catch (Exception)
            {
            }

            if (reportChannel is { } id && guild.GetChannel(id) is { } text)
            {
                try
                {
                    await text.SendAsync($"<@{user.Id}>", embed: final);
                }
                catch (Exception)
                {
                }
            }
        });
        return job;
    }

    /// <summary>
    ///     Restores a backup into a community. Check it with <see cref="CheckRestoreAsync" /> first.
    /// </summary>
    /// <param name="guild">The community to restore into.</param>
    /// <param name="data">The backup data.</param>
    /// <param name="mode">Whether to replace everything or only add what is missing.</param>
    /// <param name="keepChannelId">A channel to keep until the end, or zero.</param>
    /// <param name="progress">The progress object, updated as the restore goes.</param>
    /// <returns>The ID of a text channel to report in, or null.</returns>
    public async Task<ulong?> RestoreAsync(SocketGuild guild, ServerBackupData data, BackupRestoreMode mode,
        ulong keepChannelId, BackupRestoreProgress progress)
    {
        if (!running.TryAdd(guild.Id, 0))
            return null;

        var guildId = guild.Id;
        try
        {
            var me = guild.CurrentMember;
            var botAdmin = me.GuildPermissions.Administrator;
            var replace = mode == BackupRestoreMode.Replace;

            if (replace)
            {
                progress.Step = strings.BackupStepClearing(guildId);
                await ClearAsync(guild, me, keepChannelId, progress);
            }

            progress.Step = strings.BackupStepRoles(guildId);
            var roleMap = await RestoreRolesAsync(guild, me, botAdmin, data, replace, progress);

            progress.Step = strings.BackupStepChannels(guildId);
            var (channelMap, created) = await RestoreChannelsAsync(guild, data, roleMap, replace, progress);

            progress.Step = strings.BackupStepEmojis(guildId);
            await RestoreEmojisAsync(guild, data, replace, progress);

            if (replace)
            {
                progress.Step = strings.BackupStepSettings(guildId);
                await RestoreSettingsAsync(guild, data.Settings, channelMap, progress);
            }

            if (data.Messages.Count > 0)
            {
                progress.Step = strings.BackupStepMessages(guildId);
                await RestoreMessagesAsync(guild, data, channelMap, created, progress);
            }

            var reportChannel = data.Channels
                .Where(x => x.Type == "text" && created.Contains(x.Id) && channelMap.ContainsKey(x.Id))
                .Select(x => (ulong?)channelMap[x.Id]).FirstOrDefault();

            if (replace && keepChannelId != 0 && !channelMap.Values.Contains(keepChannelId))
            {
                try
                {
                    if (guild.GetChannel(keepChannelId) is { } keep)
                        await keep.DeleteAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not delete the kept channel after a restore");
                }
            }

            progress.Step = strings.BackupStepDone(guildId);
            return reportChannel;
        }
        finally
        {
            running.TryRemove(guild.Id, out _);
        }
    }

    private async Task ClearAsync(SocketGuild guild, SocketGuildMember me, ulong keepChannelId,
        BackupRestoreProgress progress)
    {
        var guildId = guild.Id;

        foreach (var emote in guild.Emotes.ToList())
        {
            try
            {
                await client.Rest.DeleteEmojiAsync(guildId, emote.Id);
                await Task.Delay(RequestDelayMs);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup restore could not delete emoji {Emoji}", emote.Id);
                progress.Failures.Add(strings.BackupFailDeleteEmoji(guildId, emote.Name));
            }
        }

        foreach (var channel in (await guild.GetChannelsAsync()).Where(x => x.Id != keepChannelId)
                     .OrderBy(x => x.Type == ChannelType.GuildCategory ? 1 : 0).ToList())
        {
            try
            {
                await channel.DeleteAsync();
                await Task.Delay(RequestDelayMs);
            }
            catch (FluxerApiException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup restore could not delete channel {Channel}", channel.Id);
                progress.Failures.Add(strings.BackupFailDeleteChannel(guildId, channel.Name ?? ""));
            }
        }

        foreach (var role in guild.Roles.Values.Where(x => x.Id != guild.Id && x.Position < me.Hierarchy).ToList())
        {
            try
            {
                await role.DeleteAsync();
                await Task.Delay(RequestDelayMs);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup restore could not delete role {Role}", role.Id);
                progress.Failures.Add(strings.BackupFailDeleteRole(guildId, role.Name));
            }
        }
    }

    private async Task<Dictionary<ulong, ulong>> RestoreRolesAsync(SocketGuild guild, SocketGuildMember me,
        bool botAdmin, ServerBackupData data, bool replace, BackupRestoreProgress progress)
    {
        var guildId = guild.Id;
        var map = new Dictionary<ulong, ulong>();
        var botPerms = (ulong)me.GuildPermissions.RawValue;

        var everyone = data.Roles.FirstOrDefault(x => x.IsEveryone);
        if (everyone is not null)
        {
            map[everyone.Id] = guild.Id;
            if (replace && guild.EveryoneRole is { } everyoneRole)
            {
                try
                {
                    var raw = botAdmin ? everyone.Permissions : everyone.Permissions & botPerms;
                    await everyoneRole.ModifyAsync(new UpdateGuildRoleRequest
                    {
                        Permissions = raw
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Backup restore could not edit @everyone");
                    progress.Failures.Add(strings.BackupFailRole(guildId, "@everyone"));
                }
            }
        }

        var existing = guild.Roles.Values.ToList();
        foreach (var backed in data.Roles.Where(x => !x.IsEveryone).OrderByDescending(x => x.Position))
        {
            if (!replace)
            {
                var match = existing.FirstOrDefault(x => x.Name.Equals(backed.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    map[backed.Id] = match.Id;
                    continue;
                }
            }

            try
            {
                var raw = backed.Permissions;
                if (!botAdmin && (raw & ~botPerms) != 0)
                {
                    raw &= botPerms;
                    progress.Failures.Add(strings.BackupFailRolePerms(guildId, backed.Name));
                }

                var role = await guild.CreateRoleAsync(new CreateGuildRoleRequest
                {
                    Name = backed.Name, Color = (int)backed.Color, Permissions = raw
                });
                map[backed.Id] = role.Id;
                progress.RolesCreated++;

                if (backed.Hoist || backed.Mentionable)
                {
                    try
                    {
                        await role.ModifyAsync(new UpdateGuildRoleRequest
                        {
                            Hoist = backed.Hoist, Mentionable = backed.Mentionable
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Backup restore could not set the flags of role {Role}", backed.Name);
                    }
                }

                if (backed.Icon is not null || backed.UnicodeEmoji is not null)
                {
                    try
                    {
                        var update = new UpdateGuildRoleRequest();
                        if (backed.Icon is not null)
                            update.IconBase64 = Convert.ToBase64String(backed.Icon);
                        else
                            update.UnicodeEmoji = backed.UnicodeEmoji;
                        await role.ModifyAsync(update);
                    }
                    catch (Exception)
                    {
                        progress.Failures.Add(strings.BackupFailRoleIcon(guildId, backed.Name));
                    }
                }

                await Task.Delay(RequestDelayMs);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup restore could not create role {Role}", backed.Name);
                progress.Failures.Add(strings.BackupFailRole(guildId, backed.Name));
            }
        }

        return map;
    }

    private async Task<(Dictionary<ulong, ulong> Map, HashSet<ulong> Created)> RestoreChannelsAsync(SocketGuild guild,
        ServerBackupData data, Dictionary<ulong, ulong> roleMap, bool replace, BackupRestoreProgress progress)
    {
        var guildId = guild.Id;
        var map = new Dictionary<ulong, ulong>();
        var created = new HashSet<ulong>();
        var existing = replace
            ? []
            : (await guild.GetChannelsAsync()).Where(x => x.ThreadMetadata is null).ToList();

        var ordered = data.Channels.OrderBy(x => x.Type == "category" ? 0 : 1).ThenBy(x => x.Position).ToList();
        foreach (var backed in ordered)
        {
            if (!replace)
            {
                var match = existing.FirstOrDefault(x =>
                    ChannelKind(x) == backed.Type &&
                    (x.Name ?? "").Equals(backed.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    map[backed.Id] = match.Id;
                    continue;
                }
            }

            try
            {
                var overwrites = await MapOverwritesAsync(guild, backed.Overwrites, roleMap);
                ulong? parent = backed.ParentId is { } p && map.TryGetValue(p, out var mapped) ? mapped : null;
                var channel = await CreateChannelAsync(guild, backed, parent, overwrites);

                map[backed.Id] = channel.Id;
                created.Add(backed.Id);
                progress.ChannelsCreated++;
                await Task.Delay(RequestDelayMs);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup restore could not create channel {Channel}", backed.Name);
                progress.Failures.Add(strings.BackupFailChannel(guildId, backed.Name));
            }
        }

        return (map, created);
    }

    private static async Task<ChannelOverwriteRequest[]> MapOverwritesAsync(SocketGuild guild,
        List<BackupOverwrite> overwrites, Dictionary<ulong, ulong> roleMap)
    {
        var result = new List<ChannelOverwriteRequest>();
        foreach (var overwrite in overwrites)
        {
            if (overwrite.IsRole)
            {
                if (roleMap.TryGetValue(overwrite.TargetId, out var role))
                {
                    result.Add(new ChannelOverwriteRequest
                    {
                        Id = role, Type = OverwriteRequestType.Role, Allow = overwrite.Allow, Deny = overwrite.Deny
                    });
                }
            }
            else if (await guild.TryGetUserAsync(overwrite.TargetId) is not null)
            {
                result.Add(new ChannelOverwriteRequest
                {
                    Id = overwrite.TargetId, Type = OverwriteRequestType.Member, Allow = overwrite.Allow,
                    Deny = overwrite.Deny
                });
            }
        }

        return result.ToArray();
    }

    private async Task RestoreEmojisAsync(SocketGuild guild, ServerBackupData data, bool replace,
        BackupRestoreProgress progress)
    {
        var guildId = guild.Id;
        var existing = replace
            ? []
            : guild.Emotes.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = 0;

        foreach (var emoji in data.Emojis)
        {
            if (existing.Contains(emoji.Name))
                continue;

            try
            {
                await guild.CreateEmojiAsync(new CreateGuildEmojiRequest
                {
                    Name = emoji.Name, ImageBase64 = Convert.ToBase64String(emoji.Image)
                });
                progress.EmojisCreated++;
                failures = 0;
                await Task.Delay(RequestDelayMs);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup restore could not create emoji {Emoji}", emoji.Name);
                progress.Failures.Add(strings.BackupFailEmoji(guildId, emoji.Name));
                if (++failures < EmojiFailureLimit)
                    continue;

                progress.Failures.Add(strings.BackupFailEmojiSlots(guildId));
                break;
            }
        }
    }

    private async Task RestoreSettingsAsync(SocketGuild guild, BackupSettings settings,
        Dictionary<ulong, ulong> channelMap, BackupRestoreProgress progress)
    {
        try
        {
            var request = new UpdateGuildRequest
            {
                Name = settings.Name,
                VerificationLevel = (GuildVerificationLevel)settings.VerificationLevel,
                DefaultMessageNotifications = (GuildDefaultNotifications)settings.DefaultMessageNotifications,
                ExplicitContentFilter = (GuildContentFilter)settings.ExplicitContentFilter,
                AfkTimeout = settings.AfkTimeout,
                SystemChannelFlags = (SystemChannelFlags)settings.SystemChannelFlags
            };
            if (settings.Icon is not null)
                request.IconBase64 = Convert.ToBase64String(settings.Icon);
            if (settings.SystemChannelId is { } systemId && channelMap.TryGetValue(systemId, out var newSystem))
                request.SystemChannelId = newSystem;
            if (settings.AfkChannelId is { } afkId && channelMap.TryGetValue(afkId, out var newAfk))
                request.AfkChannelId = newAfk;

            await guild.ModifyAsync(request);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backup restore could not edit the community settings");
            progress.Failures.Add(strings.BackupFailSettings(guild.Id));
        }
    }

    private async Task RestoreMessagesAsync(SocketGuild guild, ServerBackupData data,
        Dictionary<ulong, ulong> channelMap, HashSet<ulong> created, BackupRestoreProgress progress)
    {
        var guildId = guild.Id;
        foreach (var saved in data.Messages)
        {
            if (!created.Contains(saved.ChannelId) || !channelMap.TryGetValue(saved.ChannelId, out var newId))
                continue;

            var channel = guild.GetChannel(newId);
            if (channel is null)
                continue;

            FluxerWebhookClient? hook = null;
            try
            {
                var webhook = await client.Rest.CreateWebhookAsync(channel.Id, "Mewdeko Backup");
                hook = new FluxerWebhookClient(webhook.GetUrl());
                foreach (var message in saved.Messages)
                {
                    try
                    {
                        var name = WebhookName.Replace(message.Author, "_").Trim();
                        if (name.Length < 2)
                            name = "User";
                        if (name.Length > 80)
                            name = name[..80];

                        await hook.SendAsync(message.Content, username: name, avatarUrl: message.AvatarUrl,
                            allowedMentions: AllowedMentions.None);
                        progress.MessagesRestored++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Backup restore could not post a message in {Channel}", channel.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup restore could not use a webhook in {Channel}", channel.Id);
                progress.Failures.Add(strings.BackupFailMessages(guildId, channel.Name ?? ""));
            }
            finally
            {
                if (hook is not null)
                {
                    try
                    {
                        await hook.DeleteAsync();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }

    private static string? ChannelKind(Channel channel)
    {
        return channel.Type switch
        {
            ChannelType.GuildCategory => "category",
            ChannelType.GuildForum => "forum",
            ChannelType.GuildMedia => "media",
            ChannelType.GuildStageVoice => "stage",
            ChannelType.GuildVoice => "voice",
            ChannelType.GuildText or ChannelType.GuildNews => "text",
            _ => null
        };
    }

    private static async Task<byte[]?> DownloadAsync(HttpClient http, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            return await http.GetByteArrayAsync(url);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
