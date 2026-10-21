using System.Globalization;
using System.Text.Json;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Modules.Games.Common;
using Mewdeko.Services.Strings;
using Poll = DataModel.Poll;

namespace Mewdeko.Modules.Games.Services;

/// <summary>
///     Posts poll messages, counts the votes members cast by reacting, and keeps each message showing the current
///     results.
/// </summary>
public class PollMessageService : INService, IReadyExecutor, IUnloadableService
{
    /// <summary>
    ///     Most options one poll can have, one reaction each.
    /// </summary>
    public const int MaxOptions = 20;

    private const string EventModuleName = "PollMessageService";
    private const int BarWidth = 10;
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromSeconds(2);

    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly EventHandler eventHandler;
    private readonly ILogger<PollMessageService> logger;
    private readonly ConcurrentDictionary<int, byte> pendingRefreshes = new();
    private readonly PollService pollService;
    private readonly ConcurrentDictionary<ulong, int> pollsByMessage = new();
    private readonly GeneratedBotStrings strings;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PollMessageService" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="eventHandler">The event handler used for reaction events.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="pollService">The poll data service.</param>
    /// <param name="strings">The localized strings service.</param>
    public PollMessageService(FluxerClient client, IDataConnectionFactory dbFactory,
        EventHandler eventHandler, ILogger<PollMessageService> logger, PollService pollService,
        GeneratedBotStrings strings)
    {
        this.client = client;
        this.dbFactory = dbFactory;
        this.eventHandler = eventHandler;
        this.logger = logger;
        this.pollService = pollService;
        this.strings = strings;

        eventHandler.ReactionAdded.Subscribe(EventModuleName, OnReactionAddedAsync);
        eventHandler.ReactionRemoved.Subscribe(EventModuleName, OnReactionRemovedAsync);
    }

