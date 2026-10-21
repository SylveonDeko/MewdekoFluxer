using System.IO;
using System.Text.RegularExpressions;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Modules.Starboard.Common;
using Mewdeko.Services.Analytics;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.Starboard.Services;

/// <summary>
///     Service responsible for managing multiple starboards in Discord servers.
/// </summary>
public class StarboardService : INService, IReadyExecutor, IUnloadableService
{
    private readonly FluxerClient client;
    private readonly IAnalyticsCollector collector;
    private readonly IDataConnectionFactory dbFactory;
    private readonly EventHandler eventHandler;
    private readonly ILogger<StarboardService> logger;
    private readonly GeneratedBotStrings strings;
    private List<DataModel.Starboard> starboardConfigs = [];
    private List<StarboardPost> starboardPosts = [];
    private List<StarboardReaction> starboardReactions = [];
    private List<StarboardStats> starboardStats = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="StarboardService" /> class.
    /// </summary>
    /// <param name="client">The Discord socket client.</param>
    /// <param name="dbFactory">The database context provider.</param>
    /// <param name="eventHandler">The event handler.</param>
    /// <param name="strings">The localized strings service.</param>
    /// <param name="logger">The logger instance for structured logging.</param>
    /// <param name="collector">The analytics collector.</param>
    public StarboardService(FluxerClient client, IDataConnectionFactory dbFactory,
        EventHandler eventHandler, GeneratedBotStrings strings, ILogger<StarboardService> logger,
        IAnalyticsCollector collector)
    {
        this.client = client;
        this.dbFactory = dbFactory;
        this.eventHandler = eventHandler;
        this.strings = strings;
        this.logger = logger;
        this.collector = collector;
        eventHandler.ReactionAdded.Subscribe("StarboardService", OnReactionAddedAsync);
        eventHandler.MessageDeleted.Subscribe("StarboardService", OnMessageDeletedAsync);
        eventHandler.ReactionRemoved.Subscribe("StarboardService", OnReactionRemoveAsync);
        eventHandler.ReactionsCleared.Subscribe("StarboardService", OnAllReactionsClearedAsync);
    }

    /// <inheritdoc />
    public async Task OnReadyAsync()
    {
        logger.LogInformation($"Starting {GetType()} Cache");
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        starboardPosts = await dbContext.StarboardPosts.ToListAsync();
        starboardConfigs = await dbContext.Starboards.ToListAsync();
        starboardStats = await dbContext.StarboardStats.Where(x => x.IsActive).ToListAsync();
        starboardReactions = await dbContext.StarboardReactions.ToListAsync();
        logger.LogInformation("Starboard Cache Ready");
    }

    /// <summary>
    ///     Unloads the service and unsubscribes from events.
    /// </summary>
    public Task Unload()
    {
        eventHandler.ReactionAdded.Unsubscribe("StarboardService", OnReactionAddedAsync);
        eventHandler.MessageDeleted.Unsubscribe("StarboardService", OnMessageDeletedAsync);
        eventHandler.ReactionRemoved.Unsubscribe("StarboardService", OnReactionRemoveAsync);
        eventHandler.ReactionsCleared.Unsubscribe("StarboardService", OnAllReactionsClearedAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Creates a new starboard configuration for a guild.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="channelId">The ID of the starboard channel.</param>
    /// <param name="emote">The emote to use for this starboard.</param>
    /// <param name="threshold">The number of reactions required.</param>
    /// <returns>The ID of the created starboard configuration.</returns>
    public async Task<int> CreateStarboard(SocketGuild guild, ulong channelId, string emote, int threshold)
    {
        // Validate emotes don't collide with existing starboards
        var emotes = ParseEmotes(emote);
        var existingStarboards = GetStarboards(guild.Id);

        foreach (var existingStarboard in existingStarboards)
        {
            var existingEmotes = ParseEmotes(existingStarboard.Emote);
            var collision = emotes.Intersect(existingEmotes).FirstOrDefault();
            if (collision != null)
                throw new InvalidOperationException(
                    $"Emoji {collision} is already used by another starboard in this guild.");
        }

        await using var db = await dbFactory.CreateConnectionAsync();
        var config = new DataModel.Starboard
        {
            GuildId = guild.Id,
            StarboardChannelId = channelId,
            Emote = emote,
            Threshold = threshold,
            CheckedChannels = "",
            UseBlacklist = false,
            AllowBots = false,
            RemoveOnDelete = true,
            RemoveOnReactionsClear = true,
            RemoveOnBelowThreshold = true,
            RepostThreshold = 0
        };

        config.Id = await db.InsertWithInt32IdentityAsync(config);
        starboardConfigs.Add(config);
        return config.Id;
    }

    /// <summary>
    ///     Deletes a starboard configuration.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <returns>True if the starboard was deleted, false otherwise.</returns>
    public async Task<bool> DeleteStarboard(SocketGuild guild, int starboardId)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        await db.DeleteAsync(config);

        starboardConfigs.Remove(config);
        return true;
    }

    /// <summary>
    ///     Gets all starboard configurations for a guild.
    /// </summary>
    /// <param name="guildId">The ID of the guild.</param>
    /// <returns>A list of starboard configurations.</returns>
    public List<DataModel.Starboard> GetStarboards(ulong guildId)
        => starboardConfigs.Where(x => x.GuildId == guildId).ToList();

    /// <summary>
    ///     Matches a Discord message jump link and captures its channel and message ids.
    /// </summary>
    private static readonly Regex JumpLinkRegex =
        new(@"discord(?:app)?\.com/channels/\d+/(\d+)/(\d+)", RegexOptions.Compiled);

    /// <summary>
    ///     Matches a bolded reaction count on a starboard repost stars line.
    /// </summary>
    private static readonly Regex StarsCountRegex = new(@"\*\*(\d+)\*\*", RegexOptions.Compiled);

    /// <summary>
    ///     Gets starboard highlights for a guild, resolving each post back to the original message so the
    ///     real content, author, image and channel are returned.
    /// </summary>
    /// <param name="guildId">The ID of the guild.</param>
    /// <param name="limit">The maximum number of highlights to return, clamped to 1 through 25.</param>
    /// <param name="sort">
    ///     "top" orders by star count (current or peak, whichever is higher); anything else returns the most
    ///     recently starred posts first.
    /// </param>
    /// <returns>A list of starboard highlights.</returns>
    public async Task<List<StarboardHighlight>> GetRecentHighlights(ulong guildId, int limit = 5,
        string? sort = null)
    {
        var sortByTop = string.Equals(sort, "top", StringComparison.OrdinalIgnoreCase);
        limit = Math.Clamp(limit, 1, 25);

        var guildStarboards = starboardConfigs.Where(s => s.GuildId == guildId).ToList();
        if (guildStarboards.Count == 0)
            return [];

        var guild = client.GetGuild(guildId);
        if (guild == null)
            return [];

        var starboardIds = guildStarboards.Select(gs => gs.Id).ToList();
        var statsByMessage = starboardStats
            .ToList()
            .Where(s => starboardIds.Contains(s.StarboardId))
            .GroupBy(s => s.MessageId)
            .ToDictionary(g => g.Key, g => g.ToList());

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        var postsQuery = dbContext.StarboardPosts.Where(sp => starboardIds.Contains(sp.StarboardConfigId));

        List<StarboardPost> candidates;
        if (sortByTop)
        {
            var allPosts = await postsQuery.ToListAsync();
            candidates = allPosts
                .OrderByDescending(p => HighlightScore(p, statsByMessage))
                .ThenByDescending(p => p.Id)
                .Take(limit * 3)
                .ToList();
        }
        else
        {
            candidates = await postsQuery
                .OrderByDescending(sp => sp.Id)
                .Take(limit * 3)
                .ToListAsync();
        }

        var highlights = new List<StarboardHighlight>();

        foreach (var post in candidates)
        {
            if (highlights.Count >= limit)
                break;

            try
            {
                var highlight = await BuildHighlight(guild, post, guildStarboards, statsByMessage);
                if (highlight != null)
                    highlights.Add(highlight);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to process starboard highlight for post {PostId}: {Message}", post.Id,
                    ex.Message);
            }
        }

        return sortByTop
            ? highlights.OrderByDescending(h => h.StarCount).ToList()
            : highlights;
    }

