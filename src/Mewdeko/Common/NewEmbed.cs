using System.Text.Json.Serialization;
using Mewdeko.Common.JsonConverters;
using static Mewdeko.Extensions.StringExtensions;

// ReSharper disable NotNullOrRequiredMemberIsNotInitialized

namespace Mewdeko.Common;

/// <summary>
///     Represents an author of an embed.
/// </summary>
public class Author
{
    /// <summary>
    ///     Gets or sets the name of the author.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets the URL associated with the author.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    ///     Gets or sets the icon URL associated with the author.
    /// </summary>
    [JsonPropertyName("icon_url")]
    public string IconUrl { get; set; }
}

/// <summary>
///     Represents a thumbnail image for an embed.
/// </summary>
public class Thumbnail
{
    /// <summary>
    ///     Gets or sets the URL of the thumbnail image.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; }
}

/// <summary>
///     Represents an image for an embed.
/// </summary>
public class Image
{
    /// <summary>
    ///     Gets or sets the URL of the image.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; }
}

/// <summary>
///     Represents the footer of an embed.
/// </summary>
public class Footer
{
    /// <summary>
    ///     Gets or sets the text of the footer.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; }

    /// <summary>
    ///     Gets or sets the icon URL of the footer.
    /// </summary>
    [JsonPropertyName("icon_url")]
    public string IconUrl { get; set; }
}

/// <summary>
///     Represents a field in an embed.
/// </summary>
public class Field
{
    /// <summary>
    ///     Gets or sets the name of the field.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    ///     Gets or sets the value of the field.
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; }

    /// <summary>
    ///     Gets or sets whether the field is displayed inline.
    /// </summary>
    [JsonPropertyName("inline")]
    public bool Inline { get; set; }
}

/// <summary>
///     Represents an embed message.
/// </summary>
public class Embed
{
    /// <summary>
    ///     Gets or sets the title of the embed.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    ///     Gets or sets the description of the embed.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    ///     Gets or sets the color of the embed.
    /// </summary>
    [JsonPropertyName("color")]
    [JsonConverter(typeof(DiscordColorConverter))]
    public Color? Color { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp of the embed.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }

    /// <summary>
    ///     Gets or sets the URL of the embed.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    ///     Gets or sets the author of the embed.
    /// </summary>
    [JsonPropertyName("author")]
    public Author? Author { get; set; }

    /// <summary>
    ///     Gets or sets the thumbnail image of the embed.
    /// </summary>
    [JsonPropertyName("thumbnail")]
    public Thumbnail? Thumbnail { get; set; }

    /// <summary>
    ///     Gets or sets the image of the embed.
    /// </summary>
    [JsonPropertyName("image")]
    public Image? Image { get; set; }

    /// <summary>
    ///     Gets or sets the footer of the embed.
    /// </summary>
    [JsonPropertyName("footer")]
    public Footer? Footer { get; set; }

    /// <summary>
    ///     Gets or sets the fields of the embed.
    /// </summary>
    [JsonPropertyName("fields")]
    public List<Field>? Fields { get; set; }
}

/// <summary>
///     Represents a new embed message.
/// </summary>
public class NewEmbed
{
    /// <summary>
    ///     Gets or sets the content of the message.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>
    ///     Gets or sets the embed of the message.
    /// </summary>
    [JsonPropertyName("embed")]
    public Embed? Embed { get; set; }

    /// <summary>
    ///     Gets or sets the list of embeds of the message.
    /// </summary>
    [JsonPropertyName("embeds")]
    public List<Embed>? Embeds { get; set; }

    /// <summary>
    ///     Gets or sets the list of components of the message.
    /// </summary>
    [JsonPropertyName("components")]
    public List<NewEmbedComponent>? Components { get; set; }

    /// <summary>
    ///     Gets a value indicating whether the message is valid.
    /// </summary>
    public bool IsValid
    {
        get
        {
            if (Content != null)
                return true;
            if (Embed != null)
                return true;
            if (Embeds != null)
                return true;
            return Components != null;
        }
    }

    /// <summary>
    ///     Gets a value indicating whether the embed is valid.
    /// </summary>
    public bool IsEmbedValid
    {
        get
        {
            return !string.IsNullOrWhiteSpace(Embed?.Description) ||
                   !string.IsNullOrWhiteSpace(Embed?.Url) ||
                   Embed?.Thumbnail != null ||
                   Embed?.Image != null ||
                   Embed?.Footer != null && (!string.IsNullOrWhiteSpace(Embed?.Footer.Text) ||
                                             !string.IsNullOrWhiteSpace(Embed?.Footer.IconUrl)) ||
                   Embed?.Fields is { Count: > 0 };
        }
    }

    /// <summary>
    ///     Renders the link buttons of the message as markdown links, since Fluxer messages cannot carry buttons.
    ///     Buttons without a link and select menus have no text equivalent and are left out.
    /// </summary>
    /// <returns>One markdown link per line, or null when the message has no link buttons.</returns>
    public string? GetLinkText()
    {
        var links = Components?
            .Where(c => !c.IsSelect && !string.IsNullOrWhiteSpace(c.Url) && !string.IsNullOrWhiteSpace(c.DisplayName))
            .OrderBy(c => c.Row)
            .Select(c => $"{(c.Emoji is null ? "" : $"{c.Emoji} ")}[{c.DisplayName}]({c.Url})")
            .ToList();

        return links is { Count: > 0 } ? string.Join("\n", links) : null;
    }

