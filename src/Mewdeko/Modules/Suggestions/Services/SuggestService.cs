using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.Configs;
using Mewdeko.Modules.Administration.Services;
using Mewdeko.Modules.Permissions.Common;
using Mewdeko.Modules.Permissions.Services;
using Mewdeko.Services.Analytics;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.Suggestions.Services;

/// <summary>
///     Manages suggestion operations within a Discord server, facilitating suggestion creation, tracking, and status
///     updates.
/// </summary>
public class SuggestionsService : INService
{
    /// <summary>
    ///     Used to track the state of a suggestion.
    /// </summary>
    public enum SuggestState
    {
        /// <summary>
        ///     The suggestion is suggested.
        /// </summary>
        Suggested = 0,

        /// <summary>
        ///     The suggestion is accepted.
        /// </summary>
        Accepted = 1,

        /// <summary>
        ///     The suggestion is denied.
        /// </summary>
        Denied = 2,

        /// <summary>
        ///     The suggestion is considered.
        /// </summary>
        Considered = 3,

        /// <summary>
        ///     The suggestion is implemented.
        /// </summary>
        Implemented = 4
    }

    private readonly AdministrationService adminserv;
    private readonly FluxerClient client;
    private readonly IAnalyticsCollector collector;
    private readonly BotConfig config;
    private readonly IDataConnectionFactory dbFactory;
    private readonly EventHandler eventHandler;
    private readonly GuildSettingsService guildSettings;
    private readonly ILogger<SuggestionsService> logger;
    private readonly PermissionService perms;
    private readonly List<ulong> spamCheck;
    private readonly GeneratedBotStrings strings;

    /// <summary>
    ///     Initializes a new instance of the SuggestionsService class.
    /// </summary>
    /// <param name="dbFactory">Database service for data persistence.</param>
    /// <param name="client">The Discord client instance.</param>
    /// <param name="aserv">Service for administration tasks.</param>
    /// <param name="permserv">Service for managing permissions.</param>
    /// <param name="guildSettings">Service for guild-specific settings.</param>
    /// <param name="eventHandler">Event handler for Discord client events.</param>
    /// <param name="config">The bot config service.</param>
    /// <param name="strings">The generated bot strings service for localization.</param>
    /// <param name="logger">The logger instance for structured logging.</param>
    /// <param name="collector">The analytics collector.</param>
    public SuggestionsService(
        IDataConnectionFactory dbFactory,
        FluxerClient client,
        AdministrationService aserv,
        PermissionService permserv,
        GuildSettingsService guildSettings, EventHandler eventHandler, BotConfig config, GeneratedBotStrings strings,
        ILogger<SuggestionsService> logger, IAnalyticsCollector collector)
    {
        this.collector = collector;
        perms = permserv;
        this.guildSettings = guildSettings;
        this.config = config;
        this.strings = strings;
        this.logger = logger;
        spamCheck = [];
        adminserv = aserv;
        this.client = client;
        this.eventHandler = eventHandler;
        eventHandler.Subscribe("MessageReceived", "SuggestService", MessageRecieved);
        eventHandler.Subscribe("ReactionAdded", "SuggestService", UpdateCountOnReact);
        eventHandler.Subscribe("ReactionRemoved", "SuggestService", UpdateCountOnRemoveReact);
        this.dbFactory = dbFactory;
    }

    private Task UpdateCountOnReact(Cacheable<Message> message,
        Cacheable<Channel> channel, SocketReaction reaction)
    {
        return RefreshVoteCountAsync(message, channel, reaction, true);
    }

    private Task UpdateCountOnRemoveReact(Cacheable<Message> message,
        Cacheable<Channel> channel, SocketReaction reaction)
    {
        return RefreshVoteCountAsync(message, channel, reaction, false);
    }

    private async Task RefreshVoteCountAsync(Cacheable<Message> cached,
        Cacheable<Channel> cachedChannel, SocketReaction reaction, bool added)
    {
        if (await cachedChannel.GetOrDownloadAsync().ConfigureAwait(false) is not Channel channel)
            return;
        if (channel.Id != await GetSuggestionChannel(channel.Guild.Id))
            return;

        var index = (await GetVoteEmotes(channel.GuildId)).FindIndex(e => Equals(e, reaction.Emoji));
        if (index < 0)
            return;

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var suggestion = await dbContext.Suggestions
            .FirstOrDefaultAsync(x => x.GuildId == channel.GuildId && x.MessageId == cached.Id);
        if (suggestion is null)
            return;

        var message = await cached.GetOrDownloadAsync().ConfigureAwait(false);
        if (message is null)
            return;

        var count = (await message.GetReactionUsersAsync(reaction.Emoji, 500).FlattenAsync().ConfigureAwait(false))
            .Count(x => !x.IsBot);
        switch (index)
        {
            case 0:
                suggestion.EmoteCount1 = count;
                break;
            case 1:
                suggestion.EmoteCount2 = count;
                break;
            case 2:
                suggestion.EmoteCount3 = count;
                break;
            case 3:
                suggestion.EmoteCount4 = count;
                break;
            default:
                suggestion.EmoteCount5 = count;
                break;
        }

        await dbContext.UpdateAsync(suggestion);
        if (added)
            collector.Feature("suggestion_vote", channel.GuildId);
    }

