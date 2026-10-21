using System.Globalization;
using Mewdeko.Common.Configs;
using Mewdeko.Services.Strings;

// ReSharper disable NotNullOrRequiredMemberIsNotInitialized

namespace Mewdeko.Common;

/// <summary>
///     Base class for Discord command modules with support for localization and string resources.
/// </summary>
public abstract class MewdekoModule : ModuleBase
{
    /// <summary>
    ///     Gets or sets the CultureInfo for the current module.
    /// </summary>
    protected CultureInfo? CultureInfo { get; set; }

    /// <summary>
    ///     Source generated strings.
    /// </summary>
    public GeneratedBotStrings Strings { get; set; }

    // ReSharper disable once InconsistentNaming
    /// <summary>
    ///     Gets the command context for the current module.
    /// </summary>
    protected CommandContext ctx
    {
        get
        {
            return Context;
        }
    }

    /// <summary>
    ///     Gets or sets the bot configuration for the current module.
    /// </summary>
    public BotConfig Config { get; set; }

    /// <summary>
    ///     Gets or sets the event handler for the current module.
    /// </summary>
    public EventHandler EventHandler { get; set; }

    /// <summary>
    ///     Gets or sets the service that runs reaction prompts and paginators.
    /// </summary>
    public InteractiveService Interactive { get; set; }


    /// <summary>
    ///     Sends an error message to the channel with localized text.
    /// </summary>
    /// <param name="text">The key identifying the text string.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task<Message> ErrorAsync(string? text)
    {
        return ctx.Channel.SendErrorAsync($"{Config.ErrorEmote} {text}", Config);
    }

    /// <summary>
    ///     Sends an error message to the channel with localized text, mentioning the user who invoked the command.
    /// </summary>
    /// <param name="text">The key identifying the text string.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task<Message> ReplyErrorAsync(string? text)
    {
        return ctx.Channel.SendErrorAsync($"{Format.Bold(ctx.User.ToString())} {text}", Config);
    }

    /// <summary>
    ///     Sends a confirmation message to the channel with localized text.
    /// </summary>
    /// <param name="text">The key identifying the text string.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task<Message> ConfirmAsync(string? text)
    {
        return ctx.Channel.SendConfirmAsync(text);
    }

    /// <summary>
    ///     Sends a confirmation message to the channel with localized text.
    /// </summary>
    /// <param name="text">The key identifying the text string.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task<Message> SuccessAsync(string? text)
    {
        return ctx.Channel.SendConfirmAsync($"{Config.SuccessEmote} {text}");
    }

    /// <summary>
    ///     Sends a confirmation message to the channel with localized text.
    /// </summary>
    /// <param name="text">The key identifying the text string.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task<Message> LoadingLocalizedAsync(string? text)
    {
        return ctx.Channel.SendConfirmAsync($"{Config.LoadingEmote} {text}");
    }

    /// <summary>
    ///     Sends a confirmation message to the channel with localized text, mentioning the user who invoked the command.
    /// </summary>
    /// <param name="text">The key identifying the text string.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task<Message> ReplyConfirmAsync(string? text)
    {
        return ctx.Channel.SendConfirmAsync($"{Format.Bold(ctx.User.ToString())} {text}");
    }

    /// <summary>
    ///     Prompts the user with a confirmation message asynchronously.
    /// </summary>
    /// <param name="message">The message to be displayed to the user.</param>
    /// <param name="userid">The ID of the user to prompt.</param>
    /// <returns>A task representing the asynchronous operation, containing a boolean indicating the user's response.</returns>
    public Task<bool> PromptUserConfirmAsync(string message, ulong userid)
    {
        return PromptUserConfirmAsync(new EmbedBuilder().WithOkColor().WithDescription(message), userid);
    }

    /// <summary>
    ///     Prompts the user with a confirmation message asynchronously.
    /// </summary>
    /// <param name="embed">The embed to be displayed to the user.</param>
    /// <param name="userid">The ID of the user to prompt.</param>
    /// <returns>A task representing the asynchronous operation, containing a boolean indicating the user's response.</returns>
    public async Task<bool> PromptUserConfirmAsync(EmbedBuilder embed, ulong userid)
    {
        embed.WithOkColor();
        var msg = await ctx.Channel.SendAsync(embed: embed.Build()).ConfigureAwait(false);
        try
        {
            return await WaitForConfirmAsync(msg, userid).ConfigureAwait(false);
        }
        finally
        {
            _ = msg.DeleteAsync();
        }
    }

    /// <summary>
    ///     Checks if the invoking user has a higher role hierarchy than the target user.
    /// </summary>
    /// <param name="target">The target user to check against.</param>
    /// <param name="displayError">Determines whether to display an error message if hierarchy check fails.</param>
    /// <returns>
    ///     A task representing the asynchronous operation, containing a boolean indicating the result of the hierarchy
    ///     check.
    /// </returns>
    public async Task<bool> CheckRoleHierarchy(SocketGuildMember? target, bool displayError = true)
    {
        var curUser = await ctx.Guild.GetCurrentUserAsync().ConfigureAwait(false);
        var ownerId = Context.Guild.OwnerId;
        var botMaxRole = curUser.GetRoles().Max(r => r.Position);
        var targetMaxRole = target.GetRoles().Max(r => r.Position);
        var modMaxRole = ctx.Member!.GetRoles().Max(r => r.Position);

        var hierarchyCheck = ctx.User.Id == ownerId
            ? botMaxRole > targetMaxRole
            : botMaxRole >= targetMaxRole && modMaxRole > targetMaxRole;

        if (!hierarchyCheck && displayError)
            await ReplyErrorAsync(Strings.Hierarchy(ctx.Guild.Id)).ConfigureAwait(false);

        return hierarchyCheck;
    }

