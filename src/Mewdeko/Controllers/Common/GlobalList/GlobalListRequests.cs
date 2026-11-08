using DataModel;

namespace Mewdeko.Controllers.Common.GlobalList;

/// <summary>
///     A report on a user.
/// </summary>
public class GlobalListReportRequest
{
    /// <summary>
    ///     Gets or sets the reported user's ID.
    /// </summary>
    public ulong TargetUserId { get; set; }

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
    ///     Gets or sets the screenshots as base64 or data URIs.
    /// </summary>
    public List<string> Screenshots { get; set; } = [];
}

/// <summary>
///     A vote on a pending entry.
/// </summary>
public class GlobalListVoteRequest
{
    /// <summary>
    ///     Gets or sets whether the vote confirms the report. False disputes it.
    /// </summary>
    public bool Confirm { get; set; }

    /// <summary>
    ///     Gets or sets the optional note.
    /// </summary>
    public string? Note { get; set; }
}

/// <summary>
///     A reviewer's decision.
/// </summary>
public class GlobalListReviewRequest
{
    /// <summary>
    ///     Gets or sets whether to approve. False rejects.
    /// </summary>
    public bool Approve { get; set; }

    /// <summary>
    ///     Gets or sets the note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    ///     Gets or sets a category to list the user under instead of the reported one.
    /// </summary>
    public GlobalListCategory? Category { get; set; }
}

/// <summary>
///     A note sent with an owner action.
/// </summary>
public class GlobalListNoteRequest
{
    /// <summary>
    ///     Gets or sets the note.
    /// </summary>
    public string? Note { get; set; }
}

/// <summary>
///     Whether the signed-in user can review the global list.
/// </summary>
public class GlobalListReviewerStatus
{
    /// <summary>
    ///     Gets or sets a value indicating whether the user is a bot owner or an appointed reviewer.
    /// </summary>
    public bool IsReviewer { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the user is a bot owner.
    /// </summary>
    public bool IsOwner { get; set; }
}

/// <summary>
///     A reviewer to appoint.
/// </summary>
public class GlobalListReviewerRequest
{
    /// <summary>
    ///     Gets or sets the user ID.
    /// </summary>
    public ulong UserId { get; set; }
}

/// <summary>
///     A screenshot served to the dashboard.
/// </summary>
public class GlobalListImageResponse
{
    /// <summary>
    ///     Gets or sets the screenshot as a data URI.
    /// </summary>
    public string Image { get; set; } = "";
}
