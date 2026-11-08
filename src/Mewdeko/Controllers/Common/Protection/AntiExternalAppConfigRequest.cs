using Mewdeko.Modules.Administration.Common;

namespace Mewdeko.Controllers.Common.Protection;

/// <summary>
///     Request to configure anti-external-app protection
/// </summary>
public class AntiExternalAppConfigRequest
{
    /// <summary>
    ///     Whether the protection is enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     The punishment to apply
    /// </summary>
    public PunishmentAction Action { get; set; } = PunishmentAction.Timeout;

    /// <summary>
    ///     The punishment duration in minutes
    /// </summary>
    public int PunishDuration { get; set; } = 60;

    /// <summary>
    ///     The role to give when the action adds a role
    /// </summary>
    public ulong? RoleId { get; set; }

    /// <summary>
    ///     How many mentions one app message may have
    /// </summary>
    public int MentionThreshold { get; set; } = 5;

    /// <summary>
    ///     Whether invite links in app messages count as violations
    /// </summary>
    public bool BlockInvites { get; set; } = true;

    /// <summary>
    ///     How many app messages one member may trigger within the time window
    /// </summary>
    public int MaxMessages { get; set; } = 5;

    /// <summary>
    ///     The time window in seconds
    /// </summary>
    public int TimeWindowSeconds { get; set; } = 10;

    /// <summary>
    ///     Whether offending messages are deleted
    /// </summary>
    public bool DeleteMessages { get; set; } = true;

    /// <summary>
    ///     Whether the member is sent a DM
    /// </summary>
    public bool NotifyUser { get; set; } = true;
}
