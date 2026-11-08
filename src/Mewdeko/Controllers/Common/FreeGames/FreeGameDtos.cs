namespace Mewdeko.Controllers.Common.FreeGames;

/// <summary>
///     A channel that gets free game alerts.
/// </summary>
public class FreeGameAlertDto
{
    /// <summary>
    ///     Gets or sets the channel alerts are posted in.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the role pinged with each alert, or null for no ping.
    /// </summary>
    public ulong? RoleId { get; set; }

    /// <summary>
    ///     Gets or sets whether Epic Games Store games are announced.
    /// </summary>
    public bool Epic { get; set; } = true;

    /// <summary>
    ///     Gets or sets whether Steam games are announced.
    /// </summary>
    public bool Steam { get; set; } = true;

    /// <summary>
    ///     Gets or sets whether GOG games are announced.
    /// </summary>
    public bool Gog { get; set; } = true;
}
