
using Mewdeko.Common.Configs;

namespace Mewdeko.Extensions;

/// <summary>
///     Extensions for Channel objects. Used a lot throughout the bot.
/// </summary>
public static class MessageChannelExtensions
{
    /// <summary>
    ///     Sends an embed message to the message channel asynchronously.
    /// </summary>
    /// <param name="ch">The message channel to send the message to.</param>
    /// <param name="embed">The embed builder containing the embed message.</param>
    /// <param name="msg">The optional message content to include.</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> EmbedAsync(this Channel ch, EmbedBuilder embed, string? msg = "")
    {
        return ch.SendAsync(msg, embed: embed.Build());
    }

    /// <summary>
    ///     Sends an error message to the message channel asynchronously.
    /// </summary>
    /// <param name="ch">The message channel to send the message to.</param>
    /// <param name="title">The optional title of the error message.</param>
    /// <param name="error">The error message content.</param>
    /// <param name="url">The optional URL to include in the error message.</param>
    /// <param name="footer">The optional footer text for the error message.</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendErrorAsync(this Channel ch, string? title, string? error,
        string? url = null, string? footer = null)
    {
        var eb = new EmbedBuilder().WithErrorColor().WithDescription(error)
            .WithTitle(title);
        if (url != null && Uri.IsWellFormedUriString(url, UriKind.Absolute))
            eb.WithUrl(url);
        if (!string.IsNullOrWhiteSpace(footer))
            eb.WithFooter(efb => efb.WithText(footer));
        return ch.SendAsync(embed: eb.Build());
    }

    /// <summary>
    ///     Sends an error message to the message channel asynchronously.
    /// </summary>
    /// <param name="ch">The message channel to send the message to.</param>
    /// <param name="error">The error message content.</param>
    /// <param name="config">The bot config</param>
    /// <param name="fields">Optional fields to include in the error message.</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendErrorAsync(
        this Channel ch,
        string? error,
        BotConfig config,
        EmbedFieldBuilder[]? fields = null)
    {
        var eb = new EmbedBuilder().WithErrorColor().WithDescription(error);
        if (fields is not null)
            eb.WithFields(fields);
        return ch.SendAsync(embed: eb.Build());
    }


    /// <summary>
    ///     Sends a confirmation message to the message channel asynchronously.
    /// </summary>
    /// <param name="ch">The message channel to send the message to.</param>
    /// <param name="title">The optional title of the confirmation message.</param>
    /// <param name="text">The text content of the confirmation message.</param>
    /// <param name="url">The optional URL to include in the confirmation message.</param>
    /// <param name="footer">The optional footer text for the confirmation message.</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendConfirmAsync(this Channel ch, string? title, string? text,
        string? url = null, string? footer = null)
    {
        var eb = new EmbedBuilder().WithOkColor().WithDescription(text)
            .WithTitle(title);
        if (url != null && Uri.IsWellFormedUriString(url, UriKind.Absolute))
            eb.WithUrl(url);
        if (!string.IsNullOrWhiteSpace(footer))
            eb.WithFooter(efb => efb.WithText(footer));
        return ch.SendAsync(embed: eb.Build());
    }

    /// <summary>
    ///     Sends a confirmation reply message to the original message asynchronously.
    /// </summary>
    /// <param name="msg">The original message to reply to.</param>
    /// <param name="text">The text content of the confirmation reply message.</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendConfirmReplyAsync(this Message msg, string? text)
    {
        return msg.ReplyAsync(embeds: [new EmbedBuilder().WithOkColor().WithDescription(text).Build()]);
    }

    /// <summary>
    ///     Sends a confirmation message to the webhook client asynchronously.
    /// </summary>
    /// <param name="msg">The webhook client to send the message to.</param>
    /// <param name="text">The text content of the confirmation message.</param>
    /// <returns>A task representing the asynchronous operation, returning the ID of the sent message.</returns>
    public static Task<ulong> SendConfirmAsync(this FluxerWebhookClient msg, string? text)
    {
        return msg.SendAsync(embeds:
        [
            new EmbedBuilder().WithOkColor().WithDescription(text).Build()
        ]);
    }


    /// <summary>
    ///     Sends an error reply message to the original message asynchronously.
    /// </summary>
    /// <param name="msg">The original message to reply to.</param>
    /// <param name="text">The text content of the error reply message.</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendErrorReplyAsync(this Message msg, string? text)
    {
        return msg.ReplyAsync(embeds: [new EmbedBuilder().WithErrorColor().WithDescription(text).Build()]);
    }

    /// <summary>
    ///     Sends a confirmation message to the text channel asynchronously.
    /// </summary>
    /// <param name="ch">The text channel to send the message to.</param>
    /// <param name="text">The text content of the confirmation message.</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendConfirmAsync(this Channel ch, string? text)
    {
        return ch.SendAsync(embed: new EmbedBuilder().WithOkColor().WithDescription(text).Build());
    }


    /// <summary>
    ///     Sends a table message asynchronously to the message channel.
    /// </summary>
    /// <typeparam name="T">The type of items in the table.</typeparam>
    /// <param name="ch">The message channel to send the table message to.</param>
    /// <param name="seed">The seed text for the table message.</param>
    /// <param name="items">The items to display in the table.</param>
    /// <param name="howToPrint">The function that defines how to print each item.</param>
    /// <param name="columns">The number of columns in the table (default is 3).</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendTableAsync<T>(this Channel ch, string seed, IEnumerable<T> items,
        Func<T, string> howToPrint, int columns = 3)
    {
        var i = 0;
        return ch.SendAsync($"""
                                    {seed}```css
                                    {string.Join("\n", items.GroupBy(_ => i++ / columns)
                                        .Select(ig => string.Concat(ig.Select(howToPrint))))}
                                    ```
                                    """);
    }

    /// <summary>
    ///     Sends a table message asynchronously to the message channel.
    /// </summary>
    /// <typeparam name="T">The type of items in the table.</typeparam>
    /// <param name="ch">The message channel to send the table message to.</param>
    /// <param name="items">The items to display in the table.</param>
    /// <param name="howToPrint">The function that defines how to print each item.</param>
    /// <param name="columns">The number of columns in the table (default is 3).</param>
    /// <returns>A task representing the asynchronous operation, returning the sent user message.</returns>
    public static Task<Message> SendTableAsync<T>(this Channel ch, IEnumerable<T> items,
        Func<T, string> howToPrint, int columns = 3)
    {
        return ch.SendTableAsync("", items, howToPrint, columns);
    }

    /// <summary>
    ///     Adds a checkmark reaction to the message asynchronously.
    /// </summary>
    /// <param name="ctx">The command context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task OkAsync(this CommandContext ctx)
    {
        return ctx.Message.AddReactionAsync(Emoji.FromUnicode("✅"));
    }

    /// <summary>
    ///     Adds a cross mark reaction to the message asynchronously.
    /// </summary>
    /// <param name="ctx">The command context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task ErrorAsync(this CommandContext ctx)
    {
        return ctx.Message.AddReactionAsync(Emoji.FromUnicode("❌"));
    }

    /// <summary>
    ///     Adds a warning sign reaction to the message asynchronously.
    /// </summary>
    /// <param name="ctx">The command context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task WarningAsync(this CommandContext ctx)
    {
        return ctx.Message.AddReactionAsync(Emoji.FromUnicode("⚠"));
    }
}