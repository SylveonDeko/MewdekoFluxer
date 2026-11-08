using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DataModel;
using Humanizer;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.Moderation.Services;

/// <summary>
///     The outcome of a member report.
/// </summary>
public enum ReportResult
{
    /// <summary>
    ///     The report was posted.
    /// </summary>
    Sent,

    /// <summary>
    ///     No report channel is set.
    /// </summary>
    NoChannel,

    /// <summary>
    ///     The reporter is on cooldown.
    /// </summary>
    Cooldown,

    /// <summary>
    ///     The report channel is gone or the bot cannot post in it.
    /// </summary>
    ChannelUnavailable
}

/// <summary>
///     Keeps numbered moderation cases, staff notes, reason aliases and custom punishment DMs.
/// </summary>
public class ModCaseService : INService
{
    private static readonly TimeSpan ReportCooldown = TimeSpan.FromMinutes(5);
    private static readonly Regex MessageLink = new(@"/channels/(\d+)/(\d+)/(\d+)", RegexOptions.IgnoreCase);

    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> locks = new();
    private readonly ILogger<ModCaseService> logger;
    private readonly GeneratedBotStrings strings;

    /// <summary>
    ///     Initializes a new instance of <see cref="ModCaseService" />.
    /// </summary>
    /// <param name="dbFactory">The database provider.</param>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="eventHandler">The event handler used to catch bans, unbans and kicks done by hand.</param>
    /// <param name="mute">The mute service, used to record mutes and unmutes.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="strings">The localized strings.</param>
    public ModCaseService(IDataConnectionFactory dbFactory, FluxerClient client, EventHandler eventHandler,
        MuteService mute, ILogger<ModCaseService> logger, GeneratedBotStrings strings)
    {
        this.strings = strings;
        this.dbFactory = dbFactory;
        this.client = client;
        this.logger = logger;
        eventHandler.AuditLogCreated.Subscribe("ModCaseService", OnAuditLogCreated);
        mute.UserMuted += OnUserMuted;
        mute.UserUnmuted += OnUserUnmuted;
    }