    /// <summary>
    ///     Scores a starboard post for the "top" ordering: the higher of its stored reaction count and the
    ///     peak count tracked in the starboard stats.
    /// </summary>
    private static int HighlightScore(StarboardPost post, Dictionary<ulong, List<StarboardStats>> statsByMessage)
    {
        var peak = statsByMessage.TryGetValue(post.MessageId, out var stats)
            ? stats.Where(s => s.StarboardId == post.StarboardConfigId).Select(s => s.PeakReactionCount)
                .DefaultIfEmpty(0).Max()
            : 0;
        return Math.Max(post.ReactionCount ?? 0, peak);
    }

    /// <summary>
    ///     Builds a single highlight. The original message is preferred; the starboard repost is only parsed
    ///     when the original can no longer be fetched. Returns null when neither message exists.
    /// </summary>
    private async Task<StarboardHighlight?> BuildHighlight(SocketGuild guild, StarboardPost post,
        List<DataModel.Starboard> guildStarboards, Dictionary<ulong, List<StarboardStats>> statsByMessage)
    {
        var starboardConfig = guildStarboards.FirstOrDefault(s => s.Id == post.StarboardConfigId);
        if (starboardConfig == null)
            return null;

        statsByMessage.TryGetValue(post.MessageId, out var messageStats);
        var stat = messageStats?.FirstOrDefault(s => s.StarboardId == post.StarboardConfigId)
                   ?? messageStats?.FirstOrDefault();

        var channelId = stat?.ChannelId ?? 0;
        Message? repost = null;

        if (channelId == 0)
        {
            repost = await TryGetMessageAsync(guild, starboardConfig.StarboardChannelId, post.PostId);
            channelId = FindJumpChannelId(repost, post.MessageId);
        }

        var original = channelId == 0 ? null : await TryGetMessageAsync(guild, channelId, post.MessageId);

        string? content;
        string authorName;
        string? authorAvatarUrl;
        string? imageUrl;

        if (original != null)
        {
            content = string.IsNullOrWhiteSpace(original.Content)
                ? (original.Embeds ?? []).Select(e => e.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
                : original.Content;

            var member = guild.GetUser(original.Author.Id);
            authorName = member?.DisplayName ?? original.Author.GlobalName ?? original.Author.Username;
            authorAvatarUrl = member?.GetDisplayAvatarUrl() ?? original.Author.GetAvatarOrDefaultUrl(160);
            imageUrl = FindImageUrl(original);
        }
        else
        {
            repost ??= await TryGetMessageAsync(guild, starboardConfig.StarboardChannelId, post.PostId);
            if (repost == null)
                return null;

            (content, authorName, authorAvatarUrl, imageUrl) = ParseRepost(repost);
        }

        var starboardEmotes = ParseEmotes(starboardConfig.Emote);
        var starEmote = !string.IsNullOrWhiteSpace(stat?.Emote) ? stat.Emote : starboardEmotes.FirstOrDefault();

        var starCount = post.ReactionCount ?? 0;
        if (starCount == 0)
            starCount = messageStats?.Where(s => s.StarboardId == post.StarboardConfigId)
                .Sum(s => s.ReactionCount) ?? 0;
        if (starCount == 0 && repost != null)
            starCount = ParseRepostStarCount(repost);

        return new StarboardHighlight
        {
            MessageId = post.MessageId,
            ChannelId = channelId,
            StarCount = starCount,
            Content = string.IsNullOrWhiteSpace(content) ? null : content.Trim(),
            AuthorName = authorName,
            AuthorAvatarUrl = string.IsNullOrWhiteSpace(authorAvatarUrl) ? null : authorAvatarUrl,
            ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl,
            StarEmote = starEmote ?? "⭐",
            CreatedAt = original?.Timestamp.UtcDateTime
                        ?? post.DateAdded
                        ?? stat?.FirstStarredAt
                        ?? SnowflakeUtils.FromSnowflake(post.MessageId).UtcDateTime
        };
    }

    /// <summary>
    ///     Fetches a message from a guild channel, using the cache first and REST second. Returns null when the
    ///     channel is gone, is not a message channel, or the message was deleted or is not readable.
    /// </summary>
    private static async Task<Message?> TryGetMessageAsync(SocketGuild guild, ulong channelId, ulong messageId)
    {
        if (channelId == 0 || guild.GetChannel(channelId) is not Channel channel)
            return null;

        try
        {
            return await channel.GetMessageAsync(messageId);
        }
        catch (FluxerApiException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Finds the source channel id in a starboard repost by reading its jump link from the embed. Returns 0
    ///     when none is found.
    /// </summary>
    private static ulong FindJumpChannelId(Message? repost, ulong messageId)
    {
        if (repost == null)
            return 0;

        var sources = (repost.Embeds ?? [])
            .SelectMany(e => new[] { e.Url, e.Description }.Concat((e.Fields ?? []).Select(f => f.Value)))
            .Append(repost.Content);

        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source))
                continue;

            foreach (Match match in JumpLinkRegex.Matches(source))
            {
                if (ulong.TryParse(match.Groups[2].Value, out var linkedMessage) && linkedMessage == messageId &&
                    ulong.TryParse(match.Groups[1].Value, out var linkedChannel))
                    return linkedChannel;
            }
        }

        return 0;
    }

