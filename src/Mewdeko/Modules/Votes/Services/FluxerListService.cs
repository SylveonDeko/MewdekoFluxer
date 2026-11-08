using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.Votes.Services;

/// <summary>
///     The outcome of a single vote check.
/// </summary>
public enum FluxerListCheckStatus
{
    /// <summary>
    ///     The check went through.
    /// </summary>
    Ok,

    /// <summary>
    ///     The community has no FluxerList API key.
    /// </summary>
    NotConnected,

    /// <summary>
    ///     Too many checks were made recently.
    /// </summary>
    RateLimited,

    /// <summary>
    ///     FluxerList refused or could not answer the check.
    /// </summary>
    Failed
}

/// <summary>
///     The result of checking whether a member has voted.
/// </summary>
/// <param name="Status">Whether the check went through.</param>
/// <param name="Voted">Whether the member has an active vote.</param>
/// <param name="Votes">The community's total votes.</param>
/// <param name="Error">The reason the check failed.</param>
public record FluxerListCheckResult(FluxerListCheckStatus Status, bool Voted = false, int Votes = 0,
    string? Error = null);

/// <summary>
///     What a community's FluxerList connection looks like. Never holds the key.
/// </summary>
/// <param name="HasKey">Whether a key is stored.</param>
/// <param name="LastSyncAt">When the voters were last read.</param>
/// <param name="LastError">The last problem the sync ran into.</param>
public record FluxerListStatus(bool HasKey, DateTime? LastSyncAt, string? LastError);

/// <summary>
///     Reads each community's FluxerList voters on a schedule and runs the vote handling for new ones.
/// </summary>
public class FluxerListService : INService, IReadyExecutor
{
    private const string BaseUrl = "https://fluxerlist.com/api/v1";
    private const int PageSize = 100;
    private const int MaxPollPages = 10;
    private const int MaxWalkPages = 200;
    private const int CheckCacheSeconds = 120;
    private const int ChecksPerHour = 30;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FullSyncInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan PageDelay = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(31);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<(ulong, ulong), (FluxerListCheckResult Result, DateTime Expires)> checkCache =
        new();

    private readonly ConcurrentDictionary<ulong, (DateTime Start, int Count)> checkCounts = new();

    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<FluxerListService> logger;
    private readonly FluxerListKeyProtector protector;
    private readonly GeneratedBotStrings strings;
    private readonly VoteService voteService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FluxerListService" /> class.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="dbFactory">The database factory.</param>
    /// <param name="httpFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="protector">Encrypts the stored keys.</param>
    /// <param name="strings">The localized strings.</param>
    /// <param name="voteService">Runs the vote message and roles.</param>
    public FluxerListService(FluxerClient client, IDataConnectionFactory dbFactory, IHttpClientFactory httpFactory,
        ILogger<FluxerListService> logger, FluxerListKeyProtector protector, GeneratedBotStrings strings,
        VoteService voteService)
    {
        this.client = client;
        this.dbFactory = dbFactory;
        this.httpFactory = httpFactory;
        this.logger = logger;
        this.protector = protector;
        this.strings = strings;
        this.voteService = voteService;
    }

