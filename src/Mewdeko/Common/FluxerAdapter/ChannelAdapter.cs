using System.IO;
using System.Threading;

namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Channel members Mewdeko relies on that Fluxer.Net names differently or does not have.
/// </summary>
public static class ChannelAdapter
{
    extension(Channel channel)
    {
        /// <summary>
        ///     The guild the channel belongs to, from the gateway cache, or null for a private channel.
        /// </summary>
        public SocketGuild? Guild => channel.GuildId is { } guildId ? FluxerContext.Client.Gateway.GetGuild(guildId) : null;

        /// <summary>
        ///     The category the channel sits in.
        /// </summary>
        public ulong? CategoryId => channel.ParentId;

        /// <summary>
        ///     The slowmode delay in seconds, zero when off.
        /// </summary>
        public int SlowModeInterval => channel.RateLimitPerUser ?? 0;

        /// <summary>
        ///     Whether members can type messages in the channel.
        /// </summary>
        public bool IsTextChannel => channel.IsTextable;

        /// <summary>
        ///     The members currently connected to the voice channel, from the gateway cache.
        /// </summary>
        public IEnumerable<SocketGuildMember> ConnectedUsers =>
            channel.Guild?.Members.Values.Where(m => m.VoiceStates.Values.Any(v => v.ChannelId == channel.Id)) ?? [];

        /// <summary>
        ///     Sends a message with optional text, embeds, mention rules, reply target and files.
        /// </summary>
        public Task<Message> SendAsync(string? text = null, EmbedRequest? embed = null,
            IEnumerable<EmbedRequest>? embeds = null, AllowedMentionsRequest? allowedMentions = null,
            MessageReferenceRequest? messageReference = null, IEnumerable<AttachmentRequest>? attachments = null,
            RequestOptions? options = null)
        {
            var allEmbeds = new List<EmbedRequest>();
            if (embed != null)
                allEmbeds.Add(embed);
            if (embeds != null)
                allEmbeds.AddRange(embeds);

            return channel.SendMessageAsync(string.IsNullOrEmpty(text) ? null : text,
                allEmbeds.Count > 0 ? allEmbeds : null, messageReference, allowedMentions,
                attachments: attachments?.ToList(), options: options);
        }

        /// <summary>
        ///     Sends a file with optional text and embed.
        /// </summary>
        public Task<Message> SendFileAsync(Stream stream, string filename, string? text = null,
            EmbedRequest? embed = null, IEnumerable<EmbedRequest>? embeds = null,
            AllowedMentionsRequest? allowedMentions = null, MessageReferenceRequest? messageReference = null) =>
            channel.SendAsync(text, embed, embeds, allowedMentions, messageReference,
                [new AttachmentRequest(stream, filename)]);

        /// <summary>
        ///     Shows the typing indicator.
        /// </summary>
        public Task TriggerTypingAsync() => channel.TriggerTypingIndicatorAsync();

        /// <summary>
        ///     The category the channel sits in, from the gateway cache.
        /// </summary>
        public Channel? Category => channel.ParentId is { } id ? FluxerContext.Client.GetChannel(id) : null;

        /// <summary>
        ///     The channel a thread was started in, from the gateway cache.
        /// </summary>
        public Channel? ParentChannel => channel.ParentId is { } id ? FluxerContext.Client.GetChannel(id) : null;

        /// <summary>
        ///     Deletes several messages at once.
        /// </summary>
        public Task DeleteMessagesAsync(IEnumerable<ulong> messageIds) =>
            channel.BulkDeleteMessagesAsync([..messageIds]);

        /// <summary>
        ///     Deletes several messages at once.
        /// </summary>
        public Task DeleteMessagesAsync(IEnumerable<Message> messages) =>
            channel.BulkDeleteMessagesAsync([..messages.Select(m => m.Id)]);

        /// <summary>
        ///     Gets a member of the channel's guild, from the cache or the API.
        /// </summary>
        public Task<SocketGuildMember?> GetUserAsync(ulong userId) =>
            channel.Guild is { } guild ? guild.GetMemberAsync(userId) : Task.FromResult<SocketGuildMember?>(null);

        /// <summary>
        ///     Reads the overwrite the channel stores for a role.
        /// </summary>
        public OverwritePermissions? GetPermissionOverwrite(Role role) =>
            channel.GetPermissionOverwrite(role.Id) is { } overwrite ? OverwritePermissions.From(overwrite) : null;

        /// <summary>
        ///     Reads the overwrite the channel stores for a member.
        /// </summary>
        public OverwritePermissions? GetPermissionOverwrite(GuildMember member) =>
            channel.GetPermissionOverwrite(member.Id) is { } overwrite ? OverwritePermissions.From(overwrite) : null;

        /// <summary>
        ///     Creates or replaces the overwrite for a role.
        /// </summary>
        public Task AddPermissionOverwriteAsync(Role role, OverwritePermissions permissions) =>
            channel.AddPermissionOverwriteAsync(role.Id, PermissionOverwriteType.Role, permissions.AllowValue,
                permissions.DenyValue);

        /// <summary>
        ///     Creates or replaces the overwrite for a member.
        /// </summary>
        public Task AddPermissionOverwriteAsync(GuildMember member, OverwritePermissions permissions) =>
            channel.AddPermissionOverwriteAsync(member.Id, PermissionOverwriteType.Member, permissions.AllowValue,
                permissions.DenyValue);

        /// <summary>
        ///     Removes the overwrite for a role.
        /// </summary>
        public Task RemovePermissionOverwriteAsync(Role role) => channel.RemovePermissionOverwriteAsync(role.Id);

        /// <summary>
        ///     Removes the overwrite for a member.
        /// </summary>
        public Task RemovePermissionOverwriteAsync(GuildMember member) =>
            channel.RemovePermissionOverwriteAsync(member.Id);

        /// <summary>
        ///     The web link to the channel.
        /// </summary>
        public string GetJumpUrl() =>
            $"{FluxerContext.WebUrl}/channels/{(channel.GuildId is { } guildId ? guildId.ToString() : "@me")}/{channel.Id}";
    }
}