    /// <summary>
    ///     Finds the first image on a message: an image attachment first, then an embed image or thumbnail.
    /// </summary>
    private static string? FindImageUrl(Message message)
    {
        var attachment = (message.Attachments ?? []).FirstOrDefault(a =>
            a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            || IsImageExtension(Path.GetExtension(a.Filename).ToLowerInvariant()));
        if (attachment != null)
            return attachment.ProxyUrl ?? attachment.Url;

        foreach (var embed in message.Embeds ?? [])
        {
            if (embed.Image != null)
                return embed.Image.Url;
            if (embed.Thumbnail != null)
                return embed.Thumbnail.Url;
        }

        return null;
    }

    /// <summary>
    ///     Reads content, author and image back out of a starboard repost embed.
    /// </summary>
    private static (string? Content, string AuthorName, string? AuthorAvatarUrl, string? ImageUrl)
        ParseRepost(Message repost)
    {
        var embed = repost.Embeds?.FirstOrDefault();
        if (embed == null)
            return (repost.Content, "Unknown User", null, null);

        var embedImage = embed.Image != null ? embed.Image.Url :
            embed.Thumbnail != null ? embed.Thumbnail.Url : null;
        return (embed.Description, embed.Author?.Name ?? "Unknown User", embed.Author?.IconUrl, embedImage);
    }

    /// <summary>
    ///     Sums the bolded reaction counts on a repost stars line, which is the message content.
    /// </summary>
    private static int ParseRepostStarCount(Message repost)
    {
        var starsLine = repost.Content;
        if (string.IsNullOrWhiteSpace(starsLine))
            return 0;

        var total = StarsCountRegex.Matches(starsLine)
            .Sum(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0);
        if (total > 0)
            return total;

        var digits = new string(starsLine.Split(' ')[0].Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var legacy) ? legacy : 0;
    }