    /// <inheritdoc />
    public Task OnReadyAsync()
    {
        _ = Task.Run(RunLoop);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Gets whether a community has a key and how its last sync went.
    /// </summary>
    /// <param name="guildId">The community ID.</param>
    public async Task<FluxerListStatus> GetStatus(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.FluxerListKeys.FirstOrDefaultAsync(x => x.GuildId == guildId);
        return row is null
            ? new FluxerListStatus(false, null, null)
            : new FluxerListStatus(true, row.LastSyncAt, row.LastError);
    }

    /// <summary>
    ///     Checks a key belongs to an account that owns the community's listing, then stores it encrypted.
    /// </summary>
    /// <param name="guildId">The community ID.</param>
    /// <param name="apiKey">The FluxerList API key.</param>
    public async Task<(bool Success, string? Error)> SetKey(ulong guildId, string apiKey)
    {
        apiKey = apiKey?.Trim() ?? "";
        if (apiKey.Length == 0 || apiKey.Length > 200 || apiKey.Any(char.IsWhiteSpace))
            return (false, strings.FluxerListKeyInvalid(guildId));

        var response = await Send(apiKey, "/users/@me/servers");
        switch (response.Status)
        {
            case HttpStatusCode.OK:
                break;
            case HttpStatusCode.Unauthorized:
                return (false, strings.FluxerListKeyInvalid(guildId));
            case HttpStatusCode.TooManyRequests:
                return (false, strings.FluxerListKeyRateLimited(guildId));
            default:
                return (false, strings.FluxerListKeyUnreachable(guildId));
        }

        MeServers? servers;
        try
        {
            servers = JsonSerializer.Deserialize<MeServers>(response.Body ?? "", Json);
        }
        catch (JsonException)
        {
            return (false, strings.FluxerListKeyUnreachable(guildId));
        }

        var owns = servers?.Servers?.Any(x => x.Role == "owner" && x.FluxerGuildId == guildId.ToString()) ?? false;
        if (!owns)
            return (false, strings.FluxerListKeyNotOwner(guildId));

        var encrypted = protector.Protect(apiKey);
        await using var db = await dbFactory.CreateConnectionAsync();
        var existing = await db.FluxerListKeys.FirstOrDefaultAsync(x => x.GuildId == guildId);
        if (existing is null)
        {
            await db.InsertAsync(new FluxerListKey
            {
                GuildId = guildId, EncryptedKey = encrypted, NextPollAt = DateTime.UtcNow, DateAdded = DateTime.UtcNow
            });
        }
        else
        {
            await db.FluxerListKeys.Where(x => x.Id == existing.Id)
                .Set(x => x.EncryptedKey, encrypted)
                .Set(x => x.LastError, (string?)null)
                .Set(x => x.BackoffUntil, (DateTime?)null)
                .Set(x => x.ConsecutiveFailures, 0)
                .Set(x => x.NextPollAt, DateTime.UtcNow)
                .UpdateAsync();
        }

        return (true, null);
    }

    /// <summary>
    ///     Removes a community's key and the voters seen for it.
    /// </summary>
    /// <param name="guildId">The community ID.</param>
    public async Task RemoveKey(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        await db.FluxerListKeys.Where(x => x.GuildId == guildId).DeleteAsync();
        await db.FluxerListVoters.Where(x => x.GuildId == guildId).DeleteAsync();
    }

    /// <summary>
    ///     Asks FluxerList whether a member currently has a vote for the community.
    /// </summary>
    /// <param name="guildId">The community ID.</param>
    /// <param name="userId">The member ID.</param>
    public async Task<FluxerListCheckResult> CheckVote(ulong guildId, ulong userId)
    {
        if (checkCache.TryGetValue((guildId, userId), out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Result;

        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.FluxerListKeys.FirstOrDefaultAsync(x => x.GuildId == guildId);
        if (row is null)
            return new FluxerListCheckResult(FluxerListCheckStatus.NotConnected);

        if (!TakeCheckSlot(guildId))
            return new FluxerListCheckResult(FluxerListCheckStatus.RateLimited);

        var apiKey = protector.Unprotect(row.EncryptedKey);
        if (apiKey is null)
            return new FluxerListCheckResult(FluxerListCheckStatus.Failed, Error: "Stored key could not be read");

        var response = await Send(apiKey, $"/servers/{guildId}/check?fluxerId={userId}");
        if (response.Status == HttpStatusCode.TooManyRequests)
            return new FluxerListCheckResult(FluxerListCheckStatus.RateLimited);

        if (response.Status != HttpStatusCode.OK)
            return new FluxerListCheckResult(FluxerListCheckStatus.Failed, Error: Describe(response.Status));

        CheckResponse? body;
        try
        {
            body = JsonSerializer.Deserialize<CheckResponse>(response.Body ?? "", Json);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
            return new FluxerListCheckResult(FluxerListCheckStatus.Failed, Error: "Unexpected response");

        var result = new FluxerListCheckResult(FluxerListCheckStatus.Ok, body.Voted, body.Votes);
        checkCache[(guildId, userId)] = (result, DateTime.UtcNow.AddSeconds(CheckCacheSeconds));
        return result;
    }

    private bool TakeCheckSlot(ulong guildId)
    {
        var now = DateTime.UtcNow;
        var allowed = true;
        checkCounts.AddOrUpdate(guildId, _ => (now, 1), (_, current) =>
        {
            if (now - current.Start >= TimeSpan.FromHours(1))
                return (now, 1);

            if (current.Count >= ChecksPerHour)
            {
                allowed = false;
                return current;
            }

            return (current.Start, current.Count + 1);
        });
        return allowed;
    }

    private async Task RunLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await PollDueKeys();
            }
            catch (Exception e)
            {
                logger.LogError(e, "FluxerList poll loop failed");
            }

            PruneCheckState();
        } while (await timer.WaitForNextTickAsync());
    }

    private void PruneCheckState()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, value) in checkCache)
        {
            if (value.Expires <= now)
                checkCache.TryRemove(key, out _);
        }

        foreach (var (key, value) in checkCounts)
        {
            if (now - value.Start >= TimeSpan.FromHours(1))
                checkCounts.TryRemove(key, out _);
        }
    }

    private async Task PollDueKeys()
    {
        List<FluxerListKey> due;
        await using (var db = await dbFactory.CreateConnectionAsync())
        {
            var now = DateTime.UtcNow;
            due = await db.FluxerListKeys
                .Where(x => x.NextPollAt <= now && (x.BackoffUntil == null || x.BackoffUntil <= now))
                .OrderBy(x => x.NextPollAt)
                .Take(100)
                .ToListAsync();
        }

        if (due.Count == 0)
            return;

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(due.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                await PollKey(row);
            }
            catch (Exception e)
            {
                logger.LogError(e, "FluxerList poll failed for guild {GuildId}", row.GuildId);
                await Record(row, "Sync failed, will retry", failure: true);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private async Task PollKey(FluxerListKey row)
    {
        if (client.GetGuild(row.GuildId) is null)
        {
            await Record(row, null, skipped: true);
            return;
        }

        var apiKey = protector.Unprotect(row.EncryptedKey);
        if (apiKey is null)
        {
            await Record(row, "Stored key could not be read. Add it again.", backoff: TimeSpan.FromDays(1));
            return;
        }

        var seeding = row.LastFullSyncAt is null;
        var fullDue = seeding || DateTime.UtcNow - row.LastFullSyncAt >= FullSyncInterval;

        var outcome = fullDue
            ? await FullSync(row, apiKey, seeding)
            : await QuickSync(row, apiKey);

        if (outcome.Failure is { } failure)
        {
            await Record(row, failure.Message, failure.Backoff, failure.Transient);
            return;
        }

        await Record(row, null, fullSynced: fullDue);
    }

    private async Task<SyncOutcome> QuickSync(FluxerListKey row, string apiKey)
    {
        var known = await LoadKnown(row.GuildId);
        var fresh = new List<Voter>();

        for (var page = 1; page <= MaxPollPages; page++)
        {
            var (voters, total, failure) = await FetchPage(row.GuildId, apiKey, page);
            if (failure is not null)
                return new SyncOutcome(failure);

            var hitKnown = false;
            foreach (var voter in voters)
            {
                if (!TryParse(voter, out var userId, out var votedAt))
                    continue;

                if (known.TryGetValue(userId, out var seen) && votedAt - seen < TimeSpan.FromSeconds(1))
                    hitKnown = true;
                else
                    fresh.Add(voter);
            }

            if (hitKnown || voters.Count == 0 || page * PageSize >= total)
                break;

            await Task.Delay(PageDelay);
        }

        await Reward(row.GuildId, fresh, known);
        return new SyncOutcome(null);
    }

    private async Task<SyncOutcome> FullSync(FluxerListKey row, string apiKey, bool seeding)
    {
        var known = await LoadKnown(row.GuildId);
        var all = new List<Voter>();

        for (var page = 1; page <= MaxWalkPages; page++)
        {
            var (voters, total, failure) = await FetchPage(row.GuildId, apiKey, page);
            if (failure is not null)
                return new SyncOutcome(failure);

            all.AddRange(voters);
            if (voters.Count == 0 || page * PageSize >= total)
                break;

            await Task.Delay(PageDelay);
        }

        var current = new Dictionary<ulong, DateTime>();
        foreach (var voter in all)
        {
            if (TryParse(voter, out var userId, out var votedAt))
                current[userId] = votedAt;
        }

        var fresh = all
            .Where(v => TryParse(v, out var id, out var at) &&
                        (!known.TryGetValue(id, out var seen) || at - seen >= TimeSpan.FromSeconds(1)))
            .ToList();

        if (seeding)
        {
            await Remember(row.GuildId, fresh);
            return new SyncOutcome(null);
        }

        await Reward(row.GuildId, fresh, known);

        var gone = known.Keys.Where(id => !current.ContainsKey(id)).ToList();
        if (gone.Count > 0)
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            foreach (var chunk in gone.Chunk(500))
            {
                var ids = chunk;
                await db.FluxerListVoters.Where(x => x.GuildId == row.GuildId && ids.Contains(x.UserId))
                    .DeleteAsync();
            }

            await voteService.RemoveLapsedVoteRoles(row.GuildId, gone);
        }

        return new SyncOutcome(null);
    }

    private async Task Reward(ulong guildId, List<Voter> fresh, Dictionary<ulong, DateTime> known)
    {
        foreach (var voter in Enumerable.Reverse(fresh))
        {
            if (!TryParse(voter, out var userId, out _))
                continue;

            await Remember(guildId, [voter]);
            try
            {
                await voteService.HandleVote(guildId, userId);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Vote handling failed for user {UserId} in guild {GuildId}", userId, guildId);
            }
        }

        foreach (var voter in fresh)
        {
            if (TryParse(voter, out var userId, out var votedAt))
                known[userId] = votedAt;
        }
    }

    private async Task Remember(ulong guildId, List<Voter> voters)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        foreach (var voter in voters)
        {
            if (!TryParse(voter, out var userId, out var votedAt))
                continue;

            var updated = await db.FluxerListVoters
                .Where(x => x.GuildId == guildId && x.UserId == userId)
                .Set(x => x.VotedAt, votedAt)
                .UpdateAsync();

            if (updated == 0)
            {
                await db.InsertAsync(new FluxerListVoter
                {
                    GuildId = guildId, UserId = userId, VotedAt = votedAt, DateAdded = DateTime.UtcNow
                });
            }
        }
    }

    private async Task<Dictionary<ulong, DateTime>> LoadKnown(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var rows = await db.FluxerListVoters.Where(x => x.GuildId == guildId).ToListAsync();
        return rows.ToDictionary(x => x.UserId, x => x.VotedAt);
    }

    private static bool TryParse(Voter voter, out ulong userId, out DateTime votedAt)
    {
        votedAt = voter.VotedAt.UtcDateTime;
        return ulong.TryParse(voter.FluxerId, out userId) && userId != 0;
    }

    private async Task<(List<Voter> Voters, int Total, SyncFailure? Failure)> FetchPage(ulong guildId, string apiKey,
        int page)
    {
        var response = await Send(apiKey, $"/servers/{guildId}/voters?page={page}&limit={PageSize}");
        if (response.Status != HttpStatusCode.OK)
            return ([], 0, Fail(response));

        try
        {
            var body = JsonSerializer.Deserialize<VotersResponse>(response.Body ?? "", Json);
            return (body?.Voters ?? [], body?.Total ?? 0, null);
        }
        catch (JsonException)
        {
            return ([], 0, new SyncFailure("FluxerList sent an unreadable response", null, true));
        }
    }

    private static SyncFailure Fail(ApiResponse response)
    {
        return response.Status switch
        {
            HttpStatusCode.TooManyRequests => new SyncFailure("FluxerList is rate limiting this key",
                response.RetryAfter is { } wait && wait > TimeSpan.Zero
                    ? wait > MaxBackoff ? MaxBackoff : wait
                    : TimeSpan.FromHours(1), false),
            HttpStatusCode.Unauthorized => new SyncFailure("FluxerList rejected the key. Add it again.",
                TimeSpan.FromHours(6), false),
            HttpStatusCode.Forbidden => new SyncFailure("The key's account no longer owns this listing",
                TimeSpan.FromHours(6), false),
            HttpStatusCode.NotFound => new SyncFailure("This community's FluxerList listing was not found",
                TimeSpan.FromHours(6), false),
            _ => new SyncFailure("Could not reach FluxerList", null, true)
        };
    }

    private static string Describe(HttpStatusCode status)
    {
        return status switch
        {
            HttpStatusCode.Unauthorized => "FluxerList rejected the key",
            HttpStatusCode.Forbidden => "The key's account does not own this listing",
            HttpStatusCode.NotFound => "Listing not found",
            _ => "FluxerList is unavailable"
        };
    }

    private async Task Record(FluxerListKey row, string? error, TimeSpan? backoff = null, bool failure = false,
        bool skipped = false, bool fullSynced = false)
    {
        var now = DateTime.UtcNow;
        var failures = error is null ? 0 : failure ? row.ConsecutiveFailures + 1 : row.ConsecutiveFailures;
        var wait = backoff ?? (failure
            ? TimeSpan.FromMinutes(Math.Min(15 * Math.Pow(2, Math.Min(failures, 5)), 360))
            : PollInterval);

        var nextPollAt = now + wait;
        var backoffUntil = backoff is null && !failure ? (DateTime?)null : nextPollAt;

        await using var db = await dbFactory.CreateConnectionAsync();
        var query = db.FluxerListKeys.Where(x => x.Id == row.Id)
            .Set(x => x.NextPollAt, nextPollAt)
            .Set(x => x.BackoffUntil, backoffUntil)
            .Set(x => x.ConsecutiveFailures, failures);

        if (!skipped)
            query = query.Set(x => x.LastError, error);

        if (!skipped && error is null)
            query = query.Set(x => x.LastSyncAt, now);

        if (fullSynced)
            query = query.Set(x => x.LastFullSyncAt, now);

        await query.UpdateAsync();
    }

    private async Task<ApiResponse> Send(string apiKey, string path)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var retry = response.Headers.RetryAfter?.Delta;
            return new ApiResponse(response.StatusCode, body, retry);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ApiResponse(HttpStatusCode.ServiceUnavailable, null, null);
        }
    }

    private record ApiResponse(HttpStatusCode Status, string? Body, TimeSpan? RetryAfter);

    private record SyncFailure(string Message, TimeSpan? Backoff, bool Transient);

    private record SyncOutcome(SyncFailure? Failure);

    private class Voter
    {
        [JsonPropertyName("fluxerId")]
        public string? FluxerId { get; set; }

        [JsonPropertyName("votedAt")]
        public DateTimeOffset VotedAt { get; set; }
    }

    private class VotersResponse
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("voters")]
        public List<Voter>? Voters { get; set; }
    }

    private class CheckResponse
    {
        [JsonPropertyName("voted")]
        public bool Voted { get; set; }

        [JsonPropertyName("votes")]
        public int Votes { get; set; }
    }

    private class MeServers
    {
        [JsonPropertyName("servers")]
        public List<MeServer>? Servers { get; set; }
    }

    private class MeServer
    {
        [JsonPropertyName("fluxerGuildId")]
        public string? FluxerGuildId { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }
}
