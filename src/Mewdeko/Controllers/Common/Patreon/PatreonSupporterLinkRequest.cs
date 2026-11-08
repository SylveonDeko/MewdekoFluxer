namespace Mewdeko.Controllers.Common.Patreon;

/// <summary>
///     Request model for linking a Patreon supporter to a community member
/// </summary>
public class PatreonSupporterLinkRequest
{
    /// <summary>
    ///     Patreon user ID of the supporter
    /// </summary>
    public string PatreonUserId { get; set; } = null!;

    /// <summary>
    ///     User ID to link, 0 to unlink
    /// </summary>
    public ulong DiscordUserId { get; set; }
}