    /// <summary>
    ///     Sets whether bots are allowed to be starred for a specific starboard.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="allowed">Whether bots are allowed.</param>
    /// <returns>True if the setting was updated, false if the starboard wasn't found.</returns>
    public async Task<bool> SetAllowBots(SocketGuild guild, int starboardId, bool allowed)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        config.AllowBots = allowed;
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Sets whether to remove starred messages when the original is deleted.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="removeOnDelete">Whether to remove on delete.</param>
    /// <returns>True if the setting was updated, false if the starboard wasn't found.</returns>
    public async Task<bool> SetRemoveOnDelete(SocketGuild guild, int starboardId, bool removeOnDelete)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        config.RemoveOnDelete = removeOnDelete;
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Sets whether to remove starred messages when reactions are cleared.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="removeOnClear">Whether to remove on clear.</param>
    /// <returns>True if the setting was updated, false if the starboard wasn't found.</returns>
    public async Task<bool> SetRemoveOnClear(SocketGuild guild, int starboardId, bool removeOnClear)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        config.RemoveOnReactionsClear = removeOnClear;
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Sets whether to remove starred messages when they fall below threshold.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="removeBelowThreshold">Whether to remove below threshold.</param>
    /// <returns>True if the setting was updated, false if the starboard wasn't found.</returns>
    public async Task<bool> SetRemoveBelowThreshold(SocketGuild guild, int starboardId, bool removeBelowThreshold)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        config.RemoveOnBelowThreshold = removeBelowThreshold;
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Sets the repost threshold for a starboard.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="threshold">The threshold value.</param>
    /// <returns>True if the setting was updated, false if the starboard wasn't found.</returns>
    public async Task<bool> SetRepostThreshold(SocketGuild guild, int starboardId, int threshold)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        config.RepostThreshold = threshold;
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Sets the star threshold for a starboard.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="threshold">The threshold value.</param>
    /// <returns>True if the setting was updated, false if the starboard wasn't found.</returns>
    public async Task<bool> SetStarThreshold(SocketGuild guild, int starboardId, int threshold)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        config.Threshold = threshold;
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Sets whether to use blacklist mode for channel checking.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="useBlacklist">Whether to use blacklist mode.</param>
    /// <returns>True if the setting was updated, false if the starboard wasn't found.</returns>
    public async Task<bool> SetUseBlacklist(SocketGuild guild, int starboardId, bool useBlacklist)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        config.UseBlacklist = useBlacklist;
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Toggles a channel in the starboard's check list.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="starboardId">The ID of the starboard configuration.</param>
    /// <param name="channelId">The channel ID to toggle.</param>
    /// <returns>A tuple containing whether the channel was added and the starboard configuration.</returns>
    public async Task<(bool WasAdded, DataModel.Starboard Config)> ToggleChannel(SocketGuild guild, int starboardId,
        string channelId)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return (false, null);

        var channels = config.CheckedChannels.Split(" ", StringSplitOptions.RemoveEmptyEntries).ToList();

        await using var db = await dbFactory.CreateConnectionAsync();
        if (!channels.Contains(channelId))
        {
            channels.Add(channelId);
            config.CheckedChannels = string.Join(" ", channels);
            await db.UpdateAsync(config);

            return (true, config);
        }

        channels.Remove(channelId);
        config.CheckedChannels = string.Join(" ", channels);
        await db.UpdateAsync(config);

