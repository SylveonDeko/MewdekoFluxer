namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Webhook client members Mewdeko relies on that Fluxer.Net names differently.
/// </summary>
public static class WebhookAdapter
{
    extension(FluxerWebhookClient webhook)
    {
        /// <summary>
        ///     Sends a message through the webhook and returns its id.
        /// </summary>
        public async Task<ulong> SendAsync(string? text = null, bool isTTS = false,
            IEnumerable<EmbedRequest>? embeds = null, string? username = null, string? avatarUrl = null,
            AllowedMentionsRequest? allowedMentions = null, IEnumerable<AttachmentRequest>? attachments = null)
        {
            var message = await webhook.SendMessageWaitAsync(string.IsNullOrEmpty(text) ? null : text,
                embeds?.ToList(), username, avatarUrl, allowedMentions: allowedMentions, tts: isTTS ? true : null,
                attachments: attachments?.ToList()).ConfigureAwait(false);
            return message.Id;
        }

        /// <summary>
        ///     Edits a message the webhook sent. Anything left unset stays as it was.
        /// </summary>
        public Task<Message> ModifyMessageAsync(ulong messageId, Action<MessageProperties> update)
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

            return webhook.EditMessageAsync(messageId, properties.Content, embeds,
                allowedMentions: properties.AllowedMentions, attachments: properties.Attachments?.ToList());
        }
    }
}
