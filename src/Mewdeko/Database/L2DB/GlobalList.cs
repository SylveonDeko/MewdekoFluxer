#nullable enable

using LinqToDB.Mapping;

namespace DataModel;

/// <summary>
///     Why a user is on the global list.
/// </summary>
public enum GlobalListCategory
{
    /// <summary>
    ///     Scams and fraud.
    /// </summary>
    Scamming = 0,

    /// <summary>
    ///     Joining to raid communities.
    /// </summary>
    Raiding = 1,

    /// <summary>
    ///     Spam accounts.
    /// </summary>
    SpamBot = 2,

    /// <summary>
    ///     Harassing members.
    /// </summary>
    Harassment = 3,

    /// <summary>
    ///     Posting adult content where it does not belong.
    /// </summary>
    Nsfw = 4,

    /// <summary>
    ///     Pretending to be someone else.
    /// </summary>
    Impersonation = 5,

    /// <summary>
    ///     Anything else.
    /// </summary>
    Other = 6
}

/// <summary>
///     What a community does when a listed user joins.
/// </summary>
public enum GlobalListAction
{
    /// <summary>
    ///     Does nothing.
    /// </summary>
    Ignore = 0,

    /// <summary>
    ///     Posts to the log channel only.
    /// </summary>
    Alert = 1,

    /// <summary>
    ///     Gives the member the quarantine role.
    /// </summary>
    QuarantineRole = 2,

    /// <summary>
    ///     Kicks the member.
    /// </summary>
    Kick = 3,

    /// <summary>
    ///     Bans the member.
    /// </summary>
    Ban = 4
}

/// <summary>
///     Where a global list entry stands.
/// </summary>
public enum GlobalListStatus
{
    /// <summary>
    ///     Waiting for a reviewer.
    /// </summary>
    Pending = 0,

    /// <summary>
    ///     Approved, so communities act on it.
    /// </summary>
    Live = 1,

    /// <summary>
    ///     Turned down by a reviewer.
    /// </summary>
    Rejected = 2,

    /// <summary>
    ///     Taken off the list.
    /// </summary>
    Removed = 3
}

/// <summary>
///     Where an appeal stands.
/// </summary>
public enum GlobalListAppealStatus
{
    /// <summary>
    ///     Waiting for a reviewer.
    /// </summary>
    Open = 0,

    /// <summary>
    ///     Accepted and removed.
    /// </summary>
    Approved = 1,

    /// <summary>
    ///     Turned down.
    /// </summary>
    Rejected = 2
}

