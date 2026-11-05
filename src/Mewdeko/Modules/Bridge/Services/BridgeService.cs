using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DataModel;
using Fluxer.Net.Rest;
using Humanizer;
using LinqToDB;
using LinqToDB.Async;
using Embed = Fluxer.Net.Embed;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Modules.Bridge.Common;
using Mewdeko.Modules.Permissions.Services;
using Mewdeko.Services.Strings;
using StackExchange.Redis;

namespace Mewdeko.Modules.Bridge.Services;

/// <summary>
///     The outcome of a bridge operation.
/// </summary>
public enum BridgeResult
{
    /// <summary>The operation succeeded.</summary>
    Ok,

    /// <summary>The channel does not exist or cannot be bridged.</summary>
    ChannelInvalid,

    /// <summary>The channel is already bridged.</summary>
    AlreadyBridged,

    /// <summary>The bot lacks Manage Webhooks in the channel.</summary>
    MissingPermission,

    /// <summary>The code does not exist or has expired.</summary>
    CodeInvalid,

    /// <summary>The code was made on this platform instead of the other one.</summary>
    CodeWrongPlatform,

    /// <summary>There is no such bridge.</summary>
    NotFound
}

/// <summary>
///     A bridge as shown to commands and the dashboard.
/// </summary>
/// <param name="BridgeId">The bridge id.</param>
/// <param name="ChannelId">The local channel.</param>
/// <param name="RemoteGuildId">The community on the other platform.</param>
/// <param name="RemoteChannelId">The channel on the other platform.</param>
/// <param name="RemoteGuildName">The other community's name.</param>
/// <param name="RemoteChannelName">The other channel's name.</param>
/// <param name="Paused">Whether the bridge is paused.</param>
/// <param name="AllowBots">Whether bot messages are relayed.</param>
/// <param name="DateAdded">When the bridge was made.</param>
/// <param name="Options">The per-bridge options of this side.</param>
/// <param name="PairId">The pair the bridge was made through, or null.</param>
public sealed record BridgeInfo(Guid BridgeId, ulong ChannelId, ulong RemoteGuildId, ulong RemoteChannelId,
    string RemoteGuildName, string RemoteChannelName, bool Paused, bool AllowBots, DateTime DateAdded,
    BridgeOptions Options, Guid? PairId = null);

/// <summary>
///     Relays bridged channels to channels on the Discord bot through the shared Redis.
/// </summary>
public sealed partial class BridgeService : INService, IReadyExecutor, IUnloadableService, IBridgePairHost
{
    private const string WebhookName = "Mewdeko Bridge";
    private const string ThisPlatform = BridgeProtocol.Fluxer;
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private const long MaxTotalBytes = 24 * 1024 * 1024;
    private const int MessageLimit = 2000;
    private const int BucketSize = 10;
    private const int MaxQueued = 50;
    private static readonly TimeSpan BucketWindow = TimeSpan.FromSeconds(5);

    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly IDataCache cache;
    private readonly IHttpClientFactory httpFactory;
    private readonly GeneratedBotStrings strings;
    private readonly FilterService filterService;
    private readonly ILogger<BridgeService> logger;
    private readonly ConcurrentDictionary<ulong, BridgeState> byChannel = new();
    private readonly ConcurrentDictionary<Guid, BridgeState> byId = new();
    private readonly ConcurrentDictionary<ulong, byte> deletedCopies = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly string consumerName = $"{ThisPlatform}-{Environment.ProcessId}";
    private CancellationTokenSource? cts;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BridgeService" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="cache">The data cache, for its Redis connection.</param>
    /// <param name="httpFactory">The HTTP client factory, for downloading attachments.</param>
    /// <param name="handler">The event handler.</param>
    /// <param name="strings">The localized strings.</param>
    /// <param name="filterService">The filter service, for the community's filtered words.</param>
    /// <param name="logger">The logger.</param>
    public BridgeService(FluxerClient client, IDataConnectionFactory dbFactory, IDataCache cache,
        IHttpClientFactory httpFactory, EventHandler handler, GeneratedBotStrings strings,
        FilterService filterService, ILogger<BridgeService> logger)
    {
        this.filterService = filterService;
        this.client = client;
        this.dbFactory = dbFactory;
        this.cache = cache;
        this.httpFactory = httpFactory;
        this.strings = strings;
        this.logger = logger;

        handler.MessageReceived.Subscribe("BridgeService", OnMessageReceived);
        handler.MessageUpdated.Subscribe("BridgeService", OnMessageUpdated);
        handler.MessageDeleted.Subscribe("BridgeService", OnMessageDeleted);
        handler.ReactionAdded.Subscribe("BridgeService", OnReactionAdded);
        handler.ReactionRemoved.Subscribe("BridgeService", OnReactionRemoved);
        handler.ChannelCreated.Subscribe("BridgeService", OnChannelCreated);
        handler.ChannelDestroyed.Subscribe("BridgeService", OnChannelDestroyed);
        handler.ChannelUpdated.Subscribe("BridgeService", OnChannelUpdated);
        handler.RoleUpdated.Subscribe("BridgeService", OnRoleUpdated);
        Pairs = new BridgePairService(this, dbFactory, cache, strings, logger);
    }

    /// <summary>
    ///     Gets the pairs between communities on both platforms and the channel proposals inside them.
    /// </summary>
    public BridgePairService Pairs { get; }

    private IDatabase Redis => cache.Redis.GetDatabase();

    private ulong BotId => client.Gateway.CurrentUser!.Id;