    /// <summary>
    ///     Records a moderation case and posts it to the case channel. Never throws.
    /// </summary>
    /// <param name="guildId">The guild the action happened in.</param>
    /// <param name="userId">The member the action was taken on.</param>
    /// <param name="moderatorId">The moderator who took the action.</param>
    /// <param name="action">The action taken.</param>
    /// <param name="reason">The reason, if any.</param>
    /// <param name="duration">How long the action lasts, if it has a duration.</param>
    /// <param name="warningId">The linked warning for warn cases.</param>
    /// <returns>The new case, or null if it could not be saved.</returns>
    public async Task<ModCase?> RecordAsync(ulong guildId, ulong userId, ulong moderatorId, ModCaseAction action,
        string? reason, TimeSpan? duration = null, int? warningId = null)
    {
        ModCase entry;
        reason = await ResolveReasonAsync(guildId, reason).ConfigureAwait(false);
        var gate = locks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
            var last = await db.ModCases
                .Where(x => x.GuildId == guildId)
                .MaxAsync(x => (int?)x.CaseNumber)
                .ConfigureAwait(false) ?? 0;

            entry = new ModCase
            {
                GuildId = guildId,
                CaseNumber = last + 1,
                UserId = userId,
                ModeratorId = moderatorId,
                Action = action,
                Reason = string.IsNullOrWhiteSpace(reason) || reason == "-" ? null : reason.Trim(),
                DurationSeconds = duration is { } d && d > TimeSpan.Zero ? (long)d.TotalSeconds : null,
                WarningId = warningId,
                DateAdded = DateTime.UtcNow
            };
            entry.Id = await db.InsertWithInt32IdentityAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record a {Action} case for {UserId} in {GuildId}", action, userId,
                guildId);
            return null;
        }
        finally
        {
            gate.Release();
        }

        await PostToLogAsync(entry).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    ///     Gets one case by its number.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="caseNumber">The case number.</param>
    public async Task<ModCase?> GetCaseAsync(ulong guildId, int caseNumber)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModCases
            .FirstOrDefaultAsync(x => x.GuildId == guildId && x.CaseNumber == caseNumber)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Gets every case in a guild, oldest first.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    public async Task<List<ModCase>> GetAllCasesAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModCases
            .Where(x => x.GuildId == guildId)
            .OrderBy(x => x.CaseNumber)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Changes a case's reason.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="caseNumber">The case number.</param>
    /// <param name="reason">The new reason.</param>
    /// <returns>Whether the case exists.</returns>
    public async Task<bool> SetReasonAsync(ulong guildId, int caseNumber, string reason)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModCases
            .Where(x => x.GuildId == guildId && x.CaseNumber == caseNumber)
            .Set(x => x.Reason, reason.Trim())
            .UpdateAsync()
            .ConfigureAwait(false) > 0;
    }

    /// <summary>
    ///     Deletes a case.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="caseNumber">The case number.</param>
    /// <returns>Whether the case existed.</returns>
    public async Task<bool> DeleteCaseAsync(ulong guildId, int caseNumber)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModCases
            .Where(x => x.GuildId == guildId && x.CaseNumber == caseNumber)
            .DeleteAsync()
            .ConfigureAwait(false) > 0;
    }

    /// <summary>
    ///     Gets how many cases each moderator has, highest first.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="take">How many moderators to return.</param>
    public async Task<List<(ulong ModeratorId, int Count)>> GetLeaderboardAsync(ulong guildId, int take = 15)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        var rows = await db.ModCases
            .Where(x => x.GuildId == guildId)
            .GroupBy(x => x.ModeratorId)
            .Select(x => new
            {
                ModeratorId = x.Key, Count = x.Count()
            })
            .OrderByDescending(x => x.Count)
            .Take(take)
            .ToListAsync()
            .ConfigureAwait(false);
        return rows.Select(x => (x.ModeratorId, x.Count)).ToList();
    }

    /// <summary>
    ///     Builds the text of a guild's case export as CSV.
    /// </summary>
    /// <param name="cases">The cases to export.</param>
    public static string BuildExport(IEnumerable<ModCase> cases)
    {
        var sb = new StringBuilder("case,date,action,user_id,moderator_id,duration_seconds,warning_id,reason\n");
        foreach (var c in cases)
        {
            sb.Append(c.CaseNumber).Append(',')
                .Append(c.DateAdded.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                .Append(c.Action).Append(',')
                .Append(c.UserId).Append(',')
                .Append(c.ModeratorId).Append(',')
                .Append(c.DurationSeconds).Append(',')
                .Append(c.WarningId).Append(',')
                .Append('"').Append((c.Reason ?? "").Replace("\"", "\"\"")).Append('"')
                .Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Gets a guild's case and report channels.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    public async Task<ModCaseSetting> GetSettingsAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModCaseSettings.FirstOrDefaultAsync(x => x.GuildId == guildId).ConfigureAwait(false)
               ?? new ModCaseSetting
               {
                   GuildId = guildId
               };
    }

    /// <summary>
    ///     Sets or clears the channel new cases are posted to.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="channelId">The channel, or null to turn it off.</param>
    public Task SetLogChannelAsync(ulong guildId, ulong? channelId)
    {
        return UpsertAsync(guildId, x => x.LogChannelId = channelId);
    }

    /// <summary>
    ///     Sets or clears the channel member reports are posted to.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="channelId">The channel, or null to turn it off.</param>
    public Task SetReportChannelAsync(ulong guildId, ulong? channelId)
    {
        return UpsertAsync(guildId, x => x.ReportChannelId = channelId);
    }

    private async Task UpsertAsync(ulong guildId, Action<ModCaseSetting> change)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        var setting = await db.ModCaseSettings.FirstOrDefaultAsync(x => x.GuildId == guildId).ConfigureAwait(false);
        if (setting is null)
        {
            setting = new ModCaseSetting
            {
                GuildId = guildId
            };
            change(setting);
            await db.InsertAsync(setting).ConfigureAwait(false);
            return;
        }

        change(setting);
        await db.UpdateAsync(setting).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds a staff note to a member.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="userId">The member.</param>
    /// <param name="moderatorId">The moderator writing the note.</param>
    /// <param name="text">The note.</param>
    /// <returns>The new note.</returns>
    public async Task<ModNote> AddNoteAsync(ulong guildId, ulong userId, ulong moderatorId, string text)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        var note = new ModNote
        {
            GuildId = guildId,
            UserId = userId,
            ModeratorId = moderatorId,
            Note = text.Trim(),
            DateAdded = DateTime.UtcNow
        };
        note.Id = await db.InsertWithInt32IdentityAsync(note).ConfigureAwait(false);
        return note;
    }

    /// <summary>
    ///     Removes one note.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="noteId">The note ID.</param>
    /// <returns>Whether the note existed.</returns>
    public async Task<bool> RemoveNoteAsync(ulong guildId, int noteId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModNotes
            .Where(x => x.GuildId == guildId && x.Id == noteId)
            .DeleteAsync()
            .ConfigureAwait(false) > 0;
    }

    /// <summary>
    ///     Gets a member's cases, newest first.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="userId">The member.</param>
    public async Task<List<ModCase>> GetUserCasesAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModCases
            .Where(x => x.GuildId == guildId && x.UserId == userId)
            .OrderByDescending(x => x.CaseNumber)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Gets a member's notes, newest first.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="userId">The member.</param>
    public async Task<List<ModNote>> GetNotesAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModNotes
            .Where(x => x.GuildId == guildId && x.UserId == userId)
            .OrderByDescending(x => x.Id)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes every note on a member.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="userId">The member.</param>
    /// <returns>How many notes were removed.</returns>
    public async Task<int> ClearNotesAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModNotes
            .Where(x => x.GuildId == guildId && x.UserId == userId)
            .DeleteAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Posts a member report to the report channel.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="reporter">The member reporting.</param>
    /// <param name="target">The member being reported.</param>
    /// <param name="reason">Why they are reported.</param>
    /// <param name="messageLink">A link to the offending message, if given.</param>
    /// <param name="fallbackJumpUrl">The link used when no valid message link is given.</param>
    /// <returns>The outcome, and how long the reporter still has to wait when on cooldown.</returns>
    public async Task<(ReportResult Result, TimeSpan Remaining)> ReportAsync(SocketGuild guild, IUser reporter,
        IUser target, string reason, string? messageLink, string fallbackJumpUrl)
    {
        var channelId = (await GetSettingsAsync(guild.Id).ConfigureAwait(false)).ReportChannelId;
        if (channelId is null)
            return (ReportResult.NoChannel, TimeSpan.Zero);

        var last = await GetLastReportAsync(guild.Id, reporter.Id).ConfigureAwait(false);
        if (last is { } lastAt && DateTime.UtcNow - lastAt < ReportCooldown)
            return (ReportResult.Cooldown, ReportCooldown - (DateTime.UtcNow - lastAt));

        var channel = guild.GetTextChannel(channelId.Value);
        if (channel is null)
            return (ReportResult.ChannelUnavailable, TimeSpan.Zero);

        var jump = fallbackJumpUrl;
        var match = messageLink is null ? null : MessageLink.Match(messageLink);
        if (match is { Success: true } && match.Groups[1].Value == guild.Id.ToString())
            jump = messageLink!;

        var embed = new EmbedBuilder()
            .WithErrorColor()
            .WithTitle(strings.ModReportTitle(guild.Id))
            .WithThumbnailUrl(target.RealAvatarUrl().ToString())
            .AddField("User", $"{target.Mention} ({target.Id})", true)
            .AddField("Reporter", $"{reporter.Mention} ({reporter.Id})", true)
            .AddField("Reason", reason.TrimTo(1000))
            .AddField("Context", $"[Jump to message]({jump})")
            .WithCurrentTimestamp();

        try
        {
            await channel.SendAsync(embed: embed.Build()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not post a report in {ChannelId}", channelId);
            return (ReportResult.ChannelUnavailable, TimeSpan.Zero);
        }

        await SetLastReportAsync(guild.Id, reporter.Id).ConfigureAwait(false);
        return (ReportResult.Sent, TimeSpan.Zero);
    }

    private async Task<DateTime?> GetLastReportAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModReportCooldowns
            .Where(x => x.GuildId == guildId && x.UserId == userId)
            .Select(x => (DateTime?)x.LastReportAt)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task SetLastReportAsync(ulong guildId, ulong userId)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        await db.ModReportCooldowns.InsertOrUpdateAsync(
                () => new ModReportCooldown
                {
                    GuildId = guildId,
                    UserId = userId,
                    LastReportAt = now
                },
                x => new ModReportCooldown
                {
                    LastReportAt = now
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Replaces a reason that is the name of a reason alias with the full reason.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="reason">The reason as typed.</param>
    /// <returns>The full reason, or the reason unchanged if it is not an alias.</returns>
    public async Task<string?> ResolveReasonAsync(ulong guildId, string? reason)
    {
        var name = reason?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
            return reason;

        try
        {
            await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
            var match = await db.ModReasonAliases
                .FirstOrDefaultAsync(x => x.GuildId == guildId && x.Name == name)
                .ConfigureAwait(false);
            return match?.Reason ?? reason;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not look up reason aliases in {GuildId}", guildId);
            return reason;
        }
    }

    /// <summary>
    ///     Gets a guild's reason aliases.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    public async Task<List<ModReasonAlias>> GetReasonAliasesAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModReasonAliases
            .Where(x => x.GuildId == guildId)
            .OrderBy(x => x.Name)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds a reason alias, replacing one with the same name.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="name">The short name.</param>
    /// <param name="reason">The full reason.</param>
    public async Task SetReasonAliasAsync(ulong guildId, string name, string reason)
    {
        var key = name.Trim().ToLowerInvariant();
        var text = reason.Trim();
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        await db.ModReasonAliases.InsertOrUpdateAsync(
                () => new ModReasonAlias
                {
                    GuildId = guildId,
                    Name = key,
                    Reason = text,
                    DateAdded = DateTime.UtcNow
                },
                x => new ModReasonAlias
                {
                    Reason = text
                },
                () => new ModReasonAlias
                {
                    GuildId = guildId,
                    Name = key
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a reason alias.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="name">The short name.</param>
    /// <returns>Whether the alias existed.</returns>
    public async Task<bool> RemoveReasonAliasAsync(ulong guildId, string name)
    {
        var key = name.Trim().ToLowerInvariant();
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.ModReasonAliases
            .Where(x => x.GuildId == guildId && x.Name == key)
            .DeleteAsync()
            .ConfigureAwait(false) > 0;
    }

    /// <summary>
    ///     Gets a guild's custom DM templates by action.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    public async Task<Dictionary<ModCaseAction, string>> GetPunishDmTemplatesAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        return await db.PunishDmTemplates
            .Where(x => x.GuildId == guildId)
            .ToDictionaryAsync(x => x.Action, x => x.Template)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the custom DM for an action, or clears it.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="action">The action.</param>
    /// <param name="template">The template, or null to go back to the default DM.</param>
    public async Task SetPunishDmTemplateAsync(ulong guildId, ModCaseAction action, string? template)
    {
        await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(template))
        {
            await db.PunishDmTemplates
                .Where(x => x.GuildId == guildId && x.Action == action)
                .DeleteAsync()
                .ConfigureAwait(false);
            return;
        }

        await db.PunishDmTemplates.InsertOrUpdateAsync(
                () => new PunishDmTemplate
                {
                    GuildId = guildId,
                    Action = action,
                    Template = template
                },
                x => new PunishDmTemplate
                {
                    Template = template
                },
                () => new PunishDmTemplate
                {
                    GuildId = guildId,
                    Action = action
                })
            .ConfigureAwait(false);
    }

    private async Task PostToLogAsync(ModCase entry)
    {
        try
        {
            var channelId = (await GetSettingsAsync(entry.GuildId).ConfigureAwait(false)).LogChannelId;
            if (channelId is null)
                return;

            var channel = client.GetGuild(entry.GuildId)?.GetTextChannel(channelId.Value);
            if (channel is null)
                return;

            var title = $"Case #{entry.CaseNumber} | {entry.Action}";
            var eb = new EmbedBuilder()
                .WithOkColor()
                .WithTitle(title)
                .AddField("User", $"<@{entry.UserId}> ({entry.UserId})", true)
                .AddField("Moderator", $"<@{entry.ModeratorId}>", true)
                .AddField("Reason", entry.Reason ?? "No reason given");

            if (entry.DurationSeconds is { } seconds)
                eb.AddField("Duration", TimeSpan.FromSeconds(seconds).ToString(), true);
            if (entry.WarningId is { } warningId)
                eb.AddField("Warning", warningId.ToString(), true);

            await channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not post case {Case} to the case channel in {GuildId}", entry.CaseNumber,
                entry.GuildId);
        }
    }

    private async Task OnAuditLogCreated(SocketAuditLog entry, SocketGuild guild)
    {
        TimeSpan? duration = null;
        var action = entry.Action switch
        {
            ActionType.Ban => ModCaseAction.Ban,
            ActionType.Unban => ModCaseAction.Unban,
            ActionType.Kick => ModCaseAction.Kick,
            ActionType.MemberUpdated => TimeoutAction(entry, out duration),
            _ => (ModCaseAction?)null
        };

        if (action is not { } caseAction || entry.TargetId is not { } targetId || entry.UserId is not { } moderatorId ||
            moderatorId == client.CurrentUser?.Id)
            return;

        await RecordAsync(guild.Id, targetId, moderatorId, caseAction, entry.Reason, duration).ConfigureAwait(false);
    }

    private static ModCaseAction? TimeoutAction(SocketAuditLog entry, out TimeSpan? duration)
    {
        duration = null;
        var before = (entry.OldData as MemberChangeDataJson)?.CommunicationDisabledUntil;
        var after = (entry.NewData as MemberChangeDataJson)?.CommunicationDisabledUntil;
        var now = DateTimeOffset.UtcNow;
        if (after is { } until && until > now)
        {
            duration = until - now;
            return ModCaseAction.Timeout;
        }

        return before is { } old && old > now && after is null ? ModCaseAction.Untimeout : null;
    }

    /// <summary>
    ///     Records a case, sends the custom DM, then runs the punishment.
    /// </summary>
    /// <param name="guild">The guild the action happens in.</param>
    /// <param name="user">The member being punished.</param>
    /// <param name="moderator">The moderator taking the action.</param>
    /// <param name="action">The action taken.</param>
    /// <param name="reason">The reason, if any.</param>
    /// <param name="duration">How long the action lasts, if it has a duration.</param>
    /// <param name="act">The punishment itself.</param>
    /// <param name="dm">
    ///     Sends the DM in place of the custom one, given the case number and returning whether it failed. Used by
    ///     commands that fall back to a default DM.
    /// </param>
    /// <returns>The case, or null if it could not be saved, and whether the DM failed.</returns>
    public Task<(ModCase? Case, bool DmFailed)> PunishAsync(SocketGuild guild, IUser user, IUser moderator,
        ModCaseAction action, string? reason, TimeSpan? duration, Func<Task> act, Func<int?, Task<bool>>? dm = null)
    {
        return PunishByIdAsync(guild, user.Id, user, moderator, action, reason, duration, act, dm);
    }

    /// <summary>
    ///     Same as <see cref="PunishAsync" /> for a member who may not be resolvable, in which case no DM is sent.
    /// </summary>
    /// <param name="guild">The guild the action happens in.</param>
    /// <param name="userId">The member being punished.</param>
    /// <param name="user">The member, or null if they could not be fetched.</param>
    /// <param name="moderator">The moderator taking the action.</param>
    /// <param name="action">The action taken.</param>
    /// <param name="reason">The reason, if any.</param>
    /// <param name="duration">How long the action lasts, if it has a duration.</param>
    /// <param name="act">The punishment itself.</param>
    /// <param name="dm">Sends the DM in place of the custom one.</param>
    public async Task<(ModCase? Case, bool DmFailed)> PunishByIdAsync(SocketGuild guild, ulong userId, IUser? user,
        IUser moderator, ModCaseAction action, string? reason, TimeSpan? duration, Func<Task> act,
        Func<int?, Task<bool>>? dm = null)
    {
        var entry = await RecordAsync(guild.Id, userId, moderator.Id, action, reason, duration).ConfigureAwait(false);
        bool dmFailed;
        if (dm is not null)
        {
            dmFailed = await dm(entry?.CaseNumber).ConfigureAwait(false);
        }
        else if (user is null)
        {
            dmFailed = false;
        }
        else
        {
            var sent = await SendPunishDmAsync(guild, user, moderator, action, reason, duration, entry?.CaseNumber)
                .ConfigureAwait(false);
            dmFailed = sent == false;
        }

        try
        {
            await act().ConfigureAwait(false);
        }
        catch
        {
            if (entry is not null)
                await DeleteCaseAsync(guild.Id, entry.CaseNumber).ConfigureAwait(false);
            throw;
        }

        return (entry, dmFailed);
    }

    /// <summary>
    ///     Sends the custom DM for an action, or runs the default DM when none is set.
    /// </summary>
    /// <param name="guild">The guild the action happened in.</param>
    /// <param name="user">The member to message.</param>
    /// <param name="moderator">The moderator who took the action.</param>
    /// <param name="action">The action taken.</param>
    /// <param name="reason">The reason, if any.</param>
    /// <param name="duration">How long the action lasts, if it has a duration.</param>
    /// <param name="caseNumber">The number of the recorded case, if any.</param>
    /// <param name="defaultDm">Sends the default DM.</param>
    /// <returns>Whether the DM failed.</returns>
    public async Task<bool> SendDmOrDefaultAsync(SocketGuild guild, IUser user, IUser moderator, ModCaseAction action,
        string? reason, TimeSpan? duration, int? caseNumber, Func<Task> defaultDm)
    {
        try
        {
            var custom = await SendPunishDmAsync(guild, user, moderator, action, reason, duration, caseNumber)
                .ConfigureAwait(false);
            if (custom is not null)
                return !custom.Value;

            await defaultDm().ConfigureAwait(false);
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    ///     Replaces the punishment DM placeholders in a template.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="server">The community name.</param>
    /// <param name="user">The member being punished.</param>
    /// <param name="reason">The reason.</param>
    /// <param name="moderator">The moderator.</param>
    /// <param name="duration">The duration, or null when the action has none.</param>
    /// <param name="caseNumber">The case number, or null when no case was saved.</param>
    public static string RenderPunishDm(string template, string server, string user, string reason, string moderator,
        TimeSpan? duration, int? caseNumber)
    {
        var length = duration is { } d && d > TimeSpan.Zero ? d.Humanize(3) : "-";
        return template
            .Replace("%server%", server, StringComparison.OrdinalIgnoreCase)
            .Replace("%ban.user.fullname%", user, StringComparison.OrdinalIgnoreCase)
            .Replace("%ban.user%", user, StringComparison.OrdinalIgnoreCase)
            .Replace("%user%", user, StringComparison.OrdinalIgnoreCase)
            .Replace("%ban.reason%", reason, StringComparison.OrdinalIgnoreCase)
            .Replace("%reason%", reason, StringComparison.OrdinalIgnoreCase)
            .Replace("%ban.mod.fullname%", moderator, StringComparison.OrdinalIgnoreCase)
            .Replace("%ban.mod%", moderator, StringComparison.OrdinalIgnoreCase)
            .Replace("%mod%", moderator, StringComparison.OrdinalIgnoreCase)
            .Replace("%ban.duration%", length, StringComparison.OrdinalIgnoreCase)
            .Replace("%duration%", length, StringComparison.OrdinalIgnoreCase)
            .Replace("%case%", caseNumber?.ToString() ?? "-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Sends the guild's custom DM for an action, if one is set.
    /// </summary>
    /// <param name="guild">The guild the action happened in.</param>
    /// <param name="user">The member to message.</param>
    /// <param name="moderator">The moderator who took the action.</param>
    /// <param name="action">The action taken.</param>
    /// <param name="reason">The reason, if any.</param>
    /// <param name="duration">How long the action lasts, if it has a duration.</param>
    /// <param name="caseNumber">The number of the recorded case, if any.</param>
    /// <returns>Null if no custom DM is set, otherwise whether the member was messaged or the DM is turned off.</returns>
    public async Task<bool?> SendPunishDmAsync(SocketGuild guild, IUser user, IUser moderator, ModCaseAction action,
        string? reason, TimeSpan? duration = null, int? caseNumber = null)
    {
        string? template;
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
            template = action == ModCaseAction.Ban
                ? await db.BanTemplates
                    .Where(x => x.GuildId == guild.Id)
                    .Select(x => x.Text)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false)
                : await db.PunishDmTemplates
                    .Where(x => x.GuildId == guild.Id && x.Action == action)
                    .Select(x => x.Template)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load the {Action} DM in {GuildId}", action, guild.Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(template))
            return null;

        if (template == "-")
            return true;

        try
        {
            var shown = await ResolveReasonAsync(guild.Id, reason).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(shown) || shown == "-")
                shown = "No reason given";

            var replacer = new ReplacementBuilder()
                .WithServer(client, guild)
                .Build();

            var text = replacer.Replace(RenderPunishDm(template, guild.Name, user.ToString(), shown,
                moderator.ToString(), duration, caseNumber));
            if (action == ModCaseAction.Ban)
            {
                text = text
                    .Replace("%ban.user.name%", user.Username, StringComparison.OrdinalIgnoreCase)
                    .Replace("%ban.mod.name%", moderator.Username, StringComparison.OrdinalIgnoreCase)
                    .Replace("%ban.user.discrim%", user.Discriminator, StringComparison.OrdinalIgnoreCase)
                    .Replace("%ban.mod.discrim%", moderator.Discriminator, StringComparison.OrdinalIgnoreCase);
            }

            if (!SmartEmbed.TryParse(text, guild.Id, out var embeds, out var plainText)
                || (embeds is null && plainText is null))
            {
                embeds = [new EmbedBuilder().WithErrorColor().WithDescription(text).Build()];
                plainText = null;
            }

            var channel = await user.CreateDMChannelAsync().ConfigureAwait(false);
            await channel.SendAsync(plainText, embeds: embeds).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not send the {Action} DM to {UserId} in {GuildId}", action, user.Id, guild.Id);
            return false;
        }
    }

    private Task OnUserMuted(SocketGuildMember user, IUser mod, MuteType type, string reason)
    {
        return RecordAsync(user.Guild.Id, user.Id, mod.Id, ModCaseAction.Mute, reason);
    }

    private Task OnUserUnmuted(SocketGuildMember user, IUser mod, MuteType type, string reason)
    {
        return RecordAsync(user.Guild.Id, user.Id, mod.Id, ModCaseAction.Unmute, reason);
    }
}
