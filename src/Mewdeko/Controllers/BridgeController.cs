using Mewdeko.Controllers.Common.Bridge;
using Mewdeko.Modules.Bridge.Common;
using Mewdeko.Modules.Bridge.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mewdeko.Controllers;

/// <summary>
///     Lists and manages a guild's channel bridges for the dashboard.
/// </summary>
[ApiController]
[Route("botapi/[controller]/{guildId}")]
[Authorize("ApiKeyPolicy")]
public partial class BridgeController(
    BridgeService bridges,
    FluxerClient client,
    IDashboardAuditContext auditContext) : Controller
{
    /// <summary>
    ///     Gets the guild's bridges.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    [HttpGet]
    public IActionResult Get(ulong guildId)
    {
        if (client.GetGuild(guildId) is not { } guild)
            return NotFound("Guild not found");

        return Ok(bridges.GetBridges(guildId).Select(x => ToResponse(guild, x)));
    }

    /// <summary>
    ///     Links a channel to a code made on the other platform.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="request">The code, the channel and the dashboard user.</param>
    [HttpPost("link")]
    public async Task<IActionResult> Link(ulong guildId, [FromBody] BridgeLinkRequest request)
    {
        if (client.GetGuild(guildId) is not { } guild)
            return NotFound("Guild not found");
        if (guild.Members.GetValueOrDefault(request.UserId) is not { } member
            || !member.HasPermission(GuildPermission.ManageGuild))
            return StatusCode(StatusCodes.Status403Forbidden, "The user needs the Manage Community permission");

        var (result, accepted) = await bridges.AcceptCodeAsync(guildId, request.ChannelId, request.UserId,
            request.Code ?? "");
        if (accepted is null)
            return BadRequest(new
            {
                error = result switch
                {
                    BridgeResult.AlreadyBridged => "already_bridged",
                    BridgeResult.MissingPermission => "missing_permission",
                    BridgeResult.CodeWrongPlatform => "code_wrong_platform",
                    BridgeResult.CodeInvalid => "code_invalid",
                    _ => "channel_invalid"
                }
            });

        return bridges.GetBridge(request.ChannelId) is { } created
            ? Ok(ToResponse(guild, created))
            : BadRequest(new { error = "channel_invalid" });
    }

    private static BridgeResponse ToResponse(SocketGuild guild, BridgeInfo x) => new()
    {
        BridgeId = x.BridgeId,
        ChannelId = x.ChannelId,
        ChannelName = guild.GetChannel(x.ChannelId)?.Name,
        RemoteGuildId = x.RemoteGuildId,
        RemoteChannelId = x.RemoteChannelId,
        RemoteGuildName = x.RemoteGuildName,
        RemoteChannelName = x.RemoteChannelName,
        Platform = BridgeProtocol.Fluxer,
        Paused = x.Paused,
        AllowBots = x.AllowBots,
        Direction = x.Options.Direction,
        AllowedRoleIds = x.Options.AllowedRoleIds.ToArray(),
        BlockedRoleIds = x.Options.BlockedRoleIds.ToArray(),
        BlockedUserIds = x.Options.BlockedUserIds.ToArray(),
        MinMemberAgeMinutes = x.Options.MinMemberAgeMinutes,
        BlockInvites = x.Options.BlockInvites,
        UseWordFilter = x.Options.UseWordFilter,
        NameFormat = x.Options.NameFormat,
        PairId = x.PairId?.ToString(),
        DateAdded = x.DateAdded
    };

    /// <summary>
    ///     Replaces all the options of a bridge.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="bridgeId">The bridge ID.</param>
    /// <param name="request">The new options.</param>
    [HttpPost("{bridgeId:guid}/settings")]
    public async Task<IActionResult> Settings(ulong guildId, Guid bridgeId, [FromBody] BridgeSettingsRequest? request)
    {
        if (client.GetGuild(guildId) is not { } guild || bridges.GetBridge(guildId, bridgeId) is not { } before)
            return BadRequest(new { error = "not_found" });

        if (request is not
            {
                Direction: { } direction, AllowedRoleIds: { } allowed, BlockedRoleIds: { } blockedRoles,
                BlockedUserIds: { } blockedUsers, MinMemberAgeMinutes: { } minAge, BlockInvites: { } invites,
                UseWordFilter: { } filter, NameFormat: { } nameFormat
            })
            return BadRequest(new { error = "invalid_request" });

        var options = new BridgeOptions(direction, allowed.Distinct().ToList(), blockedRoles.Distinct().ToList(),
            blockedUsers.Distinct().ToList(), minAge, invites, filter, nameFormat);
        if (BridgeSettings.Validate(options) is { } error)
            return BadRequest(new { error });

        auditContext.RecordBefore(before);
        await bridges.SetOptionsAsync(guildId, bridgeId, options);
        return bridges.GetBridge(guildId, bridgeId) is { } after
            ? Ok(ToResponse(guild, after))
            : BadRequest(new { error = "not_found" });
    }

    /// <summary>
    ///     Creates a link code for a channel.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="request">The channel and the dashboard user.</param>
    [HttpPost("code")]
    public async Task<IActionResult> CreateCode(ulong guildId, [FromBody] BridgeCodeRequest request)
    {
        if (client.GetGuild(guildId) is not { } guild)
            return NotFound("Guild not found");
        if (guild.Members.GetValueOrDefault(request.UserId) is not { } member
            || !member.HasPermission(GuildPermission.ManageGuild))
            return StatusCode(StatusCodes.Status403Forbidden, "The user needs the Manage Community permission");

        var (result, code) = await bridges.CreateCodeAsync(guildId, request.ChannelId, request.UserId);
        if (code is null)
            return result switch
            {
                BridgeResult.AlreadyBridged => BadRequest(new { error = "already_bridged" }),
                BridgeResult.MissingPermission => BadRequest(new { error = "missing_permission" }),
                _ => BadRequest(new { error = "channel_invalid" })
            };

        return Ok(new BridgeCodeResponse
        {
            Code = code.Code, ExpiresAt = code.CreatedAt + BridgeProtocol.LinkCodeLifetime
        });
    }

    /// <summary>
    ///     Pauses a bridge.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="bridgeId">The bridge ID.</param>
    [HttpPost("{bridgeId:guid}/pause")]
    public Task<IActionResult> Pause(ulong guildId, Guid bridgeId) => SetPaused(guildId, bridgeId, true);

    /// <summary>
    ///     Resumes a bridge.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="bridgeId">The bridge ID.</param>
    [HttpPost("{bridgeId:guid}/resume")]
    public Task<IActionResult> Resume(ulong guildId, Guid bridgeId) => SetPaused(guildId, bridgeId, false);

    /// <summary>
    ///     Sets whether a bridge relays bot messages.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="bridgeId">The bridge ID.</param>
    /// <param name="request">The new setting.</param>
    [HttpPost("{bridgeId:guid}/allow-bots")]
    public async Task<IActionResult> AllowBots(ulong guildId, Guid bridgeId, [FromBody] BridgeAllowBotsRequest request)
    {
        if (bridges.GetBridge(guildId, bridgeId) is not { } before)
            return NotFound("Bridge not found");

        auditContext.RecordBefore(before);
        await bridges.SetAllowBotsAsync(guildId, bridgeId, request.Allow);
        return Ok();
    }

    /// <summary>
    ///     Removes a bridge.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="bridgeId">The bridge ID.</param>
    [HttpDelete("{bridgeId:guid}")]
    public async Task<IActionResult> Remove(ulong guildId, Guid bridgeId)
    {
        if (bridges.GetBridge(guildId, bridgeId) is not { } before)
            return NotFound("Bridge not found");

        auditContext.RecordBefore(before);
        await bridges.RemoveAsync(guildId, bridgeId);
        return Ok();
    }

    private async Task<IActionResult> SetPaused(ulong guildId, Guid bridgeId, bool paused)
    {
        if (bridges.GetBridge(guildId, bridgeId) is not { } before)
            return NotFound("Bridge not found");

        auditContext.RecordBefore(before);
        await bridges.SetPausedAsync(guildId, bridgeId, paused);
        return Ok();
    }
}
