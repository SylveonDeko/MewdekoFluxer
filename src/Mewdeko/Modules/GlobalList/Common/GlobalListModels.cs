using DataModel;

namespace Mewdeko.Modules.GlobalList.Common;

/// <summary>
///     Why a report was refused.
/// </summary>
public enum ReportError
{
    /// <summary>
    ///     Nothing is wrong.
    /// </summary>
    None,

    /// <summary>
    ///     The community has not opted in.
    /// </summary>
    NotOptedIn,

    /// <summary>
    ///     Too many of the community's reports were rejected.
    /// </summary>
    Blocked,

    /// <summary>
    ///     The community used up its reports for the day.
    /// </summary>
    DailyCap,

    /// <summary>
    ///     The report has no message link or screenshot.
    /// </summary>
    NoEvidence,

    /// <summary>
    ///     The reason is missing.
    /// </summary>
    NoReason,

    /// <summary>
    ///     The user ID is not one that can be reported.
    /// </summary>
    InvalidTarget,

    /// <summary>
    ///     The user is already on the live list.
    /// </summary>
    AlreadyListed,

    /// <summary>
    ///     The community already reported this user.
    /// </summary>
    AlreadyReported,

    /// <summary>
    ///     A screenshot could not be read, or there are too many.
    /// </summary>
    InvalidImage
}

/// <summary>
///     Why a vote was refused.
/// </summary>
public enum VoteError
{
    /// <summary>
    ///     Nothing is wrong.
    /// </summary>
    None,

    /// <summary>
    ///     The community has not opted in.
    /// </summary>
    NotOptedIn,

    /// <summary>
    ///     The entry does not exist or is no longer open.
    /// </summary>
    NotOpen,

    /// <summary>
    ///     The community reported the entry or already voted on it.
    /// </summary>
    NotAllowed
}

/// <summary>
///     Why a review was refused.
/// </summary>
public enum ReviewError
{
    /// <summary>
    ///     Nothing is wrong.
    /// </summary>
    None,

    /// <summary>
    ///     The entry or appeal does not exist.
    /// </summary>
    NotFound,

    /// <summary>
    ///     It was already decided and the reviewer cannot override that.
    /// </summary>
    AlreadyDecided,

    /// <summary>
    ///     The entry is not in a state that allows this.
    /// </summary>
    WrongState
}

/// <summary>
///     Why an appeal was refused.
/// </summary>
public enum AppealError
{
    /// <summary>
    ///     Nothing is wrong.
    /// </summary>
    None,

    /// <summary>
    ///     The user is not on the live list.
    /// </summary>
    NotListed,

    /// <summary>
    ///     The entry already has an open appeal.
    /// </summary>
    AlreadyOpen,

    /// <summary>
    ///     The appeal has no text.
    /// </summary>
    NoText
}

/// <summary>
///     A community's global list settings, with a value for every category.
/// </summary>
public class GlobalListSettingsView
{
    /// <summary>
    ///     Gets or sets whether the community has opted in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Gets or sets the log channel, or 0.
    /// </summary>
    public ulong LogChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the quarantine role, or 0.
    /// </summary>
    public ulong QuarantineRoleId { get; set; }

    /// <summary>
    ///     Gets or sets whether approving an entry also acts on current members.
    /// </summary>
    public bool ApplyToExisting { get; set; }

    /// <summary>
    ///     Gets or sets the action for each category.
    /// </summary>
    public Dictionary<GlobalListCategory, GlobalListAction> Actions { get; set; } = new();
}

/// <summary>
///     A change to a community's settings. Null fields stay as they are, and 0 clears a channel or role.
/// </summary>
public class GlobalListSettingsPatch
{
    /// <summary>
    ///     Gets or sets whether the community opts in.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     Gets or sets the log channel, or 0 to clear it.
    /// </summary>
    public ulong? LogChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the quarantine role, or 0 to clear it.
    /// </summary>
    public ulong? QuarantineRoleId { get; set; }

    /// <summary>
    ///     Gets or sets whether approving an entry also acts on current members.
    /// </summary>
    public bool? ApplyToExisting { get; set; }

    /// <summary>
    ///     Gets or sets the actions to change.
    /// </summary>
    public Dictionary<GlobalListCategory, GlobalListAction>? Actions { get; set; }
}

/// <summary>
///     A community's reporting record and today's usage.
/// </summary>
public class GlobalListStandingView
{
    /// <summary>
    ///     Gets or sets how many reports were approved.
    /// </summary>
    public int Approved { get; set; }

    /// <summary>
    ///     Gets or sets how many reports were rejected.
    /// </summary>
    public int Rejected { get; set; }

    /// <summary>
    ///     Gets or sets whether the community can no longer report.
    /// </summary>
    public bool Blocked { get; set; }

    /// <summary>
    ///     Gets or sets how many reports the community made in the last 24 hours.
    /// </summary>
    public int ReportsToday { get; set; }

    /// <summary>
    ///     Gets or sets the daily report cap.
    /// </summary>
    public int DailyCap { get; set; } = GlobalListRules.DailyReportCap;
}

/// <summary>
///     The settings page data: settings and standing together.
/// </summary>
public class GlobalListOverview
{
    /// <summary>
    ///     Gets or sets the settings.
    /// </summary>
    public GlobalListSettingsView Settings { get; set; } = new();

    /// <summary>
    ///     Gets or sets the reporting standing.
    /// </summary>
    public GlobalListStandingView Standing { get; set; } = new();
}

/// <summary>
///     One entry in a list, without its reports.
/// </summary>
public class GlobalListEntryItem
{
    /// <summary>
    ///     Gets or sets the entry ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the listed user's ID.
    /// </summary>
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the category.
    /// </summary>
    public GlobalListCategory Category { get; set; }

