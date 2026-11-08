namespace Mewdeko.Controllers.Common.Stickies;

/// <summary>
///     A sticky message in a channel.
/// </summary>
public class StickyDto
{
    /// <summary>
    ///     Gets or sets the channel the sticky is in.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the message text or embed json.
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    ///     Gets or sets how many new messages bring the sticky back to the bottom.
    /// </summary>
    public int Messages { get; set; } = 5;

    /// <summary>
    ///     Gets or sets how many seconds of new chat bring the sticky back to the bottom.
    /// </summary>
    public int Seconds { get; set; } = 15;
}

/// <summary>
///     A saved sticky message.
/// </summary>
public class StickyTemplateDto
{
    /// <summary>
    ///     Gets or sets the template name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Gets or sets the message text or embed json.
    /// </summary>
    public string Content { get; set; } = "";
}

/// <summary>
///     Asks for a template to be stuck to a channel.
/// </summary>
public class UseStickyTemplateRequest
{
    /// <summary>
    ///     Gets or sets the channel to stick the template to.
    /// </summary>
    public ulong ChannelId { get; set; }
}
