using System.Diagnostics;
using System.Threading;
using Fluxer.Net.Gateway;
using Mewdeko.Modules.Xp.Events;
using Mewdeko.Services.Analytics;
using Channel = Fluxer.Net.Channel;

namespace Mewdeko.Services.Impl;

/// <summary>
///     Turns Fluxer gateway events into the events modules subscribe to, filling in what changed from the snapshot
///     cache, and runs each module's handlers with metrics and error isolation.
/// </summary>
public sealed class EventHandler : IDisposable
{
    private readonly FluxerClient client;
    private readonly IAnalyticsCollector collector;
    private readonly ConcurrentDictionary<string, EventMetrics> eventMetrics = new();
    private readonly ILogger<EventHandler> logger;
    private readonly ConcurrentDictionary<string, ModuleMetrics> moduleMetrics = new();
    private readonly PerformanceMonitorService perfService;
    private readonly RateLimiter presenceLimiter;
    private readonly GatewaySnapshotCache snapshots;
    private readonly RateLimiter typingLimiter;
    private volatile bool disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EventHandler" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client to handle events for.</param>
    /// <param name="snapshots">Remembers earlier states so updates can report what changed.</param>
    /// <param name="perfService">Times each handler.</param>
    /// <param name="logger">Records handler failures.</param>
    /// <param name="options">Rate limits for the noisiest events.</param>
    /// <param name="collector">The analytics collector that counts and times events.</param>
    public EventHandler(
        FluxerClient client,
        GatewaySnapshotCache snapshots,
        PerformanceMonitorService perfService,
        ILogger<EventHandler> logger,
        EventHandlerOptions options,
        IAnalyticsCollector collector)
    {
        this.client = client;
        this.snapshots = snapshots;
        this.perfService = perfService;
        this.logger = logger;
        this.collector = collector;
        presenceLimiter = new RateLimiter(options.PresenceUpdateRateLimit, TimeSpan.FromSeconds(1));
        typingLimiter = new RateLimiter(options.TypingRateLimit, TimeSpan.FromSeconds(1));

        Connected = new BotEvent<FluxerClient>(this, nameof(Connected));
        Disconnected = new BotEvent<FluxerClient>(this, nameof(Disconnected));
        Ready = new BotEvent<FluxerClient>(this, nameof(Ready));
        JoinedGuild = new BotEvent<SocketGuild>(this, nameof(JoinedGuild));
        LeftGuild = new BotEvent<SocketGuild>(this, nameof(LeftGuild));
        GuildAvailable = new BotEvent<SocketGuild>(this, nameof(GuildAvailable));
        GuildUnavailable = new BotEvent<SocketGuild>(this, nameof(GuildUnavailable));
        GuildUpdated = new BotEvent<SocketGuild, SocketGuild>(this, nameof(GuildUpdated));
        MessageReceived = new BotEvent<SocketMessage>(this, nameof(MessageReceived));
        MessageUpdated = new BotEvent<Message?, SocketMessage>(this, nameof(MessageUpdated));
        MessageDeleted = new BotEvent<Cacheable<Message>, Channel>(this, nameof(MessageDeleted));
        MessagesBulkDeleted =
            new BotEvent<IReadOnlyCollection<Cacheable<Message>>, Channel>(this, nameof(MessagesBulkDeleted));
        ReactionAdded = new BotEvent<Cacheable<Message>, Channel, SocketReaction>(this, nameof(ReactionAdded));
        ReactionRemoved = new BotEvent<Cacheable<Message>, Channel, SocketReaction>(this, nameof(ReactionRemoved));
        ReactionsCleared = new BotEvent<Cacheable<Message>, Channel>(this, nameof(ReactionsCleared));
        UserJoined = new BotEvent<SocketGuildMember>(this, nameof(UserJoined));
        UserLeft = new BotEvent<SocketGuild, User, MemberSnapshot?>(this, nameof(UserLeft));
        GuildMemberUpdated = new BotEvent<MemberSnapshot?, SocketGuildMember>(this, nameof(GuildMemberUpdated));
        UserUpdated = new BotEvent<UserSnapshot, SocketGuildMember>(this, nameof(UserUpdated));
        UserBanned = new BotEvent<User, SocketGuild>(this, nameof(UserBanned));
        UserUnbanned = new BotEvent<User, SocketGuild>(this, nameof(UserUnbanned));
        UserVoiceStateUpdated =
            new BotEvent<SocketGuildMember, VoiceStateSnapshot, VoiceStateSnapshot>(this,
                nameof(UserVoiceStateUpdated));
        PresenceUpdated = new BotEvent<User, PresenceSnapshot?, PresenceSnapshot>(this, nameof(PresenceUpdated));
        UserIsTyping = new BotEvent<ulong, Channel>(this, nameof(UserIsTyping));
        ChannelCreated = new BotEvent<Channel>(this, nameof(ChannelCreated));
        ChannelUpdated = new BotEvent<Channel, Channel>(this, nameof(ChannelUpdated));
        ChannelDestroyed = new BotEvent<Channel>(this, nameof(ChannelDestroyed));
        ThreadCreated = new BotEvent<ThreadChannel>(this, nameof(ThreadCreated));
        ThreadUpdated = new BotEvent<Cacheable<ThreadChannel>, ThreadChannel>(this, nameof(ThreadUpdated));
        ThreadDeleted = new BotEvent<Cacheable<ThreadChannel>>(this, nameof(ThreadDeleted));
        RoleCreated = new BotEvent<SocketRole>(this, nameof(RoleCreated));
        RoleUpdated = new BotEvent<SocketRole, SocketRole>(this, nameof(RoleUpdated));
        RoleDeleted = new BotEvent<SocketRole>(this, nameof(RoleDeleted));
        AuditLogCreated = new BotEvent<SocketAuditLog, SocketGuild>(this, nameof(AuditLogCreated));
        InviteCreated = new BotEvent<Invite>(this, nameof(InviteCreated));
        InviteDeleted = new BotEvent<Channel, string>(this, nameof(InviteDeleted));
        XpLevelChanged = new BotEvent<XpLevelChangedEventArgs>(this, nameof(XpLevelChanged));

        RegisterEvents();
    }

