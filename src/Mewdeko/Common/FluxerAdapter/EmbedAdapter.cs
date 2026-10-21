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
}