    /// <summary>
    ///     Converts a collection of <see cref="Embed" /> objects to a collection of Discord.NET <see cref="Embed" /> objects.
    /// </summary>
    /// <param name="embeds">The collection of <see cref="Embed" /> objects to convert.</param>
    /// <returns>An array of <see cref="Discord.Embed" /> objects, without embeds that would show nothing.</returns>
    public static Discord.Embed[] ToEmbedArray(IEnumerable<Embed> embeds)
    {
        var toReturn = new List<Discord.Embed>();
        foreach (var i in embeds)
        {
            var embed = new EmbedBuilder();

            if (!string.IsNullOrWhiteSpace(i.Title))
                embed.WithTitle(i.Title.TrimTo(EmbedBuilder.MaxTitleLength));
            if (!string.IsNullOrWhiteSpace(i.Description))
                embed.WithDescription(i.Description.TrimTo(EmbedBuilder.MaxDescriptionLength));
            if (i.Url != null && Uri.IsWellFormedUriString(i.Url, UriKind.Absolute))
                embed.WithUrl(i.Url);
            if (i.Color is not null)
                embed.WithColor(i.Color.Value);
            if (i.Footer != null && !string.IsNullOrWhiteSpace(i.Footer.Text))
            {
                embed.WithFooter(efb =>
                {
                    efb.WithText(i.Footer.Text.TrimTo(EmbedFooterBuilder.MaxFooterTextLength));
                    if (Uri.IsWellFormedUriString(i.Footer.IconUrl, UriKind.Absolute))
                        efb.WithIconUrl(i.Footer.IconUrl);
                });
            }

            if (i.Thumbnail != null && Uri.IsWellFormedUriString(i.Thumbnail.Url, UriKind.Absolute))
                embed.WithThumbnailUrl(i.Thumbnail.Url);
            if (i.Image != null && Uri.IsWellFormedUriString(i.Image.Url, UriKind.Absolute))
                embed.WithImageUrl(i.Image.Url);
            if (i.Author != null && !string.IsNullOrWhiteSpace(i.Author.Name))
            {
                if (!Uri.IsWellFormedUriString(i.Author.IconUrl, UriKind.Absolute))
                    i.Author.IconUrl = null;
                if (!Uri.IsWellFormedUriString(i.Author.Url, UriKind.Absolute))
                    i.Author.Url = null;

                embed.WithAuthor(i.Author.Name.TrimTo(EmbedAuthorBuilder.MaxAuthorNameLength), i.Author.IconUrl,
                    i.Author.Url);
            }

            if (i.Fields != null)
            {
                foreach (var f in i.Fields.Where(f =>
                             !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value)))
                    embed.AddField(efb => efb.WithName(f.Name).WithValue(f.Value).WithIsInline(f.Inline));
            }

            var built = embed.Build();
            if (HasVisibleContent(built))
                toReturn.Add(built);
        }

        return toReturn.ToArray();
    }

    /// <summary>
    ///     Whether an embed shows anything. Discord rejects the whole message when one embed carries nothing but a
    ///     color or a link, so those are left out.
    /// </summary>
    /// <param name="embed">The built embed.</param>
    /// <returns>True when it has a title, description, field, image, thumbnail, author, or footer.</returns>
    public static bool HasVisibleContent(Discord.Embed embed) =>
        !string.IsNullOrWhiteSpace(embed.Title) ||
        !string.IsNullOrWhiteSpace(embed.Description) ||
        embed.Fields.Length > 0 ||
        embed.Image is not null ||
        embed.Thumbnail is not null ||
        embed.Author is not null ||
        embed.Footer is not null;

    /// <summary>
    ///     Represents a component in a new embed message.
    /// </summary>
    public class NewEmbedComponent
    {
        /// <summary>
        ///     Gets or sets the row number (0-4) for this component.
        /// </summary>
        [JsonPropertyName("row")]
        public int Row { get; set; } = 0;

        /// <summary>
        ///     Gets or sets the display name of the component.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        ///     Gets or sets the ID of the component.
        /// </summary>
        public string Id { get; set; } = null!;

        /// <summary>
        ///     Gets or sets the style of the component.
        /// </summary>
        public ButtonStyle Style { get; set; } = ButtonStyle.Primary;

        /// <summary>
        ///     Gets or sets the URL of the component.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        ///     Gets or sets the emoji of the component.
        /// </summary>
        public string? Emoji { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the component is a select menu.
        /// </summary>
        public bool IsSelect { get; set; } = false;

        /// <summary>
        ///     Gets or sets the maximum number of options in the select menu.
        /// </summary>
        public int MaxOptions { get; set; } = 1;

        /// <summary>
        ///     Gets or sets the minimum number of options in the select menu.
        /// </summary>
        public int MinOptions { get; set; } = 1;

        /// <summary>
        ///     Gets or sets the list of options for the select menu.
        /// </summary>
        public List<NewEmbedSelectOption>? Options { get; set; }
    }

    /// <summary>
    ///     Represents an option in a select menu of a new embed message.
    /// </summary>
    public class NewEmbedSelectOption
    {
        /// <summary>
        ///     Gets or sets the ID of the option.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        ///     Gets or sets the name of the option.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Gets or sets the emoji of the option.
        /// </summary>
        public string? Emoji { get; set; }

        /// <summary>
        ///     Gets or sets the description of the option.
        /// </summary>
        public string? Description { get; set; }
    }
}