    /// <summary>Fires when the gateway connection opens.</summary>
    public BotEvent<FluxerClient> Connected { get; }

    /// <summary>Fires when the gateway connection drops.</summary>
    public BotEvent<FluxerClient> Disconnected { get; }

    /// <summary>Fires when the gateway has sent the bot's guilds and the client is ready.</summary>
    public BotEvent<FluxerClient> Ready { get; }

    /// <summary>Fires when the bot is added to a guild.</summary>
    public BotEvent<SocketGuild> JoinedGuild { get; }

    /// <summary>Fires when the bot is removed from a guild.</summary>
    public BotEvent<SocketGuild> LeftGuild { get; }

    /// <summary>Fires when a guild becomes available, including on startup.</summary>
    public BotEvent<SocketGuild> GuildAvailable { get; }

    /// <summary>Fires when a guild becomes unavailable because of an outage.</summary>
    public BotEvent<SocketGuild> GuildUnavailable { get; }

    /// <summary>Fires when a guild's settings change, with the guild before and after.</summary>
    public BotEvent<SocketGuild, SocketGuild> GuildUpdated { get; }

    /// <summary>
    ///     Fires for every new message, in guilds and in direct messages. A direct message arrives as a
    ///     <see cref="SocketPrivateMessage" /> and has no guild.
    /// </summary>
    public BotEvent<SocketMessage> MessageReceived { get; }

    /// <summary>Fires when a message is edited, with the cached version before the edit if there is one.</summary>
    public BotEvent<Message?, SocketMessage> MessageUpdated { get; }

    /// <summary>
    ///     Fires when a message is deleted. The cacheable holds the message when it was cached, or one rebuilt from the
    ///     content Fluxer sends with the deletion.
    /// </summary>
    public BotEvent<Cacheable<Message>, Channel> MessageDeleted { get; }

    /// <summary>Fires when several messages are deleted at once.</summary>
    public BotEvent<IReadOnlyCollection<Cacheable<Message>>, Channel> MessagesBulkDeleted { get; }

    /// <summary>Fires when someone reacts to a message.</summary>
    public BotEvent<Cacheable<Message>, Channel, SocketReaction> ReactionAdded { get; }

    /// <summary>Fires when someone removes a reaction.</summary>
    public BotEvent<Cacheable<Message>, Channel, SocketReaction> ReactionRemoved { get; }

    /// <summary>Fires when every reaction is removed from a message.</summary>
    public BotEvent<Cacheable<Message>, Channel> ReactionsCleared { get; }

    /// <summary>Fires when a member joins a guild.</summary>
    public BotEvent<SocketGuildMember> UserJoined { get; }

