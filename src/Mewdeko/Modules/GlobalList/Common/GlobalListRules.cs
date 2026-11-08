using System.Text.RegularExpressions;
using DataModel;

namespace Mewdeko.Modules.GlobalList.Common;

/// <summary>
///     What happens to a new report.
/// </summary>
public enum ReportRoute
{
    /// <summary>
    ///     The user has no open entry.
    /// </summary>
    NewEntry,

    /// <summary>
    ///     The user has a pending entry.
    /// </summary>
    MergeIntoPending,

    /// <summary>
    ///     The user is already on the live list.
    /// </summary>
    AlreadyListed,

    /// <summary>
    ///     The community already reported this user on the pending entry.
    /// </summary>
    AlreadyReported
}

/// <summary>
///     The pure rules behind the global list.
/// </summary>
public static partial class GlobalListRules
{
    /// <summary>
    ///     Most reports a community can make in 24 hours.
    /// </summary>
    public const int DailyReportCap = 20;

    /// <summary>
    ///     Fewest rejected reports before a community can lose the right to report.
    /// </summary>
    public const int MinRejectedToBlock = 5;

    /// <summary>
    ///     The rejection rate a community must go over to lose the right to report.
    /// </summary>
    public const double RejectionRateLimit = 0.6;

    /// <summary>
    ///     Most message links one report can carry.
    /// </summary>
    public const int MaxLinks = 10;

    /// <summary>
    ///     Most screenshots one report can carry.
    /// </summary>
    public const int MaxScreenshots = 4;

    /// <summary>
    ///     Longest reason, appeal or note, in characters.
    /// </summary>
    public const int MaxTextLength = 1000;

    /// <summary>
    ///     Longest vote note, in characters.
    /// </summary>
    public const int MaxNoteLength = 200;

    [GeneratedRegex(@"^https?://[^\s/]+/channels/\d+/\d+/\d+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex MessageLinkPattern();

    [GeneratedRegex(@"https?://[^\s<>]+/channels/\d+/\d+/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex MessageLinkSearch();

    /// <summary>
    ///     Whether a community's record bars it from reporting: at least 5 rejected and a rejection rate over 60%.
    /// </summary>
    /// <param name="approved">Reports that were approved.</param>
    /// <param name="rejected">Reports that were rejected.</param>
    public static bool IsBlocked(int approved, int rejected)
    {
        if (rejected < MinRejectedToBlock)
            return false;

        return (double)rejected / (approved + rejected) > RejectionRateLimit;
    }

    /// <summary>
    ///     Whether a community used up its reports for the day.
    /// </summary>
    /// <param name="reportsInLastDay">Reports it made in the last 24 hours.</param>
    public static bool IsOverDailyCap(int reportsInLastDay)
    {
        return reportsInLastDay >= DailyReportCap;
    }

    /// <summary>
    ///     Decides where a new report goes.
    /// </summary>
    /// <param name="open">The user's pending or live entry, if any.</param>
    /// <param name="communityAlreadyReported">Whether the community has a report on that entry.</param>
    public static ReportRoute Route(GlobalListEntry? open, bool communityAlreadyReported)
    {
        if (open is null)
            return ReportRoute.NewEntry;

        if (open.Status == GlobalListStatus.Live)
            return ReportRoute.AlreadyListed;

        return communityAlreadyReported ? ReportRoute.AlreadyReported : ReportRoute.MergeIntoPending;
    }

    /// <summary>
    ///     Whether a link points at a message.
    /// </summary>
    /// <param name="link">The link.</param>
    public static bool IsMessageLink(string link)
    {
        return MessageLinkPattern().IsMatch(link.Trim());
    }

    /// <summary>
    ///     Finds the message links in free text.
    /// </summary>
    /// <param name="text">The text.</param>
    public static List<string> FindMessageLinks(string text)
    {
        return MessageLinkSearch().Matches(text).Select(x => x.Value).Distinct().Take(MaxLinks).ToList();
    }

    /// <summary>
    ///     Keeps the valid message links, once each.
    /// </summary>
    /// <param name="links">The links as sent.</param>
    public static List<string> CleanLinks(IEnumerable<string>? links)
    {
        return (links ?? [])
            .Select(x => x.Trim())
            .Where(IsMessageLink)
            .Distinct()
            .Take(MaxLinks)
            .ToList();
    }

    /// <summary>
    ///     Whether a report has the evidence it needs: a message link or a screenshot.
    /// </summary>
    /// <param name="links">The cleaned links.</param>
    /// <param name="screenshots">How many screenshots came with it.</param>
    public static bool HasEvidence(IReadOnlyCollection<string> links, int screenshots)
    {
        return links.Count > 0 || screenshots > 0;
    }

    /// <summary>
    ///     Whether a community can vote on an entry: it is not one of the reporters and has not voted yet.
    /// </summary>
    /// <param name="reportingGuilds">The guilds that reported the entry.</param>
    /// <param name="votedGuilds">The guilds that already voted.</param>
    /// <param name="guildId">The voting guild.</param>
    public static bool CanVote(IReadOnlyCollection<ulong> reportingGuilds, IReadOnlyCollection<ulong> votedGuilds,
        ulong guildId)
    {
        return !reportingGuilds.Contains(guildId) && !votedGuilds.Contains(guildId);
    }

    /// <summary>
    ///     Whether a user may review: a bot owner or an appointed reviewer.
    /// </summary>
    /// <param name="isOwner">Whether the user is a bot owner.</param>
    /// <param name="isReviewer">Whether the user is an appointed reviewer.</param>
    public static bool CanReview(bool isOwner, bool isReviewer)
    {
        return isOwner || isReviewer;
    }

    /// <summary>
    ///     Trims text and cuts it to a length. Empty text becomes null.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="max">The longest allowed length.</param>
    public static string? Clip(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
