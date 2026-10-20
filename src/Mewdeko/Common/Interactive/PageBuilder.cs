namespace Mewdeko.Common.Interactive;

/// <summary>
///     Builds one page of a paginator: an embed plus optional message text.
/// </summary>
public sealed class PageBuilder
{
    private readonly EmbedBuilder embed;

    /// <summary>
    ///     Creates an empty page.
    /// </summary>
    public PageBuilder() : this(new EmbedBuilder())
    {
    }

    private PageBuilder(EmbedBuilder embed)
    {
        this.embed = embed;
    }

    /// <summary>
    ///     The message text sent alongside the embed.
    /// </summary>
    public string? Text { get; private set; }

    /// <summary>
    ///     The page title.
    /// </summary>
    public string? Title
    {
        get
        {
            return embed.Title;
        }
    }

    /// <summary>
    ///     The page description.
    /// </summary>
    public string? Description
    {
        get
        {
            return embed.Description;
        }
    }

    /// <summary>
    ///     The page fields.
    /// </summary>
    public List<EmbedFieldBuilder> Fields
    {
        get
        {
            return embed.Fields;
        }
    }

    /// <summary>
    ///     Creates a page from an existing embed builder.
    /// </summary>
    /// <param name="builder">The embed builder to wrap.</param>
    /// <returns>A page that renders the given embed.</returns>
    public static PageBuilder FromEmbedBuilder(EmbedBuilder builder)
    {
        return new PageBuilder(builder);
    }

    /// <summary>
    ///     Sets the message text sent alongside the embed.
    /// </summary>
    public PageBuilder WithText(string? text)
    {
        Text = text;
        return this;
    }

    /// <summary>
    ///     Sets the title.
    /// </summary>
    public PageBuilder WithTitle(string? title)
    {
        embed.WithTitle(title);
        return this;
    }

    /// <summary>
    ///     Sets the description.
    /// </summary>
    public PageBuilder WithDescription(string? description)
    {
        embed.WithDescription(description);
        return this;
    }

    /// <summary>
    ///     Sets the title link.
    /// </summary>
    public PageBuilder WithUrl(string? url)
    {
        embed.WithUrl(url);
        return this;
    }

    /// <summary>
    ///     Sets the color.
    /// </summary>
    public PageBuilder WithColor(Color color)
    {
        embed.WithColor(color);
        return this;
    }

    /// <summary>
    ///     Sets the large image.
    /// </summary>
    public PageBuilder WithImageUrl(string? url)
    {
        embed.WithImageUrl(url);
        return this;
    }

    /// <summary>
    ///     Sets the thumbnail.
    /// </summary>
    public PageBuilder WithThumbnailUrl(string? url)
    {
        embed.WithThumbnailUrl(url);
        return this;
    }

    /// <summary>
    ///     Sets the timestamp.
    /// </summary>
    public PageBuilder WithTimestamp(DateTimeOffset timestamp)
    {
        embed.WithTimestamp(timestamp);
        return this;
    }

    /// <summary>
    ///     Sets the timestamp to now.
    /// </summary>
    public PageBuilder WithCurrentTimestamp()
    {
        embed.WithCurrentTimestamp();
        return this;
    }

    /// <summary>
    ///     Sets the author from a name and optional icon and link.
    /// </summary>
    public PageBuilder WithAuthor(string name, string? iconUrl = null, string? url = null)
    {
        embed.WithAuthor(name, iconUrl, url);
        return this;
    }

    /// <summary>
    ///     Sets the author from a user.
    /// </summary>
    public PageBuilder WithAuthor(IUser user)
    {
        embed.WithAuthor(user);
        return this;
    }

    /// <summary>
    ///     Sets the author from a builder.
    /// </summary>
    public PageBuilder WithAuthor(EmbedAuthorBuilder author)
    {
        embed.WithAuthor(author);
        return this;
    }

    /// <summary>
    ///     Sets the author through a callback.
    /// </summary>
    public PageBuilder WithAuthor(Action<EmbedAuthorBuilder> action)
    {
        embed.WithAuthor(action);
        return this;
    }

    /// <summary>
    ///     Sets the footer from text and an optional icon.
    /// </summary>
    public PageBuilder WithFooter(string text, string? iconUrl = null)
    {
        embed.WithFooter(text, iconUrl);
        return this;
    }

    /// <summary>
    ///     Sets the footer from a builder.
    /// </summary>
    public PageBuilder WithFooter(EmbedFooterBuilder footer)
    {
        embed.WithFooter(footer);
        return this;
    }

    /// <summary>
    ///     Sets the footer through a callback.
    /// </summary>
    public PageBuilder WithFooter(Action<EmbedFooterBuilder> action)
    {
        embed.WithFooter(action);
        return this;
    }

    /// <summary>
    ///     Adds a field.
    /// </summary>
    public PageBuilder AddField(string name, object? value, bool inline = false)
    {
        embed.AddField(name, value, inline);
        return this;
    }

    /// <summary>
    ///     Adds a field from a builder.
    /// </summary>
    public PageBuilder AddField(EmbedFieldBuilder field)
    {
        embed.AddField(field);
        return this;
    }

    /// <summary>
    ///     Adds a field through a callback.
    /// </summary>
    public PageBuilder AddField(Action<EmbedFieldBuilder> action)
    {
        embed.AddField(action);
        return this;
    }

    /// <summary>
    ///     Replaces the fields.
    /// </summary>
    public PageBuilder WithFields(IEnumerable<EmbedFieldBuilder> fields)
    {
        embed.WithFields(fields);
        return this;
    }

    /// <summary>
    ///     Replaces the fields.
    /// </summary>
    public PageBuilder WithFields(params EmbedFieldBuilder[] fields)
    {
        embed.WithFields(fields);
        return this;
    }

    /// <summary>
    ///     Builds the embed for this page, replacing its footer when a footer override is given.
    /// </summary>
    /// <param name="footerOverride">Footer text that replaces the page's own footer, or null to keep it.</param>
    /// <returns>The built embed, or null when the page is text only.</returns>
    public Discord.Embed? BuildEmbed(string? footerOverride = null)
    {
        var hasContent = embed.Length > 0 || embed.ImageUrl is not null || embed.ThumbnailUrl is not null;
        if (!hasContent && footerOverride is null)
            return null;

        if (footerOverride is null)
            return embed.Build();

        var footerIcon = embed.Footer?.IconUrl;
        var previous = embed.Footer;
        embed.WithFooter(footerOverride, footerIcon);
        var built = embed.Build();
        embed.Footer = previous;
        return built;
    }
}
