using DataModel;
using Mewdeko.Modules.RoleMenus.Services;

namespace Mewdeko.Modules.RoleMenus.Common;

/// <summary>
///     The full desired state of a role menu, used for creating and replacing menus.
/// </summary>
public class RoleMenuDraft
{
    /// <summary>
    ///     Internal name shown in lists and as the default message title. Blank means the default name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Channel the message lives in.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Raw message source: plain text or embed builder JSON. Null or empty means the default message.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    ///     Pick any or pick one.
    /// </summary>
    public RoleMenuMode Mode { get; set; }

    /// <summary>
    ///     Fewest roles from this menu a member must keep once they have picked.
    /// </summary>
    public int MinRoles { get; set; }

    /// <summary>
    ///     Most roles from this menu a member can hold. Zero means no limit.
    /// </summary>
    public int MaxRoles { get; set; }

    /// <summary>
    ///     Role a member must have to use the menu. Null or zero means anyone.
    /// </summary>
    public ulong? RequiredRoleId { get; set; }

    /// <summary>
    ///     Roles that stop a member from using the menu.
    /// </summary>
    public List<ulong> BlockedRoleIds { get; set; } = [];

    /// <summary>
    ///     Seconds a role from this menu lasts before it is taken back. Zero means it stays.
    /// </summary>
    public int TempRoleSeconds { get; set; }

    /// <summary>
    ///     When the menu deletes its message and turns itself off, in UTC. Null means never.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    ///     Group this menu is linked into. Null means the menu stands alone.
    /// </summary>
    public int? GroupId { get; set; }

    /// <summary>
    ///     RGB colour of the default embed. Null means the bot's usual colour.
    /// </summary>
    public int? AccentColor { get; set; }

    /// <summary>
    ///     Short note or no note.
    /// </summary>
    public RoleMenuReplyMode ReplyMode { get; set; }

    /// <summary>
    ///     False when the menu is paused.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Options in display order.
    /// </summary>
    public List<RoleMenuOptionDraft> Options { get; set; } = [];

    /// <summary>
    ///     Copies a stored menu into a draft, keeping option IDs so an update edits the same rows.
    /// </summary>
    /// <param name="menu">The stored menu with its options loaded.</param>
    /// <returns>A draft describing the menu as it is now.</returns>
    public static RoleMenuDraft From(RoleMenu menu)
    {
        return new RoleMenuDraft
        {
            Name = menu.Name,
            ChannelId = menu.ChannelId,
            Message = menu.Message,
            Mode = (RoleMenuMode)menu.Mode,
            MinRoles = menu.MinRoles,
            MaxRoles = menu.MaxRoles,
            RequiredRoleId = menu.RequiredRoleId,
            BlockedRoleIds = RoleMenuService.ParseRoleIds(menu.BlockedRoleIds),
            TempRoleSeconds = menu.TempRoleSeconds,
            ExpiresAt = menu.ExpiresAt,
            GroupId = menu.GroupId,
            AccentColor = menu.AccentColor,
            ReplyMode = (RoleMenuReplyMode)menu.ReplyMode,
            Enabled = menu.Enabled,
            Options = menu.Options
                .OrderBy(x => x.Position)
                .ThenBy(x => x.Id)
                .Select(x => new RoleMenuOptionDraft
                {
                    Id = x.Id,
                    RoleId = x.RoleId,
                    Label = x.Label,
                    Emoji = x.Emoji,
                    Description = x.Description,
                    TempRoleSeconds = x.TempRoleSeconds,
                    MaxHolders = x.MaxHolders
                })
                .ToList()
        };
    }
}

/// <summary>
///     The desired state of one option on a role menu.
/// </summary>
public class RoleMenuOptionDraft
{
    /// <summary>
    ///     An existing option ID to keep, or null for a new option.
    /// </summary>
    public int? Id { get; set; }

    /// <summary>
    ///     The role this option gives and takes.
    /// </summary>
    public ulong RoleId { get; set; }

    /// <summary>
    ///     Text on the option. Blank means the role name.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    ///     Unicode emoji or custom emoji text. Null gets the next free number emoji.
    /// </summary>
    public string? Emoji { get; set; }

    /// <summary>
    ///     Short text shown beside the option in the default message.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Seconds this role lasts before it is taken back. Zero uses the menu's setting.
    /// </summary>
    public int TempRoleSeconds { get; set; }

    /// <summary>
    ///     Most members who can hold this role through the menu. Zero means no limit.
    /// </summary>
    public int MaxHolders { get; set; }
}

/// <summary>
///     Settings for moving an older emoji role setup into a new role menu.
/// </summary>
public class RoleMenuImportDraft
{
    /// <summary>
    ///     ID of the older setup to import.
    /// </summary>
    public int SourceId { get; set; }

    /// <summary>
    ///     Channel for the new menu. Null or zero means the older setup's channel.
    /// </summary>
    public ulong? ChannelId { get; set; }

    /// <summary>
    ///     Name for the new menu. Blank means the default name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Whether to copy the older message's text and embeds into the new menu.
    /// </summary>
    public bool CopyMessage { get; set; } = true;

    /// <summary>
    ///     Whether to remove the older setup once the new menu is posted.
    /// </summary>
    public bool RetireOriginal { get; set; } = true;
}