    /// <summary>
    ///     Prompts the user with a confirmation message asynchronously using an existing message.
    /// </summary>
    /// <param name="message">The existing message to modify with the prompt.</param>
    /// <param name="embed">The embed to be displayed to the user.</param>
    /// <param name="userid">The ID of the user to prompt.</param>
    /// <returns>A task representing the asynchronous operation, containing a boolean indicating the user's response.</returns>
    public async Task<bool> PromptUserConfirmAsync(Message message, EmbedBuilder embed, ulong userid)
    {
        embed.WithOkColor();
        await message.ModifyAsync(x => x.Embed = embed.Build()).ConfigureAwait(false);
        return await WaitForConfirmAsync(message, userid).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a prompt that lists each choice beside its reaction, then waits for the user to react with one.
    /// </summary>
    /// <param name="embed">The prompt embed. The choice legend is appended to its description.</param>
    /// <param name="userId">The user whose reaction counts.</param>
    /// <param name="choices">The options to offer.</param>
    /// <returns>The prompt message and the picked value, or a null value when the user did not answer in time.</returns>
    public async Task<(Message Message, string? Value)> PromptReactionChoiceAsync(EmbedBuilder embed,
        ulong userId, params ReactionChoice[] choices)
    {
        var legend = string.Join("  ", choices.Select(c => $"{c.Emoji} {c.Label}"));
        embed.WithDescription(string.IsNullOrWhiteSpace(embed.Description)
            ? legend
            : $"{embed.Description}\n\n{legend}");

        var msg = await ctx.Channel.SendAsync(embed: embed.Build()).ConfigureAwait(false);
        var index = await Interactive.WaitForReactionAsync(msg, userId, choices.Select(c => c.Emoji).ToList())
            .ConfigureAwait(false);
        return (msg, index is { } picked ? choices[picked].Value : null);
    }

    private async Task<bool> WaitForConfirmAsync(Message message, ulong userId)
    {
        var choice = await Interactive.WaitForReactionAsync(message, userId,
            [InteractiveService.Yes, InteractiveService.No]).ConfigureAwait(false);
        return choice == 0;
    }

    /// <summary>
    ///     Gets the next message sent by the user asynchronously.
    /// </summary>
    /// <param name="channelId">The ID of the channel where the message is expected.</param>
    /// <param name="userId">The ID of the user whose message is awaited.</param>
    /// <returns>A task representing the asynchronous operation, containing the user's message content.</returns>
    public async Task<string?>? NextMessageAsync(ulong channelId, ulong userId)
    {
        var userInputTask = new TaskCompletionSource<string>();
        var handler = EventHandler;
        try
        {
            handler.MessageReceived.Subscribe("MewdekoModuleBase", Interaction);
            if (await Task.WhenAny(userInputTask.Task, Task.Delay(60000)).ConfigureAwait(false) !=
                userInputTask.Task)
            {
                return null;
            }

            return await userInputTask.Task.ConfigureAwait(false);
        }
        finally
        {
            handler.MessageReceived.Unsubscribe("MewdekoModuleBase", Interaction);
        }

        async Task Interaction(SocketMessage arg)
        {
            if (arg.Author.Id != userId || arg.Channel.Id != channelId) return;
            userInputTask.TrySetResult(arg.Content);
            try
            {
                await arg.DeleteAsync().ConfigureAwait(false);
            }
            catch
            {
                //Exclude
            }
        }
    }

    /// <summary>
    ///     Gets the next message sent by the user asynchronously, including the message object.
    /// </summary>
    /// <param name="channelId">The ID of the channel where the message is expected.</param>
    /// <param name="userId">The ID of the user whose message is awaited.</param>
    /// <returns>A task representing the asynchronous operation, containing the user's message.</returns>
    public async Task<SocketMessage?>? NextFullMessageAsync(ulong channelId, ulong userId)
    {
        var userInputTask = new TaskCompletionSource<SocketMessage>();
        var handler = EventHandler;
        try
        {
            handler.MessageReceived.Subscribe("MewdekoModuleBase", Interaction);
            if (await Task.WhenAny(userInputTask.Task, Task.Delay(60000)).ConfigureAwait(false) !=
                userInputTask.Task)
            {
                return null;
            }

            return await userInputTask.Task.ConfigureAwait(false);
        }
        finally
        {
            handler.MessageReceived.Unsubscribe("MewdekoModuleBase", Interaction);
        }

        async Task Interaction(SocketMessage arg)
        {
            if (arg.Author.Id != userId || arg.Channel.Id != channelId) return;
            userInputTask.TrySetResult(arg);
            try
            {
                await arg.DeleteAsync().ConfigureAwait(false);
            }
            catch
            {
                //Exclude
            }
        }
    }
}

/// <summary>
///     Base class for Discord command modules with a specified service type and support for localization and string
///     resources.
/// </summary>
/// <typeparam name="TService">The type of service associated with the module.</typeparam>
public abstract class MewdekoModuleBase<TService> : MewdekoModule
{
    /// <summary>
    ///     Gets or sets the service instance associated with the module.
    /// </summary>
    public TService Service { get; set; }
}

/// <summary>
///     Base class for submodules of Discord command modules.
/// </summary>
public abstract class MewdekoSubmodule : MewdekoModule
{
}

/// <summary>
///     Base class for submodules of Discord command modules with a specified service type.
/// </summary>
/// <typeparam name="TService">The type of service associated with the submodule.</typeparam>
public abstract class MewdekoSubmodule<TService> : MewdekoModuleBase<TService>
{
}