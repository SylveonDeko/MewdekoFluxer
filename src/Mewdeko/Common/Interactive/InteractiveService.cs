using System.Threading;
using Mewdeko.Services.Strings;

namespace Mewdeko.Common.Interactive;

/// <summary>
///     Runs reaction driven paginators and one shot reaction prompts.
/// </summary>
public sealed class InteractiveService : INService
{
    private const string ModuleName = "InteractiveService";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Jumps to the first page.
    /// </summary>
    public static readonly Emoji First = new("⏮️");

    /// <summary>
    ///     Goes back one page.
    /// </summary>
    public static readonly Emoji Back = new("◀️");

    /// <summary>
    ///     Goes forward one page.
    /// </summary>
    public static readonly Emoji Forward = new("▶️");

    /// <summary>
    ///     Jumps to the last page.
    /// </summary>
    public static readonly Emoji Last = new("⏭️");

    /// <summary>
    ///     Stops the paginator.
    /// </summary>
    public static readonly Emoji Stop = new("🛑");

    /// <summary>
    ///     Accepts a confirmation prompt.
    /// </summary>
    public static readonly Emoji Yes = new("✅");

    /// <summary>
    ///     Declines a confirmation prompt.
    /// </summary>
    public static readonly Emoji No = new("❌");

    private static readonly IEmote[] NavigationEmotes = [First, Back, Forward, Last, Stop];

    private readonly DiscordShardedClient client;
    private readonly ILogger<InteractiveService> logger;
    private readonly ConcurrentDictionary<ulong, PromptSession> prompts = new();
    private readonly ConcurrentDictionary<ulong, PaginatorSession> sessions = new();
    private readonly GeneratedBotStrings strings;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InteractiveService" /> class.
    /// </summary>
    /// <param name="client">The Discord client.</param>
    /// <param name="eventHandler">The event handler that delivers reaction events.</param>
    /// <param name="strings">The localized strings, used for the page footer.</param>
    /// <param name="logger">The logger instance.</param>
    public InteractiveService(DiscordShardedClient client, EventHandler eventHandler, GeneratedBotStrings strings,
        ILogger<InteractiveService> logger)
    {
        this.client = client;
        this.strings = strings;
        this.logger = logger;
        eventHandler.Subscribe("ReactionAdded", ModuleName, OnReactionAddedAsync);
        eventHandler.Subscribe("MessageDeleted", ModuleName, OnMessageDeletedAsync);
    }

    /// <summary>
    ///     Sends a paginator and returns once the first page is posted. Page turns are handled in the background.
    /// </summary>
    /// <param name="paginator">The paginator to send.</param>
    /// <param name="channel">The channel to send it to.</param>
    /// <param name="timeout">How long the paginator accepts input, ten minutes when not given.</param>
    /// <returns>The posted message.</returns>
    public async Task<IUserMessage> SendPaginatorAsync(Paginator paginator, IMessageChannel channel,
        TimeSpan? timeout = null)
    {
        var guildId = (channel as IGuildChannel)?.GuildId ?? 0;
        var first = await paginator.PageFactory(0).ConfigureAwait(false);
        var message = await channel.SendMessageAsync(first.Text,
                embed: first.BuildEmbed(FooterFor(paginator, guildId, 0)),
                allowedMentions: AllowedMentions.None)
            .ConfigureAwait(false);

        if (paginator.MaxPageIndex == 0)
            return message;

        var session = new PaginatorSession(paginator, message, guildId);
        sessions[message.Id] = session;
        _ = AddReactionsAsync(message, NavigationEmotes);
        _ = ExpireAsync(session, timeout ?? DefaultTimeout);
        return message;
    }