    /// <summary>
    ///     Gets the emotes members react with to vote on a suggestion: the server's custom emotes, or thumbs up and
    ///     thumbs down when none are set.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>Up to five emotes, in the order their counts are stored.</returns>
    public async Task<List<Emoji>> GetVoteEmotes(ulong guildId)
    {
        var setting = await GetEmotes(guildId);
        if (string.IsNullOrWhiteSpace(setting) || setting.StartsWith("disable", StringComparison.OrdinalIgnoreCase) ||
            setting == "-")
            return [Emoji.FromUnicode("👍"), Emoji.FromUnicode("👎")];

        return setting
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TryToIEmote(out var emote) ? emote : null)
            .OfType<Emoji>()
            .Take(5)
            .ToList();
    }

    private async Task MessageRecieved(SocketMessage msg)
    {
        if (msg.Channel is not Channel chan)
            return;
        if (spamCheck.Contains(chan.Id))
            return;
        spamCheck.Add(chan.Id);
        var guild = chan?.Guild;
        var prefix = await guildSettings.GetPrefix(guild);
        if (guild != null && !msg.Author.IsBot && !msg.Content.StartsWith(prefix))
        {
            if (chan.Id != await GetSuggestionChannel(guild.Id))
            {
                spamCheck.Remove(chan.Id);
                return;
            }

            var guser = msg.Author as SocketGuildMember;
            var pc = await perms.GetCacheFor(guild.Id);
            var test = pc.Permissions.CheckPermissions(msg as Message, "suggest", "Suggestions".ToLowerInvariant(),
                out _);
            if (!test)
            {
                spamCheck.Remove(chan.Id);
                return;
            }

            if (guser.RoleIds.Contains(await adminserv.GetStaffRole(guser.Guild.Id)))
            {
                spamCheck.Remove(chan.Id);
                return;
            }

            if (msg.Content.Length > await GetMaxLength(guild.Id))
            {
                try
                {
                    await msg.DeleteAsync().ConfigureAwait(false);
                    spamCheck.Remove(chan.Id);
                }
                catch
                {
                    spamCheck.Remove(chan.Id);
                }

                try
                {
                    await guser.SendErrorAsync(
                            $"Cannot send this suggestion as its over the max length `({await GetMaxLength(guild.Id)})` of this server!")
                        .ConfigureAwait(false);
                    spamCheck.Remove(chan.Id);
                }
                catch
                {
                    spamCheck.Remove(chan.Id);
                }

                return;
            }

            if (msg.Content.Length < await GetMinLength(guild.Id))
            {
                try
                {
                    await msg.DeleteAsync().ConfigureAwait(false);
                    spamCheck.Remove(chan.Id);
                }
                catch
                {
                    spamCheck.Remove(chan.Id);
                }

                try
                {
                    await guser.SendErrorAsync(
                            $"Cannot send this suggestion as its under the minimum length `({await GetMaxLength(guild.Id)})` of this server!")
                        .ConfigureAwait(false);
                    spamCheck.Remove(chan.Id);
                }
                catch
                {
                    spamCheck.Remove(chan.Id); // ignore
                }

                return;
            }

            await SendSuggestion(chan.Guild, msg.Author as SocketGuildMember, client, msg.Content, msg.Channel as Channel)
                .ConfigureAwait(false);
            spamCheck.Remove(chan.Id);
            try
            {
                await msg.DeleteAsync().ConfigureAwait(false);
                spamCheck.Remove(chan.Id);
            }
            catch
            {
                spamCheck.Remove(chan.Id);
            }
        }
        else
            spamCheck.Remove(chan.Id);
    }

    /// <summary>
    ///     Gets the current suggestion number for a guild.
    /// </summary>
    /// <param name="id">The unique identifier of the guild.</param>
    /// <returns>The current suggestion number.</returns>
    public async Task<ulong> GetSNum(ulong id)
    {
        return (await guildSettings.GetGuildConfig(id)).Sugnum;
    }

    /// <summary>
    ///     Gets the maximum length allowed for suggestions in a guild.
    /// </summary>
    /// <param name="id">The unique identifier of the guild.</param>
    /// <returns>The maximum length of suggestions.</returns>
    public async Task<int> GetMaxLength(ulong id)
    {
        return (await guildSettings.GetGuildConfig(id)).MaxSuggestLength;
    }

    /// <summary>
    ///     Gets the minimum length allowed for suggestions in a guild.
    /// </summary>
    /// <param name="id">The unique identifier of the guild.</param>
    /// <returns>The minimum length of suggestions.</returns>
    public async Task<int> GetMinLength(ulong id)
    {
        return (await guildSettings.GetGuildConfig(id)).MinSuggestLength;
    }

    /// <summary>
    ///     Retrieves the custom emotes set for suggestions in a guild.
    /// </summary>
    /// <param name="id">The unique identifier of the guild.</param>
    /// <returns>A string representing custom emotes.</returns>
    public async Task<string> GetEmotes(ulong id)
    {
        return (await guildSettings.GetGuildConfig(id)).SuggestEmotes;
    }

    /// <summary>
    ///     Gets the message and channel IDs for a reposted suggestion, based on its current state.
    /// </summary>
    /// <param name="suggestions">The suggestions model containing the current state.</param>
    /// <param name="guild">The guild from which to retrieve the settings.</param>
    /// <returns>A tuple containing the message ID and channel ID.</returns>
    public async Task<(ulong, ulong)> GetRepostedMessageAndChannel(Suggestion suggestions, SocketGuild guild)
    {
        (ulong, ulong) toreturn = suggestions.CurrentState switch
        {
            1 => (suggestions.StateChangeMessageId, await GetAcceptChannel(guild)),
            2 => (suggestions.StateChangeMessageId, await GetDenyChannel(guild)),
            3 => (suggestions.StateChangeMessageId, await GetConsiderChannel(guild)),
            4 => (suggestions.StateChangeMessageId, await GetImplementChannel(guild)),
            _ => (0, 0)
        };
        return toreturn;
    }

    /// <summary>
    ///     Sets the custom emotes used for suggestions in the guild.
    /// </summary>
    /// <param name="guild">The guild to configure.</param>
    /// <param name="parsedEmotes">A string representation of the custom emotes.</param>
    /// <returns>A task that represents the asynchronous operation of updating the suggestion emotes.</returns>
    public async Task SetSuggestionEmotes(SocketGuild guild, string parsedEmotes)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.SuggestEmotes = parsedEmotes;
        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the channel ID for suggestions in the guild.
    /// </summary>
    /// <param name="guild">The guild to configure.</param>
    /// <param name="channel">The channel ID where suggestions will be posted.</param>
    /// <returns>A task that represents the asynchronous operation of setting the suggestion channel ID.</returns>
    public async Task SetSuggestionChannelId(SocketGuild guild, ulong channel)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.Sugchan = channel;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the minimum length for suggestions in the guild.
    /// </summary>
    /// <param name="guild">The guild to configure.</param>
    /// <param name="minLength">The minimum length allowed for suggestions.</param>
    /// <returns>A task that represents the asynchronous operation of setting the minimum suggestion length.</returns>
    public async Task SetMinLength(SocketGuild guild, int minLength)
    {
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.MinSuggestLength = minLength;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the maximum length for suggestions in the guild.
    /// </summary>
    /// <param name="guild">The guild to configure.</param>
    /// <param name="maxLength">The maximum length allowed for suggestions.</param>
    /// <returns>A task that represents the asynchronous operation of setting the maximum suggestion length.</returns>
    public async Task SetMaxLength(SocketGuild guild, int maxLength)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.MaxSuggestLength = maxLength;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Resets the suggestion counter and removes all suggestions in the guild.
    /// </summary>
    /// <param name="guild">The guild where the reset will occur.</param>
    /// <returns>A task that represents the asynchronous operation of resetting suggestions.</returns>
    public async Task SuggestReset(SocketGuild guild)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        await dbContext.Suggestions.Where(x => x.GuildId == guild.Id).DeleteAsync().ConfigureAwait(false);
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.Sugnum = 1;
        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets a new default message that is used when users submit suggestions in a guild. This message can contain
    ///     placeholders that are replaced with suggestion-specific data.
    /// </summary>
    /// <param name="guild">The guild to set the new default suggestion message in.</param>
    /// <param name="message">The new default message for suggestions.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetSuggestionMessage(SocketGuild guild, string message)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.SuggestMessage = message;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets a new default message that is displayed when a suggestion is accepted in a guild.
    /// </summary>
    /// <param name="guild">The guild to set the new default accept message in.</param>
    /// <param name="message">The new default message for when suggestions are accepted.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetAcceptMessage(SocketGuild guild, string message)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.AcceptMessage = message;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets a new default message that is displayed when a suggestion is denied in a guild.
    /// </summary>
    /// <param name="guild">The guild to set the new default deny message in.</param>
    /// <param name="message">The new default message for when suggestions are denied.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetDenyMessage(SocketGuild guild, string message)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.DenyMessage = message;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets a new default message that is displayed when a suggestion is implemented in a guild.
    /// </summary>
    /// <param name="guild">The guild to set the new default implement message in.</param>
    /// <param name="message">The new default message for when suggestions are implemented.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetImplementMessage(SocketGuild guild, string message)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ImplementMessage = message;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Updates the state of a suggestion, marking it as accepted, denied, considered, or implemented, and records the user
    ///     responsible for the state change.
    /// </summary>
    /// <param name="Suggestion">The suggestion model to update.</param>
    /// <param name="state">The new state of the suggestion.</param>
    /// <param name="stateChangeId">The ID of the user responsible for the state change.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task UpdateSuggestState(Suggestion Suggestion, int state, ulong stateChangeId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        Suggestion.CurrentState = state;
        Suggestion.StateChangeUser = stateChangeId;
        Suggestion.StateChangeCount++;
        await dbContext.UpdateAsync(Suggestion);
    }

    /// <summary>
    ///     Updates the message ID associated with the state change of a suggestion. This could refer to the message announcing
    ///     the suggestion's acceptance, denial, consideration, or implementation.
    /// </summary>
    /// <param name="Suggestion">The suggestion model to update.</param>
    /// <param name="messageStateId">The new message ID associated with the state change.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task UpdateStateMessageId(Suggestion Suggestion, ulong messageStateId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        Suggestion.StateChangeMessageId = messageStateId;
        await dbContext.UpdateAsync(Suggestion);
    }

    /// <summary>
    ///     Sets the type of threads that can be created for discussions on suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild to set the thread type in.</param>
    /// <param name="num">The thread type number (e.g., 0 for no threads, 1 for public threads).</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetSuggestThreadsType(SocketGuild guild, int num)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.SuggestionThreadType = num;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets a new default message that is displayed when a suggestion is under consideration in a guild.
    /// </summary>
    /// <param name="guild">The guild to set the new default consider message in.</param>
    /// <param name="message">The new default message for when suggestions are under consideration.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetConsiderMessage(SocketGuild guild, string message)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ConsiderMessage = message;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Updates the suggestion number counter in a guild, typically after a new suggestion is added.
    /// </summary>
    /// <param name="guild">The guild to update the suggestion number in.</param>
    /// <param name="num">The new suggestion number.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task Sugnum(SocketGuild guild, ulong num)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.Sugnum = num;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets whether suggestions that are denied should be automatically archived in a guild.
    /// </summary>
    /// <param name="guild">The guild to configure the auto-archive setting for denied suggestions.</param>
    /// <param name="value">True if denied suggestions should be archived; otherwise, false.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetArchiveOnDeny(SocketGuild guild, bool value)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ArchiveOnDeny = value;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the behavior for archiving suggestions upon acceptance within a guild.
    /// </summary>
    /// <param name="guild">The guild to set the archive behavior for.</param>
    /// <param name="value">True to archive suggestions on acceptance; false otherwise.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetArchiveOnAccept(SocketGuild guild, bool value)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ArchiveOnAccept = value;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the behavior for archiving suggestions upon consideration within a guild.
    /// </summary>
    /// <param name="guild">The guild to set the archive behavior for.</param>
    /// <param name="value">True to archive suggestions on consideration; false otherwise.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetArchiveOnConsider(SocketGuild guild, bool value)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ArchiveOnConsider = value;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the behavior for archiving suggestions upon implementation within a guild.
    /// </summary>
    /// <param name="guild">The guild to set the archive behavior for.</param>
    /// <param name="value">True to archive suggestions on implementation; false otherwise.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetArchiveOnImplement(SocketGuild guild, bool value)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ArchiveOnImplement = value;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the channel to be used for accepted suggestions within a guild.
    /// </summary>
    /// <param name="guild">The guild to set the accept channel for.</param>
    /// <param name="channelId">The channel ID for accepted suggestions.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetAcceptChannel(SocketGuild guild, ulong channelId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.AcceptChannel = channelId;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the channel to be used for denied suggestions within a guild.
    /// </summary>
    /// <param name="guild">The guild to set the deny channel for.</param>
    /// <param name="channelId">The channel ID for denied suggestions.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetDenyChannel(SocketGuild guild, ulong channelId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.DenyChannel = channelId;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the channel to be used for suggestions under consideration within a guild.
    /// </summary>
    /// <param name="guild">The guild to set the consider channel for.</param>
    /// <param name="channelId">The channel ID for suggestions under consideration.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetConsiderChannel(SocketGuild guild, ulong channelId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ConsiderChannel = channelId;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Sets the channel to be used for implemented suggestions within a guild.
    /// </summary>
    /// <param name="guild">The guild to set the implement channel for.</param>
    /// <param name="channelId">The channel ID for implemented suggestions.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetImplementChannel(SocketGuild guild, ulong channelId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var gc = await guildSettings.GetGuildConfig(guild.Id);
        gc.ImplementChannel = channelId;

        await guildSettings.UpdateGuildConfig(guild.Id, gc);
    }

    /// <summary>
    ///     Retrieves the current count of reactions for a specific emote on a suggestion.
    /// </summary>
    /// <param name="messageId">The message ID of the suggestion.</param>
    /// <param name="emoteNumber">The emote number to retrieve the count for.</param>
    /// <returns>The current count of the specified emote for the suggestion.</returns>
    public async Task<int> GetCurrentCount(ulong messageId, int emoteNumber)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        var toupdate = dbContext.Suggestions.FirstOrDefault(x => x.MessageId == messageId);
        return emoteNumber switch
        {
            1 => toupdate.EmoteCount1,
            2 => toupdate.EmoteCount2,
            3 => toupdate.EmoteCount3,
            4 => toupdate.EmoteCount4,
            5 => toupdate.EmoteCount5,
            _ => 0
        };
    }

    /// <summary>
    ///     Retrieves the ID of the channel designated for suggestions in a guild.
    /// </summary>
    /// <param name="id">The ID of the guild.</param>
    /// <returns>The channel ID designated for suggestions.</returns>
    public async Task<ulong> GetSuggestionChannel(ulong id)
    {
        return (await guildSettings.GetGuildConfig(id)).Sugchan;
    }

    /// <summary>
    ///     Retrieves the custom message set for suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the message.</param>
    /// <returns>The custom suggestion message if set; otherwise, null.</returns>
    public async Task<string>? GetSuggestionMessage(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).SuggestMessage;
    }

    /// <summary>
    ///     Retrieves the custom accept message for suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the accept message.</param>
    /// <returns>The custom accept message if set; otherwise, null.</returns>
    public async Task<string>? GetAcceptMessage(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).AcceptMessage;
    }

    /// <summary>
    ///     Retrieves the custom deny message for suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the deny message.</param>
    /// <returns>The custom deny message if set; otherwise, null.</returns>
    public async Task<string>? GetDenyMessage(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).DenyMessage;
    }

    /// <summary>
    ///     Retrieves the custom implement message for suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the implement message.</param>
    /// <returns>The custom implement message if set; otherwise, null.</returns>
    public async Task<string>? GetImplementMessage(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ImplementMessage;
    }

    /// <summary>
    ///     Retrieves the custom consider message for suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the consider message.</param>
    /// <returns>The custom consider message if set; otherwise, null.</returns>
    public async Task<string>? GetConsiderMessage(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ConsiderMessage;
    }

    /// <summary>
    ///     Retrieves the thread type used for suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the thread type.</param>
    /// <returns>The thread type for suggestions.</returns>
    public async Task<int> GetThreadType(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).SuggestionThreadType;
    }

    /// <summary>
    ///     Retrieves the channel ID for suggestions under consideration in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the consider channel ID.</param>
    /// <returns>The consider channel ID.</returns>
    public async Task<ulong> GetConsiderChannel(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ConsiderChannel;
    }

    /// <summary>
    ///     Retrieves the channel ID for accepted suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the accept channel ID.</param>
    /// <returns>The accept channel ID.</returns>
    public async Task<ulong> GetAcceptChannel(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).AcceptChannel;
    }

    /// <summary>
    ///     Retrieves the channel ID for implemented suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the implement channel ID.</param>
    /// <returns>The implement channel ID.</returns>
    public async Task<ulong> GetImplementChannel(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ImplementChannel;
    }

    /// <summary>
    ///     Retrieves the channel ID for denied suggestions in a guild.
    /// </summary>
    /// <param name="guild">The guild from which to retrieve the deny channel ID.</param>
    /// <returns>The deny channel ID.</returns>
    public async Task<ulong> GetDenyChannel(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).DenyChannel;
    }

    /// <summary>
    ///     Determines whether suggestions are archived upon denial in a guild.
    /// </summary>
    /// <param name="guild">The guild to check the archive setting for.</param>
    /// <returns>True if suggestions are archived upon denial; otherwise, false.</returns>
    public async Task<bool> GetArchiveOnDeny(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ArchiveOnDeny;
    }

    /// <summary>
    ///     Determines whether suggestions are archived upon acceptance in a guild.
    /// </summary>
    /// <param name="guild">The guild to check the archive setting for.</param>
    /// <returns>True if suggestions are archived upon acceptance; otherwise, false.</returns>
    public async Task<bool> GetArchiveOnAccept(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ArchiveOnAccept;
    }

    /// <summary>
    ///     Determines whether suggestions are archived upon consideration in a guild.
    /// </summary>
    /// <param name="guild">The guild to check the archive setting for.</param>
    /// <returns>True if suggestions are archived upon consideration; otherwise, false.</returns>
    public async Task<bool> GetArchiveOnConsider(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ArchiveOnConsider;
    }

    /// <summary>
    ///     Determines whether suggestions are archived upon implementation in a guild.
    /// </summary>
    /// <param name="guild">The guild to check the archive setting for.</param>
    /// <returns>True if suggestions are archived upon implementation; otherwise, false.</returns>
    public async Task<bool> GetArchiveOnImplement(SocketGuild guild)
    {
        return (await guildSettings.GetGuildConfig(guild.Id)).ArchiveOnImplement;
    }

    /// <summary>
    ///     Sends a denial embed for a suggestion in a guild. This method handles fetching the suggestion,
    ///     checking if it exists, and then sending a customized embed based on whether a custom deny message
    ///     is set. It also handles archiving the suggestion thread if necessary.
    /// </summary>
    /// <param name="guild">The guild where the suggestion is made.</param>
    /// <param name="user">The user who denied the suggestion.</param>
    /// <param name="suggestion">The ID of the suggestion being denied.</param>
    /// <param name="channel">The text channel where the denial message will be sent.</param>
    /// <param name="reason">The reason for denial. Optional.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task SendDenyEmbed(SocketGuild guild, IUser user, ulong suggestion, Channel? channel = null,
        string? reason = null)
    {
        await SendStatusEmbed(guild, client, user, suggestion, SuggestState.Denied, channel, reason);
    }

    /// <summary>
    ///     Sends a consideration embed for a suggestion in a guild. Similar to denial, it sends an embed indicating
    ///     the suggestion is under consideration, with customized messages if set, and handles archiving.
    /// </summary>
    /// <param name="guild">The guild where the suggestion is made.</param>
    /// <param name="user">The user considering the suggestion.</param>
    /// <param name="suggestion">The ID of the suggestion being considered.</param>
    /// <param name="channel">The channel where the consideration message will be sent.</param>
    /// <param name="reason">The reason for consideration. Optional.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task SendConsiderEmbed(SocketGuild guild, IUser user, ulong suggestion, Channel? channel = null,
        string? reason = null)
    {
        await SendStatusEmbed(guild, client, user, suggestion, SuggestState.Considered, channel, reason);
    }

    /// <summary>
    ///     Sends an implementation embed for a suggestion in a guild. This method handles the entire process
    ///     of marking a suggestion as implemented, including sending a customized embed message, and managing
    ///     the suggestion's thread.
    /// </summary>
    /// <param name="guild">The guild where the suggestion is made.</param>
    /// <param name="user">The user who implemented the suggestion.</param>
    /// <param name="suggestion">The ID of the suggestion being implemented.</param>
    /// <param name="channel">The channel where the implementation message will be sent.</param>
    /// <param name="reason">The reason for implementation. Optional.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task SendImplementEmbed(SocketGuild guild, IUser user, ulong suggestion, Channel? channel = null,
        string? reason = null)
    {
        await SendStatusEmbed(guild, client, user, suggestion, SuggestState.Implemented, channel, reason);
    }

    /// <summary>
    ///     Sends an acceptance embed for a suggestion in a guild. It manages sending a custom embed for suggestions
    ///     marked as accepted, archiving the thread if set, and notifying the suggestion's author.
    /// </summary>
    /// <param name="guild">The guild where the suggestion is made.</param>
    /// <param name="user">The user who accepted th     e suggestion.</param>
    /// <param name="suggestion">The ID of the suggestion being accepted.</param>
    /// <param name="channel">The channel where the acceptance message will be sent.</param>
    /// <param name="reason">The reason for acceptance. Optional.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task SendAcceptEmbed(SocketGuild guild, IUser user, ulong suggestion, Channel? channel = null,
        string? reason = null)
    {
        await SendStatusEmbed(guild, client, user, suggestion, SuggestState.Accepted, channel, reason);
    }

    private async Task SendStatusEmbed(SocketGuild guild, FluxerClient client, IUser user, ulong suggestionId,
        SuggestState state, Channel? channel = null, string? reason = null)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var suggest = (await Suggestions(guild.Id, suggestionId))?.FirstOrDefault();
        if (suggest == null)
        {
            await SendErrorMessage(channel,
                "That suggestion wasn't found! Please check the number and try again.");
            return;
        }

        var statusConfig = await GetStatusConfig(guild, state);
        var embed = await CreateStatusEmbed(guild, user, suggest, state,
            !string.IsNullOrWhiteSpace(reason) ? reason : "none", statusConfig);

        await UpdateSuggestionMessage(guild, suggest, embed, statusConfig);
        await NotifyUser(guild, user, suggest, state, reason, channel);
        await UpdateSuggestState(suggest, (int)state, user.Id);
        await HandleStatusChannel(guild, client, suggest, embed, state);
    }

    private async Task<StatusConfig> GetStatusConfig(SocketGuild guild, SuggestState state)
    {
        var message = state switch
        {
            SuggestState.Denied => await GetDenyMessage(guild),
            SuggestState.Considered => await GetConsiderMessage(guild),
            SuggestState.Implemented => await GetImplementMessage(guild),
            SuggestState.Accepted => await GetAcceptMessage(guild),
            _ => null
        };

        var archive = state switch
        {
            SuggestState.Denied => await GetArchiveOnDeny(guild),
            SuggestState.Considered => await GetArchiveOnConsider(guild),
            SuggestState.Implemented => await GetArchiveOnImplement(guild),
            SuggestState.Accepted => await GetArchiveOnAccept(guild),
            _ => false
        };

        ulong channelId = state switch
        {
            SuggestState.Denied => await GetDenyChannel(guild),
            SuggestState.Considered => await GetConsiderChannel(guild),
            SuggestState.Implemented => await GetImplementChannel(guild),
            SuggestState.Accepted => await GetAcceptChannel(guild),
            _ => 0
        };

        return new StatusConfig(message, archive, channelId);
    }

    private async Task<string> GetSuggestionContent(SocketGuild guild, Suggestion suggest)
    {
        if (suggest.Suggestion1 != null)
            return suggest.Suggestion1;

        var channel = await guild.GetTextChannelAsync(await GetSuggestionChannel(guild.Id));
        var message = await channel.GetMessageAsync(suggest.MessageId);
        return message.Embeds.FirstOrDefault()?.Description ?? "No content found";
    }

    private async Task<EmbedRequest> CreateStatusEmbed(SocketGuild guild, IUser user, Suggestion suggest, SuggestState state,
        string reason, StatusConfig statusConfig)
    {
        var suggestUser = await guild.GetUserAsync(suggest.UserId);
        var sug = suggest.Suggestion1 ?? await GetSuggestionContent(guild, suggest);

        if (statusConfig.Message is "-" or "" or null)
        {
            return new EmbedBuilder()
                .WithAuthor(suggestUser)
                .WithTitle(GetSuggestionStateTitle(guild.Id, suggest.SuggestionId, state))
                .WithDescription(sug)
                .WithColor(GetColorForState(state))
                .AddField("Reason", reason)
                .Build();
        }

        var replacer = new ReplacementBuilder()
            .WithServer(client, guild as SocketGuild)
            .WithOverride("%suggest.user%", () => suggestUser.ToString())
            .WithOverride("%suggest.user.id%", () => suggestUser.Id.ToString())
            .WithOverride("%suggest.message%", () => sug.SanitizeMentions(true))
            .WithOverride("%suggest.number%", () => suggest.SuggestionId.ToString())
            .WithOverride("%suggest.user.name%", () => suggestUser.Username)
            .WithOverride("%suggest.user.avatar%", () => suggestUser.RealAvatarUrl().ToString())
            .WithOverride("%suggest.mod.user%", user.ToString)
            .WithOverride("%suggest.mod.avatar%", () => user.RealAvatarUrl().ToString())
            .WithOverride("%suggest.mod.name%", () => user.Username)
            .WithOverride("%suggest.mod.message%", () => reason)
            .WithOverride("%suggest.mod.Id%", () => user.Id.ToString())
            .WithOverride("%suggest.emote1count%", () => suggest.EmoteCount1.ToString())
            .WithOverride("%suggest.emote2count%", () => suggest.EmoteCount2.ToString())
            .WithOverride("%suggest.emote3count%", () => suggest.EmoteCount3.ToString())
            .WithOverride("%suggest.emote4count%", () => suggest.EmoteCount4.ToString())
            .WithOverride("%suggest.emote5count%", () => suggest.EmoteCount5.ToString())
            .Build();

        var content = replacer.Replace(statusConfig.Message);
        return SmartEmbed.TryParse(content, guild.Id, out var embed, out _)
            ? embed.FirstOrDefault()
            : new EmbedBuilder().WithDescription(content).Build();
    }

    private async Task UpdateSuggestionMessage(SocketGuild guild, Suggestion suggest, EmbedRequest embed,
        StatusConfig statusConfig)
    {
        var chan = await guild.GetTextChannelAsync(await GetSuggestionChannel(guild.Id));
        if (chan == null)
        {
            throw new InvalidOperationException("The suggestion channel is invalid, please set it and try again!");
        }

        if (statusConfig.Archive)
        {
            await ArchiveSuggestionThread(guild, suggest);
        }

        if (await chan.GetMessageAsync(suggest.MessageId) is Message message)
        {
            await message.ModifyAsync(x =>
            {
                x.Content = null;
                x.Embed = embed;
            });
            await message.RemoveAllReactionsAsync();
        }
        else
        {
            var newMessage = await chan.SendAsync(embed: embed);
            suggest.MessageId = newMessage.Id;
            await using var dbContext = await dbFactory.CreateConnectionAsync();
            await dbContext.UpdateAsync(suggest);
        }
    }

    private async Task NotifyUser(SocketGuild guild, IUser moderator, Suggestion suggest, SuggestState state,
        string? reason, Channel? channel)
    {
        var suggestUser = await guild.GetUserAsync(suggest.UserId);
        var embed = new EmbedBuilder()
            .WithAuthor(suggestUser)
            .WithTitle(GetSuggestionStateTitle(guild.Id, suggest.SuggestionId, state))
            .WithDescription(suggest.Suggestion1)
            .AddField("Reason", !string.IsNullOrWhiteSpace(reason) ? reason : "none")
            .AddField($"{state} By", moderator)
            .WithColor(GetColorForState(state))
            .Build();

        try
        {
            await suggestUser.SendAsync(embed: embed);
            await SendConfirmationMessage(channel,
                $"Suggestion set as {state.ToString().ToLower()} and the user has been DMed.");
        }
        catch
        {
            await SendConfirmationMessage(channel,
                $"Suggestion set as {state.ToString().ToLower()} but the user had their DMs off.");
        }
    }

    private async Task HandleStatusChannel(SocketGuild guild, FluxerClient client, Suggestion suggest,
        EmbedRequest embed, SuggestState state)
    {
        var statusConfig = await GetStatusConfig(guild, state);
        if (statusConfig.ChannelId != 0)
        {
            var statusChannel = await guild.GetTextChannelAsync(statusConfig.ChannelId);
            if (statusChannel != null && (await guild.GetUserAsync(client.CurrentUser.Id)).GetPermissions(statusChannel)
                .EmbedLinks)
            {
                await DeletePreviousStatusMessage(guild, suggest);
                var newStatusMessage = await statusChannel.SendAsync(embed: embed);
                await UpdateStateMessageId(suggest, newStatusMessage.Id);
            }
        }
    }

    private async Task DeletePreviousStatusMessage(SocketGuild guild, Suggestion suggest)
    {
        var (messageId, messageChannel) = await GetRepostedMessageAndChannel(suggest, guild);
        if (messageId != 0)
        {
            var channel = await guild.GetTextChannelAsync(messageChannel);
            if (channel != null)
            {
                var message = await channel.GetMessageAsync(messageId);
                if (message != null)
                {
                    await message.DeleteAsync();
                }
            }
        }
    }

    private async Task ArchiveSuggestionThread(SocketGuild guild, Suggestion suggest)
    {
        var threadChannel = await guild.GetThreadChannelAsync(await GetThreadByMessage(suggest.MessageId));
        if (threadChannel != null)
        {
            await threadChannel.ModifyAsync(x => x.Archived = true);
        }
    }

    private async Task SendErrorMessage(Channel? channel, string message)
    {
        if (channel != null)
            await channel.SendErrorAsync(message, config);
    }

    private async Task SendConfirmationMessage(Channel? channel, string message)
    {
        if (channel != null)
            await channel.SendConfirmAsync(message);
    }

    private Color GetColorForState(SuggestState state)
    {
        return state switch
        {
            SuggestState.Denied => Color.Red,
            SuggestState.Considered => Color.Gold,
            SuggestState.Implemented => Color.Green,
            SuggestState.Accepted => Color.Blue,
            _ => Color.Default
        };
    }

    private string GetSuggestionStateTitle(ulong guildId, ulong suggestionId, SuggestState state)
    {
        return state switch
        {
            SuggestState.Denied => strings.SuggestionDenied(guildId, suggestionId),
            SuggestState.Considered => strings.SuggestionConsidered(guildId, suggestionId),
            SuggestState.Implemented => strings.SuggestionImplemented(guildId, suggestionId),
            SuggestState.Accepted => strings.SuggestionAccepted(guildId, suggestionId),
            _ => $"Suggestion #{suggestionId} {state}"
        };
    }

    /// <summary>
    ///     Submits a suggestion in a guild: posts the suggestion message, adds the voting reactions and, when the
    ///     server uses threads for suggestions, opens a discussion thread for it.
    /// </summary>
    /// <param name="guild">The guild where the suggestion is submitted.</param>
    /// <param name="user">The user submitting the suggestion.</param>
    /// <param name="client">The Discord client.</param>
    /// <param name="suggestion">The text of the suggestion being submitted.</param>
    /// <param name="channel">The channel where the suggestion submission confirmation will be sent.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task SendSuggestion(
        SocketGuild guild,
        SocketGuildMember user,
        FluxerClient client,
        string suggestion,
        Channel channel)
    {
        if (await GetSuggestionChannel(guild.Id) == 0)
        {
            var msg = await channel
                .SendErrorAsync(
                    strings.NoSuggestionChannelSet(guild.Id),
                    config)
                .ConfigureAwait(false);
            msg.DeleteAfter(3);
            return;
        }

        var suggestionChannel = await guild.GetTextChannelAsync(await GetSuggestionChannel(guild.Id))
            .ConfigureAwait(false);
        var sugnum1 = await GetSNum(guild.Id);
        var customMessage = await GetSuggestionMessage(guild) is not ("-" or "");
        Message posted;
        if (!customMessage)
        {
            posted = await suggestionChannel.SendAsync(
                embed: new EmbedBuilder().WithAuthor(user)
                    .WithTitle(strings.SuggestionNumber(channel.Guild.Id, sugnum1))
                    .WithDescription(suggestion).WithOkColor().Build()).ConfigureAwait(false);
        }
        else
        {
            var replacer = new ReplacementBuilder().WithServer(client, guild as SocketGuild)
                .WithOverride("%suggest.user%", user.ToString)
                .WithOverride("%suggest.message%", () => suggestion.SanitizeMentions(true))
                .WithOverride("%suggest.number%", () => sugnum1.ToString())
                .WithOverride("%suggest.user.name%", () => user.Username)
                .WithOverride("%suggest.user.avatar%", () => user.RealAvatarUrl().ToString()).Build();
            var rendered = replacer.Replace(await GetSuggestionMessage(guild));
            posted = SmartEmbed.TryParse(rendered, guild.Id, out var embed, out var plainText)
                ? await suggestionChannel.SendAsync(plainText, embeds: embed).ConfigureAwait(false)
                : await suggestionChannel.SendAsync(rendered).ConfigureAwait(false);
        }

        foreach (var voteEmote in await GetVoteEmotes(guild.Id))
            await posted.AddReactionAsync(voteEmote).ConfigureAwait(false);

        await Sugnum(guild, sugnum1 + 1).ConfigureAwait(false);
        await Suggest(guild, sugnum1, posted.Id, user.Id, suggestion).ConfigureAwait(false);
        await CreateSuggestionThread(guild, user, posted, sugnum1).ConfigureAwait(false);

        if (customMessage)
            await channel.SendConfirmAsync(strings.SuggestionSent(guild.Id)).ConfigureAwait(false);
    }

    private async Task CreateSuggestionThread(SocketGuild guild, SocketGuildMember user, Message message, ulong number)
    {
        var threadType = await GetThreadType(guild);
        if (threadType is not (1 or 2) || message.Channel is not Channel channel)
            return;

        try
        {
            var name = strings.SuggestionThreadName(guild.Id, number);
            Channel thread;
            if (threadType == 1)
            {
                thread = await channel.CreateThreadAsync(name, ThreadType.PublicThread,
                    ThreadArchiveDuration.OneWeek, message).ConfigureAwait(false);
            }
            else
            {
                thread = await channel.CreateThreadAsync(name, ThreadType.PrivateThread,
                    ThreadArchiveDuration.OneWeek, invitable: false).ConfigureAwait(false);
                await thread.AddUserAsync(user).ConfigureAwait(false);
            }

            await AddThreadChannel(message.Id, thread.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open a discussion thread for suggestion {Number} in {GuildId}", number,
                guild.Id);
        }
    }

    /// <summary>
    ///     Records a new suggestion in the database.
    /// </summary>
    /// <param name="guild">The guild where the suggestion is made.</param>
    /// <param name="suggestId">The suggestion number within the guild.</param>
    /// <param name="messageId">The ID of the message containing the suggestion.</param>
    /// <param name="userId">The ID of the user who made the suggestion.</param>
    /// <param name="suggestion">The content of the suggestion.</param>
    /// <returns>A Task representing the asynchronous operation to save the suggestion.</returns>
    public async Task Suggest(
        SocketGuild guild,
        ulong suggestId,
        ulong messageId,
        ulong userId,
        string suggestion)
    {
        var guildId = guild.Id;

        var suggest = new Suggestion
        {
            GuildId = guildId,
            SuggestionId = suggestId,
            MessageId = messageId,
            UserId = userId,
            Suggestion1 = suggestion
        };
        try
        {
            await using var dbContext = await dbFactory.CreateConnectionAsync();

            await dbContext.InsertAsync(suggest);
            collector.Feature("suggestion", guildId);
        }
        catch (Exception e)
        {
            collector.Feature("suggestion", guildId, false, e.GetType().Name);
            Console.WriteLine(e);
            throw;
        }
    }

    /// <summary>
    ///     Retrieves suggestions by guild and suggestion ID, primarily for operations involving a specific suggestion.
    /// </summary>
    /// <param name="gid">The guild ID where the suggestion was made.</param>
    /// <param name="sid">The suggestion ID to retrieve.</param>
    /// <returns>An array of <see cref="Suggestion" /> matching the criteria or null if not found.</returns>
    public async Task<Suggestion[]?> Suggestions(ulong gid, ulong sid)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        return await dbContext.Suggestions.Where(x => x.GuildId == gid && x.SuggestionId == sid).ToArrayAsync();
    }

    /// <summary>
    ///     Lists all suggestions made in a guild, useful for overview or management purposes.
    /// </summary>
    /// <param name="gid">The guild ID to retrieve suggestions for.</param>
    /// <returns>A list of all <see cref="Suggestion" /> in the specified guild.</returns>
    public async Task<List<Suggestion>> Suggestions(ulong gid)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        return dbContext.Suggestions.Where(x => x.GuildId == gid).ToList();
    }

    /// <summary>
    ///     Retrieves a suggestion based on its associated message ID, useful for operations triggered by message interactions.
    /// </summary>
    /// <param name="msgId">The message ID linked to the suggestion.</param>
    /// <returns>The <see cref="Suggestion" /> associated with the message ID.</returns>
    public async Task<Suggestion> GetSuggestByMessage(ulong msgId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        return await dbContext.Suggestions.FirstOrDefaultAsync(x => x.MessageId == msgId);
    }

    /// <summary>
    ///     Retrieves all suggestions made by a specific user in a guild, useful for personal suggestion tracking or
    ///     management.
    /// </summary>
    /// <param name="guildId">The guild ID where the suggestions were made.</param>
    /// <param name="userId">The user ID to retrieve suggestions for.</param>
    /// <returns>An array of <see cref="Suggestion" /> made by the specified user in the specified guild.</returns>
    public async Task<Suggestion[]> ForUser(ulong guildId, ulong userId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        return await dbContext.Suggestions.Where(x => x.GuildId == guildId && x.UserId == userId).ToArrayAsync();
    }

    /// <summary>
    ///     Adds a new thread channel association with a suggestion message. This is used to track thread channels
    ///     that are created for discussing specific suggestions.
    /// </summary>
    /// <param name="messageId">The message ID of the suggestion.</param>
    /// <param name="threadChannelId">The thread channel ID created for the suggestion's discussion.</param>
    /// <returns>A Task representing the asynchronous operation of adding the thread channel association.</returns>
    public async Task AddThreadChannel(ulong messageId, ulong threadChannelId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        await dbContext.InsertAsync(new SuggestThread
        {
            MessageId = messageId, ThreadChannelId = threadChannelId
        });
    }

    /// <summary>
    ///     Retrieves the ID of the thread channel associated with a specific suggestion message. This is used to
    ///     find the discussion thread for a suggestion.
    /// </summary>
    /// <param name="messageId">The message ID of the suggestion.</param>
    /// <returns>The ID of the thread channel associated with the message, or 0 if no association exists.</returns>
    public async Task<ulong> GetThreadByMessage(ulong messageId)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        return dbContext.SuggestThreads.FirstOrDefault(x => x.MessageId == messageId)?.ThreadChannelId ?? 0;
    }

    /// <summary>
    ///     Unloads the service and unsubscribes from events.
    /// </summary>
    public Task Unload()
    {
        eventHandler.Unsubscribe("MessageReceived", "SuggestService", MessageRecieved);
        eventHandler.Unsubscribe("ReactionAdded", "SuggestService", UpdateCountOnReact);
        eventHandler.Unsubscribe("ReactionRemoved", "SuggestService", UpdateCountOnRemoveReact);
        return Task.CompletedTask;
    }
}

/// <summary>
/// </summary>
public class StatusConfig
{
    /// <summary>
    /// </summary>
    /// <param name="message"></param>
    /// <param name="archive"></param>
    /// <param name="channelId"></param>
    public StatusConfig(string message, bool archive, ulong channelId)
    {
        Message = message;
        Archive = archive;
        ChannelId = channelId;
    }

    /// <summary>
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// </summary>
    public bool Archive { get; }

    /// <summary>
    /// </summary>
    public ulong ChannelId { get; }
}
