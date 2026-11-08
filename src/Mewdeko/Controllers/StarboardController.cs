using Mewdeko.Controllers.Common.Starboard;
using Mewdeko.Modules.Starboard.Common;
using Mewdeko.Modules.Starboard.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mewdeko.Controllers;

/// <summary>
///     Controller for managing starboard configurations in guilds
/// </summary>
[ApiController]
[Route("botapi/[controller]/{guildId}")]
[Authorize("ApiKeyPolicy")]
public class StarboardController : Controller
{
    private readonly FluxerClient client;
    private readonly StarboardService starboardService;
    private readonly IDashboardAuditContext auditContext;

    /// <summary>
    ///     Initializes a new instance of the StarboardController
    /// </summary>
    /// <param name="starboardService">The starboardservice service.</param>
    /// <param name="client">The Discord client instance.</param>
    /// <param name="auditContext">Records before/after state for the dashboard audit log.</param>
    public StarboardController(
        StarboardService starboardService,
        FluxerClient client,
        IDashboardAuditContext auditContext)
    {
        this.starboardService = starboardService;
        this.client = client;
        this.auditContext = auditContext;
    }

    /// <summary>
    ///     Gets all starboard configurations for a guild
    /// </summary>
    [HttpGet("all")]
    public IActionResult GetStarboards(ulong guildId)
    {
        var starboards = starboardService.GetStarboards(guildId);
        return Ok(starboards);
    }

    /// <summary>
    ///     Creates a new starboard for a guild
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateStarboard(ulong guildId, [FromBody] StarboardCreateRequest request)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        try
        {
            var starboardId =
                await starboardService.CreateStarboard(guild, request.ChannelId, request.Emote, request.Threshold);
            return Ok(starboardId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already used"))
        {
            return BadRequest("Emoji is already in use by another starboard configuration.");
        }
    }

    /// <summary>
    ///     Deletes a starboard configuration
    /// </summary>
    [HttpDelete("{starboardId}")]
    public async Task<IActionResult> DeleteStarboard(ulong guildId, int starboardId)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.DeleteStarboard(guild, starboardId);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok();
    }

