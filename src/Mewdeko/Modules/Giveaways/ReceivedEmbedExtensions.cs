namespace Mewdeko.Modules.Giveaways;

/// <summary>
///     Conversion of received Fluxer embeds back into builders so they can be edited and resent.
/// </summary>
internal static class ReceivedEmbedExtensions
{
    /// <summary>
    ///     Copies a received embed into an embed builder.
    /// </summary>
    /// <param name="embed">The received embed, or null for an empty builder.</param>
    /// <returns>A builder holding the same content.</returns>
    public static EmbedBuilder ToEmbedBuilder(this Embed? embed)
    {
        var builder = new EmbedBuilder();
        if (embed is null)
            return builder;

        builder.Title = embed.Title;
        builder.Description = embed.Description;
        builder.Url = embed.Url;
        builder.Color = embed.Color;
        builder.Timestamp = embed.Timestamp;
        builder.ThumbnailUrl = embed.Thumbnail?.Url;
        builder.ImageUrl = embed.Image?.Url;

        if (embed.Author is { Name: not null } author)
            builder.WithAuthor(author.Name, author.IconUrl, author.Url);

        if (embed.Footer is { Text: not null } footer)
            builder.WithFooter(footer.Text, footer.IconUrl);

        foreach (var field in embed.Fields ?? [])
            builder.AddField(field.Name, field.Value, field.IsInline);

        return builder;
    }
}
