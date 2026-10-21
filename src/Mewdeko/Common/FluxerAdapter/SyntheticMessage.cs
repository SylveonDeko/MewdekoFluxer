namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Builds messages that only exist in memory, for features that run as if a user had written something, such as
///     chat triggers fired by events and permission checks without a real message.
/// </summary>
public static class SyntheticMessage
{
    /// <summary>
    ///     Creates a message from the given author in the given channel. Its id is zero, which is how
    ///     IsSynthetic tells it apart from a real message.
    /// </summary>
    public static Message Create(IUser author, Channel channel, string? content = null) =>
        Message.Create(FluxerContext.Client, new MessageJson
        {
            ChannelId = channel.Id,
            Author = new UserJson
            {
                Id = author.Id,
                Username = author.Username,
                Discriminator = author.Discriminator,
                DisplayName = author.DisplayName,
                AvatarHash = author.AvatarHash,
                AvatarColor = author.AvatarColor,
                Flags = author.Flags,
                IsBot = author.IsBot,
                IsSystem = author.IsSystem
            },
            Content = content ?? "",
            CreatedAt = DateTimeOffset.UtcNow
        });

    /// <summary>
    ///     Creates a message from a member in the given channel.
    /// </summary>
    public static Message Create(GuildMember author, Channel channel, string? content = null) =>
        Create(author.User, channel, content);

    extension(Message message)
    {
        /// <summary>
        ///     Whether the message was built in memory instead of received from Fluxer.
        /// </summary>
        public bool IsSynthetic => message.Id == 0;
    }
}
