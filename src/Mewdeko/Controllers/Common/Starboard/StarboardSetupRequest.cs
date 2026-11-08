namespace Mewdeko.Controllers.Common.Starboard;

/// <summary>
///     Request model for creating a starboard together with its channel.
/// </summary>
public class StarboardSetupRequest
{
    /// <summary>
    ///     Gets or sets the name of the channel to create.
    /// </summary>
    public string Name { get; set; } = "starboard";

    /// <summary>
    ///     Gets or sets the emote members react with.
    /// </summary>
    public string Emote { get; set; } = "⭐";

    /// <summary>
    ///     Gets or sets the number of stars a message needs.
    /// </summary>
    public int Threshold { get; set; } = 1;
}

/// <summary>
///     Request model for setting how old a message has to be, and how old it may be, to count.
/// </summary>
public class StarboardMessageAgeRequest
{
    /// <summary>
    ///     Gets or sets the minimum age in minutes, or null to keep it.
    /// </summary>
    public int? MinMinutes { get; set; }

    /// <summary>
    ///     Gets or sets the maximum age in minutes, or null to keep it.
    /// </summary>
    public int? MaxMinutes { get; set; }
}

/// <summary>
///     Request model for sending a design preview.
/// </summary>
public class StarboardDesignTestRequest
{
    /// <summary>
    ///     Gets or sets the channel to send the preview to and to take the sample message from.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the message to preview with, or null for a recent one.
    /// </summary>
    public ulong? MessageId { get; set; }
}