    /// <summary>
    ///     Sets whether bots are allowed to be starred for a specific starboard
    /// </summary>
    [HttpPost("{starboardId}/allow-bots")]
    public async Task<IActionResult> SetAllowBots(ulong guildId, int starboardId, [FromBody] bool allowed)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetAllowBots(guild, starboardId, allowed);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(allowed);
    }

    /// <summary>
    ///     Sets whether to remove starred messages when the original is deleted
    /// </summary>
    [HttpPost("{starboardId}/remove-on-delete")]
    public async Task<IActionResult> SetRemoveOnDelete(ulong guildId, int starboardId, [FromBody] bool removeOnDelete)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetRemoveOnDelete(guild, starboardId, removeOnDelete);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(removeOnDelete);
    }

    /// <summary>
    ///     Sets whether to remove starred messages when reactions are cleared
    /// </summary>
    [HttpPost("{starboardId}/remove-on-clear")]
    public async Task<IActionResult> SetRemoveOnClear(ulong guildId, int starboardId, [FromBody] bool removeOnClear)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetRemoveOnClear(guild, starboardId, removeOnClear);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(removeOnClear);
    }

    /// <summary>
    ///     Sets whether to remove starred messages when they fall below threshold
    /// </summary>
    [HttpPost("{starboardId}/remove-below-threshold")]
    public async Task<IActionResult> SetRemoveBelowThreshold(ulong guildId, int starboardId,
        [FromBody] bool removeBelowThreshold)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetRemoveBelowThreshold(guild, starboardId, removeBelowThreshold);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(removeBelowThreshold);
    }

    /// <summary>
    ///     Sets the repost threshold for a starboard
    /// </summary>
    [HttpPost("{starboardId}/repost-threshold")]
    public async Task<IActionResult> SetRepostThreshold(ulong guildId, int starboardId, [FromBody] int threshold)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetRepostThreshold(guild, starboardId, threshold);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(threshold);
    }

    /// <summary>
    ///     Sets the star threshold for a starboard
    /// </summary>
    [HttpPost("{starboardId}/star-threshold")]
    public async Task<IActionResult> SetStarThreshold(ulong guildId, int starboardId, [FromBody] int threshold)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetStarThreshold(guild, starboardId, threshold);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(threshold);
    }

    /// <summary>
    ///     Sets whether to use blacklist mode for channel checking
    /// </summary>
    [HttpPost("{starboardId}/use-blacklist")]
    public async Task<IActionResult> SetUseBlacklist(ulong guildId, int starboardId, [FromBody] bool useBlacklist)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetUseBlacklist(guild, starboardId, useBlacklist);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(useBlacklist);
    }

    /// <summary>
    ///     Toggles a channel in the starboard's check list
    /// </summary>
    [HttpPost("{starboardId}/toggle-channel")]
    public async Task<IActionResult> ToggleChannel(ulong guildId, int starboardId, [FromBody] ulong channelId)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var result = await starboardService.ToggleChannel(guild, starboardId, channelId.ToString());
        if (result.Config == null)
            return NotFound("Starboard configuration not found");

        return Ok(new
        {
            wasAdded = result.WasAdded, config = result.Config
        });
    }

    /// <summary>
    ///     Sets whether members can star their own messages
    /// </summary>
    [HttpPost("{starboardId}/self-star")]
    public async Task<IActionResult> SetAllowSelfStar(ulong guildId, int starboardId, [FromBody] bool allowed)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetAllowSelfStar(guild, starboardId, allowed);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(allowed);
    }

    /// <summary>
    ///     Sets whether age-restricted messages can be starred into a channel that isn't
    /// </summary>
    [HttpPost("{starboardId}/nsfw")]
    public async Task<IActionResult> SetAllowNsfw(ulong guildId, int starboardId, [FromBody] bool allowed)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetAllowNsfw(guild, starboardId, allowed);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(allowed);
    }

    /// <summary>
    ///     Locks a starboard until a given time, or unlocks it
    /// </summary>
    [HttpPost("{starboardId}/lock")]
    public async Task<IActionResult> SetLockedUntil(ulong guildId, int starboardId, [FromBody] DateTime? lockedUntil)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetLockedUntil(guild, starboardId, lockedUntil?.ToUniversalTime());
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(lockedUntil);
    }

    /// <summary>
    ///     Toggles a channel whose messages are posted without needing stars
    /// </summary>
    [HttpPost("{starboardId}/autostar-channel")]
    public async Task<IActionResult> ToggleAutoStarChannel(ulong guildId, int starboardId,
        [FromBody] ulong channelId)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var result = await starboardService.ToggleAutoStarChannel(guild, starboardId, channelId);
        if (result.Config == null)
            return NotFound("Starboard configuration not found");

        auditContext.RecordAfter(result.Config);
        return Ok(new
        {
            wasAdded = result.WasAdded, config = result.Config
        });
    }

    /// <summary>
    ///     Creates a starboard together with a read-only channel for its posts
    /// </summary>
    [HttpPost("setup")]
    public async Task<IActionResult> SetupStarboard(ulong guildId, [FromBody] StarboardSetupRequest request)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        try
        {
            var created = await starboardService.CreateStarboardWithChannel(guild, request.Name, request.Emote,
                request.Threshold);
            if (created is not { } result)
                return BadRequest("The channel could not be created. Check the bot's Manage Channels permission.");

            return Ok(new
            {
                starboardId = result.StarboardId, channelId = result.Channel.Id
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already used"))
        {
            return BadRequest("Emote is already in use by another starboard configuration.");
        }
    }

    /// <summary>
    ///     Turns a starboard on or off
    /// </summary>
    [HttpPost("{starboardId}/enabled")]
    public async Task<IActionResult> SetEnabled(ulong guildId, int starboardId, [FromBody] bool enabled)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetEnabled(guild, starboardId, enabled);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(enabled);
    }

    /// <summary>
    ///     Gets the placeholders a starboard post design can use
    /// </summary>
    [HttpGet("placeholders")]
    public IActionResult GetDesignPlaceholders()
        => Ok(StarboardPostDesign.Placeholders);

    /// <summary>
    ///     Sets the post design of a starboard, or clears it
    /// </summary>
    [HttpPost("{starboardId}/design")]
    public async Task<IActionResult> SetPostDesign(ulong guildId, int starboardId, [FromBody] string? design)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        switch (await starboardService.SetPostDesign(guild, starboardId, design))
        {
            case StarboardDesignResult.NotFound:
                return NotFound("Starboard configuration not found");
            case StarboardDesignResult.Invalid:
                return BadRequest("The design is not valid embed json or shows nothing.");
            default:
                return Ok(design);
        }
    }

    /// <summary>
    ///     Sends a preview of a starboard's post design for a sample message
    /// </summary>
    [HttpPost("{starboardId}/design/test")]
    public async Task<IActionResult> SendDesignTest(ulong guildId, int starboardId,
        [FromBody] StarboardDesignTestRequest request)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        if (await guild.GetTextChannelAsync(request.ChannelId) is not { } channel)
            return NotFound("Channel not found");

        var sample = await StarboardService.FindSampleMessageAsync(channel, request.MessageId);
        if (sample == null)
            return NotFound("No message found to preview with.");

        var success = await starboardService.SendDesignTestAsync(guild, starboardId, channel, sample);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok();
    }

    /// <summary>
    ///     Sets which emotes the bot reacts with on its own posts
    /// </summary>
    [HttpPost("{starboardId}/auto-react")]
    public async Task<IActionResult> SetAutoReact(ulong guildId, int starboardId, [FromBody] StarboardAutoReact mode)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        if (!Enum.IsDefined(mode))
            return BadRequest("Mode must be 0, 1 or 2.");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetAutoReact(guild, starboardId, mode);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(mode);
    }

    /// <summary>
    ///     Sets whether one member's stars on several of the starboard's emotes all count
    /// </summary>
    [HttpPost("{starboardId}/multi-react")]
    public async Task<IActionResult> SetCountMultipleReactions(ulong guildId, int starboardId,
        [FromBody] bool enabled)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetCountMultipleReactions(guild, starboardId, enabled);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(enabled);
    }

    /// <summary>
    ///     Sets whether stars that can't count are removed from the message
    /// </summary>
    [HttpPost("{starboardId}/remove-invalid-stars")]
    public async Task<IActionResult> SetRemoveInvalidStars(ulong guildId, int starboardId, [FromBody] bool enabled)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetRemoveInvalidStars(guild, starboardId, enabled);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(enabled);
    }

    /// <summary>
    ///     Toggles a role whose members' stars count, when any role is listed
    /// </summary>
    [HttpPost("{starboardId}/allowed-role")]
    public Task<IActionResult> ToggleAllowedRole(ulong guildId, int starboardId, [FromBody] ulong roleId)
        => ToggleStarRole(guildId, starboardId, roleId, false);

    /// <summary>
    ///     Toggles a role whose members' stars never count
    /// </summary>
    [HttpPost("{starboardId}/denied-role")]
    public Task<IActionResult> ToggleDeniedRole(ulong guildId, int starboardId, [FromBody] ulong roleId)
        => ToggleStarRole(guildId, starboardId, roleId, true);

    private async Task<IActionResult> ToggleStarRole(ulong guildId, int starboardId, ulong roleId, bool deny)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var result = await starboardService.ToggleStarRole(guild, starboardId, roleId, deny);
        if (result.Config == null)
            return NotFound("Starboard configuration not found");

        auditContext.RecordAfter(result.Config);
        return Ok(new
        {
            wasAdded = result.WasAdded, config = result.Config
        });
    }

    /// <summary>
    ///     Sets how old a message has to be, and how old it may be, to be starred
    /// </summary>
    [HttpPost("{starboardId}/message-age")]
    public async Task<IActionResult> SetMessageAge(ulong guildId, int starboardId,
        [FromBody] StarboardMessageAgeRequest request)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetMessageAge(guild, starboardId, request.MinMinutes,
            request.MaxMinutes);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(request);
    }

    /// <summary>
    ///     Sets how long a post waits before it can be moved to the bottom of the starboard again
    /// </summary>
    [HttpPost("{starboardId}/repost-cooldown")]
    public async Task<IActionResult> SetRepostCooldown(ulong guildId, int starboardId, [FromBody] int minutes)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        auditContext.RecordBefore(starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
        var success = await starboardService.SetRepostCooldown(guild, starboardId, minutes);
        if (!success)
            return NotFound("Starboard configuration not found");

        return Ok(minutes);
    }

    /// <summary>
    ///     Gets starboard highlights for a guild
    /// </summary>
    /// <param name="guildId">The guild id.</param>
    /// <param name="limit">Maximum number of highlights, 1 through 25.</param>
    /// <param name="sort">"recent" (default) for newest starred first, "top" for most starred first.</param>
    [HttpGet("highlights")]
    public async Task<IActionResult> GetStarboardHighlights(ulong guildId, [FromQuery] int limit = 5,
        [FromQuery] string? sort = null)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        var highlights = await starboardService.GetRecentHighlights(guildId, limit, sort);
        return Ok(highlights);
    }

    /// <summary>
    ///     Adds an emote to an existing starboard configuration
    /// </summary>
    [HttpPost("{starboardId}/add-emote")]
    public async Task<IActionResult> AddEmoteToStarboard(ulong guildId, int starboardId, [FromBody] string emote)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        try
        {
            auditContext.RecordBefore(
                starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
            var success = await starboardService.AddEmoteToStarboard(guild, starboardId, emote);
            if (!success)
                return NotFound("Starboard configuration not found");

            return Ok(emote);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already used"))
        {
            return BadRequest("Emoji is already in use by another starboard configuration.");
        }
    }

    /// <summary>
    ///     Removes an emote from an existing starboard configuration
    /// </summary>
    [HttpPost("{starboardId}/remove-emote")]
    public async Task<IActionResult> RemoveEmoteFromStarboard(ulong guildId, int starboardId, [FromBody] string emote)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null)
            return NotFound("Guild not found");

        try
        {
            auditContext.RecordBefore(
                starboardService.GetStarboards(guildId).FirstOrDefault(s => s.Id == starboardId));
            var success = await starboardService.RemoveEmoteFromStarboard(guild, starboardId, emote);
            if (!success)
                return NotFound("Emoji not found in starboard configuration");

            return Ok(emote);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Cannot remove the last emote"))
        {
            return BadRequest("Cannot remove the last emote from a starboard. Delete the starboard instead.");
        }
    }

    /// <summary>
    ///     Gets starboard statistics for a guild
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStarboardStats(ulong guildId)
    {
        var stats = await starboardService.GetStarboardStats(guildId);
        if (stats == null)
            return NotFound("No starboard statistics available for this guild.");

        return Ok(stats);
    }
}