    /// <summary>
    ///     Loads the message of every open poll so reactions on it are counted after a restart.
    /// </summary>
    /// <returns>A task that completes when the index is filled.</returns>
    public async Task OnReadyAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            var open = await db.Polls.Where(p => p.IsActive && p.MessageId != 0)
                .Select(p => new
                {
                    p.Id, p.MessageId
                })
                .ToListAsync();
            foreach (var poll in open)
                pollsByMessage[poll.MessageId] = poll.Id;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to index open polls");
        }
    }

    /// <summary>
    ///     Unsubscribes from reaction events.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task Unload()
    {
        eventHandler.ReactionAdded.Unsubscribe(EventModuleName, OnReactionAddedAsync);
        eventHandler.ReactionRemoved.Unsubscribe(EventModuleName, OnReactionRemovedAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Checks whether a poll can run in a channel and returns the reason when it cannot.
    /// </summary>
    /// <param name="channel">The channel the poll would be posted in.</param>
    /// <param name="settings">The poll settings.</param>
    /// <param name="options">The poll options.</param>
    /// <returns>The localized reason, or null when the poll can be posted.</returns>
    public async Task<string?> GetPostProblemAsync(Channel channel, PollSettings settings,
        IReadOnlyList<PollOptionData> options)
    {
        var guildId = channel.GuildId ?? 0;
        if (options.Count > MaxOptions)
            return strings.PollMaximumOptions(guildId, MaxOptions);

        var botUser = await channel.Guild.GetCurrentUserAsync();
        var permissions = botUser.GetPermissions(channel);
        if (!permissions.AddReactions)
            return strings.PollNeedsAddReactions(guildId, channel.Mention);
        if (settings.IsAnonymous && !permissions.ManageMessages)
            return strings.PollAnonymousNeedsManageMessages(guildId, channel.Mention);

        var seen = new HashSet<string>();
        foreach (var option in options.Where(o => !string.IsNullOrWhiteSpace(o.Emote)))
        {
            if (!option.Emote!.TryToIEmote(out var emote) || emote is null)
                return strings.PollInvalidEmote(guildId, option.Emote!);
            if (!seen.Add(EmoteKey(emote)))
                return strings.PollDuplicateEmote(guildId, option.Emote!);
        }

        return null;
    }

    /// <summary>
    ///     Posts a poll message with one reaction per option and records the poll.
    /// </summary>
    /// <param name="channel">The channel to post in.</param>
    /// <param name="creatorId">The member who created the poll.</param>
    /// <param name="question">The poll question.</param>
    /// <param name="options">The options. Options without an emoji get the next free number emoji.</param>
    /// <param name="type">The poll type.</param>
    /// <param name="settings">The poll settings.</param>
    /// <returns>The created poll.</returns>
    public async Task<Poll> PostPollAsync(Channel channel, ulong creatorId, string question,
        List<PollOptionData> options, PollType type, PollSettings settings)
    {
        var taken = options.Where(o => !string.IsNullOrWhiteSpace(o.Emote))
            .Select(o => o.Emote!.TryToIEmote(out var emote) ? EmoteKey(emote!) : "")
            .ToHashSet();
        foreach (var option in options.Where(o => string.IsNullOrWhiteSpace(o.Emote)))
        {
            option.Emote = NumberEmojis.All.First(e => !taken.Contains(e));
            taken.Add(option.Emote);
        }

        var poll = await pollService.CreatePollAsync(channel.GuildId ?? 0, channel.Id, 0, creatorId, question, options,
            type, settings);
        try
        {
            var loaded = await pollService.GetPollAsync(poll.Id) ?? poll;
            var message = await channel.SendAsync(embed: BuildPollEmbed(loaded, null));
            await pollService.UpdatePollMessageIdAsync(poll.Id, message.Id);
            poll.MessageId = message.Id;
            pollsByMessage[message.Id] = poll.Id;

            foreach (var option in options)
                await message.AddReactionAsync(option.Emote!.ToIEmote());
        }
        catch
        {
            await DeleteAsync(poll.Id, creatorId);
            throw;
        }

        return poll;
    }

    /// <summary>
    ///     Closes a poll and rewrites its message with the final results.
    /// </summary>
    /// <param name="pollId">The poll ID.</param>
    /// <param name="closedBy">The member closing the poll, or zero when it expired.</param>
    /// <returns>True when the poll was open and is now closed.</returns>
    public async Task<bool> CloseAsync(int pollId, ulong closedBy)
    {
        if (!await pollService.ClosePollAsync(pollId, closedBy))
            return false;

        await RefreshPollMessageAsync(pollId, closedBy == 0);
        return true;
    }

    /// <summary>
    ///     Deletes a poll with its votes and its message.
    /// </summary>
    /// <param name="pollId">The poll ID.</param>
    /// <param name="deletedBy">The member deleting the poll.</param>
    /// <returns>True when the poll existed and is deleted.</returns>
    public async Task<bool> DeleteAsync(int pollId, ulong deletedBy)
    {
        var poll = await pollService.GetPollAsync(pollId);
        if (poll is null || !await pollService.DeletePollAsync(pollId, deletedBy))
            return false;

        pollsByMessage.TryRemove(poll.MessageId, out _);
        try
        {
            if (client.GetGuild(poll.GuildId)?.GetTextChannel(poll.ChannelId) is { } channel && poll.MessageId != 0)
                await channel.DeleteMessageAsync(poll.MessageId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete the message of poll {PollId}", pollId);
        }

        return true;
    }

    /// <summary>
    ///     Rewrites a poll's message with its current question, options and results.
    /// </summary>
    /// <param name="pollId">The poll ID.</param>
    /// <param name="expired">True when the poll is closed because its time ran out.</param>
    /// <returns>A task that completes when the message is updated.</returns>
    public async Task RefreshPollMessageAsync(int pollId, bool expired = false)
    {
        try
        {
            var poll = await pollService.GetPollAsync(pollId);
            if (poll is null || poll.MessageId == 0)
                return;

            if (!poll.IsActive)
                pollsByMessage.TryRemove(poll.MessageId, out _);

            if (client.GetGuild(poll.GuildId)?.GetTextChannel(poll.ChannelId) is not { } channel ||
                await channel.GetMessageAsync(poll.MessageId) is not Message message)
                return;

            var stats = await pollService.GetPollStatsAsync(pollId);
            var embed = BuildPollEmbed(poll, stats, expired);
            await message.ModifyAsync(m => m.Embed = embed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update the message of poll {PollId}", pollId);
        }
    }

    /// <summary>
    ///     Builds the embed that shows a poll: its options with their emoji and, depending on the settings, results.
    /// </summary>
    /// <param name="poll">The poll with its options loaded.</param>
    /// <param name="stats">The vote counts, or null for a poll nobody voted on yet.</param>
    /// <param name="expired">True when the poll is closed because its time ran out.</param>
    /// <returns>The embed.</returns>
    public EmbedRequest BuildPollEmbed(Poll poll, PollStats? stats, bool expired = false)
    {
        var guildId = poll.GuildId;
        var settings = ParseSettings(poll);
        var type = (PollType)poll.Type;
        var isOpen = poll.IsActive;
        var showCounts = isOpen ? settings.ShowResults : true;
        var total = stats?.TotalVotes ?? 0;

        var icon = !isOpen
            ? expired ? "⏰" : "\U0001F512"
            : type switch
            {
                PollType.YesNo => "✅❌",
                PollType.MultiChoice => "☑️",
                PollType.Anonymous => "\U0001F512",
                PollType.RoleRestricted => "\U0001F465",
                _ => "\U0001F4CA"
            };

        var embed = new EmbedBuilder()
            .WithTitle(strings.PollTitleFormat(guildId, icon, poll.Question).TrimTo(256))
            .WithColor(GetColor(poll, settings, isOpen));

        foreach (var option in poll.PollOptions.OrderBy(o => o.Index))
        {
            var name = ((option.Emote is null ? "" : option.Emote + " ") + option.Text).TrimTo(256);
            if (!showCounts)
            {
                embed.AddField(name, strings.PollResultsHidden(guildId), true);
                continue;
            }

            var votes = stats?.OptionVotes.GetValueOrDefault(option.Index, 0) ?? 0;
            var percent = total > 0 ? (double)votes / total * 100 : 0;
            var value = strings.PollOptionVotes(guildId, votes, percent.ToString("F1", CultureInfo.InvariantCulture));
            if (settings.ShowProgressBars)
            {
                var filled = total > 0 ? (int)Math.Round(percent / 100 * BarWidth) : 0;
                value = new string('█', filled) + new string('░', BarWidth - filled) + "\n" + value;
            }

            embed.AddField(name, value, true);
        }

        if (!isOpen)
        {
            embed.AddField(strings.PollStatsTotalVotes(guildId), total.ToString(CultureInfo.InvariantCulture), true);
            embed.AddField(strings.PollStatsUniqueVoters(guildId),
                (stats?.UniqueVoters ?? 0).ToString(CultureInfo.InvariantCulture), true);
        }

        var footer = isOpen
            ? GetOpenFooter(guildId, settings, type) + " • " + strings.PollIdLabel(guildId) + " " + poll.Id
            : expired
                ? strings.PollExpiredFooter(guildId, poll.Id)
                : strings.PollStatusClosed(guildId) + " • " + strings.PollIdLabel(guildId) + " " + poll.Id;
        embed.WithFooter(footer);

        if (isOpen && poll.ExpiresAt is { } expiresAt)
            embed.WithTimestamp(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc));

        return embed.Build();
    }

    private string GetOpenFooter(ulong guildId, PollSettings settings, PollType type)
    {
        if (settings.IsAnonymous || type == PollType.Anonymous)
            return strings.PollFooterAnonymous(guildId);
        if (settings.AllowedRoles is { Count: > 0 } || type == PollType.RoleRestricted)
            return strings.PollFooterRoleRestricted(guildId);
        return settings.AllowMultipleVotes || type == PollType.MultiChoice
            ? strings.PollFooterMultipleChoice(guildId)
            : strings.PollFooterDefault(guildId);
    }

    private static Color GetColor(Poll poll, PollSettings settings, bool isOpen)
    {
        if (!isOpen)
            return Color.DarkGrey;

        var hex = settings.Color?.TrimStart('#');
        if (hex is not null && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return new Color(raw);

        return (PollType)poll.Type switch
        {
            PollType.YesNo => Color.Green,
            PollType.MultiChoice => Color.Purple,
            PollType.Anonymous => Color.DarkGrey,
            PollType.RoleRestricted => Color.Orange,
            _ => Color.Blue
        };
    }

    private static PollSettings ParseSettings(Poll poll)
    {
        try
        {
            return poll.Settings is null
                ? new PollSettings()
                : JsonSerializer.Deserialize<PollSettings>(poll.Settings) ?? new PollSettings();
        }
        catch (JsonException)
        {
            return new PollSettings();
        }
    }

    private static string EmoteKey(IEmoji emote)
    {
        return emote.Id != 0 ? emote.Id.ToString(CultureInfo.InvariantCulture) : emote.Name;
    }

    private Task OnReactionAddedAsync(Cacheable<Message> message,
        Channel channel, SocketReaction reaction)
    {
        return HandleReactionAsync(message, reaction, true);
    }

    private Task OnReactionRemovedAsync(Cacheable<Message> message,
        Channel channel, SocketReaction reaction)
    {
        return HandleReactionAsync(message, reaction, false);
    }

    private async Task HandleReactionAsync(Cacheable<Message> cached, SocketReaction reaction, bool added)
    {
        try
        {
            if (reaction.UserId == client.CurrentUser.Id || !pollsByMessage.TryGetValue(cached.Id, out var pollId))
                return;

            var user = reaction.User ?? await client.GetUserAsync(reaction.UserId);
            if (user is null || user.IsBot)
                return;

            var poll = await pollService.GetPollAsync(pollId);
            if (poll is null || !poll.IsActive)
                return;

            var settings = ParseSettings(poll);
            var option = poll.PollOptions.FirstOrDefault(o =>
                o.Emote is not null && o.Emote.TryToIEmote(out var emote) && emote is not null &&
                EmoteKey(emote) == EmoteKey(reaction.Emoji));

            if (!added)
            {
                if (option is null || settings.IsAnonymous)
                    return;

                await pollService.ApplyVoteAsync(pollId, user.Id, option.Index, false);
                QueueRefresh(pollId);
                return;
            }

            if (option is null)
            {
                await RemoveReactionAsync(cached, reaction.Emoji, user);
                return;
            }

            var (result, dropped) = await pollService.ApplyVoteAsync(pollId, user.Id, option.Index, true);
            var rejected = result is VoteResult.NotAllowed or VoteResult.AlreadyVoted or VoteResult.PollClosed
                or VoteResult.InvalidOption;
            if (rejected || settings.IsAnonymous)
                await RemoveReactionAsync(cached, reaction.Emoji, user);

            foreach (var index in dropped)
            {
                var previous = poll.PollOptions.FirstOrDefault(o => o.Index == index);
                if (previous?.Emote is not null && previous.Emote.TryToIEmote(out var previousEmote) &&
                    previousEmote is not null)
                    await RemoveReactionAsync(cached, previousEmote, user);
            }

            if (!rejected)
                QueueRefresh(pollId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle a reaction on poll message {MessageId}", cached.Id);
        }
    }

    private async Task RemoveReactionAsync(Cacheable<Message> cached, IEmoji emote, IUser user)
    {
        try
        {
            var message = await cached.GetOrDownloadAsync();
            await message.RemoveReactionAsync(Emoji.From(emote), user.Id);
        }
        catch (FluxerApiException ex)
        {
            logger.LogDebug(ex, "Could not remove a reaction from poll message {MessageId}", cached.Id);
        }
    }

    private void QueueRefresh(int pollId)
    {
        if (!pendingRefreshes.TryAdd(pollId, 0))
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(RefreshDelay);
            pendingRefreshes.TryRemove(pollId, out _);
            await RefreshPollMessageAsync(pollId);
        });
    }
}
