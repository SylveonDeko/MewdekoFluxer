namespace Mewdeko.Controllers.Common.Giveaways;

/// <summary>
///     A guild's giveaway defaults.
/// </summary>
public class GiveawaySettings
{
    /// <summary>
    ///     Gets or sets the emote members react or press to enter.
    /// </summary>
    public string? Emote { get; set; }

    /// <summary>
    ///     Gets or sets the role pinged when a giveaway starts, or 0 for none.
    /// </summary>
    public ulong PingRole { get; set; }

    /// <summary>
    ///     Gets or sets the banner image URL.
    /// </summary>
    public string? Banner { get; set; }

    /// <summary>
    ///     Gets or sets the embed colour as a hex value.
    /// </summary>
    public string? EmbedColor { get; set; }

    /// <summary>
    ///     Gets or sets whether winners get a DM.
    /// </summary>
    public bool DmOnWin { get; set; }

    /// <summary>
    ///     Gets or sets the custom message sent to winners.
    /// </summary>
    public string? EndMessage { get; set; }
}