    /// <summary>Fires when a member leaves or is removed from a guild.</summary>
    public BotEvent<SocketGuild, User, MemberSnapshot?> UserLeft { get; }

    /// <summary>
    ///     Fires when a member's nickname, roles, avatar or timeout change, with their state before the change when it
    ///     was known.
    /// </summary>
    public BotEvent<MemberSnapshot?, SocketGuildMember> GuildMemberUpdated { get; }

    /// <summary>
    ///     Fires once per change when a user's name, display name or avatar change, with the member it was seen on.
    /// </summary>
    public BotEvent<UserSnapshot, SocketGuildMember> UserUpdated { get; }

    /// <summary>Fires when a user is banned from a guild.</summary>
    public BotEvent<User, SocketGuild> UserBanned { get; }

    /// <summary>Fires when a user's ban is lifted.</summary>
    public BotEvent<User, SocketGuild> UserUnbanned { get; }

    /// <summary>
    ///     Fires when a member joins, leaves, moves between or changes their state in voice channels, with their
    ///     voice state before and after. A member outside voice has <see cref="VoiceStateSnapshot.None" />.
    /// </summary>
    public BotEvent<SocketGuildMember, VoiceStateSnapshot, VoiceStateSnapshot> UserVoiceStateUpdated { get; }

    /// <summary>Fires when a user's status or custom status changes, with the presence before when it was known.</summary>
    public BotEvent<User, PresenceSnapshot?, PresenceSnapshot> PresenceUpdated { get; }

    /// <summary>Fires when a user starts typing, with the user's id and the channel.</summary>
    public BotEvent<ulong, Channel> UserIsTyping { get; }

    /// <summary>Fires when a guild channel is created.</summary>
    public BotEvent<Channel> ChannelCreated { get; }

    /// <summary>Fires when a guild channel changes, with the channel before and after.</summary>
    public BotEvent<Channel, Channel> ChannelUpdated { get; }

    /// <summary>Fires when a guild channel is deleted.</summary>
    public BotEvent<Channel> ChannelDestroyed { get; }

    /// <summary>Fires when a thread is created.</summary>
    public BotEvent<ThreadChannel> ThreadCreated { get; }

    /// <summary>Fires when a thread changes.</summary>
    public BotEvent<Cacheable<ThreadChannel>, ThreadChannel> ThreadUpdated { get; }

    /// <summary>Fires when a thread is deleted.</summary>
    public BotEvent<Cacheable<ThreadChannel>> ThreadDeleted { get; }

    /// <summary>Fires when a role is created.</summary>
    public BotEvent<SocketRole> RoleCreated { get; }

    /// <summary>Fires when a role changes, with the role before and after.</summary>
    public BotEvent<SocketRole, SocketRole> RoleUpdated { get; }

    /// <summary>Fires when a role is deleted.</summary>
    public BotEvent<SocketRole> RoleDeleted { get; }

    /// <summary>Fires when an audit log entry is written.</summary>
    public BotEvent<SocketAuditLog, SocketGuild> AuditLogCreated { get; }

    /// <summary>Fires when an invite is created.</summary>
    public BotEvent<Invite> InviteCreated { get; }

    /// <summary>Fires when an invite is deleted, with its channel and code.</summary>
    public BotEvent<Channel, string> InviteDeleted { get; }

    /// <summary>Fires when a member's XP level changes. Raised by the XP module.</summary>
    public BotEvent<XpLevelChangedEventArgs> XpLevelChanged { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        disposed = true;
    }

    /// <summary>
    ///     Gets the processing totals of every event.
    /// </summary>
    public IReadOnlyDictionary<string, EventMetrics> GetEventMetrics()
    {
        return new Dictionary<string, EventMetrics>(eventMetrics);
    }

    /// <summary>
    ///     Gets the processing totals of every module that handles events.
    /// </summary>
    public IReadOnlyDictionary<string, ModuleMetrics> GetModuleMetrics()
    {
        return new Dictionary<string, ModuleMetrics>(moduleMetrics);
    }

    internal void CountEvent(string eventName, SocketGuild? guild)
    {
        collector.Event(eventName, 0);
        if (guild is not null)
            collector.GuildEvent(guild.Id, eventName, 0, false);
    }

