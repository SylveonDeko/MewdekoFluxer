using Mewdeko.Controllers.Common.Migration;
using Mewdeko.Modules.Migration.Common;
using Mewdeko.Modules.Migration.Services;
using Mewdeko.AuthHandlers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mewdeko.Controllers;

/// <summary>
///     Brings a server and its members over from Mewdeko on Discord, for the dashboard and the setup wizard. The
///     dashboard proves the Discord identity by signing the user in with Discord.
/// </summary>
[ApiController]
[Route("botapi/[controller]")]
[Authorize("ApiKeyPolicy")]
public class MigrationController(FluxerClient client, DiscordMigrationService service) : Controller
{
    /// <summary>
    ///     Lists the Discord servers the user runs that Mewdeko on Discord is in.
    /// </summary>
    [HttpPost("discord-servers")]
    public async Task<IActionResult> GetDiscordServers([FromBody] DiscordServersRequest request)
    {
        var servers = await service.ListDiscordServersAsync(request.DiscordUserId, request.GuildIds);
        return servers is null ? StatusCode(503, "Mewdeko on Discord did not answer.") : Ok(servers);
    }

    /// <summary>
    ///     Exports a Discord server and returns the code its data is stored under.
    /// </summary>
    [HttpPost("{guildId}/export")]
    public async Task<IActionResult> Export(ulong guildId, [FromBody] MigrationExportRequest request)
    {
        if (await AdministratorAsync(guildId) is null)
            return StatusCode(403, "Only the server owner or an administrator can bring a server over.");

        var (code, error) = await service.ExportFromDiscordAsync(request.DiscordUserId, request.DiscordGuildId,
            request.BotId);
        return error switch
        {
            null => Ok(new { code }),
            "not_admin" => StatusCode(403, error),
            "not_found" => NotFound(error),
            "unavailable" => StatusCode(503, error),
            _ => StatusCode(500, error)
        };
    }

    /// <summary>
    ///     Shows what bringing an export into this server would do.
    /// </summary>
    [HttpPost("{guildId}/preview")]
    public async Task<IActionResult> Preview(ulong guildId, [FromBody] MigrationPlanRequest request)
    {
        if (client.GetGuild(guildId) is not { } guild)
            return NotFound("Guild not found.");

        var (plan, problem) = await service.PrepareAsync(guild, request.Code, request.ChannelOverrides,
            request.RoleOverrides);
        if (plan is null)
            return Problem(problem);

        var channelNames = plan.Meta.Channels.ToDictionary(x => x.Id, x => x.Name);
        var toCopy = MigrationExpressions.CountToCopy(plan);
        return Ok(new MigrationPreviewResponse(
            plan.Code,
            plan.Meta.DiscordGuildId,
            plan.Meta.GuildName,
            plan.Meta.Channels.OrderBy(x => x.Position).Select(x => new MigrationChannelPreview(x.Id, x.Name, x.Type,
                    x.ParentId is { } parent ? channelNames.GetValueOrDefault(parent) : null,
                    plan.Match.Channels.TryGetValue(ulong.Parse(x.Id), out var fluxer) ? fluxer.ToString() : null))
                .ToList(),
            plan.Meta.Roles.Where(x => !x.Managed && x.Id != plan.Meta.DiscordGuildId)
                .OrderByDescending(x => x.Position)
                .Select(x => new MigrationRolePreview(x.Id, x.Name, x.Color,
                    plan.Match.Roles.TryGetValue(ulong.Parse(x.Id), out var fluxer) ? fluxer.ToString() : null))
                .ToList(),
            plan.Match.Emojis.Count,
            plan.Meta.Emojis.Count,
            toCopy.Emojis,
            plan.Meta.Stickers.Count,
            toCopy.Stickers,
            plan.Tables.Count,
            plan.SettingRows,
            plan.MemberRows,
            plan.Members.Count,
            plan.Members.Count(x => x.Value)));
    }

    /// <summary>
    ///     Brings an export into this server, replacing its Mewdeko settings.
    /// </summary>
    [HttpPost("{guildId}/apply")]
    public async Task<IActionResult> Apply(ulong guildId, [FromBody] MigrationPlanRequest request)
    {
        if (client.GetGuild(guildId) is not { } guild)
            return NotFound("Guild not found.");

        if (await AdministratorAsync(guildId) is not { } userId)
            return StatusCode(403, "Only the server owner or an administrator can bring a server over.");

        var (plan, problem) = await service.PrepareAsync(guild, request.Code, request.ChannelOverrides,
            request.RoleOverrides);
        if (plan is null)
            return Problem(problem);

        return service.StartApply(plan, userId, request.CopyExpressions)
            ? Accepted(new { started = true })
            : Conflict("A move into this server is already running.");
    }

    /// <summary>
    ///     Gets how the background move into this server is going.
    /// </summary>
    [HttpGet("{guildId}/apply-status")]
    public IActionResult GetApplyStatus(ulong guildId)
    {
        var job = service.GetJob(guildId);
        return job switch
        {
            null => Ok(new { state = "none" }),
            { Error: { } error } => Ok(new { state = "failed", error }),
            { Outcome: { } outcome } => Ok(new { state = "done", outcome }),
            _ => Ok(new { state = "running", startedAt = job.StartedAt })
        };
    }

    /// <summary>
    ///     Gets whether this server was brought over from Discord.
    /// </summary>
    [HttpGet("{guildId}/status")]
    public async Task<IActionResult> GetStatus(ulong guildId)
    {
        var status = await service.GetStatusAsync(guildId);
        if (status is not { } found)
            return Ok(new { migrated = false });

        return Ok(new
        {
            migrated = true,
            discordGuildId = found.Migration.DiscordGuildId.ToString(),
            migratedAt = found.Migration.DateAdded,
            waitingMembers = found.WaitingMembers
        });
    }

    /// <summary>
    ///     Links a Fluxer account to a Discord account and brings the member's data over.
    /// </summary>
    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] MigrationLinkRequest request)
    {
        var outcome = await service.LinkAccountsAsync(request.UserId, request.DiscordUserId);
        return outcome.AlreadyLinkedElsewhere ? Conflict(outcome) : Ok(outcome);
    }

    /// <summary>
    ///     Gets the Discord account a Fluxer account is linked to.
    /// </summary>
    [HttpGet("link/{userId}")]
    public async Task<IActionResult> GetLink(ulong userId)
    {
        var discordUserId = await service.GetLinkedDiscordIdAsync(userId);
        return Ok(new { linked = discordUserId is not null, discordUserId = discordUserId?.ToString() });
    }

    private async Task<ulong?> AdministratorAsync(ulong guildId)
    {
        var auth = await HttpContext.AuthenticateAsync(DashJwtConstants.SchemeName);
        if (!auth.Succeeded ||
            !ulong.TryParse(auth.Principal?.FindFirst(DashJwtConstants.UserIdClaim)?.Value, out var userId))
            return null;

        var guild = client.GetGuild(guildId);
        if (guild is null)
            return null;

        if (guild.OwnerId == userId)
            return userId;

        return guild.GetUser(userId)?.GuildPermissions.Has(GuildPermission.Administrator) == true ? userId : null;
    }

    private IActionResult Problem(MigrationProblem problem)
    {
        return problem switch
        {
            MigrationProblem.NotFound => NotFound("No export was found for that code, or it expired."),
            MigrationProblem.UnsupportedVersion => StatusCode(409, "The export was made by a newer version."),
            _ => BadRequest("That is not a migration code.")
        };
    }
}
