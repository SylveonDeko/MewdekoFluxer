using DataModel;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.GlobalList.Common;
using Mewdeko.Modules.GlobalList.Services;

namespace Mewdeko.Modules.GlobalList;

/// <summary>
///     Module for the global list of reported users.
/// </summary>
public class GlobalList : MewdekoModuleBase<GlobalListService>
{
    /// <summary>
    ///     Reports a user to the global list. Message links in the reason and attached images count as evidence.
    /// </summary>
    /// <param name="userId">The ID of the user to report.</param>
    /// <param name="category">Scamming, Raiding, SpamBot, Harassment, Nsfw, Impersonation or Other.</param>
    /// <param name="reason">The reason, with at least one message link or an attached screenshot.</param>
    /// <example>.reportuser 1234567890 Scamming Sent fake gift links https://fluxer.app/channels/1/2/3</example>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task ReportUser(ulong userId, GlobalListCategory category, [Remainder] string reason)
    {
        var guild = (SocketGuild)ctx.Guild;
        var member = guild.GetUser(ctx.User.Id);
        if (member is null || !GlobalListService.IsStaff(guild, member))
        {
            await ErrorAsync(Strings.GloballistStaffOnly(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        var links = GlobalListRules.FindMessageLinks(reason);
        var screenshots = new List<byte[]>();
        foreach (var attachment in ctx.Message.Attachments ?? [])
        {
            if (string.IsNullOrEmpty(attachment.Url) || screenshots.Count >= GlobalListRules.MaxScreenshots)
                continue;

            if (await Service.DownloadImageAsync(attachment.Url).ConfigureAwait(false) is { } bytes)
                screenshots.Add(bytes);
        }

        var (error, entryId) = await Service.ReportAsync(ctx.Guild.Id, userId, category, reason, links, screenshots)
            .ConfigureAwait(false);
        if (error == ReportError.None)
        {
            await ConfirmAsync(Strings.GloballistReported(ctx.Guild.Id, userId, entryId)).ConfigureAwait(false);
            return;
        }

        await ErrorAsync(ReportErrorText(ctx.Guild.Id, error)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Checks whether a user is on the global list.
    /// </summary>
    /// <param name="userId">The ID of the user to check.</param>
    /// <example>.listcheck 1234567890</example>
    [Cmd]
    [Aliases]
    public async Task ListCheck(ulong userId)
    {
        var entry = await Service.GetLiveEntryAsync(userId).ConfigureAwait(false);
        if (entry is null)
        {
            await ConfirmAsync(Strings.GloballistCheckClear(ctx.Guild?.Id, userId)).ConfigureAwait(false);
            return;
        }

        await ConfirmAsync(Strings.GloballistCheckListed(ctx.Guild?.Id, userId, entry.Category, entry.Id))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Appeals your own entry on the global list. Send it in a DM to the bot.
    /// </summary>
    /// <param name="text">Why the entry should be removed.</param>
    /// <example>.listappeal My account was hacked and I have secured it.</example>
    [Cmd]
    [Aliases]
    public async Task ListAppeal([Remainder] string text)
    {
        if (ctx.Guild is not null)
        {
            await ErrorAsync(Strings.GloballistAppealDmOnly(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        var (error, appealId) = await Service.AppealAsync(ctx.User.Id, text).ConfigureAwait(false);
        switch (error)
        {
            case AppealError.None:
                await ConfirmAsync(Strings.GloballistAppealSent(null, appealId)).ConfigureAwait(false);
                break;
            case AppealError.NotListed:
                await ErrorAsync(Strings.GloballistAppealNotListed(null)).ConfigureAwait(false);
                break;
            case AppealError.AlreadyOpen:
                await ErrorAsync(Strings.GloballistAppealOpen(null)).ConfigureAwait(false);
                break;
            default:
                await ErrorAsync(Strings.GloballistAppealNoText(null)).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    ///     Lists the entries and appeals waiting for review.
    /// </summary>
    /// <example>.listqueue</example>
    [Cmd]
    [Aliases]
    public async Task ListQueue()
    {
        if (!await Service.CanReviewAsync(ctx.User.Id).ConfigureAwait(false))
        {
            await ErrorAsync(Strings.GloballistNotReviewer(ctx.Guild?.Id)).ConfigureAwait(false);
            return;
        }

        var queue = await Service.GetQueueAsync().ConfigureAwait(false);
        if (queue.Entries.Count == 0 && queue.Appeals.Count == 0)
        {
            await ConfirmAsync(Strings.GloballistQueueEmpty(ctx.Guild?.Id)).ConfigureAwait(false);
            return;
        }

        var entries = queue.Entries.Take(15).Select(x =>
            Strings.GloballistQueueEntry(ctx.Guild?.Id, x.Id, x.UserId, x.Category, x.ReportCount, x.ConfirmVotes,
                x.DisputeVotes));
        var appeals = queue.Appeals.Take(15).Select(x =>
            Strings.GloballistQueueAppeal(ctx.Guild?.Id, x.Id, x.EntryId, x.UserId));
        await ConfirmAsync(string.Join("\n", entries.Concat(appeals))).ConfigureAwait(false);
    }

    /// <summary>
    ///     Approves a pending entry.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="note">An optional note.</param>
    /// <example>.listapprove 12 Clear evidence</example>
    [Cmd]
    [Aliases]
    public async Task ListApprove(int entryId, [Remainder] string? note = null)
    {
        await ReviewEntryAsync(entryId, true, note).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rejects a pending entry.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="note">An optional note.</param>
    /// <example>.listreject 12 Not enough evidence</example>
    [Cmd]
    [Aliases]
    public async Task ListReject(int entryId, [Remainder] string? note = null)
    {
        await ReviewEntryAsync(entryId, false, note).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a live entry from the global list. Bot owners only.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="note">An optional note.</param>
    /// <example>.listremove 12 Listed by mistake</example>
    [Cmd]
    [Aliases]
    [OwnerOnly]
    public async Task ListRemove(int entryId, [Remainder] string? note = null)
    {
        var (error, _) = await Service.RemoveAsync(entryId, note, ctx.User.Id).ConfigureAwait(false);
        if (error == ReviewError.None)
            await ConfirmAsync(Strings.GloballistRemoved(ctx.Guild?.Id, entryId)).ConfigureAwait(false);
        else
            await ErrorAsync(ReviewErrorText(ctx.Guild?.Id, error)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Approves an appeal, which removes the entry.
    /// </summary>
    /// <param name="appealId">The appeal ID.</param>
    /// <param name="note">An optional note.</param>
    /// <example>.listappealapprove 4 Account recovered</example>
    [Cmd]
    [Aliases]
    public async Task ListAppealApprove(int appealId, [Remainder] string? note = null)
    {
        await ReviewAppealAsync(appealId, true, note).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rejects an appeal.
    /// </summary>
    /// <param name="appealId">The appeal ID.</param>
    /// <param name="note">An optional note.</param>
    /// <example>.listappealreject 4 Evidence still stands</example>
    [Cmd]
    [Aliases]
    public async Task ListAppealReject(int appealId, [Remainder] string? note = null)
    {
        await ReviewAppealAsync(appealId, false, note).ConfigureAwait(false);
    }

    private async Task ReviewEntryAsync(int entryId, bool approve, string? note)
    {
        if (!await Service.CanReviewAsync(ctx.User.Id).ConfigureAwait(false))
        {
            await ErrorAsync(Strings.GloballistNotReviewer(ctx.Guild?.Id)).ConfigureAwait(false);
            return;
        }

        var (error, _) = await Service.ReviewAsync(entryId, approve, note, ctx.User.Id, Service.IsOwner(ctx.User.Id))
            .ConfigureAwait(false);
        if (error != ReviewError.None)
        {
            await ErrorAsync(ReviewErrorText(ctx.Guild?.Id, error)).ConfigureAwait(false);
            return;
        }

        await ConfirmAsync(approve
            ? Strings.GloballistApproved(ctx.Guild?.Id, entryId)
            : Strings.GloballistRejected(ctx.Guild?.Id, entryId)).ConfigureAwait(false);
    }

    private async Task ReviewAppealAsync(int appealId, bool approve, string? note)
    {
        if (!await Service.CanReviewAsync(ctx.User.Id).ConfigureAwait(false))
        {
            await ErrorAsync(Strings.GloballistNotReviewer(ctx.Guild?.Id)).ConfigureAwait(false);
            return;
        }

        var error = await Service.ReviewAppealAsync(appealId, approve, note, ctx.User.Id).ConfigureAwait(false);
        if (error != ReviewError.None)
        {
            await ErrorAsync(ReviewErrorText(ctx.Guild?.Id, error)).ConfigureAwait(false);
            return;
        }

        await ConfirmAsync(approve
            ? Strings.GloballistAppealApproved(ctx.Guild?.Id, appealId)
            : Strings.GloballistAppealRejected(ctx.Guild?.Id, appealId)).ConfigureAwait(false);
    }

    private string ReviewErrorText(ulong? guildId, ReviewError error)
    {
        return error switch
        {
            ReviewError.NotFound => Strings.GloballistNotFound(guildId),
            ReviewError.AlreadyDecided => Strings.GloballistAlreadyDecided(guildId),
            _ => Strings.GloballistWrongState(guildId)
        };
    }

    private string ReportErrorText(ulong guildId, ReportError error)
    {
        return error switch
        {
            ReportError.NotOptedIn => Strings.GloballistReportNotOptedIn(guildId),
            ReportError.Blocked => Strings.GloballistReportBlocked(guildId),
            ReportError.DailyCap => Strings.GloballistReportDailyCap(guildId, GlobalListRules.DailyReportCap),
            ReportError.NoEvidence => Strings.GloballistReportNoEvidence(guildId),
            ReportError.NoReason => Strings.GloballistReportNoReason(guildId),
            ReportError.InvalidTarget => Strings.GloballistReportInvalidTarget(guildId),
            ReportError.AlreadyListed => Strings.GloballistReportAlreadyListed(guildId),
            ReportError.AlreadyReported => Strings.GloballistReportAlreadyReported(guildId),
            _ => Strings.GloballistReportInvalidImage(guildId)
        };
    }
}