    internal async Task RunHandlerAsync(string eventName, string moduleName, SocketGuild? guild, Func<Task> run)
    {
        if (disposed)
            return;

        var eventTotals = eventMetrics.GetOrAdd(eventName, _ => new EventMetrics());
        var moduleTotals = moduleMetrics.GetOrAdd(moduleName, _ => new ModuleMetrics());
        var started = Stopwatch.GetTimestamp();
        try
        {
            using (perfService.Measure($"Event_{eventName}_{moduleName}"))
                await run().ConfigureAwait(false);

            Interlocked.Increment(ref moduleTotals.EventsProcessed);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref moduleTotals.Errors);
            Interlocked.Increment(ref eventTotals.TotalErrors);
            collector.Counter("ev.errors", 1, ("type", eventName), ("module", moduleName));
            collector.Error(ex, $"EventHandler.{eventName}", moduleName, guild?.Id, guild is null ? null : 0);
            logger.LogError(ex, "Error in {ModuleName} handler for {EventName}", moduleName, eventName);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            Interlocked.Increment(ref eventTotals.TotalProcessed);
            Interlocked.Add(ref eventTotals.TotalExecutionTime, (long)elapsed.TotalMilliseconds);
            Interlocked.Add(ref moduleTotals.TotalExecutionTime, (long)elapsed.TotalMilliseconds);
            collector.HandlerDuration(eventName, moduleName, elapsed.TotalMilliseconds);
        }
    }

    private void RegisterEvents()
    {
        var gateway = client.Gateway;
        gateway.Connected += () =>
        {
            FluxerContext.IsConnected = true;
            Connected.Raise(client, null);
        };
        gateway.Disconnected += () =>
        {
            FluxerContext.IsConnected = false;
            Disconnected.Raise(client, null);
        };
        gateway.Ready += _ => Ready.Raise(client, null);

        gateway.GuildJoined += guild =>
        {
            _ = DownloadMembersAsync(guild);
            JoinedGuild.Raise(guild, guild);
        };
        gateway.GuildAvailable += guild =>
        {
            _ = DownloadMembersAsync(guild);
            GuildAvailable.Raise(guild, guild);
        };
        gateway.GuildLeft += guild =>
        {
            snapshots.ForgetGuild(guild.Id);
            LeftGuild.Raise(guild, guild);
        };
        gateway.GuildUnavailable += guild => GuildUnavailable.Raise(guild, guild);
        gateway.GuildUpdated += (before, after) => GuildUpdated.Raise((before, after), after);

        gateway.MessageCreated += OnMessageCreated;
        gateway.PrivateMessageCreated += OnPrivateMessageCreated;
        gateway.MessageUpdated += OnMessageUpdated;
        gateway.PrivateMessageUpdated += OnMessageUpdated;
        gateway.MessageDeleted += OnMessageDeleted;
        gateway.PrivateMessageDeleted += (channel, userId, messageId, content) =>
            WithChannel(channel, c => OnMessageDeleted(c, userId, messageId, content));
        gateway.MessagesDeleted += (channel, _, messageIds) => OnMessagesDeleted(channel, messageIds);
        gateway.PrivateMessagesDeleted += (channel, messageIds) =>
            WithChannel(channel, c => OnMessagesDeleted(c, messageIds));

        gateway.MessageReactionAdded += (channel, message, reaction) =>
            ReactionAdded.Raise((WithCachedMessage(message), channel, reaction), channel.Guild);
        gateway.PrivateMessageReactionAdded += (channel, message, reaction) =>
            WithChannel(channel, c => ReactionAdded.Raise((WithCachedMessage(message), c, reaction), null));
        gateway.MessageReactionRemoved += (channel, message, reaction) =>
            ReactionRemoved.Raise((WithCachedMessage(message), channel, reaction), channel.Guild);
        gateway.PrivateMessageReactionRemoved += (channel, message, reaction) =>
            WithChannel(channel, c => ReactionRemoved.Raise((WithCachedMessage(message), c, reaction), null));
        gateway.MessageReactionRemoveAll += (channel, message) =>
            ReactionsCleared.Raise((WithCachedMessage(message), channel), channel.Guild);
        gateway.PrivateMessageReactionRemoveAll += (channel, message) =>
            WithChannel(channel, c => ReactionsCleared.Raise((WithCachedMessage(message), c), null));

        gateway.MemberJoined += member =>
        {
            snapshots.SeedMember(member);
            UserJoined.Raise(member, member.Guild);
        };
        gateway.MemberUpdated += OnMemberUpdated;
        gateway.MemberLeft += (guild, member) => _ = OnMemberLeftAsync(guild, member);
        gateway.UserBanned += (guild, member) => _ = OnUserBannedAsync(guild, member);
        gateway.UserUnbanned += (guild, userId) => _ = OnUserUnbannedAsync(guild, userId);
        gateway.VoiceStateUpdated += OnVoiceStateUpdated;
        gateway.PresenceUpdated += OnPresenceUpdated;
        gateway.TypingStarted += (channel, guild, userId, _) =>
        {
            if (typingLimiter.TryAcquire())
                UserIsTyping.Raise((userId, channel), guild);
        };

        gateway.ChannelCreated += (guild, channel) => ChannelCreated.Raise(channel, guild);
        gateway.ChannelUpdated += (guild, before, after) => ChannelUpdated.Raise((before, after), guild);
        gateway.ChannelDeleted += (guild, channel) =>
        {
            snapshots.ForgetChannel(channel.Id);
            ChannelDestroyed.Raise(channel, guild);
        };
        gateway.ThreadCreated += (guild, thread) => ThreadCreated.Raise(thread, guild);
        gateway.ThreadUpdated += (guild, before, after) => ThreadUpdated.Raise((before, after), guild);
        gateway.ThreadDeleted += (guild, thread) => ThreadDeleted.Raise(thread, guild);

        gateway.RoleCreated += role => RoleCreated.Raise(role, role.Guild);
        gateway.RoleUpdated += (before, after) => RoleUpdated.Raise((before, after), after.Guild);
        gateway.RoleDeleted += role => RoleDeleted.Raise(role, role.Guild);

        gateway.AuditLogCreated += (guild, entry) => AuditLogCreated.Raise((entry, guild), guild);
        gateway.InviteCreated += invite =>
            InviteCreated.Raise(invite, invite.Guild is { } partial ? client.GetGuild(partial.Id) : null);
        gateway.InviteDeleted += (channel, guild, code) => InviteDeleted.Raise((channel, code), guild);
    }

    private async Task DownloadMembersAsync(SocketGuild guild)
    {
        snapshots.SeedGuild(guild);
        try
        {
            if (!guild.HasAllMembers)
                await guild.DownloadMembersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not download the members of {GuildId}", guild.Id);
        }

        snapshots.SeedGuild(guild);
    }

    private void OnMessageCreated(SocketMessage message)
    {
        snapshots.AddMessage(message);
        if (message.Member is { } member)
            snapshots.SeedMember(member);
        MessageReceived.Raise(message, message.Guild);
    }

    private void OnPrivateMessageCreated(SocketPrivateMessage message)
    {
        snapshots.AddMessage(message);
        if (((SocketMessage)message).Channel is not null)
        {
            MessageReceived.Raise(message, null);
            return;
        }

        _ = Task.Run(async () =>
        {
            if (await message.Channel.GetOrDownloadAsync().ConfigureAwait(false) is not null)
                MessageReceived.Raise(message, null);
        });
    }

    private void OnMessageUpdated(SocketMessage after)
    {
        var before = snapshots.SwapMessage(after);
        MessageUpdated.Raise((before, after), after.Guild);
    }

    private void OnMessageDeleted(Channel channel, ulong? userId, ulong messageId, string? content)
    {
        var message = snapshots.RemoveMessage(messageId);
        if (message is null && content is not null && userId is { } authorId)
            message = RebuildMessage(channel, authorId, messageId, content);

        MessageDeleted.Raise((Uncached(messageId, message), channel), channel.Guild);
    }

    private void OnMessagesDeleted(Channel channel, ulong[] messageIds)
    {
        var messages = messageIds.Select(id => Uncached(id, snapshots.RemoveMessage(id))).ToList();
        MessagesBulkDeleted.Raise((messages, channel), channel.Guild);
    }

    private void OnMemberUpdated(SocketGuildMember after)
    {
        var before = snapshots.SwapMember(after);
        GuildMemberUpdated.Raise((before, after), after.Guild);

        if (snapshots.SwapUser(after.User) is { } userBefore)
            UserUpdated.Raise((userBefore, after), after.Guild);
    }

    private async Task OnMemberLeftAsync(SocketGuild guild, Cacheable<SocketGuildMember> member)
    {
        var snapshot = snapshots.RemoveMember(guild.Id, member.Id);
        var user = member.Value?.User ?? snapshot?.User.ToUser() ?? await FetchUserAsync(member.Id);
        if (user is not null)
            UserLeft.Raise((guild, user, snapshot), guild);
    }

    private async Task OnUserBannedAsync(SocketGuild guild, Cacheable<SocketGuildMember> member)
    {
        var user = member.Value?.User ?? snapshots.GetMember(guild.Id, member.Id)?.User.ToUser()
            ?? await FetchUserAsync(member.Id);
        if (user is not null)
            UserBanned.Raise((user, guild), guild);
    }

    private async Task OnUserUnbannedAsync(SocketGuild guild, ulong userId)
    {
        var user = snapshots.GetUser(userId)?.ToUser() ?? await FetchUserAsync(userId);
        if (user is not null)
            UserUnbanned.Raise((user, guild), guild);
    }

    private void OnVoiceStateUpdated(SocketVoiceState state)
    {
        if (state.Guild is not { } guild || !guild.Members.TryGetValue(state.UserId, out var member))
            return;

        var after = VoiceStateSnapshot.From(state);
        var before = snapshots.SwapVoiceState(guild.Id, state.UserId, after);
        if (before != after)
            UserVoiceStateUpdated.Raise((member, before, after), guild);
    }

    private void OnPresenceUpdated(PresenceGatewayData data)
    {
        var after = PresenceSnapshot.From(data);
        var before = snapshots.SwapPresence(after);
        if (before == after || !presenceLimiter.TryAcquire())
            return;

        var guild = data.GuildId is { } guildId ? client.GetGuild(guildId) : null;
        var user = guild?.Members.GetValueOrDefault(data.User.Id)?.User ?? User.Create(client, data.User);
        PresenceUpdated.Raise((user, before, after), guild);
    }

    private Cacheable<Message> WithCachedMessage(Cacheable<Message> message) =>
        message.Value is not null || snapshots.GetMessage(message.Id) is not { } cached
            ? message
            : new Cacheable<Message>(message.Id, cached, message.GetOrDownloadAsync!);

    private static Cacheable<Message> Uncached(ulong messageId, Message? message) =>
        new(messageId, message!, () => Task.FromResult<Message>(null!));

    private Message RebuildMessage(Channel channel, ulong authorId, ulong messageId, string content)
    {
        var author = snapshots.GetUser(authorId);
        return Message.Create(client, new MessageJson
        {
            Id = messageId,
            ChannelId = channel.Id,
            Content = content,
            CreatedAt = SnowflakeUtils.FromSnowflake(messageId),
            Author = new UserJson
            {
                Id = authorId,
                Username = author?.Username ?? authorId.ToString(),
                Discriminator = author?.Discriminator ?? "0",
                DisplayName = author?.DisplayName,
                AvatarHash = author?.AvatarHash,
                IsBot = author?.IsBot ?? false
            }
        });
    }

    private async Task<User?> FetchUserAsync(ulong userId)
    {
        try
        {
            return await client.GetUserAsync(userId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not fetch user {UserId}", userId);
            return null;
        }
    }

    private static void WithChannel(Cacheable<Channel> channel, Action<Channel> raise)
    {
        if (channel.Value is { } cached)
        {
            raise(cached);
            return;
        }

        _ = Task.Run(async () =>
        {
            if (await channel.GetOrDownloadAsync().ConfigureAwait(false) is { } downloaded)
                raise(downloaded);
        });
    }

    #region Delegates

    /// <summary>
    ///     Represents an asynchronous event handler with a single parameter.
    /// </summary>
    /// <typeparam name="T">The type of the event argument.</typeparam>
    /// <param name="args">The event data.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public delegate Task AsyncEventHandler<in T>(T args);

    /// <summary>
    ///     Represents an asynchronous event handler with two parameters.
    /// </summary>
    /// <typeparam name="T1">The type of the first event argument.</typeparam>
    /// <typeparam name="T2">The type of the second event argument.</typeparam>
    /// <param name="args1">The first event argument.</param>
    /// <param name="args2">The second event argument.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public delegate Task AsyncEventHandler<in T1, in T2>(T1 args1, T2 args2);

    /// <summary>
    ///     Represents an asynchronous event handler with three parameters.
    /// </summary>
    /// <typeparam name="T1">The type of the first event argument.</typeparam>
    /// <typeparam name="T2">The type of the second event argument.</typeparam>
    /// <typeparam name="T3">The type of the third event argument.</typeparam>
    /// <param name="args1">The first event argument.</param>
    /// <param name="args2">The second event argument.</param>
    /// <param name="args3">The third event argument.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public delegate Task AsyncEventHandler<in T1, in T2, in T3>(T1 args1, T2 args2, T3 args3);

    /// <summary>
    ///     Represents an asynchronous event handler with four parameters.
    /// </summary>
    /// <typeparam name="T1">The type of the first event argument.</typeparam>
    /// <typeparam name="T2">The type of the second event argument.</typeparam>
    /// <typeparam name="T3">The type of the third event argument.</typeparam>
    /// <typeparam name="T4">The type of the fourth event argument.</typeparam>
    /// <param name="args1">The first event argument.</param>
    /// <param name="args2">The second event argument.</param>
    /// <param name="args3">The third event argument.</param>
    /// <param name="args4">The fourth event argument.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public delegate Task AsyncEventHandler<in T1, in T2, in T3, in T4>(T1 args1, T2 args2, T3 args3, T4 args4);

    #endregion
}