    [GeneratedRegex(@"<(?<kind>@!?|@&|#)(?<id>\d+)>")]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"<a?:(?<name>\w+):\d+>")]
    private static partial Regex EmojiRegex();

    /// <summary>
    ///     Loads the bridges and starts reading the inbound stream.
    /// </summary>
    public async Task OnReadyAsync()
    {
        await using (var db = await dbFactory.CreateConnectionAsync())
        {
            foreach (var row in await db.ChannelBridges.ToListAsync())
                Track(row);
        }

        try
        {
            await Pairs.InitializeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load the bridge pairs");
        }

        var stream = BridgeProtocol.InboundStream(ThisPlatform);
        try
        {
            await Redis.StreamCreateConsumerGroupAsync(stream, BridgeProtocol.ConsumerGroup, "0-0");
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
        }

        cts?.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        await cache.Redis.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal(BridgeProtocol.WakeChannel(ThisPlatform)), (_, _) =>
            {
                if (wake.CurrentCount == 0)
                {
                    try
                    {
                        wake.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                }
            });

        _ = Task.Run(() => RunLoopAsync(token), token);
    }

    /// <summary>
    ///     Stops the reader loop.
    /// </summary>
    public Task Unload()
    {
        Pairs.Stop();
        cts?.Cancel();
        return Task.CompletedTask;
    }

    #region Cache

    private sealed class BridgeState(ChannelBridge row)
    {
        private readonly Queue<DateTime> sent = new();
        private int waiting;

        public ChannelBridge Row { get; } = row;

        public FluxerWebhookClient Webhook { get; set; } = new(row.WebhookUrl);

        public async Task<bool> WaitForSlotAsync()
        {
            if (Interlocked.Increment(ref waiting) > MaxQueued)
            {
                Interlocked.Decrement(ref waiting);
                return false;
            }

            try
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (sent)
                    {
                        var now = DateTime.UtcNow;
                        while (sent.Count > 0 && now - sent.Peek() >= BucketWindow)
                            sent.Dequeue();
                        if (sent.Count < BucketSize)
                        {
                            sent.Enqueue(now);
                            return true;
                        }

                        wait = sent.Peek() + BucketWindow - now;
                    }

                    await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50));
                }
            }
            finally
            {
                Interlocked.Decrement(ref waiting);
            }
        }
    }

    private BridgeState Track(ChannelBridge row)
    {
        var state = new BridgeState(row);
        byChannel[row.ChannelId] = state;
        byId[row.BridgeId] = state;
        return state;
    }

    private void Untrack(BridgeState state)
    {
        byChannel.TryRemove(state.Row.ChannelId, out _);
        byId.TryRemove(state.Row.BridgeId, out _);
    }

    #endregion

    #region Commands and dashboard

    /// <summary>
    ///     Gets the bridges of a guild.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <returns>The bridges.</returns>
    public List<BridgeInfo> GetBridges(ulong guildId) =>
        byChannel.Values.Where(x => x.Row.GuildId == guildId)
            .Select(x => ToInfo(x.Row)).OrderBy(x => x.DateAdded).ToList();

    /// <summary>
    ///     Gets the bridge of a channel.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The bridge, or null.</returns>
    public BridgeInfo? GetBridge(ulong channelId) =>
        byChannel.TryGetValue(channelId, out var state) ? ToInfo(state.Row) : null;

    /// <summary>
    ///     Gets a bridge of a guild by id.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="bridgeId">The bridge.</param>
    /// <returns>The bridge, or null.</returns>
    public BridgeInfo? GetBridge(ulong guildId, Guid bridgeId) =>
        byId.TryGetValue(bridgeId, out var state) && state.Row.GuildId == guildId ? ToInfo(state.Row) : null;

    private static BridgeInfo ToInfo(ChannelBridge row) =>
        new(row.BridgeId, row.ChannelId, row.RemoteGuildId, row.RemoteChannelId, row.RemoteGuildName,
            row.RemoteChannelName, row.Paused, row.AllowBots, row.DateAdded, row.OptionsOf(), row.PairId);

    private static ChannelBridge NewRow(Guid bridgeId, ulong guildId, ulong channelId, string webhookUrl,
        ulong userId, ulong remoteGuildId, string remoteGuildName, ulong remoteChannelId, string remoteChannelName,
        Guid? pairId) => new()
    {
        BridgeId = bridgeId,
        GuildId = guildId,
        ChannelId = channelId,
        WebhookUrl = webhookUrl,
        RemoteGuildId = remoteGuildId,
        RemoteChannelId = remoteChannelId,
        RemoteGuildName = remoteGuildName,
        RemoteChannelName = remoteChannelName,
        PairId = pairId,
        CreatedBy = userId,
        DateAdded = DateTime.UtcNow
    };

    private BridgeResult CheckChannel(ulong guildId, ulong channelId, out Channel? channel)
    {
        channel = null;
        var guild = client.GetGuild(guildId);
        channel = guild?.GetChannel(channelId);
        if (guild is null || channel is null || channel.Type != ChannelType.GuildText)
            return BridgeResult.ChannelInvalid;
        if (byChannel.ContainsKey(channelId))
            return BridgeResult.AlreadyBridged;
        return guild.CurrentMember.GetPermissions(channel).ManageWebhooks
            ? BridgeResult.Ok
            : BridgeResult.MissingPermission;
    }

    /// <summary>
    ///     Creates a link code for a channel.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="channelId">The channel to bridge.</param>
    /// <param name="userId">The user asking.</param>
    /// <returns>The result and, when it worked, the stored code.</returns>
    public async Task<(BridgeResult Result, BridgeLinkCode? Code)> CreateCodeAsync(ulong guildId, ulong channelId,
        ulong userId)
    {
        var check = CheckChannel(guildId, channelId, out var channel);
        if (check != BridgeResult.Ok)
            return (check, null);

        var request = new BridgeLinkCode
        {
            Platform = ThisPlatform,
            GuildId = guildId,
            ChannelId = channelId,
            GuildName = client.GetGuild(guildId)!.Name,
            ChannelName = channel!.Name,
            CreatedByUserId = userId
        };

        for (var attempt = 0; attempt < 5; attempt++)
        {
            request.Code = BridgeProtocol.NewLinkCode();
            if (await Redis.StringSetAsync(BridgeProtocol.LinkCodeKey(request.Code),
                    JsonSerializer.Serialize(request, BridgeProtocol.Json), BridgeProtocol.LinkCodeLifetime,
                    When.NotExists))
                return (BridgeResult.Ok, request);
        }

        return (BridgeResult.CodeInvalid, null);
    }

    /// <summary>
    ///     Accepts a link code made on the other platform and bridges a channel to it.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="channelId">The channel to bridge.</param>
    /// <param name="userId">The user asking.</param>
    /// <param name="rawCode">The code as typed.</param>
    /// <returns>The result and, when it worked, the remote side's request.</returns>
    public async Task<(BridgeResult Result, BridgeLinkCode? Request)> AcceptCodeAsync(ulong guildId,
        ulong channelId, ulong userId, string rawCode)
    {
        var code = BridgeProtocol.NormalizeCode(rawCode);
        var key = BridgeProtocol.LinkCodeKey(code);
        var check = CheckChannel(guildId, channelId, out var channel);
        if (check != BridgeResult.Ok)
            return (check, null);

        var peek = code.Length == 0 ? RedisValue.Null : await Redis.StringGetAsync(key);
        if (!peek.HasValue || Parse(peek) is not { } preview)
            return (BridgeResult.CodeInvalid, null);
        if (preview.Platform != BridgeProtocol.Other(ThisPlatform))
            return (BridgeResult.CodeWrongPlatform, null);

        var (url, created) = await EnsureWebhookAsync(channel!);

        var taken = await Redis.StringGetDeleteAsync(key);
        if (!taken.HasValue || Parse(taken) is not { } request)
        {
            if (created)
                await DeleteWebhookQuietlyAsync(url);
            return (BridgeResult.CodeInvalid, null);
        }

        var row = NewRow(Guid.NewGuid(), guildId, channelId, url, userId, request.GuildId, request.GuildName,
            request.ChannelId, request.ChannelName, null);

        await using (var db = await dbFactory.CreateConnectionAsync())
            await db.InsertAsync(row);
        Track(row);

        await PublishAsync(new BridgeEnvelope
        {
            Type = BridgeEnvelopeType.LinkAccepted,
            BridgeId = row.BridgeId,
            Link = new BridgeLinkAccepted
            {
                Request = request,
                GuildId = guildId,
                ChannelId = channelId,
                GuildName = client.GetGuild(guildId)!.Name,
                ChannelName = channel!.Name,
                AcceptedByUserId = userId
            }
        });

        return (BridgeResult.Ok, request);
    }

    /// <summary>
    ///     Pauses or resumes a bridge and tells the other side.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="bridgeId">The bridge.</param>
    /// <param name="paused">Whether to pause.</param>
    /// <returns>The result.</returns>
    public async Task<BridgeResult> SetPausedAsync(ulong guildId, Guid bridgeId, bool paused)
    {
        if (!byId.TryGetValue(bridgeId, out var state) || state.Row.GuildId != guildId)
            return BridgeResult.NotFound;

        await SetPausedAsync(state, paused, true);
        return BridgeResult.Ok;
    }

    /// <summary>
    ///     Sets whether a bridge relays bot messages.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="bridgeId">The bridge.</param>
    /// <param name="allow">Whether to allow bots.</param>
    /// <returns>The result.</returns>
    public async Task<BridgeResult> SetAllowBotsAsync(ulong guildId, Guid bridgeId, bool allow)
    {
        if (!byId.TryGetValue(bridgeId, out var state) || state.Row.GuildId != guildId)
            return BridgeResult.NotFound;

        state.Row.AllowBots = allow;
        await using var db = await dbFactory.CreateConnectionAsync();
        await db.ChannelBridges.Where(x => x.BridgeId == bridgeId).Set(x => x.AllowBots, allow).UpdateAsync();
        return BridgeResult.Ok;
    }

    /// <summary>
    ///     Saves a bridge's options and, when the direction changed, tells the other side.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="bridgeId">The bridge.</param>
    /// <param name="options">The new options.</param>
    /// <returns>The result.</returns>
    public async Task<BridgeResult> SetOptionsAsync(ulong guildId, Guid bridgeId, BridgeOptions options)
    {
        if (!byId.TryGetValue(bridgeId, out var state) || state.Row.GuildId != guildId)
            return BridgeResult.NotFound;

        var directionChanged = state.Row.Direction != (int)options.Direction;
        state.Row.Apply(options);
        await using (var db = await dbFactory.CreateConnectionAsync())
            await db.UpdateAsync(state.Row);

        if (directionChanged)
            await PublishAsync(new BridgeEnvelope
            {
                Type = BridgeEnvelopeType.DirectionChanged, BridgeId = bridgeId, Direction = options.Direction
            });

        return BridgeResult.Ok;
    }

    /// <summary>
    ///     Names a direction for people.
    /// </summary>
    /// <param name="guildId">The guild whose language is used.</param>
    /// <param name="direction">The direction.</param>
    /// <returns>The localized name.</returns>
    public string DirectionName(ulong guildId, BridgeDirection direction) => direction switch
    {
        BridgeDirection.SendOnly => strings.BridgeDirectionSend(guildId),
        BridgeDirection.ReceiveOnly => strings.BridgeDirectionReceive(guildId),
        _ => strings.BridgeDirectionBoth(guildId)
    };

    /// <summary>
    ///     Describes a minimum member age for people.
    /// </summary>
    /// <param name="minutes">The age in minutes.</param>
    /// <returns>The age as words.</returns>
    public static string DescribeMinAge(int minutes) =>
        TimeSpan.FromMinutes(minutes).Humanize(2, maxUnit: TimeUnit.Day);

    /// <summary>
    ///     Lists every option of a bridge for people.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="options">The bridge's options.</param>
    /// <returns>The text to show.</returns>
    public string DescribeOptions(ulong guildId, BridgeOptions options)
    {
        string Roles(IReadOnlyList<ulong> ids, string empty) =>
            ids.Count == 0 ? empty : string.Join(' ', ids.Select(x => $"<@&{x}>"));
        string Toggle(bool on) => on ? strings.BridgeValueOn(guildId) : strings.BridgeValueOff(guildId);

        return strings.BridgeSettingsBody(guildId, DirectionName(guildId, options.Direction),
            Roles(options.AllowedRoleIds, strings.BridgeValueEveryone(guildId)),
            Roles(options.BlockedRoleIds, strings.BridgeValueNone(guildId)),
            options.BlockedUserIds.Count == 0
                ? strings.BridgeValueNone(guildId)
                : string.Join(' ', options.BlockedUserIds.Select(x => $"<@{x}>")),
            options.MinMemberAgeMinutes == 0
                ? strings.BridgeValueOff(guildId)
                : DescribeMinAge(options.MinMemberAgeMinutes),
            Toggle(options.BlockInvites), Toggle(options.UseWordFilter), options.NameFormat);
    }

    /// <summary>
    ///     Removes a bridge and tells the other side.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="bridgeId">The bridge.</param>
    /// <returns>The result.</returns>
    public async Task<BridgeResult> RemoveAsync(ulong guildId, Guid bridgeId)
    {
        if (!byId.TryGetValue(bridgeId, out var state) || state.Row.GuildId != guildId)
            return BridgeResult.NotFound;

        await RemoveAsync(state);
        await PublishAsync(new BridgeEnvelope
        {
            Type = BridgeEnvelopeType.Unlinked, BridgeId = bridgeId
        });
        return BridgeResult.Ok;
    }

    private static BridgeLinkCode? Parse(RedisValue value)
    {
        try
        {
            return JsonSerializer.Deserialize<BridgeLinkCode>(value.ToString(), BridgeProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SetPausedAsync(BridgeState state, bool paused, bool publish)
    {
        state.Row.Paused = paused;
        await using (var db = await dbFactory.CreateConnectionAsync())
        {
            await db.ChannelBridges.Where(x => x.BridgeId == state.Row.BridgeId).Set(x => x.Paused, paused)
                .UpdateAsync();
        }

        if (publish)
            await PublishAsync(new BridgeEnvelope
            {
                Type = BridgeEnvelopeType.PauseChanged, BridgeId = state.Row.BridgeId, Paused = paused
            });
    }

    private async Task RemoveAsync(BridgeState state)
    {
        Untrack(state);
        await using (var db = await dbFactory.CreateConnectionAsync())
            await db.ChannelBridges.Where(x => x.BridgeId == state.Row.BridgeId).DeleteAsync();
        await DeleteWebhookQuietlyAsync(state.Row.WebhookUrl);
    }

    private async Task<(string Url, bool Created)> EnsureWebhookAsync(Channel channel)
    {
        var existing = (await client.Rest.GetChannelWebhooksAsync(channel.Id))
            .FirstOrDefault(x => x.Name == WebhookName && !string.IsNullOrEmpty(x.Token));
        if (existing is not null)
            return (existing.GetUrl(), false);

        var webhook = await client.Rest.CreateWebhookAsync(channel.Id, WebhookName);
        return (webhook.GetUrl(), true);
    }

    private async Task DeleteWebhookQuietlyAsync(string url)
    {
        try
        {
            await new FluxerWebhookClient(url).DeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete the bridge webhook");
        }
    }

    private async Task NoticeAsync(ulong guildId, ulong channelId, string text)
    {
        try
        {
            if (client.GetGuild(guildId)?.GetChannel(channelId) is { } channel)
                await channel.SendConfirmAsync(text);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not post a bridge notice in channel {ChannelId}", channelId);
        }
    }

    #endregion

    #region Outbound

    private async Task PublishAsync(BridgeEnvelope envelope)
    {
        envelope.Origin = ThisPlatform;
        envelope.SentAt = DateTimeOffset.UtcNow;
        try
        {
            await Redis.StreamAddAsync(BridgeProtocol.InboundStream(BridgeProtocol.Other(ThisPlatform)),
                BridgeProtocol.StreamField, BridgeProtocol.Serialize(envelope),
                maxLength: BridgeProtocol.StreamMaxLength, useApproximateMaxLength: true);
            await cache.Redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(BridgeProtocol.WakeChannel(BridgeProtocol.Other(ThisPlatform))), "1");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish a {Type} envelope for bridge {BridgeId}", envelope.Type,
                envelope.BridgeId);
        }
    }

    private BridgeState? Active(ulong channelId) =>
        byChannel.TryGetValue(channelId, out var state) && !state.Row.Paused
                                                        && state.Row.Direction != (int)BridgeDirection.ReceiveOnly
            ? state
            : null;

    [GeneratedRegex(@"fluxer\.(?:gg|app/invite)/\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FluxerInviteRegex();

    private async Task<bool> PassesOutboundAsync(BridgeState state, SocketMessage message)
    {
        var row = state.Row;
        var options = row.OptionsOf();
        var authorId = message.Author?.Id ?? 0;
        var member = message.Guild?.Members.GetValueOrDefault(authorId);
        var roleIds = member?.RoleIds.ToHashSet() ?? [];
        var content = message.Content ?? "";

        string? reason = null;
        if (options.BlockedUserIds.Contains(authorId))
            reason = "blocked user";
        else if (options.BlockedRoleIds.Any(roleIds.Contains))
            reason = "blocked role";
        else if (options.AllowedRoleIds.Count > 0 && !options.AllowedRoleIds.Any(roleIds.Contains))
            reason = "missing an allowed role";
        else if (options.MinMemberAgeMinutes > 0 && (member is null
                                                     || DateTimeOffset.UtcNow - member.JoinedAt
                                                     < TimeSpan.FromMinutes(options.MinMemberAgeMinutes)))
            reason = "member too new";
        else if (options.BlockInvites && (content.IsDiscordInvite() || FluxerInviteRegex().IsMatch(content)))
            reason = "invite link";
        else if (options.UseWordFilter && await filterService.ContainsFilteredWord(row.GuildId, content))
            reason = "filtered word";

        if (reason is null)
            return true;

        logger.LogDebug("Not relaying message {MessageId} on bridge {BridgeId}: {Reason}", message.Id, row.BridgeId,
            reason);
        return false;
    }

    private async Task OnMessageReceived(SocketMessage message)
    {
        try
        {
            if (Active(message.ChannelId) is not { } state)
                return;
            if (message.WebhookId == state.Webhook.Id || message.Author is not { } author
                                                      || author.Id == BotId
                                                      || message.Type is not (MessageType.Default or MessageType.Reply)
                                                      || author.IsBot && !state.Row.AllowBots
                                                      || !await PassesOutboundAsync(state, message))
                return;

            var built = BuildMessage(message, message.Guild, true);
            if (built.Content.Length == 0 && built.Attachments.Count == 0 && built.Embeds.Count == 0
                && built.Stickers.Count == 0)
                return;

            await PublishAsync(new BridgeEnvelope
            {
                Type = BridgeEnvelopeType.Message,
                BridgeId = state.Row.BridgeId,
                OriginMessageId = message.Id,
                Message = built
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not relay message {MessageId}", message.Id);
        }
    }

    private async Task OnMessageUpdated(Message? before, SocketMessage after)
    {
        try
        {
            if (Active(after.ChannelId) is not { } state)
                return;
            if (after.WebhookId == state.Webhook.Id || after.Author?.Id == BotId
                                                    || after.Author is { IsBot: true } && !state.Row.AllowBots)
                return;
            if (before is not null && before.Content == after.Content || !await PassesOutboundAsync(state, after))
                return;

            await PublishAsync(new BridgeEnvelope
            {
                Type = BridgeEnvelopeType.Edit,
                BridgeId = state.Row.BridgeId,
                OriginMessageId = after.Id,
                Message = BuildMessage(after, after.Guild, true)
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not relay the edit of message {MessageId}", after.Id);
        }
    }

    private async Task OnMessageDeleted(Cacheable<Message> message, Channel channel)
    {
        try
        {
            if (deletedCopies.TryRemove(message.Id, out _))
                return;
            if (Active(channel.Id) is not { } state || message.Value?.WebhookId == state.Webhook.Id)
                return;

            await PublishAsync(new BridgeEnvelope
            {
                Type = BridgeEnvelopeType.Delete, BridgeId = state.Row.BridgeId, OriginMessageId = message.Id
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not relay the deletion of message {MessageId}", message.Id);
        }
    }

    private Task OnReactionAdded(Cacheable<Message> message, Channel channel, SocketReaction reaction) =>
        RelayReactionAsync(channel, reaction, BridgeEnvelopeType.ReactionAdd);

    private Task OnReactionRemoved(Cacheable<Message> message, Channel channel, SocketReaction reaction) =>
        RelayReactionAsync(channel, reaction, BridgeEnvelopeType.ReactionRemove);

    private async Task RelayReactionAsync(Channel channel, SocketReaction reaction, string type)
    {
        try
        {
            if (Active(channel.Id) is not { } state || reaction.UserId == BotId)
                return;

            var emoji = reaction.Emoji;
            var users = await client.Rest.GetReactionsForEmojiAsync(channel.Id, reaction.MessageId,
                ReactionKey(emoji.Id, emoji.Name));

            await PublishAsync(new BridgeEnvelope
            {
                Type = type,
                BridgeId = state.Row.BridgeId,
                OriginMessageId = reaction.MessageId,
                Reaction = new BridgeReaction
                {
                    Emoji = emoji.Name,
                    CustomEmojiId = emoji.Id,
                    Animated = emoji.IsAnimated,
                    Count = users.Count(x => !x.IsBot)
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not relay a reaction on message {MessageId}", reaction.MessageId);
        }
    }

    private static string ReactionKey(ulong? id, string name) => id is { } emojiId ? $"{name}:{emojiId}" : name;

    private BridgeMessage BuildMessage(SocketMessage message, SocketGuild? guild, bool withEmbeds)
    {
        var author = message.Author;
        var result = new BridgeMessage
        {
            AuthorName = message.Member?.GetCurrentName() ?? author?.DisplayName ?? author?.Username ?? "",
            AuthorAvatarUrl = message.Member?.GetAvatarOrDefaultUrl(256) ?? author?.GetAvatarOrDefaultUrl(256),
            AuthorIsBot = author?.IsBot ?? false,
            Content = CleanContent(message.Content, guild, message.Mentions)
        };

        foreach (var attachment in message.Attachments ?? [])
        {
            var url = attachment.Url ?? attachment.GetAttachmentUrl();
            if (string.IsNullOrEmpty(url))
                continue;

            result.Attachments.Add(new BridgeAttachment
            {
                Url = url,
                Filename = attachment.Filename,
                Size = attachment.Size,
                ContentType = attachment.ContentType,
                Spoiler = attachment.IsSpoiler
            });
        }

        if (withEmbeds && author?.IsBot == true)
            foreach (var embed in (message.Embeds ?? []).Where(x => x.RawType is null or "rich").Take(10))
                result.Embeds.Add(ToBridgeEmbed(embed));

        foreach (var sticker in message.Stickers ?? [])
            result.Stickers.Add(sticker.Name);

        if (message.MessageReference is { Type: MessageReferenceType.Default } reference)
        {
            var referenced = message.ReferencedMessage;
            var snippet = referenced is null
                ? ""
                : CleanContent(referenced.Content, guild, referenced.Mentions);
            snippet = Regex.Replace(snippet, @"\s+", " ").Trim();
            if (snippet.Length == 0 && referenced?.Attachments is { Length: > 0 })
                snippet = "[attachment]";
            if (snippet.Length > 100)
                snippet = snippet[..99] + "…";

            result.ReplyTo = new BridgeReply
            {
                OriginMessageId = reference.MessageId,
                AuthorName = referenced?.Author?.DisplayName ?? referenced?.Author?.Username ?? "",
                Snippet = snippet
            };
        }

        return result;
    }

    private static BridgeEmbed ToBridgeEmbed(Embed embed) => new()
    {
        Title = embed.Title,
        Description = embed.Description,
        Url = embed.Url,
        Color = embed.Color is { } color ? (uint)color : null,
        AuthorName = embed.Author?.Name,
        AuthorIconUrl = embed.Author?.IconUrl,
        Footer = embed.Footer?.Text,
        FooterIconUrl = embed.Footer?.IconUrl,
        ImageUrl = embed.Image?.Url,
        ThumbnailUrl = embed.Thumbnail?.Url,
        Timestamp = embed.Timestamp,
        Fields = (embed.Fields ?? []).Select(x => new BridgeEmbedField
        {
            Name = x.Name, Value = x.Value, Inline = x.IsInline
        }).ToList()
    };

    private static string CleanContent(string? content, SocketGuild? guild, IEnumerable<User>? mentions)
    {
        var text = MentionRegex().Replace(content ?? "", match =>
        {
            if (!ulong.TryParse(match.Groups["id"].Value, out var id))
                return match.Value;

            return match.Groups["kind"].Value switch
            {
                "@" or "@!" => $"@{guild?.Members.GetValueOrDefault(id)?.GetCurrentName()
                                   ?? mentions?.FirstOrDefault(u => u.Id == id)?.GetCurrentName() ?? "unknown-user"}",
                "@&" => $"@{guild?.Roles.GetValueOrDefault(id)?.Name ?? "deleted-role"}",
                _ => $"#{guild?.GetChannel(id)?.Name ?? "deleted-channel"}"
            };
        });
        return EmojiRegex().Replace(text, match => $":{match.Groups["name"].Value}:");
    }

    #endregion

    #region Inbound

    private async Task RunLoopAsync(CancellationToken token)
    {
        var stream = BridgeProtocol.InboundStream(ThisPlatform);
        try
        {
            var start = (RedisValue)"0-0";
            do
            {
                var claimed = await Redis.StreamAutoClaimAsync(stream, BridgeProtocol.ConsumerGroup, consumerName,
                    30_000, start, 50);
                if (claimed.ClaimedEntries.Length > 0)
                    await ProcessBatchAsync(claimed.ClaimedEntries);
                start = claimed.NextStartId;
            } while (start != "0-0" && !token.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not claim pending bridge entries");
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(1), token);
                StreamEntry[] entries;
                do
                {
                    entries = await Redis.StreamReadGroupAsync(stream, BridgeProtocol.ConsumerGroup, consumerName,
                        StreamPosition.NewMessages, 50);
                    if (entries.Length > 0)
                        await ProcessBatchAsync(entries);
                } while (entries.Length > 0 && !token.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The bridge reader failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessBatchAsync(StreamEntry[] entries)
    {
        var stream = BridgeProtocol.InboundStream(ThisPlatform);
        var groups = new Dictionary<Guid, List<(RedisValue Id, BridgeEnvelope Envelope)>>();

        foreach (var entry in entries)
        {
            var json = entry[BridgeProtocol.StreamField];
            var envelope = json.HasValue ? BridgeProtocol.Deserialize(json.ToString()) : null;
            if (envelope is null || envelope.Origin != BridgeProtocol.Other(ThisPlatform))
            {
                await AckAsync(stream, entry.Id);
                continue;
            }

            if (!groups.TryGetValue(envelope.BridgeId, out var list))
                groups[envelope.BridgeId] = list = [];
            list.Add((entry.Id, envelope));
        }

        await Task.WhenAll(groups.Values.Select(async list =>
        {
            foreach (var (id, envelope) in list)
            {
                try
                {
                    await HandleAsync(envelope);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not handle a {Type} envelope for bridge {BridgeId}", envelope.Type,
                        envelope.BridgeId);
                }
                finally
                {
                    await AckAsync(stream, id);
                }
            }
        }));
    }

    private async Task AckAsync(string stream, RedisValue id)
    {
        try
        {
            await Redis.StreamAcknowledgeAsync(stream, BridgeProtocol.ConsumerGroup, id);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not acknowledge bridge entry {Id}", id);
        }
    }

    private async Task HandleAsync(BridgeEnvelope envelope)
    {
        if (DateTimeOffset.UtcNow - envelope.SentAt > BridgeProtocol.MaxEnvelopeAge)
            return;

        if (envelope.Type == BridgeEnvelopeType.LinkAccepted)
        {
            await HandleLinkAcceptedAsync(envelope);
            return;
        }

        if (BridgeEnvelopeType.IsPairLevel(envelope.Type))
        {
            await Pairs.HandleAsync(envelope);
            return;
        }

        if (!byId.TryGetValue(envelope.BridgeId, out var state))
            return;

        if (envelope.Type == BridgeEnvelopeType.DirectionChanged)
        {
            if (envelope.Direction is { } direction && state.Row.Direction != (int)direction.Inverse())
            {
                state.Row.Direction = (int)direction.Inverse();
                await using var db = await dbFactory.CreateConnectionAsync();
                await db.ChannelBridges.Where(x => x.BridgeId == state.Row.BridgeId)
                    .Set(x => x.Direction, state.Row.Direction).UpdateAsync();
            }

            return;
        }

        if (state.Row.Direction == (int)BridgeDirection.SendOnly && envelope.Type is BridgeEnvelopeType.Message
                or BridgeEnvelopeType.Edit or BridgeEnvelopeType.Delete or BridgeEnvelopeType.ReactionAdd
                or BridgeEnvelopeType.ReactionRemove)
            return;

        try
        {
            switch (envelope.Type)
            {
                case BridgeEnvelopeType.Message:
                    await HandleMessageAsync(state, envelope);
                    break;
                case BridgeEnvelopeType.Edit:
                    await HandleEditAsync(state, envelope);
                    break;
                case BridgeEnvelopeType.Delete:
                    await HandleDeleteAsync(state, envelope);
                    break;
                case BridgeEnvelopeType.ReactionAdd:
                case BridgeEnvelopeType.ReactionRemove:
                    await HandleReactionAsync(state, envelope);
                    break;
                case BridgeEnvelopeType.Unlinked:
                    await RemoveAsync(state);
                    await NoticeAsync(state.Row.GuildId, state.Row.ChannelId,
                        strings.BridgeRemovedRemote(state.Row.GuildId));
                    break;
                case BridgeEnvelopeType.PauseChanged:
                    if (envelope.Paused is { } paused && paused != state.Row.Paused)
                    {
                        await SetPausedAsync(state, paused, false);
                        await NoticeAsync(state.Row.GuildId, state.Row.ChannelId, paused
                            ? strings.BridgePausedRemote(state.Row.GuildId)
                            : strings.BridgeResumedRemote(state.Row.GuildId));
                    }

                    break;
            }
        }
        catch (FluxerApiException ex) when (ex.ErrorCode == FluxerErrorCodes.MissingPermissions)
        {
            await SetPausedAsync(state, true, true);
            await NoticeAsync(state.Row.GuildId, state.Row.ChannelId,
                strings.BridgePausedPermissions(state.Row.GuildId));
        }
    }

    private async Task HandleLinkAcceptedAsync(BridgeEnvelope envelope)
    {
        if (envelope.Link is not { } link || link.Request.Platform != ThisPlatform
                                          || byId.ContainsKey(envelope.BridgeId))
            return;

        var request = link.Request;
        var unlink = new BridgeEnvelope
        {
            Type = BridgeEnvelopeType.Unlinked, BridgeId = envelope.BridgeId
        };

        var guild = client.GetGuild(request.GuildId);
        if (envelope.ProposalId is { } proposalId && !await Pairs.TakeOutgoingProposalAsync(proposalId)
            || guild?.GetChannel(request.ChannelId) is not { } channel || byChannel.ContainsKey(request.ChannelId)
                                                                    || !guild.CurrentMember.GetPermissions(channel)
                                                                        .ManageWebhooks)
        {
            logger.LogInformation("Bridge link accepted for channel {ChannelId} could not be completed",
                request.ChannelId);
            await PublishAsync(unlink);
            return;
        }

        string url;
        try
        {
            (url, _) = await EnsureWebhookAsync(channel);
        }
        catch (FluxerApiException ex)
        {
            logger.LogInformation(ex, "Could not create the bridge webhook in channel {ChannelId}",
                request.ChannelId);
            await PublishAsync(unlink);
            return;
        }

        var row = NewRow(envelope.BridgeId, request.GuildId, request.ChannelId, url, request.CreatedByUserId,
            link.GuildId, link.GuildName, link.ChannelId, link.ChannelName, envelope.PairId);

        await using (var db = await dbFactory.CreateConnectionAsync())
            await db.InsertAsync(row);
        Track(row);

        await NoticeAsync(row.GuildId, row.ChannelId,
            strings.BridgeLinked(row.GuildId, link.ChannelName, link.GuildName));
    }

    private async Task<T> WithWebhookAsync<T>(BridgeState state, Func<FluxerWebhookClient, Task<T>> action)
    {
        try
        {
            return await action(state.Webhook);
        }
        catch (FluxerApiException ex) when (ex.ErrorCode == FluxerErrorCodes.UnknownWebhook)
        {
            var channel = client.GetGuild(state.Row.GuildId)?.GetChannel(state.Row.ChannelId);
            if (channel is null)
                throw;

            var webhook = await client.Rest.CreateWebhookAsync(channel.Id, WebhookName);
            state.Row.WebhookUrl = webhook.GetUrl();
            state.Webhook = new FluxerWebhookClient(state.Row.WebhookUrl);
            await using (var db = await dbFactory.CreateConnectionAsync())
            {
                await db.ChannelBridges.Where(x => x.BridgeId == state.Row.BridgeId)
                    .Set(x => x.WebhookUrl, state.Row.WebhookUrl).UpdateAsync();
            }

            return await action(state.Webhook);
        }
    }

    private async Task HandleMessageAsync(BridgeState state, BridgeEnvelope envelope)
    {
        if (envelope.Message is not { } message)
            return;
        if (!await state.WaitForSlotAsync())
        {
            logger.LogWarning("Bridge {BridgeId} is over its queue limit, dropping a message", envelope.BridgeId);
            return;
        }

        var (uploads, links) = PlanAttachments(message);
        var streams = new List<MemoryStream>();
        try
        {
            var requests = new List<AttachmentRequest>();
            foreach (var attachment in uploads)
            {
                var data = await DownloadAsync(attachment.Url);
                if (data is null)
                {
                    links.Add(attachment);
                    continue;
                }

                var stream = new MemoryStream(data);
                streams.Add(stream);
                requests.Add(new AttachmentRequest(stream, FileName(attachment)));
            }

            var reference = await ReplyReferenceAsync(state, envelope, message.ReplyTo);
            var text = Compose(message, links);
            if (reference is null && message.ReplyTo is { } reply)
                text = $"> **{reply.AuthorName}**: {reply.Snippet}\n{text}".TrimEnd();

            var embeds = ToEmbedRequests(message.Embeds);
            text = Truncate(text, MessageLimit);
            if (text.Length == 0 && embeds is null && requests.Count == 0)
                return;

            var name = SanitizeName(BridgeSettings.FormatName(state.Row.NameFormat, message.AuthorName,
                envelope.Origin, state.Row.RemoteGuildName));
            var content = text.Length == 0 ? null : text;
            var sent = await WithWebhookAsync(state, w => w.SendMessageWaitAsync(content, embeds, name,
                message.AuthorAvatarUrl, reference, AllowedMentionsRequest.None,
                attachments: requests.Count > 0 ? requests : null));
            var firstId = sent.Id;

            var lifetime = BridgeProtocol.MessageMapLifetime;
            await Redis.StringSetAsync(
                BridgeProtocol.MessageMapKey(envelope.BridgeId, envelope.Origin, envelope.OriginMessageId),
                firstId.ToString(), lifetime);
            await Redis.StringSetAsync(
                BridgeProtocol.MessageMapKey(envelope.BridgeId, ThisPlatform, firstId),
                envelope.OriginMessageId.ToString(), lifetime);
        }
        finally
        {
            foreach (var stream in streams)
                await stream.DisposeAsync();
        }
    }

    private async Task<MessageReferenceRequest?> ReplyReferenceAsync(BridgeState state, BridgeEnvelope envelope,
        BridgeReply? reply)
    {
        if (reply is null)
            return null;

        var copy = await LookupAsync(envelope.BridgeId, envelope.Origin, reply.OriginMessageId);
        return copy is { } id
            ? new MessageReferenceRequest
            {
                MessageId = id, ChannelId = state.Row.ChannelId, GuildId = state.Row.GuildId
            }
            : null;
    }

    private async Task<ulong?> LookupAsync(Guid bridgeId, string platform, ulong messageId)
    {
        var value = await Redis.StringGetAsync(BridgeProtocol.MessageMapKey(bridgeId, platform, messageId));
        return value.HasValue && ulong.TryParse(value.ToString(), out var id) ? id : null;
    }

    private async Task HandleEditAsync(BridgeState state, BridgeEnvelope envelope)
    {
        if (envelope.Message is not { } message
            || await LookupAsync(envelope.BridgeId, envelope.Origin, envelope.OriginMessageId) is not { } copy)
            return;

        var (_, links) = PlanAttachments(message);
        var text = Compose(message, links);
        var content = Truncate(text, MessageLimit);
        var embeds = ToEmbedRequests(message.Embeds);
        try
        {
            await WithWebhookAsync(state, w => w.EditMessageAsync(copy, content, embeds,
                allowedMentions: AllowedMentionsRequest.None));
        }
        catch (FluxerApiException ex) when (ex.ErrorCode == FluxerErrorCodes.UnknownMessage)
        {
        }
    }

    private async Task HandleDeleteAsync(BridgeState state, BridgeEnvelope envelope)
    {
        if (await LookupAsync(envelope.BridgeId, envelope.Origin, envelope.OriginMessageId) is not { } copy)
            return;

        deletedCopies[copy] = 0;
        try
        {
            await WithWebhookAsync(state, async w =>
            {
                await w.DeleteMessageAsync(copy);
                return 0;
            });
        }
        catch (FluxerApiException)
        {
            deletedCopies.TryRemove(copy, out _);
        }
    }

    private async Task HandleReactionAsync(BridgeState state, BridgeEnvelope envelope)
    {
        if (envelope.Reaction is not { } reaction
            || await LookupAsync(envelope.BridgeId, envelope.Origin, envelope.OriginMessageId) is not { } copy)
            return;

        ulong? emojiId = null;
        if (reaction.CustomEmojiId is { } customId)
        {
            if (client.GetGuild(state.Row.GuildId)?.Emojis.ContainsKey(customId) != true)
                return;
            emojiId = customId;
        }

        var key = ReactionKey(emojiId, reaction.Emoji);
        try
        {
            if (reaction.Count > 0)
                await client.Rest.AddReactionAsync(state.Row.ChannelId, copy, key);
            else
                await client.Rest.RemoveOwnReactionAsync(state.Row.ChannelId, copy, key);
        }
        catch (FluxerApiException ex) when (ex.ErrorCode != FluxerErrorCodes.MissingPermissions)
        {
            logger.LogDebug(ex, "Could not mirror a reaction on message {MessageId}", copy);
        }
    }

    private static (List<BridgeAttachment> Uploads, List<BridgeAttachment> Links) PlanAttachments(
        BridgeMessage message)
    {
        var uploads = new List<BridgeAttachment>();
        var links = new List<BridgeAttachment>();
        long total = 0;
        foreach (var attachment in message.Attachments)
        {
            if (attachment.Size <= MaxFileBytes && total + attachment.Size <= MaxTotalBytes && uploads.Count < 10)
            {
                total += attachment.Size;
                uploads.Add(attachment);
            }
            else
            {
                links.Add(attachment);
            }
        }

        return (uploads, links);
    }

    private static string FileName(BridgeAttachment attachment) =>
        attachment.Spoiler && !attachment.Filename.StartsWith("SPOILER_")
            ? $"SPOILER_{attachment.Filename}"
            : attachment.Filename;

    private static string Compose(BridgeMessage message, List<BridgeAttachment> links)
    {
        var builder = new StringBuilder(message.Content);
        foreach (var sticker in message.Stickers)
            builder.Append(builder.Length > 0 ? "\n" : "").Append($"[sticker: {sticker}]");
        foreach (var link in links)
            builder.Append(builder.Length > 0 ? "\n" : "").Append(link.Spoiler ? $"||{link.Url}||" : link.Url);
        return builder.ToString();
    }

    private async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var data = await httpFactory.CreateClient().GetByteArrayAsync(url, timeout.Token);
            return data.Length <= MaxFileBytes ? data : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not download a bridged attachment");
            return null;
        }
    }

    private static List<EmbedRequest>? ToEmbedRequests(List<BridgeEmbed> embeds)
    {
        if (embeds.Count == 0)
            return null;

        return embeds.Take(10).Select(x => new EmbedRequest
        {
            Title = x.Title?.TrimTo(256),
            Description = x.Description?.TrimTo(4096),
            Url = x.Url,
            Color = x.Color is { } color ? (int)color : null,
            Timestamp = x.Timestamp,
            Author = string.IsNullOrEmpty(x.AuthorName)
                ? null
                : new EmbedAuthorRequest
                {
                    Name = x.AuthorName.TrimTo(256), IconUrl = x.AuthorIconUrl
                },
            Footer = string.IsNullOrEmpty(x.Footer)
                ? null
                : new EmbedFooterRequest
                {
                    Text = x.Footer.TrimTo(2048), IconUrl = x.FooterIconUrl
                },
            Image = string.IsNullOrEmpty(x.ImageUrl)
                ? null
                : new EmbedMediaRequest
                {
                    Url = x.ImageUrl
                },
            Thumbnail = string.IsNullOrEmpty(x.ThumbnailUrl)
                ? null
                : new EmbedMediaRequest
                {
                    Url = x.ThumbnailUrl
                },
            Fields = x.Fields.Count == 0
                ? null
                : x.Fields.Take(25).Select(f => new EmbedFieldRequest
                {
                    Name = f.Name.TrimTo(256), Value = f.Value.TrimTo(1024), IsInline = f.Inline
                }).ToArray()
        }).ToList();
    }

    private static string SanitizeName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 80)
            clean = clean[..80].Trim();
        return clean.Length == 0 ? "Member" : clean;
    }

    private static string Truncate(string text, int limit)
    {
        text = text.Trim();
        return text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";
    }

    #endregion
}