    /// <summary>
    ///     Adds reactions to a message and waits for a user to pick one of them.
    /// </summary>
    /// <param name="message">The message to put the choices on.</param>
    /// <param name="userId">The user whose reaction counts.</param>
    /// <param name="choices">The reactions to offer, in order.</param>
    /// <param name="timeout">How long to wait, thirty seconds when not given.</param>
    /// <returns>The index of the chosen reaction, or null when the user did not answer in time.</returns>
    public async Task<int?> WaitForReactionAsync(IUserMessage message, ulong userId, IReadOnlyList<IEmote> choices,
        TimeSpan? timeout = null)
    {
        var prompt = new PromptSession(userId, choices);
        prompts[message.Id] = prompt;
        try
        {
            _ = AddReactionsAsync(message, choices);
            var winner = await Task.WhenAny(prompt.Result.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(30)))
                .ConfigureAwait(false);
            return winner == prompt.Result.Task ? await prompt.Result.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            prompts.TryRemove(message.Id, out _);
        }
    }

    private string? FooterFor(Paginator paginator, ulong guildId, int index)
    {
        return paginator.Footer == PaginatorFooter.PageNumber
            ? strings.PageOf(guildId, index + 1, paginator.MaxPageIndex + 1)
            : null;
    }

    private async Task AddReactionsAsync(IUserMessage message, IEnumerable<IEmote> emotes)
    {
        try
        {
            foreach (var emote in emotes)
                await message.AddReactionAsync(emote).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not add reactions to message {MessageId}", message.Id);
        }
    }

    private Task OnReactionAddedAsync(Cacheable<IUserMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel, SocketReaction reaction)
    {
        if (reaction.UserId == client.CurrentUser.Id)
            return Task.CompletedTask;

        if (prompts.TryGetValue(cachedMessage.Id, out var prompt))
        {
            if (reaction.UserId != prompt.UserId)
                return Task.CompletedTask;
            var choice = prompt.IndexOf(reaction.Emote);
            if (choice >= 0)
                prompt.Result.TrySetResult(choice);
            return Task.CompletedTask;
        }

        if (sessions.TryGetValue(cachedMessage.Id, out var session))
            _ = HandlePageTurnAsync(session, reaction);

        return Task.CompletedTask;
    }

    private Task OnMessageDeletedAsync(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
    {
        if (sessions.TryRemove(message.Id, out var session))
            session.Expiry.Cancel();
        return Task.CompletedTask;
    }

    private async Task HandlePageTurnAsync(PaginatorSession session, SocketReaction reaction)
    {
        var paginator = session.Paginator;
        if (paginator.Users.Count > 0 && !paginator.Users.Contains(reaction.UserId))
            return;

        await session.Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!sessions.ContainsKey(session.Message.Id))
                return;

            _ = RemoveUserReactionAsync(session.Message, reaction);

            if (reaction.Emote.Name == Stop.Name)
            {
                await StopAsync(session, paginator.ActionOnCancellation).ConfigureAwait(false);
                return;
            }

            var target = reaction.Emote.Name switch
            {
                var name when name == First.Name => 0,
                var name when name == Back.Name => Math.Max(0, session.CurrentIndex - 1),
                var name when name == Forward.Name => Math.Min(paginator.MaxPageIndex, session.CurrentIndex + 1),
                var name when name == Last.Name => paginator.MaxPageIndex,
                _ => session.CurrentIndex
            };

            if (target == session.CurrentIndex)
                return;

            var page = await paginator.PageFactory(target).ConfigureAwait(false);
            await session.Message.ModifyAsync(m =>
            {
                m.Content = page.Text ?? "";
                m.Embed = page.BuildEmbed(FooterFor(paginator, session.GuildId, target));
            }).ConfigureAwait(false);
            session.CurrentIndex = target;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paginator page turn failed on message {MessageId}", session.Message.Id);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private static async Task RemoveUserReactionAsync(IUserMessage message, SocketReaction reaction)
    {
        try
        {
            await message.RemoveReactionAsync(reaction.Emote, reaction.UserId).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ExpireAsync(PaginatorSession session, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, session.Expiry.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await session.Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync(session, session.Paginator.ActionOnTimeout).ConfigureAwait(false);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task StopAsync(PaginatorSession session, ActionOnStop action)
    {
        if (!sessions.TryRemove(session.Message.Id, out _))
            return;
        session.Expiry.Cancel();

        try
        {
            switch (action)
            {
                case ActionOnStop.DeleteMessage:
                    await session.Message.DeleteAsync().ConfigureAwait(false);
                    break;
                case ActionOnStop.DisableInput:
                    await session.Message.RemoveAllReactionsAsync().ConfigureAwait(false);
                    break;
                case ActionOnStop.None:
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not stop paginator on message {MessageId}", session.Message.Id);
        }
    }

    private sealed class PaginatorSession(Paginator paginator, IUserMessage message, ulong guildId)
    {
        public Paginator Paginator { get; } = paginator;
        public IUserMessage Message { get; } = message;
        public ulong GuildId { get; } = guildId;
        public int CurrentIndex { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public CancellationTokenSource Expiry { get; } = new();
    }

    private sealed class PromptSession(ulong userId, IReadOnlyList<IEmote> choices)
    {
        public ulong UserId { get; } = userId;
        public TaskCompletionSource<int> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int IndexOf(IEmote emote)
        {
            for (var i = 0; i < choices.Count; i++)
            {
                if (choices[i].Name == emote.Name)
                    return i;
            }

            return -1;
        }
    }
}