/// <summary>
///     Represents a community's global list settings.
/// </summary>
[Table("GlobalListSettings")]
public class GlobalListSetting
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the guild ID these settings belong to.
    /// </summary>
    [Column("GuildId")]
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets whether the community has opted in.
    /// </summary>
    [Column("Enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    ///     Gets or sets the channel actions are logged in.
    /// </summary>
    [Column("LogChannelId")]
    public ulong? LogChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the role given by the quarantine action.
    /// </summary>
    [Column("QuarantineRoleId")]
    public ulong? QuarantineRoleId { get; set; }

    /// <summary>
    ///     Gets or sets whether approving an entry also acts on members already in the community.
    /// </summary>
    [Column("ApplyToExisting")]
    public bool ApplyToExisting { get; set; }

    /// <summary>
    ///     Gets or sets when the row was created.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     Represents the action a community picked for one category.
/// </summary>
[Table("GlobalListCategoryActions")]
public class GlobalListCategoryAction
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the guild ID.
    /// </summary>
    [Column("GuildId")]
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets the category.
    /// </summary>
    [Column("Category")]
    public GlobalListCategory Category { get; set; }

    /// <summary>
    ///     Gets or sets the action.
    /// </summary>
    [Column("Action")]
    public GlobalListAction Action { get; set; }
}

/// <summary>
///     Represents a listed user, with every report about them attached.
/// </summary>
[Table("GlobalListEntries")]
public class GlobalListEntry
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the listed user's ID.
    /// </summary>
    [Column("UserId")]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the category communities act on.
    /// </summary>
    [Column("Category")]
    public GlobalListCategory Category { get; set; }

    /// <summary>
    ///     Gets or sets where the entry stands.
    /// </summary>
    [Column("Status")]
    public GlobalListStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets the reviewer's note on the decision.
    /// </summary>
    [Column("DecisionNote")]
    public string? DecisionNote { get; set; }

    /// <summary>
    ///     Gets or sets who decided.
    /// </summary>
    [Column("DecidedBy")]
    public ulong? DecidedBy { get; set; }

    /// <summary>
    ///     Gets or sets when the decision was made.
    /// </summary>
    [Column("DecidedAt")]
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    ///     Gets or sets who removed the entry.
    /// </summary>
    [Column("RemovedBy")]
    public ulong? RemovedBy { get; set; }

    /// <summary>
    ///     Gets or sets when the entry was removed.
    /// </summary>
    [Column("RemovedAt")]
    public DateTime? RemovedAt { get; set; }

    /// <summary>
    ///     Gets or sets the note on the removal.
    /// </summary>
    [Column("RemovedNote")]
    public string? RemovedNote { get; set; }

    /// <summary>
    ///     Gets or sets whether the entry was removed by an approved appeal.
    /// </summary>
    [Column("RemovedByAppeal")]
    public bool RemovedByAppeal { get; set; }

    /// <summary>
    ///     Gets or sets when the entry was created.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     Represents one community's report on an entry.
/// </summary>
[Table("GlobalListReports")]
public class GlobalListReport
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the entry the report belongs to.
    /// </summary>
    [Column("EntryId")]
    public int EntryId { get; set; }

    /// <summary>
    ///     Gets or sets the reporting guild's ID.
    /// </summary>
    [Column("GuildId")]
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets the category the community picked.
    /// </summary>
    [Column("Category")]
    public GlobalListCategory Category { get; set; }

    /// <summary>
    ///     Gets or sets the reason.
    /// </summary>
    [Column("Reason")]
    public string Reason { get; set; } = "";

    /// <summary>
    ///     Gets or sets the message links, one per line.
    /// </summary>
    [Column("Evidence")]
    public string Evidence { get; set; } = "";

    /// <summary>
    ///     Gets or sets when the report was made.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     Represents a screenshot attached to a report.
/// </summary>
[Table("GlobalListEvidenceImages")]
public class GlobalListEvidenceImage
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the report the screenshot belongs to.
    /// </summary>
    [Column("ReportId")]
    public int ReportId { get; set; }

    /// <summary>
    ///     Gets or sets the screenshot as PNG.
    /// </summary>
    [Column("Data")]
    public byte[] Data { get; set; } = [];

    /// <summary>
    ///     Gets or sets when the screenshot was stored.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     Represents a community's advisory vote on a pending entry.
/// </summary>
[Table("GlobalListVotes")]
public class GlobalListVote
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the entry voted on.
    /// </summary>
    [Column("EntryId")]
    public int EntryId { get; set; }

    /// <summary>
    ///     Gets or sets the voting guild's ID.
    /// </summary>
    [Column("GuildId")]
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets whether the vote confirms the report. False disputes it.
    /// </summary>
    [Column("Confirm")]
    public bool Confirm { get; set; }

    /// <summary>
    ///     Gets or sets the optional note.
    /// </summary>
    [Column("Note")]
    public string? Note { get; set; }

    /// <summary>
    ///     Gets or sets when the vote was cast.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     Represents a user appointed to review entries.
/// </summary>
[Table("GlobalListReviewers")]
public class GlobalListReviewer
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the reviewer's user ID.
    /// </summary>
    [Column("UserId")]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the owner who appointed them.
    /// </summary>
    [Column("AddedBy")]
    public ulong AddedBy { get; set; }

    /// <summary>
    ///     Gets or sets when they were appointed.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     Represents how many of a community's reports were approved and rejected.
/// </summary>
[Table("GlobalListStandings")]
public class GlobalListStanding
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the guild ID.
    /// </summary>
    [Column("GuildId")]
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets how many reports were approved.
    /// </summary>
    [Column("Approved")]
    public int Approved { get; set; }

    /// <summary>
    ///     Gets or sets how many reports were rejected.
    /// </summary>
    [Column("Rejected")]
    public int Rejected { get; set; }

    /// <summary>
    ///     Gets or sets when an owner last reset the counts.
    /// </summary>
    [Column("ResetAt")]
    public DateTime? ResetAt { get; set; }
}

/// <summary>
///     Represents a listed user's appeal.
/// </summary>
[Table("GlobalListAppeals")]
public class GlobalListAppeal
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the entry appealed.
    /// </summary>
    [Column("EntryId")]
    public int EntryId { get; set; }

    /// <summary>
    ///     Gets or sets the appealing user's ID.
    /// </summary>
    [Column("UserId")]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the appeal text.
    /// </summary>
    [Column("Text")]
    public string Text { get; set; } = "";

    /// <summary>
    ///     Gets or sets where the appeal stands.
    /// </summary>
    [Column("Status")]
    public GlobalListAppealStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets the reviewer's note.
    /// </summary>
    [Column("DecisionNote")]
    public string? DecisionNote { get; set; }

    /// <summary>
    ///     Gets or sets who decided.
    /// </summary>
    [Column("DecidedBy")]
    public ulong? DecidedBy { get; set; }

    /// <summary>
    ///     Gets or sets when the decision was made.
    /// </summary>
    [Column("DecidedAt")]
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    ///     Gets or sets when the appeal was made.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