/// <summary>
///     Implements rate limiting using a token bucket algorithm.
/// </summary>
public class RateLimiter
{
    private readonly int maxTokens;
    private readonly TimeSpan refillInterval;
    private DateTime lastRefill;
    private int tokens;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RateLimiter" /> class.
    /// </summary>
    /// <param name="maxTokens">The maximum number of tokens in the bucket.</param>
    /// <param name="refillInterval">The interval at which to refill tokens.</param>
    public RateLimiter(int maxTokens, TimeSpan refillInterval)
    {
        this.maxTokens = maxTokens;
        this.refillInterval = refillInterval;
        tokens = maxTokens;
        lastRefill = DateTime.UtcNow;
    }

    /// <summary>
    ///     Attempts to acquire a token from the rate limiter.
    /// </summary>
    /// <returns>True if a token was acquired, false otherwise.</returns>
    public bool TryAcquire()
    {
        RefillTokens();

        if (tokens > 0)
        {
            Interlocked.Decrement(ref tokens);
            return true;
        }

        return false;
    }

    private void RefillTokens()
    {
        var now = DateTime.UtcNow;
        var timeSinceRefill = now - lastRefill;

        if (timeSinceRefill >= refillInterval)
        {
            Interlocked.Exchange(ref tokens, maxTokens);
            lastRefill = now;
        }
    }
}