    /// <summary>
    ///     Gets or sets the status.
    /// </summary>
    public GlobalListStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets how many communities reported the user.
    /// </summary>
    public int ReportCount { get; set; }

    /// <summary>
    ///     Gets or sets how many communities confirmed.
    /// </summary>
    public int ConfirmVotes { get; set; }

    /// <summary>
    ///     Gets or sets how many communities disputed.
    /// </summary>
    public int DisputeVotes { get; set; }

    /// <summary>
    ///     Gets or sets whether the asking community is one of the reporters.
    /// </summary>
    public bool ReportedByMe { get; set; }

    /// <summary>
    ///     Gets or sets the asking community's vote: true confirms, false disputes, null has not voted.
    /// </summary>
    public bool? MyVote { get; set; }

    /// <summary>
    ///     Gets or sets the reviewer's note.
    /// </summary>
    public string? DecisionNote { get; set; }

    /// <summary>
    ///     Gets or sets when it was decided.
    /// </summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    ///     Gets or sets when it was created.
    /// </summary>
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     One report on an entry.
/// </summary>
public class GlobalListReportView
{
    /// <summary>
    ///     Gets or sets the report ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the reporting guild's ID.
    /// </summary>
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets the reporting guild's name, if the bot can see it.
    /// </summary>
    public string? GuildName { get; set; }

    /// <summary>
    ///     Gets or sets the category.
    /// </summary>
    public GlobalListCategory Category { get; set; }

    /// <summary>
    ///     Gets or sets the reason.
    /// </summary>
    public string Reason { get; set; } = "";

    /// <summary>
    ///     Gets or sets the message links.
    /// </summary>
    public List<string> Links { get; set; } = [];

    /// <summary>
    ///     Gets or sets the screenshot IDs.
    /// </summary>
    public List<int> ImageIds { get; set; } = [];

    /// <summary>
    ///     Gets or sets when it was made.
    /// </summary>
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     One vote on an entry.
/// </summary>
public class GlobalListVoteView
{
    /// <summary>
    ///     Gets or sets the voting guild's ID.
    /// </summary>
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets the voting guild's name, if the bot can see it.
    /// </summary>
    public string? GuildName { get; set; }

    /// <summary>
    ///     Gets or sets whether the vote confirms.
    /// </summary>
    public bool Confirm { get; set; }

    /// <summary>
    ///     Gets or sets the note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    ///     Gets or sets when it was cast.
    /// </summary>
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     One appeal.
/// </summary>
public class GlobalListAppealView
{
    /// <summary>
    ///     Gets or sets the appeal ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the entry ID.
    /// </summary>
    public int EntryId { get; set; }

    /// <summary>
    ///     Gets or sets the appealing user's ID.
    /// </summary>
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the entry's category.
    /// </summary>
    public GlobalListCategory Category { get; set; }

    /// <summary>
    ///     Gets or sets the appeal text.
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>
    ///     Gets or sets the status.
    /// </summary>
    public GlobalListAppealStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets the reviewer's note.
    /// </summary>
    public string? DecisionNote { get; set; }

    /// <summary>
    ///     Gets or sets when it was made.
    /// </summary>
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     An entry with its reports, votes and appeals.
/// </summary>
public class GlobalListEntryDetail
{
    /// <summary>
    ///     Gets or sets the entry.
    /// </summary>
    public GlobalListEntryItem Entry { get; set; } = new();

    /// <summary>
    ///     Gets or sets the reports.
    /// </summary>
    public List<GlobalListReportView> Reports { get; set; } = [];

    /// <summary>
    ///     Gets or sets the votes.
    /// </summary>
    public List<GlobalListVoteView> Votes { get; set; } = [];

    /// <summary>
    ///     Gets or sets the appeals.
    /// </summary>
    public List<GlobalListAppealView> Appeals { get; set; } = [];
}

/// <summary>
///     What waits for a reviewer.
/// </summary>
public class GlobalListQueue
{
    /// <summary>
    ///     Gets or sets the pending entries, oldest first.
    /// </summary>
    public List<GlobalListEntryItem> Entries { get; set; } = [];

    /// <summary>
    ///     Gets or sets the open appeals, oldest first.
    /// </summary>
    public List<GlobalListAppealView> Appeals { get; set; } = [];
}

/// <summary>
///     A community's standing, for the owner page.
/// </summary>
public class GlobalListCommunityStanding
{
    /// <summary>
    ///     Gets or sets the guild ID.
    /// </summary>
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets the guild name, if the bot can see it.
    /// </summary>
    public string? GuildName { get; set; }

    /// <summary>
    ///     Gets or sets how many reports were approved.
    /// </summary>
    public int Approved { get; set; }

    /// <summary>
    ///     Gets or sets how many reports were rejected.
    /// </summary>
    public int Rejected { get; set; }

    /// <summary>
    ///     Gets or sets whether the community can no longer report.
    /// </summary>
    public bool Blocked { get; set; }

    /// <summary>
    ///     Gets or sets when an owner last reset the counts.
    /// </summary>
    public DateTime? ResetAt { get; set; }
}

/// <summary>
///     A reviewer appointed by an owner.
/// </summary>
public class GlobalListReviewerView
{
    /// <summary>
    ///     Gets or sets the reviewer's user ID.
    /// </summary>
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the owner who appointed them.
    /// </summary>
    public ulong AddedBy { get; set; }

    /// <summary>
    ///     Gets or sets when they were appointed.
    /// </summary>
    public DateTime DateAdded { get; set; }
}
