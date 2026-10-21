namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     The parts of a message an edit changes. Anything left unset stays as it was.
/// </summary>
public sealed class MessageProperties
{
    /// <summary>The new text, or an empty string to clear it.</summary>
    public string? Content { get; set; }

    /// <summary>A single embed that replaces the message's embeds.</summary>
    public EmbedRequest? Embed { get; set; }

    /// <summary>Embeds that replace the message's embeds.</summary>
    public IEnumerable<EmbedRequest>? Embeds { get; set; }

    /// <summary>Which mentions in the new text ping.</summary>
    public AllowedMentionsRequest? AllowedMentions { get; set; }

    /// <summary>Files that replace the message's attachments.</summary>
    public IEnumerable<AttachmentRequest>? Attachments { get; set; }
}

/// <summary>
///     Message members Mewdeko relies on that Fluxer.Net names differently or does not have.
/// </summary>
public static class MessageAdapter
{
    extension(Message message)
    {
        /// <summary>
        ///     The users the message mentions.
        /// </summary>
        public IReadOnlyCollection<User> MentionedUsers => message.Mentions?.ToList() ?? [];

        /// <summary>
        ///     The channel the message was sent in, from the gateway cache.
        /// </summary>
        public Channel? Channel => FluxerContext.Client.Gateway.GetChannel(message.ChannelId);

        /// <summary>
        ///     The guild the message was sent in, or null in a private channel.
        /// </summary>
        public SocketGuild? Guild => message.Channel?.Guild;

        /// <summary>
        ///     When the message was sent.
        /// </summary>
        public DateTimeOffset Timestamp => message.CreatedAt;

        /// <summary>
        ///     When the message was last edited, if ever.
        /// </summary>
        public DateTimeOffset? EditedTimestamp => message.EditedAt;

        /// <summary>
        ///     The ids of the users the message mentions.
        /// </summary>
        public IReadOnlyCollection<ulong> MentionedUserIds => [..message.Mentions?.Select(u => u.Id) ?? []];

        /// <summary>
        ///     The ids of the roles the message mentions.
        /// </summary>
        public IReadOnlyCollection<ulong> MentionedRoleIds => message.MentionRoles ?? [];

        /// <summary>
        ///     The web link to the message.
        /// </summary>
        public string GetJumpUrl() =>
            $"{FluxerContext.WebUrl}/channels/{message.Channel?.GuildId?.ToString() ?? "@me"}/{message.ChannelId}/{message.Id}";

        /// <summary>
        ///     Edits the message, changing only what the callback sets.
        /// </summary>
        public Task<Message> ModifyAsync(Action<MessageProperties> update)
        {
            var properties = new MessageProperties();
            update(properties);

            List<EmbedRequest>? embeds = null;
            if (properties.Embed != null || properties.Embeds != null)
            {
                embeds = [];
                if (properties.Embed != null)
                    embeds.Add(properties.Embed);
                if (properties.Embeds != null)
                    embeds.AddRange(properties.Embeds);
            }

            return message.ModifyAsync(properties.Content, embeds, allowedMentions: properties.AllowedMentions,
                attachments: properties.Attachments?.ToList());
        }

        /// <summary>
        ///     Removes one user's reaction.
        /// </summary>
        public Task RemoveReactionAsync(Emoji emoji, ulong userId) =>
            message.RemoveUserReactionAsync(emoji.IsCustom ? $"{emoji.Name}:{emoji.Id}" : emoji.Name, userId);

        /// <summary>
        ///     Replies to the message with optional text, embeds and mention rules.
        /// </summary>
        public Task<Message> ReplyWithAsync(string? text = null, EmbedRequest? embed = null,
            IEnumerable<EmbedRequest>? embeds = null, AllowedMentionsRequest? allowedMentions = null)
        {
            var allEmbeds = new List<EmbedRequest>();
            if (embed != null)
                allEmbeds.Add(embed);
            if (embeds != null)
                allEmbeds.AddRange(embeds);

            return message.ReplyAsync(string.IsNullOrEmpty(text) ? null : text, allEmbeds.Count > 0 ? allEmbeds : null,
                allowedMentions);
        }
    }

    extension<T>(Task<IEnumerable<T>> task)
    {
        /// <summary>
        ///     Waits for a REST listing. Fluxer.Net already returns the whole page, so there is nothing to flatten.
        /// </summary>
        public async Task<IReadOnlyCollection<T>> FlattenAsync() => (await task.ConfigureAwait(false)).ToList();
    }
}
