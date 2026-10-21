using Serilog;
using Embed = Mewdeko.Common.EmbedData;
using Image = Mewdeko.Common.Image;

namespace Mewdeko.Extensions;

/// <summary>
///     Extension methods for Message.
/// </summary>
public static class MessageExtensions
{
    /// <summary>
    ///     Gets the jump link for the message.
    /// </summary>
    /// <param name="message">The message to get the jump link for.</param>
    /// <returns>The jump link for the message.</returns>
    public static string GetJumpLink(this Message message)
    {
        return message.GetJumpUrl();
    }

    /// <summary>
    ///     Replies to the message with an error embed.
    /// </summary>
    /// <param name="message">The message to reply to.</param>
    /// <param name="content">The error message content.</param>
    public static void ReplyError(this Message message, string content)
    {
        var eb = new EmbedBuilder().WithErrorColor().WithDescription(content);
        message.ReplyAsync(embeds: [eb.Build()]);
    }

    /// <summary>
    ///     Gets the source of the embed from the message.
    /// </summary>
    /// <param name="message">The message to get the embed source from.</param>
    /// <returns>The source of the embed.</returns>
    public static NewEmbed GetNewEmbedSource(this Message message)
    {
        try
        {
            var eb = new NewEmbed
            {
                Embeds = [], Components = []
            };
            var embedList = new List<Embed>();
            eb.Content = message.Content;
            foreach (var i in message.Embeds ?? [])
            {
                if (i.RawType != "rich")
                    continue;
                var e = new Embed
                {
                    Fields = []
                };
                if (i.Title != null)
                    e.Title = i.Title;
                if (i.Description != null)
                    e.Description = i.Description;
                if (i.Url != null)
                    e.Url = i.Url;
                if (i.Author is { } author)
                {
                    e.Author = new Author
                    {
                        Name = author.Name, Url = author.Url, IconUrl = author.IconUrl
                    };
                }

                if (i.Fields is { Length: > 0 })
                {
                    foreach (var field in i.Fields)
                    {
                        e.Fields.Add(new Field
                        {
                            Name = field.Name, Value = field.Value, Inline = field.IsInline
                        });
                    }
                }

                if (i.Footer is { } footer)
                {
                    e.Footer = new Footer
                    {
                        Text = footer.Text, IconUrl = footer.IconUrl ?? ""
                    };
                }

                if (i.Image is { } embedImage)
                {
                    e.Image = new Image
                    {
                        Url = embedImage.Url
                    };
                }

                if (i.Thumbnail is { } embedThumbnail)
                {
                    e.Thumbnail = new Thumbnail
                    {
                        Url = embedThumbnail.Url
                    };
                }

                if (i.Color is { } color)
                {
                    e.Color = new Color((uint)color);
                }

                embedList.Add(e);
            }

            eb.Embeds.AddRange(embedList);

            return eb;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to get embed source");
            return null;
        }
    }
}