/// <summary>
///     Contains metrics for event processing.
/// </summary>
public class EventMetrics
{
    /// <summary>
    ///     Gets the total number of errors encountered.
    /// </summary>
    public long TotalErrors;

    /// <summary>
    ///     Gets the total execution time in milliseconds.
    /// </summary>
    public long TotalExecutionTime;

    /// <summary>
    ///     Gets the total number of events processed.
    /// </summary>
    public long TotalProcessed;

    /// <summary>
    ///     Gets or sets the last access time for cleanup purposes.
    /// </summary>
    public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Gets the average execution time per event in milliseconds.
    /// </summary>
    public double AverageExecutionTime
    {
        get
        {
            return TotalProcessed > 0 ? (double)TotalExecutionTime / TotalProcessed : 0;
        }
    }

    /// <summary>
    ///     Gets the error rate as a percentage.
    /// </summary>
    public double ErrorRate
    {
        get
        {
            return TotalProcessed > 0 ? (double)TotalErrors / TotalProcessed * 100 : 0;
        }
    }
}

/// <summary>
///     Contains metrics for module performance.
/// </summary>
public class ModuleMetrics
{
    /// <summary>
    ///     Gets the total number of errors in this module.
    /// </summary>
    public long Errors;

    /// <summary>
    ///     Gets the total number of events processed by this module.
    /// </summary>
    public long EventsProcessed;

    /// <summary>
    ///     Gets the total execution time for this module in milliseconds.
    /// </summary>
    public long TotalExecutionTime;

    /// <summary>
    ///     Gets or sets the last access time for cleanup purposes.
    /// </summary>
    public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Gets the average execution time per event for this module in milliseconds.
    /// </summary>
    public double AverageExecutionTime
    {
        get
        {
            return EventsProcessed > 0 ? (double)TotalExecutionTime / EventsProcessed : 0;
        }
    }

    /// <summary>
    ///     Gets the error rate for this module as a percentage.
    /// </summary>
    public double ErrorRate
    {
        get
        {
            return EventsProcessed > 0 ? (double)Errors / EventsProcessed * 100 : 0;
        }
    }
}
