using System.Text;
using System.Text.Json;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Controllers.Common.DashboardAccess;
using Mewdeko.Controllers.Common.XpBackgrounds;
using Mewdeko.Controllers.Common.XpEvents;
using Mewdeko.Modules.Xp.Models;
using Mewdeko.Modules.Xp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Mewdeko.Controllers;

/// <summary>
///     Controller for managing XP-related functionality for the bot
/// </summary>
[ApiController]
[Route("botapi/[controller]/{guildId}")]
[Authorize("ApiKeyPolicy")]
public class XpController(
    XpService xp,
    FluxerClient client,
    IDataConnectionFactory dbFactory,
    ILogger<XpController> logger,
    IServiceProvider serviceProvider,
    XpRewardManager xpRewardManager,
    XpBackgroundService backgroundService,
    XpRoleSyncService roleSyncService,
    IDashboardAuditContext auditContext) : Controller
{
    /// <summary>
    ///     Exports every member's XP as a JSON file
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The export as a JSON file</returns>
    [HttpGet("export")]
    public async Task<IActionResult> ExportXp(ulong guildId)
    {
        var settings = await xp.GetGuildXpSettingsAsync(guildId);
        var curve = (XpCurveType)settings.XpCurveType;
        await using var db = await dbFactory.CreateConnectionAsync();
        var rows = await db.GuildUserXps.Where(x => x.GuildId == guildId && (x.TotalXp > 0 || x.BonusXp > 0))
            .OrderByDescending(x => x.TotalXp)
            .ToListAsync();

        var json = JsonSerializer.Serialize(new
        {
            format = "mewdeko-xp",
            guildId = guildId.ToString(),
            curve = curve.ToString(),
            exportedAt = DateTime.UtcNow,
            members = rows.Select(x => new
            {
                userId = x.UserId.ToString(),
                xp = x.TotalXp,
                level = XpCalculator.CalculateLevel(x.TotalXp, curve),
                bonusXp = x.BonusXp
            })
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        return File(Encoding.UTF8.GetBytes(json), "application/json", $"xp-{guildId}.json");
    }

    /// <summary>
    ///     Gets the rank card background the server uses, as a data URI the dashboard can show
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The stored value and its image, or a 404 when there is none</returns>
    [HttpGet("background")]
    public async Task<IActionResult> GetBackground(ulong guildId)
    {
        var settings = await xp.GetGuildXpSettingsAsync(guildId);
        if (string.IsNullOrWhiteSpace(settings.CustomXpImageUrl))
            return NotFound("No background set");

        var bytes = await backgroundService.LoadAsync(settings.CustomXpImageUrl);
        return bytes is null
            ? NotFound("Background could not be loaded")
            : Ok(new
            {
                Value = settings.CustomXpImageUrl, Image = $"data:image/png;base64,{Convert.ToBase64String(bytes)}"
            });
    }

    /// <summary>
    ///     Sets the rank card background used for the whole server
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="request">The image URL</param>
    /// <returns>An empty result, or the reason the image was refused</returns>
    [HttpPost("background")]
    public async Task<IActionResult> SetBackground(ulong guildId, [FromBody] XpBackgroundRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Url))
            return BadRequest("Url is required");

        auditContext.RecordBefore(await xp.GetGuildXpSettingsAsync(guildId));
        var result = await backgroundService.SetGuildBackgroundAsync(guildId, request.Url.Trim());
        if (result != XpBackgroundResult.Saved)
            return BadRequest(result.ToString());

        auditContext.RecordAfter(await xp.GetGuildXpSettingsAsync(guildId));
        return Ok();
    }

    /// <summary>
    ///     Removes the server rank card background
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>An empty result, or a 404 when there was none</returns>
    [HttpDelete("background")]
    public async Task<IActionResult> ClearBackground(ulong guildId)
    {
        auditContext.RecordBefore(await xp.GetGuildXpSettingsAsync(guildId));
        return await backgroundService.ClearGuildBackgroundAsync(guildId) ? Ok() : NotFound("No background set");
    }

    /// <summary>
    ///     Resets the XP of every member in a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="request">Whether bonus XP is wiped too</param>
    /// <returns>How many members were reset</returns>
    [HttpPost("reset")]
    public async Task<IActionResult> ResetGuildXp(ulong guildId, [FromBody] XpResetRequest? request)
    {
        var affected = await xp.ResetGuildXp(guildId, request?.ResetBonusXp ?? false);
        return Ok(affected);
    }

    /// <summary>
    ///     Starts a background sync of every member's level roles
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>A 200 status code once the sync has started</returns>
    [HttpPost("sync-roles")]
    public IActionResult SyncRoles(ulong guildId)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        _ = Task.Run(async () =>
        {
            try
            {
                await roleSyncService.SyncAllUsersAsync(guild);
            }
            catch (InvalidOperationException)
            {
                logger.LogInformation("Role sync already running for guild {GuildId}", guildId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error syncing XP roles for guild {GuildId}", guildId);
            }
        });

        return Ok();
    }

    /// <summary>
    ///     Gets the last 25 level-ups in a guild, newest first
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The level-up log</returns>
    [HttpGet("level-up-log")]
    public async Task<IActionResult> GetLevelUpLog(ulong guildId)
    {
        var entries = await xp.GetLevelUpLogAsync(guildId);
        var guild = client.GetGuild(guildId);

        return Ok(entries.Select(x => new
        {
            x.UserId,
            Username = guild?.GetUser(x.UserId)?.Username ?? x.UserId.ToString(),
            x.OldLevel,
            x.NewLevel,
            x.DateAdded
        }));
    }

    /// <summary>
    ///     Gets the custom level-up messages of a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The level-up messages</returns>
    [HttpGet("level-up-messages")]
    public async Task<IActionResult> GetLevelUpMessages(ulong guildId)
    {
        var messages = await xp.GetLevelUpMessagesAsync(guildId);
        return Ok(messages.Select(x => new
        {
            x.Id, x.MessageContent, x.IsEnabled
        }));
    }

    /// <summary>
    ///     Adds a custom level-up message
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="request">The message template</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("level-up-messages")]
    public async Task<IActionResult> AddLevelUpMessage(ulong guildId, [FromBody] XpLevelUpMessageRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("Message is required");

        await xp.AddLevelUpMessageAsync(guildId, request.Message.Trim());
        return Ok();
    }

    /// <summary>
    ///     Turns a custom level-up message on or off
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="id">The message ID</param>
    /// <param name="enabled">Whether the message is on</param>
    /// <returns>A 200 status code, or a 404 when the message does not exist</returns>
    [HttpPost("level-up-messages/{id:int}/toggle")]
    public async Task<IActionResult> ToggleLevelUpMessage(ulong guildId, int id, [FromBody] bool enabled)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var updated = await db.XpLevelUpMessages
            .Where(x => x.GuildId == guildId && x.Id == id)
            .Set(x => x.IsEnabled, enabled)
            .UpdateAsync();

        return updated > 0 ? Ok() : NotFound("Message not found");
    }

    /// <summary>
    ///     Removes a custom level-up message
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="id">The message ID</param>
    /// <returns>A 200 status code, or a 404 when the message does not exist</returns>
    [HttpDelete("level-up-messages/{id:int}")]
    public async Task<IActionResult> RemoveLevelUpMessage(ulong guildId, int id)
    {
        return await xp.RemoveLevelUpMessageAsync(guildId, id) ? Ok() : NotFound("Message not found");
    }

    /// <summary>
    ///     Gets the channel and role XP multipliers of a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The channel and role multipliers</returns>
    [HttpGet("multipliers")]
    public async Task<IActionResult> GetMultipliers(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var channels = await db.XpChannelMultipliers.Where(x => x.GuildId == guildId).ToListAsync();
        var roles = await db.XpRoleMultipliers.Where(x => x.GuildId == guildId).ToListAsync();
        var guild = client.GetGuild(guildId);

        return Ok(new
        {
            Channels = channels.Select(x => new
            {
                ChannelId = x.ChannelId.ToString(), Name = guild?.GetChannel(x.ChannelId)?.Name, x.Multiplier
            }),
            Roles = roles.Select(x => new
            {
                RoleId = x.RoleId.ToString(), Name = guild?.GetRole(x.RoleId)?.Name, x.Multiplier
            })
        });
    }

    /// <summary>
    ///     Sets the XP multiplier of a channel
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="request">The channel and multiplier</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("multipliers/channels")]
    public async Task<IActionResult> SetChannelMultiplier(ulong guildId, [FromBody] XpMultiplierRequest? request)
    {
        if (request == null || request.Id == 0 || request.Multiplier <= 0)
            return BadRequest("A channel and a positive multiplier are required");

        await xp.SetChannelMultiplierAsync(guildId, request.Id, request.Multiplier);
        return Ok();
    }

    /// <summary>
    ///     Removes the XP multiplier of a channel
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="id">The channel ID</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpDelete("multipliers/channels/{id}")]
    public async Task<IActionResult> RemoveChannelMultiplier(ulong guildId, ulong id)
    {
        await xp.SetChannelMultiplierAsync(guildId, id, 1.0);
        return Ok();
    }

    /// <summary>
    ///     Sets the XP multiplier of a role
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="request">The role and multiplier</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("multipliers/roles")]
    public async Task<IActionResult> SetRoleMultiplier(ulong guildId, [FromBody] XpMultiplierRequest? request)
    {
        if (request == null || request.Id == 0 || request.Multiplier <= 0)
            return BadRequest("A role and a positive multiplier are required");

        await xp.SetRoleMultiplierAsync(guildId, request.Id, request.Multiplier);
        return Ok();
    }

    /// <summary>
    ///     Removes the XP multiplier of a role
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="id">The role ID</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpDelete("multipliers/roles/{id}")]
    public async Task<IActionResult> RemoveRoleMultiplier(ulong guildId, ulong id)
    {
        await xp.SetRoleMultiplierAsync(guildId, id, 1.0);
        return Ok();
    }

    /// <summary>
    ///     Gets the XP boost events of a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The boost events, past and upcoming included</returns>
    [HttpGet("boosts")]
    public async Task<IActionResult> GetBoosts(ulong guildId)
    {
        var boosts = await xp.GetAllBoostEventsAsync(guildId);
        var now = DateTime.UtcNow;

        return Ok(boosts.Select(x => new
        {
            x.Id,
            x.Name,
            x.Multiplier,
            x.StartTime,
            x.EndTime,
            Active = x.StartTime <= now && x.EndTime >= now,
            ChannelIds = SplitIds(x.ApplicableChannels),
            RoleIds = SplitIds(x.ApplicableRoles)
        }));
    }

    /// <summary>
    ///     Starts a timed XP boost
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="request">The boost to start</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("boosts")]
    public async Task<IActionResult> CreateBoost(ulong guildId, [FromBody] XpBoostRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required");
        if (request.Multiplier <= 1.0)
            return BadRequest("Multiplier must be above 1");
        if (request.DurationMinutes < 1)
            return BadRequest("Duration must be at least a minute");

        var start = DateTime.UtcNow;
        await xp.CreateXpBoostEventAsync(guildId, request.Name.Trim(), request.Multiplier, start,
            start.AddMinutes(request.DurationMinutes), request.ChannelIds, request.RoleIds);
        return Ok();
    }

    /// <summary>
    ///     Cancels an XP boost event
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="id">The boost ID</param>
    /// <returns>A 200 status code, or a 404 when the boost does not exist</returns>
    [HttpDelete("boosts/{id:int}")]
    public async Task<IActionResult> CancelBoost(ulong guildId, int id)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var owned = await db.XpBoostEvents.AnyAsync(x => x.GuildId == guildId && x.Id == id);
        if (!owned)
            return NotFound("Boost not found");

        await xp.CancelXpBoostEventAsync(id);
        return Ok();
    }

    /// <summary>
    ///     Gets the XP competitions of a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The competitions with their rewards, newest first</returns>
    [HttpGet("competitions")]
    public async Task<IActionResult> GetCompetitions(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var competitions = await db.XpCompetitions.Where(x => x.GuildId == guildId)
            .OrderByDescending(x => x.Id)
            .ToListAsync();
        var ids = competitions.Select(x => x.Id).ToList();
        var rewards = await db.XpCompetitionRewards.Where(x => ids.Contains(x.CompetitionId)).ToListAsync();
        var participants = await db.XpCompetitionEntries.Where(x => ids.Contains(x.CompetitionId))
            .GroupBy(x => x.CompetitionId)
            .Select(g => new
            {
                Id = g.Key, Count = g.Count()
            })
            .ToListAsync();
        var guild = client.GetGuild(guildId);
        var now = DateTime.UtcNow;

        return Ok(competitions.Select(c => new
        {
            c.Id,
            c.Name,
            c.Type,
            c.StartTime,
            c.EndTime,
            c.TargetLevel,
            AnnouncementChannelId = c.AnnouncementChannelId?.ToString(),
            Active = c.StartTime <= now && c.EndTime > now,
            Participants = participants.FirstOrDefault(p => p.Id == c.Id)?.Count ?? 0,
            Rewards = rewards.Where(r => r.CompetitionId == c.Id).OrderBy(r => r.Position).Select(r => new
            {
                r.Id,
                r.Position,
                RoleId = r.RoleId.ToString(),
                RoleName = r.RoleId == 0 ? null : guild?.GetRole(r.RoleId)?.Name,
                r.XpAmount,
                r.CurrencyAmount
            })
        }));
    }

    /// <summary>
    ///     Starts an XP competition
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="request">The competition to start</param>
    /// <returns>The ID of the new competition</returns>
    [HttpPost("competitions")]
    public async Task<IActionResult> CreateCompetition(ulong guildId, [FromBody] XpCompetitionRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required");
        if (!Enum.IsDefined(typeof(XpCompetitionType), request.Type))
            return BadRequest("Unknown competition type");
        if (request.DurationMinutes < 60)
            return BadRequest("A competition has to run for at least an hour");

        var type = (XpCompetitionType)request.Type;
        if (type == XpCompetitionType.ReachLevel && request.TargetLevel <= 0)
            return BadRequest("A target level is required");

        var start = DateTime.UtcNow;
        var competition = await xp.CreateCompetitionAsync(guildId, request.Name.Trim(), type, start,
            start.AddMinutes(request.DurationMinutes), request.TargetLevel,
            request.AnnouncementChannelId == 0 ? null : request.AnnouncementChannelId);

        foreach (var reward in request.Rewards)
            await xp.AddCompetitionRewardAsync(competition.Id, reward.Position, reward.RoleId, reward.XpAmount,
                reward.CurrencyAmount);

        await xp.StartCompetitionAsync(competition.Id);
        return Ok(competition.Id);
    }

    /// <summary>
    ///     Gets the standings of an XP competition
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="id">The competition ID</param>
    /// <returns>The standings, best first</returns>
    [HttpGet("competitions/{id:int}/standings")]
    public async Task<IActionResult> GetCompetitionStandings(ulong guildId, int id)
    {
        var competition = await xp.GetCompetitionAsync(id);
        if (competition == null || competition.GuildId != guildId)
            return NotFound("Competition not found");

        var entries = await xp.GetCompetitionEntriesAsync(id);
        var type = (XpCompetitionType)competition.Type;
        var ordered = type switch
        {
            XpCompetitionType.MostGained => entries.OrderByDescending(e => e.CurrentXp - e.StartingXp),
            XpCompetitionType.ReachLevel => entries.OrderBy(e => e.AchievedTargetAt == null)
                .ThenBy(e => e.AchievedTargetAt)
                .ThenByDescending(e => e.CurrentXp),
            _ => entries.OrderByDescending(e => e.CurrentXp)
        };

        var guild = client.GetGuild(guildId);
        var position = 0;
        return Ok(ordered.Take(100).Select(e => new
        {
            Position = ++position,
            UserId = e.UserId.ToString(),
            Username = guild?.GetUser(e.UserId)?.Username ?? e.UserId.ToString(),
            Gained = e.CurrentXp - e.StartingXp,
            Total = e.CurrentXp,
            e.AchievedTargetAt,
            e.FinalPlacement
        }).ToList());
    }

    /// <summary>
    ///     Ends an XP competition now and hands out its rewards
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="id">The competition ID</param>
    /// <returns>A 200 status code, or the reason it could not end</returns>
    [HttpPost("competitions/{id:int}/end")]
    public async Task<IActionResult> EndCompetition(ulong guildId, int id)
    {
        var competition = await xp.GetCompetitionAsync(id);
        if (competition == null || competition.GuildId != guildId)
            return NotFound("Competition not found");
        if (competition.EndTime <= DateTime.UtcNow)
            return BadRequest("Competition already ended");

        await xp.FinalizeCompetitionAsync(id);

        await using var db = await dbFactory.CreateConnectionAsync();
        await db.XpCompetitions.Where(x => x.Id == id).Set(x => x.EndTime, DateTime.UtcNow).UpdateAsync();
        return Ok();
    }

    /// <summary>
    ///     Gets the users excluded from XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The excluded users</returns>
    [HttpGet("excluded/users")]
    public async Task<IActionResult> GetExcludedUsers(ulong guildId)
    {
        var users = await xp.GetExcludedItemsAsync(guildId, ExcludedItemType.User);
        var guild = client.GetGuild(guildId);
        return Ok(users.Select(x => new
        {
            UserId = x, Username = guild?.GetUser(x)?.Username
        }));
    }

    /// <summary>
    ///     Excludes a user from XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="userId">The user ID to exclude</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("excluded/users")]
    public async Task<IActionResult> ExcludeUser(ulong guildId, [FromBody] ulong userId)
    {
        await xp.ExcludeItemAsync(guildId, userId, ExcludedItemType.User);
        return Ok();
    }

    /// <summary>
    ///     Includes a previously excluded user for XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="userId">The user ID to include</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpDelete("excluded/users/{userId}")]
    public async Task<IActionResult> IncludeUser(ulong guildId, ulong userId)
    {
        await xp.IncludeItemAsync(guildId, userId, ExcludedItemType.User);
        return Ok();
    }

    /// <summary>
    ///     Gets the XP settings for a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The XP settings for the guild</returns>
    [HttpGet("settings")]
    public async Task<IActionResult> GetXpSettings(ulong guildId)
    {
        var settings = await xp.GetGuildXpSettingsAsync(guildId);
        return Ok(settings);
    }

    /// <summary>
    ///     Updates XP settings for a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="settings">XP settings to update</param>
    /// <returns>The updated XP settings</returns>
    [HttpPost("settings")]
    public async Task<IActionResult> UpdateXpSettings(ulong guildId, [FromBody] GuildXpSetting? settings)
    {
        if (settings == null)
            return BadRequest(BodyError());
        if (settings.GuildId != guildId)
            return BadRequest("Guild ID mismatch");

        var currentSettings = await xp.GetGuildXpSettingsAsync(guildId);
        var curveTypeChanged = currentSettings.XpCurveType != settings.XpCurveType;
        auditContext.RecordBefore(currentSettings);

        var updatedSettings = await xp.UpdateGuildXpSettingsAsync(guildId, s =>
        {
            if (settings.XpPerMessage > 0) s.XpPerMessage = settings.XpPerMessage;
            if (settings.MessageXpCooldown > 0) s.MessageXpCooldown = settings.MessageXpCooldown;
            if (settings.VoiceXpPerMinute > 0) s.VoiceXpPerMinute = settings.VoiceXpPerMinute;
            if (settings.VoiceXpTimeout > 0) s.VoiceXpTimeout = settings.VoiceXpTimeout;
            if (settings.XpMultiplier > 0) s.XpMultiplier = settings.XpMultiplier;
            s.XpCurveType = settings.XpCurveType;
            s.CustomXpImageUrl = settings.CustomXpImageUrl;
            s.XpGainDisabled = settings.XpGainDisabled;
            s.LevelUpInterval = Math.Max(1, settings.LevelUpInterval);
            s.LevelUpMinLevel = Math.Max(0, settings.LevelUpMinLevel);
            s.LevelUpRewardsOnly = settings.LevelUpRewardsOnly;
        });

        if (curveTypeChanged)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await xp.RecomputeAllLevelsAsync(guildId, (XpCurveType)settings.XpCurveType);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error recomputing levels for guild {GuildId}", guildId);
                }
            });
        }

        auditContext.RecordAfter(updatedSettings);
        return Ok(updatedSettings);
    }

    /// <summary>
    ///     Gets XP stats for a user in a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="userId">The user ID</param>
    /// <returns>The user's XP stats</returns>
    [HttpGet("user/{userId}")]
    public async Task<IActionResult> GetUserStats(ulong guildId, ulong userId)
    {
        var stats = await xp.GetUserXpStatsAsync(guildId, userId);
        if (stats == null)
            return NotFound();

        var guild = client.GetGuild(guildId);
        var user = guild?.GetUser(userId);

        var result = new
        {
            stats.UserId,
            stats.GuildId,
            stats.TotalXp,
            stats.Level,
            stats.LevelXp,
            stats.RequiredXp,
            stats.Rank,
            stats.BonusXp,
            Username = user?.Username ?? "Unknown",
            AvatarUrl = user?.GetAvatarUrl() ?? user?.GetDefaultAvatarUrl(),
            TimeOnLevel = await xp.GetTimeOnCurrentLevelAsync(guildId, userId)
        };

        return Ok(result);
    }

    /// <summary>
    ///     Adds XP to a user in a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="userId">The user ID</param>
    /// <param name="amount">Amount of XP to add</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("user/{userId}/add")]
    public async Task<IActionResult> AddUserXp(ulong guildId, ulong userId, [FromBody] int amount)
    {
        if (amount <= 0)
            return BadRequest("Amount must be positive");

        auditContext.RecordBefore(await xp.GetUserXpStatsAsync(guildId, userId));
        await xp.AddXpAsync(guildId, userId, amount);
        auditContext.RecordAfter(await xp.GetUserXpStatsAsync(guildId, userId));
        return Ok();
    }

    /// <summary>
    ///     Resets a user's XP in a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="userId">The user ID</param>
    /// <param name="resetBonusXp">Whether to reset bonus XP as well</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("user/{userId}/reset")]
    public async Task<IActionResult> ResetUserXp(ulong guildId, ulong userId, [FromBody] bool resetBonusXp = false)
    {
        auditContext.RecordBefore(await xp.GetUserXpStatsAsync(guildId, userId));
        await xp.ResetUserXpAsync(guildId, userId, resetBonusXp);
        return Ok();
    }

    /// <summary>
    ///     Sets a user's XP to a specific amount
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="userId">The user ID</param>
    /// <param name="amount">Amount of XP to set</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("user/{userId}/set")]
    public async Task<IActionResult> SetUserXp(ulong guildId, ulong userId, [FromBody] long amount)
    {
        if (amount < 0)
            return BadRequest("Amount cannot be negative");

        auditContext.RecordBefore(await xp.GetUserXpStatsAsync(guildId, userId));
        await xp.SetUserXpAsync(guildId, userId, amount);
        auditContext.RecordAfter(await xp.GetUserXpStatsAsync(guildId, userId));
        return Ok();
    }

    /// <summary>
    ///     Gets the XP leaderboard for a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="page">Page number (1-indexed)</param>
    /// <param name="pageSize">Number of entries per page</param>
    /// <returns>A page of the XP leaderboard</returns>
    [HttpGet("leaderboard")]
    [SkipDashboardAccess]
    public async Task<IActionResult> GetLeaderboard(ulong guildId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        if (page < 1)
            return BadRequest("Page must be at least 1");

        if (pageSize is < 1 or > 100)
            return BadRequest("Page size must be between 1 and 100");

        var leaderboard = await xp.GetLeaderboardAsync(guildId, page, pageSize);

        // Create a new collection with the enriched data
        var enrichedLeaderboard = new List<object>();
        var guild = client.GetGuild(guildId);

        foreach (var entry in leaderboard.Users)
        {
            var username = "Unknown";
            string avatarUrl = null;

            if (guild != null)
            {
                var user = guild.GetUser(entry.UserId);
                if (user != null)
                {
                    username = user.Username;
                    avatarUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
                }
            }

            enrichedLeaderboard.Add(new
            {
                entry.UserId,
                entry.GuildId,
                entry.TotalXp,
                entry.Level,
                entry.LevelXp,
                entry.RequiredXp,
                entry.Rank,
                entry.BonusXp,
                Username = username,
                AvatarUrl = avatarUrl
            });
        }

        return Ok(enrichedLeaderboard);
    }

    /// <summary>
    ///     Gets role rewards for a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>List of role rewards</returns>
    [HttpGet("rewards/roles")]
    public async Task<IActionResult> GetRoleRewards(ulong guildId)
    {
        var rewards = await xp.GetRoleRewardsAsync(guildId);

        var enrichedRewards = new List<object>();
        var guild = client.GetGuild(guildId);

        foreach (var reward in rewards)
        {
            string roleName = null;

            if (guild != null)
            {
                var role = guild.GetRole(reward.RoleId);
                if (role != null)
                {
                    roleName = role.Name;
                }
            }

            enrichedRewards.Add(new
            {
                reward.Id,
                reward.GuildId,
                reward.Level,
                reward.RoleId,
                RoleName = roleName ?? $"Role ID: {reward.RoleId}"
            });
        }

        return Ok(enrichedRewards);
    }

    /// <summary>
    ///     Adds a role reward for a specific level
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="reward">A <see cref="XpRoleReward" /> object.</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("rewards/roles")]
    public async Task<IActionResult> AddRoleReward(ulong guildId, [FromBody] XpRoleReward? reward)
    {
        if (reward == null)
            return BadRequest(BodyError());
        if (reward.Level < 1)
            return BadRequest("Level must be at least a positive integer");

        auditContext.RecordBefore(await xp.GetRoleRewardsAsync(guildId));
        await xpRewardManager.SetRoleRewardAsync(guildId, reward.Level, reward.RoleId);
        auditContext.RecordAfter(await xp.GetRoleRewardsAsync(guildId));
        return Ok();
    }

    /// <summary>
    ///     Removes a role reward
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="level">The level of the role reward to remove</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpDelete("rewards/roles/{level}")]
    public async Task<IActionResult> RemoveRoleReward(ulong guildId, int level)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        // Check if the reward exists
        var exists = await db.XpRoleRewards
            .AnyAsync(r => r.Level == level && r.GuildId == guildId);

        if (!exists)
            return NotFound();

        auditContext.RecordBefore(await db.XpRoleRewards
            .FirstOrDefaultAsync(r => r.Level == level && r.GuildId == guildId));

        await db.XpRoleRewards
            .Where(r => r.Level == level && r.GuildId == guildId)
            .DeleteAsync();

        return Ok();
    }

    /// <summary>
    ///     Gets currency rewards for a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>List of currency rewards</returns>
    [HttpGet("rewards/currency")]
    public async Task<IActionResult> GetCurrencyRewards(ulong guildId)
    {
        var rewards = await xp.GetCurrencyRewardsAsync(guildId);
        return Ok(rewards);
    }

    /// <summary>
    ///     Adds a currency reward for a specific level
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="reward">The currency reward to add</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("rewards/currency")]
    public async Task<IActionResult> AddCurrencyReward(ulong guildId, [FromBody] XpCurrencyReward? reward)
    {
        if (reward == null)
            return BadRequest(BodyError());
        if (reward.Level < 1)
            return BadRequest("Level must be at least a positive integer");

        if (reward.Amount <= 0)
            return BadRequest("Amount must be positive");

        auditContext.RecordBefore(await xp.GetCurrencyRewardsAsync(guildId));
        await xpRewardManager.SetCurrencyRewardAsync(guildId, reward.Level, reward.Amount);
        auditContext.RecordAfter(await xp.GetCurrencyRewardsAsync(guildId));
        return Ok();
    }

    /// <summary>
    ///     Removes a currency reward
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="level">The level of the currency reward to remove</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpDelete("rewards/currency/{level}")]
    public async Task<IActionResult> RemoveCurrencyReward(ulong guildId, int level)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        // Check if the reward exists
        var exists = await db.XpCurrencyRewards
            .AnyAsync(r => r.Level == level && r.GuildId == guildId);

        if (!exists)
            return NotFound();

        auditContext.RecordBefore(await db.XpCurrencyRewards
            .FirstOrDefaultAsync(r => r.Level == level && r.GuildId == guildId));

        await db.XpCurrencyRewards
            .Where(r => r.Level == level && r.GuildId == guildId)
            .DeleteAsync();

        return Ok();
    }

    /// <summary>
    ///     Gets excluded channels for XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>List of excluded channel IDs</returns>
    [HttpGet("excluded/channels")]
    public async Task<IActionResult> GetExcludedChannels(ulong guildId)
    {
        var channels = await xp.GetExcludedItemsAsync(guildId, ExcludedItemType.Channel);
        return Ok(channels);
    }

    /// <summary>
    ///     Excludes a channel from XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="channelId">The channel ID to exclude</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("excluded/channels")]
    public async Task<IActionResult> ExcludeChannel(ulong guildId, [FromBody] ulong channelId)
    {
        await xp.ExcludeItemAsync(guildId, channelId, ExcludedItemType.Channel);
        return Ok();
    }

    /// <summary>
    ///     Includes a previously excluded channel for XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="channelId">The channel ID to include</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpDelete("excluded/channels/{channelId}")]
    public async Task<IActionResult> IncludeChannel(ulong guildId, ulong channelId)
    {
        await xp.IncludeItemAsync(guildId, channelId, ExcludedItemType.Channel);
        return Ok();
    }

    /// <summary>
    ///     Gets excluded roles for XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>List of excluded role IDs</returns>
    [HttpGet("excluded/roles")]
    public async Task<IActionResult> GetExcludedRoles(ulong guildId)
    {
        var roles = await xp.GetExcludedItemsAsync(guildId, ExcludedItemType.Role);
        return Ok(roles);
    }

    /// <summary>
    ///     Excludes a role from XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="roleId">The role ID to exclude</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("excluded/roles")]
    public async Task<IActionResult> ExcludeRole(ulong guildId, [FromBody] ulong roleId)
    {
        await xp.ExcludeItemAsync(guildId, roleId, ExcludedItemType.Role);
        return Ok();
    }

    /// <summary>
    ///     Includes a previously excluded role for XP gain
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="roleId">The role ID to include</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpDelete("excluded/roles/{roleId}")]
    public async Task<IActionResult> IncludeRole(ulong guildId, ulong roleId)
    {
        await xp.IncludeItemAsync(guildId, roleId, ExcludedItemType.Role);
        return Ok();
    }

    /// <summary>
    ///     Gets the XP template for a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>The template configuration</returns>
    [HttpGet("template")]
    public async Task<IActionResult> GetTemplate(ulong guildId)
    {
        var cardGenerator = serviceProvider.GetRequiredService<XpCardGenerator>();
        var template = await cardGenerator.GetTemplateAsync(guildId);
        return Ok(template);
    }

    /// <summary>
    ///     Updates the XP template for a guild
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <param name="template">The template to update</param>
    /// <returns>A 200 status code if successful</returns>
    [HttpPost("template")]
    public async Task<IActionResult> UpdateTemplate(ulong guildId, [FromBody] Template? template)
    {
        if (template == null)
            return BadRequest(BodyError());
        if (template.GuildId != guildId)
            return BadRequest("Guild ID mismatch");

        await using var db = await dbFactory.CreateConnectionAsync();

        // Load the complete existing template with all related entities
        var existingTemplate = await db.Templates
            .LoadWithAsTable(t => t.TemplateUser)
            .LoadWithAsTable(t => t.TemplateBar)
            .LoadWithAsTable(t => t.TemplateClub)
            .LoadWithAsTable(t => t.TemplateGuild)
            .FirstOrDefaultAsync(t => t.Id == template.Id && t.GuildId == guildId);

        if (existingTemplate == null)
            return NotFound();

        auditContext.RecordBefore(existingTemplate);

        try
        {
            // Update main template properties
            existingTemplate.OutputSizeX = template.OutputSizeX;
            existingTemplate.OutputSizeY = template.OutputSizeY;
            existingTemplate.TimeOnLevelFormat = template.TimeOnLevelFormat;
            existingTemplate.TimeOnLevelX = template.TimeOnLevelX;
            existingTemplate.TimeOnLevelY = template.TimeOnLevelY;
            existingTemplate.TimeOnLevelFontSize = template.TimeOnLevelFontSize;
            existingTemplate.TimeOnLevelColor = template.TimeOnLevelColor;
            existingTemplate.ShowTimeOnLevel = template.ShowTimeOnLevel;
            existingTemplate.AwardedX = template.AwardedX;
            existingTemplate.AwardedY = template.AwardedY;
            existingTemplate.AwardedFontSize = template.AwardedFontSize;
            existingTemplate.AwardedColor = template.AwardedColor;
            existingTemplate.ShowAwarded = template.ShowAwarded;
            existingTemplate.CustomElementsJson = template.CustomElementsJson;
            existingTemplate.BuiltInOrderJson = template.BuiltInOrderJson;

            // Update Template User properties
            if (template.TemplateUser != null)
            {
                // Update properties but maintain the ID
                var id = existingTemplate.TemplateUser.Id;
                template.TemplateUser.Id = id;

                // Copy all properties from the incoming TemplateUser
                existingTemplate.TemplateUser.FontSize = template.TemplateUser.FontSize;
                existingTemplate.TemplateUser.IconSizeX = template.TemplateUser.IconSizeX;
                existingTemplate.TemplateUser.IconSizeY = template.TemplateUser.IconSizeY;
                existingTemplate.TemplateUser.IconX = template.TemplateUser.IconX;
                existingTemplate.TemplateUser.IconY = template.TemplateUser.IconY;
                existingTemplate.TemplateUser.ShowIcon = template.TemplateUser.ShowIcon;
                existingTemplate.TemplateUser.ShowText = template.TemplateUser.ShowText;
                existingTemplate.TemplateUser.TextColor = template.TemplateUser.TextColor;
                existingTemplate.TemplateUser.TextX = template.TemplateUser.TextX;
                existingTemplate.TemplateUser.TextY = template.TemplateUser.TextY;

                await db.UpdateAsync(existingTemplate.TemplateUser);
            }

            // Update Template Bar properties
            if (template.TemplateBar != null)
            {
                // Update properties but maintain the ID
                var id = existingTemplate.TemplateBar.Id;
                template.TemplateBar.Id = id;

                // Copy all properties from incoming TemplateBar
                existingTemplate.TemplateBar.BarColor = template.TemplateBar.BarColor;
                existingTemplate.TemplateBar.BarDirection = template.TemplateBar.BarDirection;
                existingTemplate.TemplateBar.BarLength = template.TemplateBar.BarLength;
                existingTemplate.TemplateBar.BarPointAx = template.TemplateBar.BarPointAx;
                existingTemplate.TemplateBar.BarPointAy = template.TemplateBar.BarPointAy;
                existingTemplate.TemplateBar.BarPointBx = template.TemplateBar.BarPointBx;
                existingTemplate.TemplateBar.BarPointBy = template.TemplateBar.BarPointBy;
                existingTemplate.TemplateBar.BarTransparency = template.TemplateBar.BarTransparency;
                existingTemplate.TemplateBar.ShowBar = template.TemplateBar.ShowBar;

                await db.UpdateAsync(existingTemplate.TemplateBar);
            }

            // Update Template Guild properties
            if (template.TemplateGuild != null)
            {
                // Update properties but maintain the ID
                var id = existingTemplate.TemplateGuild.Id;
                template.TemplateGuild.Id = id;

                // Copy all properties from the incoming TemplateGuild
                existingTemplate.TemplateGuild.GuildLevelColor = template.TemplateGuild.GuildLevelColor;
                existingTemplate.TemplateGuild.GuildLevelFontSize = template.TemplateGuild.GuildLevelFontSize;
                existingTemplate.TemplateGuild.GuildLevelX = template.TemplateGuild.GuildLevelX;
                existingTemplate.TemplateGuild.GuildLevelY = template.TemplateGuild.GuildLevelY;
                existingTemplate.TemplateGuild.GuildRankColor = template.TemplateGuild.GuildRankColor;
                existingTemplate.TemplateGuild.GuildRankFontSize = template.TemplateGuild.GuildRankFontSize;
                existingTemplate.TemplateGuild.GuildRankX = template.TemplateGuild.GuildRankX;
                existingTemplate.TemplateGuild.GuildRankY = template.TemplateGuild.GuildRankY;
                existingTemplate.TemplateGuild.ShowGuildLevel = template.TemplateGuild.ShowGuildLevel;
                existingTemplate.TemplateGuild.ShowGuildRank = template.TemplateGuild.ShowGuildRank;

                await db.UpdateAsync(existingTemplate.TemplateGuild);
            }

            // Update Template Club properties
            if (template.TemplateClub != null)
            {
                // Update properties but maintain the ID
                var id = existingTemplate.TemplateClub.Id;
                template.TemplateClub.Id = id;

                // Copy all properties from the incoming TemplateClub
                existingTemplate.TemplateClub.ClubIconSizeX = template.TemplateClub.ClubIconSizeX;
                existingTemplate.TemplateClub.ClubIconSizeY = template.TemplateClub.ClubIconSizeY;
                existingTemplate.TemplateClub.ClubIconX = template.TemplateClub.ClubIconX;
                existingTemplate.TemplateClub.ClubIconY = template.TemplateClub.ClubIconY;
                existingTemplate.TemplateClub.ClubNameColor = template.TemplateClub.ClubNameColor;
                existingTemplate.TemplateClub.ClubNameFontSize = template.TemplateClub.ClubNameFontSize;
                existingTemplate.TemplateClub.ClubNameX = template.TemplateClub.ClubNameX;
                existingTemplate.TemplateClub.ClubNameY = template.TemplateClub.ClubNameY;
                existingTemplate.TemplateClub.ShowClubIcon = template.TemplateClub.ShowClubIcon;
                existingTemplate.TemplateClub.ShowClubName = template.TemplateClub.ShowClubName;

                await db.UpdateAsync(existingTemplate.TemplateClub);
            }

            // Update the main template
            await db.UpdateAsync(existingTemplate);

            auditContext.RecordAfter(existingTemplate);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error updating template: {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets server XP statistics
    /// </summary>
    /// <param name="guildId">The guild ID</param>
    /// <returns>Server XP statistics</returns>
    [HttpGet("stats")]
    [SkipDashboardAccess]
    public async Task<IActionResult> GetServerStats(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        // Get settings first for level calculation
        var settings = await xp.GetGuildXpSettingsAsync(guildId);

        // Use separate queries for count and sum
        var totalUsers = await db.GuildUserXps
            .CountAsync(x => x.GuildId == guildId);

        var totalXp = await db.GuildUserXps
            .Where(x => x.GuildId == guildId)
            .SumAsync(x => x.TotalXp);

        // Get highest level and data for average level calculation
        var highestLevel = 0;
        var averageLevel = 0.0;

        if (totalUsers > 0)
        {
            // Get all XP values for level calculation - only fetch the necessary field
            var allXp = await db.GuildUserXps
                .Where(x => x.GuildId == guildId)
                .Select(x => x.TotalXp)
                .ToListAsync();

            // Calculate levels
            var levels = allXp.Select(l => XpCalculator.CalculateLevel(l, (XpCurveType)settings.XpCurveType)).ToList();
            highestLevel = levels.Count > 0 ? levels.Max() : 0;
            averageLevel = levels.Count > 0 ? levels.Average() : 0;
        }

        // Get recent activity with a single query
        var recentGains = await db.GuildUserXps
            .Where(x => x.GuildId == guildId)
            .OrderByDescending(x => x.LastActivity)
            .Take(10)
            .Select(x => new
            {
                x.UserId, x.LastActivity
            })
            .ToListAsync();

        var guild = client.GetGuild(guildId);
        var recentGainsDetails = recentGains.Select(x =>
        {
            var user = guild?.GetUser(x.UserId);
            return new
            {
                x.UserId,
                Username = user?.Username ?? "Unknown",
                AvatarUrl = user?.GetAvatarUrl() ?? user?.GetDefaultAvatarUrl(),
                Timestamp = x.LastActivity
            };
        }).ToList();

        return Ok(new
        {
            TotalUsers = totalUsers,
            TotalXp = totalXp,
            AverageLevel = Math.Round(averageLevel, 1),
            HighestLevel = highestLevel,
            RecentActivity = recentGainsDetails
        });
    }

    private static List<string> SplitIds(string ids)
    {
        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    ///     Explains why a request body bound to null. Model validation is suppressed globally, so a body that
    ///     failed to deserialize reaches the action as null with the reason left in model state.
    /// </summary>
    private string BodyError()
    {
        var reasons = ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Exception?.Message : e.ErrorMessage)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct()
            .ToList();

        return reasons.Count == 0
            ? "The request body is missing or is not valid JSON for this endpoint"
            : string.Join(" ", reasons);
    }
}