namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Embed builder members Mewdeko relies on that Fluxer.Net does not have.
/// </summary>
public static class EmbedAdapter
{
    extension(EmbedBuilder embed)
    {
        /// <summary>
        ///     Sets the author to a user's name and avatar.
        /// </summary>
        public EmbedBuilder WithAuthor(IUser user) =>
            embed.WithAuthor(user.GetCurrentName(), user.GetAvatarOrDefaultUrl(160));

        /// <summary>
        ///     Sets the author to a member's name and avatar, using their guild avatar when they have one.
        /// </summary>
        public EmbedBuilder WithAuthor(GuildMember member) =>
            embed.WithAuthor(member.GetCurrentName(), member.GetDisplayAvatarUrl());
    }

    /// <summary>
    ///     Copies a received embed into an embed builder so it can be edited and resent.
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
