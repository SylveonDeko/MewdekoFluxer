namespace Mewdeko.Modules.RoleMenus.Common;

/// <summary>
///     How many roles a member can pick from one menu.
/// </summary>
public enum RoleMenuMode
{
    /// <summary>
    ///     Members can pick any number of roles, within the menu's limits.
    /// </summary>
    Multi = 0,

    /// <summary>
    ///     Members hold one role at a time. Picking another swaps it.
    /// </summary>
    Exclusive = 1,

    /// <summary>
    ///     Reacting only adds roles. Taking the reaction off does not remove them.
    /// </summary>
    Verify = 2,

    /// <summary>
    ///     Reacting only removes roles. Roles can't be added from the menu.
    /// </summary>
    RemoveOnly = 3,

    /// <summary>
    ///     Reacting takes the role away and taking the reaction off gives it back.
    /// </summary>
    Reverse = 4
}

/// <summary>
///     Whether members get a note after using a menu.
/// </summary>
public enum RoleMenuReplyMode
{
    /// <summary>
    ///     A short note that deletes itself lists the roles that were added and removed.
    /// </summary>
    Note = 0,

    /// <summary>
    ///     No note unless something went wrong.
    /// </summary>
    Silent = 1
}

/// <summary>
///     The current state of a menu's posted message.
/// </summary>
public enum RoleMenuStatus
{
    /// <summary>
    ///     The message is posted and members can use it.
    /// </summary>
    Live,

    /// <summary>
    ///     The message is posted but reactions on it are ignored.
    /// </summary>
    Paused,

    /// <summary>
    ///     The menu has no posted message.
    /// </summary>
    NotPosted,

    /// <summary>
    ///     The menu's channel no longer exists.
    /// </summary>
    ChannelMissing
}

/// <summary>
///     Why a role can't be offered on a menu.
/// </summary>
public enum RoleMenuRoleProblem
{
    /// <summary>
    ///     The role can be offered.
    /// </summary>
    None,

    /// <summary>
    ///     The role no longer exists.
    /// </summary>
    Missing,

    /// <summary>
    ///     The role is the server's everyone role.
    /// </summary>
    Everyone,

    /// <summary>
    ///     The role is at or above the bot's highest role, or the bot lacks Manage Roles.
    /// </summary>
    AboveBot,

    /// <summary>
    ///     The role is at or above the highest role of the person editing the menu.
    /// </summary>
    AboveActor
}

/// <summary>
///     Why a role menu write was refused.
/// </summary>
public enum RoleMenuError
{
    /// <summary>
    ///     No error.
    /// </summary>
    None,

    /// <summary>
    ///     The menu does not exist in this server.
    /// </summary>
    NotFound,

    /// <summary>
    ///     The menu name is too long.
    /// </summary>
    NameInvalid,

    /// <summary>
    ///     The menu has no options.
    /// </summary>
    NoOptions,

    /// <summary>
    ///     The menu has more options than allowed.
    /// </summary>
    TooManyOptions,

    /// <summary>
    ///     A role is on the menu more than once.
    /// </summary>
    DuplicateRole,

    /// <summary>
    ///     A role on the menu no longer exists.
    /// </summary>
    RoleMissing,

    /// <summary>
    ///     The bot can't give out a role on the menu.
    /// </summary>
    RoleNotAssignable,

    /// <summary>
    ///     A role on the menu is at or above the editor's highest role.
    /// </summary>
    RoleAboveActor,

    /// <summary>
    ///     An option's emoji can't be used by the bot.
    /// </summary>
    InvalidEmoji,

    /// <summary>
    ///     The channel is missing, the wrong type, or the bot can't post there.
    /// </summary>
    InvalidChannel,

    /// <summary>
    ///     A text field is too long.
    /// </summary>
    TextTooLong,

    /// <summary>
    ///     The required role does not exist or is the everyone role.
    /// </summary>
    InvalidRequiredRole,

    /// <summary>
    ///     A reorder did not list every option exactly once.
    /// </summary>
    InvalidOrder,

    /// <summary>
    ///     A duration or expiry time is out of range or already past.
    /// </summary>
    InvalidDuration,

    /// <summary>
    ///     The group does not exist in this community.
    /// </summary>
    InvalidGroup,

    /// <summary>
    ///     The accent colour is out of range.
    /// </summary>
    InvalidColor,

    /// <summary>
    ///     The message could not be posted.
    /// </summary>
    PostFailed,

    /// <summary>
    ///     The older emoji role setup to import was not found.
    /// </summary>
    ImportSourceNotFound,

    /// <summary>
    ///     None of the roles in the older setup exist anymore.
    /// </summary>
    ImportNoRoles
}
