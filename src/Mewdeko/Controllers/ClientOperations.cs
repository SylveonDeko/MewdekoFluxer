using System.Text.Json;
using System.Text.Json.Serialization;
using Mewdeko.Controllers.Common.ClientOperations;
using Mewdeko.Controllers.Common.DashboardAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ChannelType = Mewdeko.Controllers.Common.ClientOperations.ChannelType;

namespace Mewdeko.Controllers;

/// <summary>
///     Api endpoint for operations done via the discord client, such as getting roles, users, guilds
/// </summary>
[ApiController]
[Route("botapi/[controller]")]
[Authorize("ApiKeyPolicy")]
public class ClientOperations(FluxerClient client, DashboardAccessService dashboardAccessService) : Controller
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    /// <summary>
    ///     Returns roles for a guild
    /// </summary>
    /// <param name="guildId">The guildid to check for roles</param>
    /// <returns>A 404 if the guildid doesnt exist in the bot, or a collection of roles</returns>
    [HttpGet("roles/{guildId}")]
    public async Task<IActionResult> GetRoles(ulong guildId)
    {
        await Task.CompletedTask;
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound();


        return Ok(guild.Roles.Values.Select(x => new NeededRoleInfo
        {
            Id = x.Id, Name = x.Name
        }));
    }

    /// <summary>
    ///     Gets category channels from a guild
    /// </summary>
    /// <param name="guildId">The guild id to get category channels from</param>
    /// <returns>Category channels or 404 if the guild is not found</returns>
    [HttpGet("categories/{guildId}")]
    public async Task<IActionResult> GetCategories(ulong guildId)
    {
        await Task.CompletedTask;
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound();

        var categories = guild.CategoryChannels.Select(x => new NeededRoleInfo
        {
            Id = x.Id, Name = x.Name
        });

        return Ok(categories);
    }

    /// <summary>
    ///     Gets channels of a specific type from a guildId
    /// </summary>
    /// <param name="guildId">The guild id to get channels from</param>
    /// <param name="channelType">A <see cref="ChannelType" /> for filtering</param>
    /// <returns>Channels based on the filter or 404 if the guild is not found</returns>
    [HttpGet("channels/{guildId}/{channelType}")]
    public async Task<IActionResult> GetChannels(ulong guildId, ChannelType channelType = ChannelType.None)
    {
        await Task.CompletedTask;
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound();

        var channels = channelType switch
        {
            ChannelType.Text => ToRoleInfo(guild, Fluxer.Net.ChannelType.GuildText),
            ChannelType.Voice => ToRoleInfo(guild, Fluxer.Net.ChannelType.GuildVoice),
            ChannelType.Forum => ToRoleInfo(guild, Fluxer.Net.ChannelType.GuildForum),
            ChannelType.Category => ToRoleInfo(guild, Fluxer.Net.ChannelType.GuildCategory),
            ChannelType.Announcement => ToRoleInfo(guild, Fluxer.Net.ChannelType.GuildNews),
            _ => guild.Channels.Values.Select(c => new NeededRoleInfo
            {
                Id = c.Id, Name = c.Name
            })
        };

        return Ok(channels);
    }

    /// <summary>
    ///     Gets the id and name of every cached channel of one type.
    /// </summary>
    /// <param name="guild">The guild to read channels from</param>
    /// <param name="type">The channel type to keep</param>
    /// <returns>The matching channels</returns>
    private static IEnumerable<NeededRoleInfo> ToRoleInfo(SocketGuild guild, Fluxer.Net.ChannelType type)
    {
        return guild.Channels.Values
            .Where(c => c.Type == type)
            .Select(c => new NeededRoleInfo
            {
                Id = c.Id, Name = c.Name
            });
    }

    /// <summary>
    ///     Gets all IGuildUsers for a guild.
    /// </summary>
    /// <param name="guildId">The guildId to get the users for</param>
    /// <returns>404 if guild not found or the users if found.</returns>
    [HttpGet("users/{guildId}")]
    public async Task<IActionResult> GetUsers(ulong guildId)
    {
        await Task.CompletedTask;
        var guild = client.Guilds.FirstOrDefault(g => g.Id == guildId);
        if (guild == null)
            return NotFound();

        return Ok(JsonSerializer.Serialize(guild.Users.Select(x => new
        {
            UserId = x.Id, x.Username, AvatarUrl = x.GetDisplayAvatarUrl()
        }), Options));
    }

    /// <summary>
    ///     Gets all members in a guild
    /// </summary>
    /// <param name="guildId">The guild id</param>
    /// <returns>The list of members in the guild</returns>
    [HttpGet("members/{guildId}")]
    public IActionResult GetGuildMembers(ulong guildId)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null) return NotFound("Guild not found");

        var members = guild.Users.Select(user => new
        {
            Id = user.Id.ToString(),
            user.Username,
            DisplayName = user.Nickname ?? user.Username,
            AvatarUrl = user.GetDisplayAvatarUrl(),
            user.IsBot
        });

        return Ok(members);
    }


    /// <summary>
    ///     Gets text channels from a guild
    /// </summary>
    /// <param name="guildId">The guild id to get text channels from</param>
    /// <returns>Text channels or 404 if the guild is not found</returns>
    [HttpGet("textchannels/{guildId}")]
    public async Task<IActionResult> GetTextChannels(ulong guildId)
    {
        await Task.CompletedTask;
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound();

        var channels = guild.TextChannels.Select(x => new
        {
            x.Id, x.Name
        });

        return Ok(channels);
    }

    /// <summary>
    ///     Gets every non-thread channel in a guild, typed and with category info, so clients can build a
    ///     channel picker without pulling in threads or having to guess the concrete channel type.
    /// </summary>
    /// <param name="guildId">The guild id to get channels from</param>
    /// <returns>404 if the guild is not found, otherwise the typed channel list</returns>
    [HttpGet("guildchannels/{guildId}")]
    public async Task<IActionResult> GetGuildChannels(ulong guildId)
    {
        await Task.CompletedTask;
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound();

        var channels = guild.Channels.Values
            .Where(x => x.ThreadMetadata == null)
            .Select(x => x.Type switch
            {
                Fluxer.Net.ChannelType.GuildCategory => new GuildChannelLiteInfo
                {
                    Id = x.Id, Name = x.Name, Type = "category", Position = x.Position ?? 0
                },
                Fluxer.Net.ChannelType.GuildVoice => ToLiteInfo(x, "voice"),
                Fluxer.Net.ChannelType.GuildNews => ToLiteInfo(x, "announcement"),
                Fluxer.Net.ChannelType.GuildForum => ToLiteInfo(x, "forum"),
                Fluxer.Net.ChannelType.GuildText => ToLiteInfo(x, "text"),
                _ => null
            })
            .Where(x => x != null)
            .OrderBy(x => x!.CategoryId ?? 0)
            .ThenBy(x => x!.Position);

        return Ok(channels);
    }

    /// <summary>
    ///     Maps a channel to its typed picker entry.
    /// </summary>
    /// <param name="channel">The channel to map</param>
    /// <param name="type">The type label the dashboard expects</param>
    /// <returns>The picker entry</returns>
    private static GuildChannelLiteInfo ToLiteInfo(Channel channel, string type)
    {
        return new GuildChannelLiteInfo
        {
            Id = channel.Id,
            Name = channel.Name,
            Type = type,
            CategoryId = channel.CategoryId,
            CategoryName = channel.Category?.Name,
            Position = channel.Position ?? 0
        };
    }

    /// <summary>
    ///     Gets a single user from a guild.
    /// </summary>
    /// <param name="guildId">The guildId to get the users for</param>
    /// <param name="userId">The user id of the user to get.</param>
    /// <returns>404 if guild not found or the users if found.</returns>
    [HttpGet("user/{guildId}/{userId}")]
    public async Task<IActionResult> GetUser(ulong guildId, ulong userId)
    {
        await Task.CompletedTask;
        var guild = client.Guilds.FirstOrDefault(x => x.Id == guildId);
        if (guild == null)
            return NotFound();

        var user = guild.Users.FirstOrDefault(x => x.Id == userId);
        if (user == null)
            return NotFound();
        var partial = new
        {
            UserId = user.Id, user.Username, AvatarUrl = user.GetDisplayAvatarUrl()
        };
        return Ok(partial);
    }

    /// <summary>
    ///     Gets a list of guilds the bot and user have mutual
    /// </summary>
    /// <param name="userId">The user ID to check mutual guilds for</param>
    /// <param name="adminOnly">Whether to only return guilds where user has admin permissions (default: true)</param>
    /// <returns></returns>
    [HttpGet("mutualguilds/{userId}")]
    public async Task<IActionResult> GetMutualGuilds(ulong userId, [FromQuery] bool adminOnly = true)
    {
        var mutuals = new List<object>();
        foreach (var guild in client.Guilds)
        {
            var guildUser = guild.GetUser(userId);
            if (guildUser == null)
                continue;

            var hasAdminAccess = guild.OwnerId == userId ||
                                 guildUser.GuildPermissions.Has(GuildPermission.Administrator);
            var hasRestrictedAccess = dashboardAccessService.HasAnyCachedAccess(
                guild.Id, userId, guildUser.Roles.Select(role => role.Id).ToList());

            if (adminOnly && !hasAdminAccess && !hasRestrictedAccess)
                continue;

            mutuals.Add(new
            {
                id = guild.Id,
                name = guild.Name,
                icon = guild.IconHash,
                owner = guild.OwnerId == userId,
                permissions = (int)guildUser.GuildPermissions.RawValue,
                features = guild.Features.Raw,
                banner = guild.BannerUrl ?? guild.GetInviteSplashUrl(),
                hasAdminAccess,
                hasRestrictedAccess
            });
        }

        return Ok(mutuals);
    }

    /// <summary>
    ///     Checks if this bot instance has the specified guild
    /// </summary>
    /// <param name="guildId">The guild ID to check</param>
    /// <returns>Whether this instance has the guild with basic info</returns>
    [HttpGet("hasguild/{guildId}")]
    [SkipDashboardAccess]
    public async Task<IActionResult> HasGuild(ulong guildId)
    {
        await Task.CompletedTask;
        var guild = client.GetGuild(guildId);

        return Ok(new
        {
            hasGuild = guild != null,
            guildName = guild?.Name,
            memberCount = guild?.TotalMembers,
            iconUrl = guild?.IconUrl,
            createdAt = guild?.CreatedAt,
            description = (string?)null,
            features = guild?.Features.Raw
        });
    }

    /// <summary>
    ///     Gets the guilds the bot is in
    /// </summary>
    /// <returns>A list of guildIds  the bot is in</returns>
    [HttpGet("guilds")]
    public async Task<IActionResult> GetGuilds()
    {
        await Task.CompletedTask;
        return Ok(JsonSerializer.Serialize(client.Guilds.Select(x => x.Id), Options));
    }

    /// <summary>
    ///     Gets forum channels with detailed information including tags and threads
    /// </summary>
    /// <param name="guildId">The guild ID to get forum channels from</param>
    /// <returns>Detailed forum channel information or 404 if guild not found</returns>
    [HttpGet("forumchannels/{guildId}")]
    public async Task<IActionResult> GetForumChannels(ulong guildId)
    {
        await Task.CompletedTask;
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound();

        var forumChannels = new List<ForumChannelInfo>();

        foreach (var forum in guild.Channels.Values.Where(c => c.Type == Fluxer.Net.ChannelType.GuildForum))
        {
            try
            {
                var activeThreads = ForumThreads(guild, forum, false);

                var forumInfo = new ForumChannelInfo
                {
                    Id = forum.Id,
                    Name = forum.Name,
                    Topic = forum.Topic,
                    RequiresTags = (forum.ForumTags ?? []).Any(t => t.IsModerated),
                    MaxActiveThreads = null,
                    DefaultAutoArchiveDuration = (int?)forum.DefaultAutoArchiveDuration,
                    Tags = ToTagInfos(forum),
                    ActiveThreads = activeThreads.Select(ToThreadInfo).ToList(),
                    TotalThreadCount = activeThreads.Count
                };

                forumChannels.Add(forumInfo);
            }
            catch (Exception)
            {
                // Continue with basic info if thread fetching fails
                forumChannels.Add(new ForumChannelInfo
                {
                    Id = forum.Id,
                    Name = forum.Name,
                    Topic = forum.Topic,
                    Tags = ToTagInfos(forum)
                });
            }
        }

        return Ok(forumChannels);
    }

    /// <summary>
    ///     Gets detailed information about a specific forum channel
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="forumId">The forum channel ID</param>
    /// <returns>Detailed forum information or 404 if not found</returns>
    [HttpGet("forumchannel/{guildId}/{forumId}")]
    public async Task<IActionResult> GetForumChannel(ulong guildId, ulong forumId)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        var forum = guild.GetChannel(forumId);
        if (forum == null)
            return NotFound("Forum channel not found");

        try
        {
            var activeThreads = ForumThreads(guild, forum, false);

            var forumInfo = new ForumChannelInfo
            {
                Id = forum.Id,
                Name = forum.Name,
                Topic = forum.Topic,
                RequiresTags = (forum.ForumTags ?? []).Any(t => t.IsModerated),
                MaxActiveThreads = null,
                DefaultAutoArchiveDuration = (int?)forum.DefaultAutoArchiveDuration,
                Tags = ToTagInfos(forum),
                ActiveThreads = activeThreads.Select(ToThreadInfo).ToList(),
                TotalThreadCount = activeThreads.Count
            };

            return Ok(forumInfo);
        }
        catch (Exception)
        {
            var basicInfo = new ForumChannelInfo
            {
                Id = forum.Id,
                Name = forum.Name,
                Topic = forum.Topic,
                Tags = ToTagInfos(forum)
            };

            return Ok(basicInfo);
        }
    }

    /// <summary>
    ///     Gets threads for a specific forum channel
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="forumId">The forum channel ID</param>
    /// <param name="includeArchived">Whether to include archived threads</param>
    /// <returns>List of threads or 404 if not found</returns>
    [HttpGet("forumthreads/{guildId}/{forumId}")]
    public async Task<IActionResult> GetForumThreads(ulong guildId, ulong forumId,
        [FromQuery] bool includeArchived = false)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        var forum = guild.GetChannel(forumId);
        if (forum == null)
            return NotFound("Forum channel not found");

        try
        {
            var activeThreads = ForumThreads(guild, forum, false);
            var threads = activeThreads.Select(ToThreadInfo);

            if (includeArchived)
            {
                var archivedThreads = ForumThreads(guild, forum, true);
                var allThreads = threads.Concat(archivedThreads.Select(ToThreadInfo));

                return Ok(allThreads);
            }

            return Ok(threads);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to retrieve forum threads: {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets emojis from mutual guilds for the emoji picker
    /// </summary>
    /// <param name="userId">The user ID to get mutual guild emojis for</param>
    /// <param name="adminOnly">Whether to only include guilds where user has admin permissions (default: true)</param>
    /// <returns>List of guild emojis grouped by guild</returns>
    [HttpGet("emojis/{userId}")]
    public async Task<IActionResult> GetMutualGuildEmojis(ulong userId, [FromQuery] bool adminOnly = false)
    {
        await Task.CompletedTask;
        var guilds = client.Guilds;
        var mutualGuilds = guilds
            .Where(x => x.Users.Any(y => y.Id == userId &&
                                         (!adminOnly || y.GuildPermissions.Has(GuildPermission.Administrator))))
            .ToList();

        if (mutualGuilds.Count == 0)
            return NotFound("No mutual guilds found");

        var result = new List<GuildEmojiInfo>();

        foreach (var guild in mutualGuilds)
        {
            var emojis = guild.Emotes
                .Select(e => new EmojiInfo
                {
                    Id = e.Id,
                    Name = e.Name,
                    Animated = e.IsAnimated,
                    IsAvailable = true,
                    RoleIds = [],
                    RequireColons = true,
                    Url = e.GetEmojiUrl() ?? string.Empty
                })
                .ToList();

            // Only include guilds that have emojis
            if (emojis.Count > 0)
            {
                result.Add(new GuildEmojiInfo
                {
                    Guild = new GuildInfo
                    {
                        Id = guild.Id, Name = guild.Name, IconUrl = guild.IconUrl
                    },
                    Emojis = emojis
                });
            }
        }

        return Ok(result);
    }

    /// <summary>
    ///     Gets the cached threads of a forum channel.
    /// </summary>
    /// <param name="guild">The guild that holds the forum</param>
    /// <param name="forum">The forum channel</param>
    /// <param name="archived">Whether to return archived threads instead of active ones</param>
    /// <returns>The matching threads</returns>
    private static List<Channel> ForumThreads(SocketGuild guild, Channel forum, bool archived)
    {
        return guild.ThreadChannels
            .Where(thread => thread.ParentId == forum.Id && (thread.ThreadMetadata?.IsArchived ?? false) == archived)
            .ToList();
    }

    /// <summary>
    ///     Maps a forum's tags to their response model.
    /// </summary>
    /// <param name="forum">The forum channel</param>
    /// <returns>The forum's tags</returns>
    private static List<ForumTagInfo> ToTagInfos(Channel forum)
    {
        return (forum.ForumTags ?? []).Select(tag => new ForumTagInfo
        {
            Id = tag.Id, Name = tag.Name, Emoji = tag.EmojiName, IsModerated = tag.IsModerated
        }).ToList();
    }

    /// <summary>
    ///     Maps a thread to its response model.
    /// </summary>
    /// <param name="thread">The thread channel</param>
    /// <returns>The thread information</returns>
    private static ThreadInfo ToThreadInfo(Channel thread)
    {
        return new ThreadInfo
        {
            Id = thread.Id,
            Name = thread.Name,
            AppliedTags = thread.AppliedTags?.ToList() ?? new List<ulong>(),
            CreatorId = thread.OwnerId ?? 0,
            CreatedAt = thread.ThreadMetadata?.CreatedAt.UtcDateTime ?? thread.CreatedAt.UtcDateTime,
            MessageCount = 0,
            IsArchived = thread.ThreadMetadata?.IsArchived ?? false,
            IsLocked = thread.ThreadMetadata?.IsLocked ?? false
        };
    }
}