        return (false, config);
    }

    /// <summary>
    ///     Adds a new emote to an existing starboard configuration.
    /// </summary>
    public async Task<bool> AddEmoteToStarboard(SocketGuild guild, int starboardId, string newEmote)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        // Check for collisions
        var existingStarboards = GetStarboards(guild.Id).Where(x => x.Id != starboardId);
        foreach (var existingStarboard in existingStarboards)
        {
            var existingEmotes = ParseEmotes(existingStarboard.Emote);
            if (existingEmotes.Contains(newEmote))
                throw new InvalidOperationException(
                    $"Emoji {newEmote} is already used by another starboard in this guild.");
        }

        var currentEmotes = ParseEmotes(config.Emote);
        if (currentEmotes.Contains(newEmote))
            return false; // Already has this emote

        currentEmotes.Add(newEmote);
        config.Emote = string.Join("|", currentEmotes);

        await using var db = await dbFactory.CreateConnectionAsync();
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Removes an emote from a starboard configuration.
    /// </summary>
    public async Task<bool> RemoveEmoteFromStarboard(SocketGuild guild, int starboardId, string emoteToRemove)
    {
        var config = starboardConfigs.FirstOrDefault(x => x.Id == starboardId && x.GuildId == guild.Id);
        if (config == null)
            return false;

        var currentEmotes = ParseEmotes(config.Emote);
        if (!currentEmotes.Remove(emoteToRemove))
            return false; // Didn't have this emote

        if (currentEmotes.Count == 0)
            throw new InvalidOperationException(
                "Cannot remove the last emote from a starboard. Delete the starboard instead.");

        config.Emote = string.Join("|", currentEmotes);

        await using var db = await dbFactory.CreateConnectionAsync();
        await db.UpdateAsync(config);

        return true;
    }

    /// <summary>
    ///     Gets starboard statistics for a guild.
    /// </summary>
    public async Task<StarboardStatsDto?> GetStarboardStats(ulong guildId)
    {
        return await GetGuildStarboardStats(guildId);
    }

    /// <summary>
    ///     Gets starboard statistics for a specific user in a guild.
    /// </summary>
    public async Task<UserStarboardStatsDto?> GetUserStarboardStats(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        var guildStarboards = starboardConfigs.Where(x => x.GuildId == guildId).Select(x => x.Id).ToList();
        if (guildStarboards.Count == 0)
            return null;

        var userStats = starboardStats.Where(x =>
            guildStarboards.Contains(x.StarboardId) && x.IsActive && x.AuthorId == userId);
        var userReactions =
            starboardReactions.Where(x => guildStarboards.Contains(x.StarboardId) && x.UserId == userId);
        var receivedReactions = starboardReactions.Where(x => guildStarboards.Contains(x.StarboardId));

        var messagesStarred = userStats.Count();
        var totalStarsReceived = userStats.Sum(x => x.PeakReactionCount);
        var totalStarsGiven = userReactions.Count();

        // Get top starred posts by this user
        var topStarredPosts = userStats.OrderByDescending(x => x.PeakReactionCount)
            .Take(3)
            .Select(x => new TopStarredPost(x.MessageId, x.PeakReactionCount, x.Emote))
            .ToList();

        // Get who this user stars the most (their "idols")
        var starredUsers = receivedReactions
            .Where(r => userReactions.Any(ur => ur.MessageId == r.MessageId))
            .Join(starboardStats.Where(s => guildStarboards.Contains(s.StarboardId)),
                r => new
                {
                    r.MessageId, r.Emote
                },
                s => new
                {
                    s.MessageId, s.Emote
                },
                (r, s) => s.AuthorId)
            .Where(authorId => authorId != userId)
            .GroupBy(authorId => authorId)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => new UserStarGiven(g.Key, g.Count()))
            .ToList();

        // Get who stars this user the most (their "fans")
        var fans = receivedReactions
            .Join(starboardStats.Where(s => guildStarboards.Contains(s.StarboardId) && s.AuthorId == userId),
                r => new
                {
                    r.MessageId, r.Emote
                },
                s => new
                {
                    s.MessageId, s.Emote
                },
                (r, s) => r.UserId)
            .Where(fanId => fanId != userId)
            .GroupBy(fanId => fanId)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => new UserStarGiven(g.Key, g.Count()))
            .ToList();

        return new UserStarboardStatsDto
        {
            MessagesStarred = messagesStarred,
            StarsReceived = totalStarsReceived,
            StarsGiven = totalStarsGiven,
            TopStarredPosts = topStarredPosts,
            MostStarredUsers = starredUsers,
            TopFans = fans
        };
    }

    /// <summary>
    ///     Gets guild-wide starboard statistics.
    /// </summary>
    private async Task<StarboardStatsDto?> GetGuildStarboardStats(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        var guildStarboards = starboardConfigs.Where(x => x.GuildId == guildId).Select(x => x.Id).ToList();
        if (guildStarboards.Count == 0)
            return new StarboardStatsDto();

        var stats = starboardStats.Where(x => guildStarboards.Contains(x.StarboardId) && x.IsActive);
        var reactions = starboardReactions.Where(x => guildStarboards.Contains(x.StarboardId));

        return new StarboardStatsDto
        {
            MostStarredUser = stats
                .GroupBy(x => x.AuthorId)
                .OrderByDescending(g => g.Sum(x => x.PeakReactionCount))
                .Select(g => new UserStarStats
                {
                    UserId = g.Key, TotalStars = g.Sum(x => x.PeakReactionCount), MessageCount = g.Count()
                })
                .FirstOrDefault(),
            MostActiveChannel = stats
                .GroupBy(x => x.ChannelId)
                .OrderByDescending(g => g.Sum(x => x.PeakReactionCount))
                .Select(g => new ChannelStarStats
                {
                    ChannelId = g.Key, TotalStars = g.Sum(x => x.PeakReactionCount), MessageCount = g.Count()
                })
                .FirstOrDefault(),
            MostActiveStarrer = reactions
                .GroupBy(x => x.UserId)
                .OrderByDescending(g => g.Count())
                .Select(g => new StarrerStats
                {
                    UserId = g.Key, StarsGiven = g.Count(), UniqueEmotesUsed = g.Select(x => x.Emote).Distinct().Count()
                })
                .FirstOrDefault(),
            TotalStarredMessages = stats.Count(),
            TotalStars = stats.Sum(x => x.PeakReactionCount)
        };
    }

    /// <summary>
    ///     Parses emotes from a pipe-separated string.
    /// </summary>
    private static List<string> ParseEmotes(string emoteString)
    {
        if (string.IsNullOrWhiteSpace(emoteString))
            return new List<string>();

        return emoteString.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    ///     Builds a starboard post: the star counts as message text and an embed with the author, content, first
    ///     image, a markdown jump link and any other attachments as links.
    /// </summary>
    private async Task<(string Stars, EmbedRequest EmbedRequest)> CreateStarboardPostAsync(Message message, string? content,
        List<string> starboardEmotes, bool allowBots, ulong guildId)
    {
        var reactionCounts = new List<string>();
        foreach (var emote in starboardEmotes.Select(emoteString => emoteString.ToIEmote()))
        {
            try
            {
                var count = await CountStarsAsync(message, emote, allowBots);
                if (count > 0)
                    reactionCounts.Add($"{emote} **{count}**");
            }
            catch
            {
                // Ignore if emote can't be processed
            }
        }

        var embed = new EmbedBuilder()
            .WithOkColor()
            .WithAuthor(message.Author)
            .WithTimestamp(message.Timestamp)
            .AddField(strings.StarboardSource(guildId),
                strings.StarboardJumpToMessage(guildId, message.GetJumpUrl()));

        if (!string.IsNullOrWhiteSpace(content))
            embed.WithDescription(content);

        var image = (message.Attachments ?? []).FirstOrDefault(a => IsImageExtension(Path.GetExtension(a.Filename).ToLowerInvariant()));
        if (image != null)
            embed.WithImageUrl(image.ProxyUrl);

        var otherFiles = message.Attachments.Where(a => a != image).Take(5)
            .Select(a => $"[{a.Filename}]({a.Url})").ToList();
        if (otherFiles.Count > 0)
            embed.AddField(strings.StarboardAttachments(guildId), string.Join("\n", otherFiles));

        var stars = reactionCounts.Count > 0 ? string.Join(" ", reactionCounts) : "✨";
        return (stars, embed.Build());
    }

    /// <summary>
    ///     Checks if a file extension is for an image.
    /// </summary>
    private static bool IsImageExtension(string extension)
    {
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tiff" or ".svg" => true,
            _ => false
        };
    }

    /// <summary>
    ///     Tracks starboard statistics for a message.
    /// </summary>
    private async Task TrackStarboardStats(Message message, int starboardId, string emote, int reactionCount,
        ulong channelId)
    {
        // Safety check to prevent foreign key constraint violations
        if (starboardId <= 0)
        {
            logger.LogWarning("[Starboard] Attempted to track stats for invalid starboard ID: {StarboardId}",
                starboardId);
            return;
        }

        await using var db = await dbFactory.CreateConnectionAsync();

        var existing = starboardStats.FirstOrDefault(x =>
            x.MessageId == message.Id && x.StarboardId == starboardId && x.Emote == emote);

        if (existing != null)
        {
            // Update existing stats
            existing.ReactionCount = reactionCount;
            existing.PeakReactionCount = Math.Max(existing.PeakReactionCount, reactionCount);
            existing.LastUpdatedAt = DateTime.UtcNow;
            existing.IsActive = reactionCount > 0;

            await db.UpdateAsync(existing);
        }
        else if (reactionCount > 0)
        {
            // Create new stats entry
            var newStats = new StarboardStats
            {
                StarboardId = starboardId,
                MessageId = message.Id,
                ChannelId = channelId,
                AuthorId = message.Author.Id,
                Emote = emote,
                ReactionCount = reactionCount,
                PeakReactionCount = reactionCount,
                FirstStarredAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                IsActive = true
            };

            await db.InsertAsync(newStats);
            starboardStats.Add(newStats);
        }
    }

    /// <summary>
    ///     Tracks an individual star reaction.
    /// </summary>
    private async Task TrackStarboardReaction(ulong messageId, int starboardId, ulong userId, string emote)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        var existing = starboardReactions.FirstOrDefault(x =>
            x.MessageId == messageId && x.UserId == userId && x.Emote == emote);

        if (existing == null)
        {
            var newReaction = new StarboardReaction
            {
                StarboardId = starboardId,
                MessageId = messageId,
                UserId = userId,
                Emote = emote,
                DateAdded = DateTime.UtcNow
            };

            await db.InsertAsync(newReaction);
            starboardReactions.Add(newReaction);
        }
    }


    private async Task AddStarboardPost(ulong messageId, ulong postId, int starboardId, int reactionCount = 0)
    {
        await using var dbContext = await dbFactory.CreateConnectionAsync();

        var post = starboardPosts.Find(x => x.MessageId == messageId && x.StarboardConfigId == starboardId);
        if (post == null)
        {
            var toAdd = new StarboardPost
            {
                MessageId = messageId, PostId = postId, StarboardConfigId = starboardId, ReactionCount = reactionCount
            };
            starboardPosts.Add(toAdd);
            await dbContext.InsertAsync(toAdd);
            collector.Feature("starboard", starboardConfigs.Find(x => x.Id == starboardId)?.GuildId);

            return;
        }

        if (post.PostId == postId)
        {
            // Update reaction count if different
            if (post.ReactionCount != reactionCount)
            {
                post.ReactionCount = reactionCount;
                await dbContext.UpdateAsync(post);
            }

            return;
        }

        starboardPosts.Remove(post);
        post.PostId = postId;
        post.ReactionCount = reactionCount;
        await dbContext.UpdateAsync(post);
        starboardPosts.Add(post);
    }

    private async Task RemoveStarboardPost(ulong messageId, int starboardId)
    {
        var toRemove = starboardPosts.Find(x => x.MessageId == messageId && x.StarboardConfigId == starboardId);
        if (toRemove == null)
            return;

        await using var dbContext = await dbFactory.CreateConnectionAsync();
        await dbContext.DeleteAsync(toRemove);
        starboardPosts.Remove(toRemove);
    }

    /// <summary>
    ///     Counts the stars a message has for one emote, using the reaction metadata
    ///     Discord already sends with the message. The reactor list is only fetched
    ///     when bots must be excluded and the raw count could matter: at or above
    ///     <paramref name="listWhenAtLeast" /> when given, otherwise whenever it is
    ///     non-zero. Below the threshold the bot's own reaction is subtracted from the
    ///     metadata count, since it is the only bot reaction knowable without a fetch.
    /// </summary>
    /// <param name="message">The message being counted.</param>
    /// <param name="emote">The starboard emote to count.</param>
    /// <param name="allowBots">Whether reactions from bots count.</param>
    /// <param name="listWhenAtLeast">Raw count at which the exact non-bot count is worth a fetch.</param>
    /// <returns>The number of counting reactions.</returns>
    private static async Task<int> CountStarsAsync(Message message, Emoji emote, bool allowBots,
        int? listWhenAtLeast = null)
    {
        var meta = message.Reactions?.FirstOrDefault(r => r.Emoji.Matches(emote));
        if (meta == null || meta.Count <= 0)
            return 0;

        var raw = meta.Count;
        if (allowBots)
            return raw;

        if (listWhenAtLeast is { } floor && raw < floor)
            return meta.Me == true ? raw - 1 : raw;

        var users = await message.GetReactionsForEmojiAsync(emote);
        return users.Count(u => !u.IsBot);
    }

    private async Task OnReactionAddedAsync(Cacheable<Message> message,
        Channel channel,
        SocketReaction reaction)
    {
        if (reaction.User is not { IsBot: false } || channel.Guild is not { } guild)
            return;

        var guildStarboards = GetStarboards(guild.Id);
        if (!guildStarboards.Any())
            return;

        foreach (var starboard in guildStarboards)
        {
            await HandleReactionChange(message, channel, reaction, starboard, true);
        }
    }

    private async Task OnReactionRemoveAsync(Cacheable<Message> message,
        Channel channel,
        SocketReaction reaction)
    {
        if (reaction.User is not { IsBot: false } || channel.Guild is not { } guild)
            return;

        var guildStarboards = GetStarboards(guild.Id);
        if (!guildStarboards.Any())
            return;

        foreach (var starboard in guildStarboards)
        {
            await HandleReactionChange(message, channel, reaction, starboard, false);
        }
    }

    private async Task HandleReactionChange(Cacheable<Message> message,
        Channel channel,
        SocketReaction reaction,
        DataModel.Starboard starboard,
        bool isAdd)
    {
        var textChannel = channel;

        // Check if the reaction matches any of the starboard's emotes
        var starboardEmotes = ParseEmotes(starboard.Emote);
        var matchingEmote = starboardEmotes.FirstOrDefault(e =>
        {
            var emote = e.ToIEmote();
            return emote.Name != null && emote.Matches(reaction.Emoji);
        });

        if (matchingEmote == null)
            return;

        var starboardChannel = await textChannel.Guild.GetTextChannelAsync(starboard.StarboardChannelId);
        if (starboardChannel == null)
            return;

        Message newMessage;
        if (!message.HasValue)
            newMessage = await message.GetOrDownloadAsync();
        else
            newMessage = message.Value;

        if (newMessage == null)
            return;

        var gUser = await textChannel.Guild.GetUserAsync(client.CurrentUser.Id);
        var botPerms = gUser.GetPermissions(starboardChannel);

        if (!botPerms.Has(ChannelPermission.SendMessages))
            return;

        if (starboard.UseBlacklist)
        {
            if (!starboard.CheckedChannels.IsNullOrWhiteSpace() &&
                starboard.CheckedChannels.Split(" ").Contains(newMessage.Channel.Id.ToString()))
                return;
        }
        else
        {
            if (!starboard.CheckedChannels.IsNullOrWhiteSpace() &&
                !starboard.CheckedChannels.Split(" ").Contains(newMessage.Channel.Id.ToString()))
                return;
        }

        string content;
        string imageurl;

        // Get all emotes for this starboard to show individual counters
        starboardEmotes = ParseEmotes(starboard.Emote);

        if (newMessage.Author.IsBot)
        {
            if (!starboard.AllowBots)
                return;

            content = newMessage.Embeds?.Any() == true
                ? newMessage.Embeds.Select(x => x.Description).FirstOrDefault()
                : newMessage.Content;
            imageurl = newMessage.Attachments?.Length > 0
                ? newMessage.Attachments.First().ProxyUrl
                : newMessage.Embeds?.Select(x => x.Image).FirstOrDefault()?.ProxyUrl;
        }
        else
        {
            content = newMessage.Content;
            imageurl = newMessage.Attachments?.FirstOrDefault()?.ProxyUrl;
        }

        if (content is null && imageurl is null)
            return;

        var reactionEmote = matchingEmote.ToIEmote();
        var matchingCount = await CountStarsAsync(newMessage, reactionEmote, starboard.AllowBots, starboard.Threshold);
        var maybePost = starboardPosts.Find(x => x.MessageId == newMessage.Id && x.StarboardConfigId == starboard.Id);

        // Track stats and individual reactions
        await TrackStarboardStats(newMessage, starboard.Id, matchingEmote, matchingCount, textChannel.Id);
        if (isAdd)
        {
            await TrackStarboardReaction(newMessage.Id, starboard.Id, reaction.UserId, matchingEmote);
        }

        // Calculate total stars across all emotes for this starboard
        var totalStars = 0;
        foreach (var emoteString in starboardEmotes)
        {
            var emote = emoteString.ToIEmote();
            try
            {
                totalStars += emote.Matches(reactionEmote)
                    ? matchingCount
                    : await CountStarsAsync(newMessage, emote, starboard.AllowBots, starboard.Threshold);
            }
            catch
            {
                // Ignore if emote can't be processed
            }
        }

        if (totalStars < starboard.Threshold)
        {
            if (maybePost != null && starboard.RemoveOnBelowThreshold)
            {
                await RemoveStarboardPost(newMessage.Id, starboard.Id);
                try
                {
                    var post = await starboardChannel.GetMessageAsync(maybePost.PostId);
                    if (post != null)
                        await post.DeleteAsync();
                }
                catch
                {
                    // ignored
                }
            }

            return;
        }

        Message? existingPost = null;
        if (maybePost != null)
        {
            if (starboard.RepostThreshold > 0)
            {
                var recent = await starboardChannel.GetMessagesAsync(starboard.RepostThreshold).FlattenAsync();
                existingPost = recent.FirstOrDefault(x => x.Id == maybePost.PostId) as Message;

                if (existingPost == null)
                {
                    var stalePost = await starboardChannel.GetMessageAsync(maybePost.PostId);
                    if (stalePost != null)
                    {
                        try
                        {
                            await stalePost.DeleteAsync();
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }
            }
            else
            {
                existingPost = await starboardChannel.GetMessageAsync(maybePost.PostId) as Message;
            }
        }

        var (stars, embed) = await CreateStarboardPostAsync(newMessage, content, starboardEmotes,
            starboard.AllowBots, textChannel.Guild.Id);

        if (existingPost != null)
        {
            await existingPost.ModifyAsync(x =>
            {
                x.Content = stars;
                x.Embed = embed;
                x.AllowedMentions = AllowedMentions.None;
            });
            return;
        }

        var sent = await starboardChannel.SendAsync(stars, embed: embed,
            allowedMentions: AllowedMentions.None);
        await AddStarboardPost(newMessage.Id, sent.Id, starboard.Id, totalStars);
    }

    private async Task OnMessageDeletedAsync(Cacheable<Message> arg1, Channel arg2)
    {
        if (!arg1.HasValue)
            return;

        var msg = arg1.Value;
        var chan = arg2;
        if (chan is not Channel { IsTextable: true } channel)
            return;

        var permissions = (await channel.Guild.GetUserAsync(client.CurrentUser.Id)).GetPermissions(channel);
        if (!permissions.ManageMessages)
            return;

        var posts = starboardPosts.Where(x => x.MessageId == msg.Id);
        foreach (var post in posts)
        {
            var config = starboardConfigs.FirstOrDefault(x => x.Id == post.StarboardConfigId);
            if (config?.RemoveOnDelete != true)
                continue;

            var starboardChannel = await channel.Guild.GetTextChannelAsync(config.StarboardChannelId);
            if (starboardChannel == null)
                continue;

            try
            {
                var starboardMessage = await starboardChannel.GetMessageAsync(post.PostId);
                if (starboardMessage != null)
                    await starboardMessage.DeleteAsync();
            }
            catch
            {
                // ignored
            }

            await RemoveStarboardPost(msg.Id, config.Id);
        }
    }

    private async Task OnAllReactionsClearedAsync(Cacheable<Message> arg1,
        Channel arg2)
    {
        if (arg2 is not { } channel)
            return;

        Message msg;
        if (!arg1.HasValue)
            msg = await arg1.GetOrDownloadAsync();
        else
            msg = arg1.Value;

        if (msg == null)
            return;

        var posts = starboardPosts.Where(x => x.MessageId == msg.Id);
        foreach (var post in posts)
        {
            var config = starboardConfigs.FirstOrDefault(x => x.Id == post.StarboardConfigId);
            if (config?.RemoveOnReactionsClear != true)
                continue;

            var starboardChannel = await channel.Guild.GetTextChannelAsync(config.StarboardChannelId);
            if (starboardChannel == null)
                continue;

            try
            {
                var starboardMessage = await starboardChannel.GetMessageAsync(post.PostId);
                if (starboardMessage != null)
                    await starboardMessage.DeleteAsync();
            }
            catch
            {
                // ignored
            }

            await RemoveStarboardPost(msg.Id, config.Id);
        }
    }
}

/// <summary>
///     Represents a starboard highlight for dashboard display
/// </summary>
public class StarboardHighlight
{
    /// <summary>
    ///     The original message ID
    /// </summary>
    public ulong MessageId { get; set; }

    /// <summary>
    ///     The channel ID where the message was posted
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     The number of stars this message has
    /// </summary>
    public int StarCount { get; set; }

    /// <summary>
    ///     The text content of the message, or null when it has no text (attachment only, or the original and
    ///     the repost carry nothing readable).
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    ///     The name of the message author
    /// </summary>
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>
    ///     The avatar URL of the message author
    /// </summary>
    public string? AuthorAvatarUrl { get; set; }

    /// <summary>
    ///     The URL of any image attached to the message
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    ///     The star emote used for this starboard
    /// </summary>
    public string StarEmote { get; set; } = "⭐";

    /// <summary>
    ///     When the message was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
///     DTO for starboard statistics
/// </summary>
public class StarboardStatsDto
{
    /// <summary>
    ///     The user who gets starred the most.
    /// </summary>
    public UserStarStats? MostStarredUser { get; set; }

    /// <summary>
    ///     The channel with the most starboard activity.
    /// </summary>
    public ChannelStarStats? MostActiveChannel { get; set; }

    /// <summary>
    ///     The user who gives the most stars.
    /// </summary>
    public StarrerStats? MostActiveStarrer { get; set; }

    /// <summary>
    ///     Total number of starred messages.
    /// </summary>
    public int TotalStarredMessages { get; set; }

    /// <summary>
    ///     Total number of stars given.
    /// </summary>
    public int TotalStars { get; set; }
}

/// <summary>
///     Statistics for a user's starred messages
/// </summary>
public class UserStarStats
{
    /// <summary>
    ///     The user's Discord ID.
    /// </summary>
    public ulong UserId { get; set; }

    /// <summary>
    ///     Total number of stars received.
    /// </summary>
    public int TotalStars { get; set; }

    /// <summary>
    ///     Number of messages that were starred.
    /// </summary>
    public int MessageCount { get; set; }
}

/// <summary>
///     Statistics for a channel's starred messages
/// </summary>
public class ChannelStarStats
{
    /// <summary>
    ///     The channel's Discord ID.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Total number of stars in this channel.
    /// </summary>
    public int TotalStars { get; set; }

    /// <summary>
    ///     Number of starred messages from this channel.
    /// </summary>
    public int MessageCount { get; set; }
}

/// <summary>
///     Statistics for a user who gives stars
/// </summary>
public class StarrerStats
{
    /// <summary>
    ///     The user's Discord ID.
    /// </summary>
    public ulong UserId { get; set; }

    /// <summary>
    ///     Number of stars given by this user.
    /// </summary>
    public int StarsGiven { get; set; }

    /// <summary>
    ///     Number of different emotes used by this user.
    /// </summary>
    public int UniqueEmotesUsed { get; set; }
}