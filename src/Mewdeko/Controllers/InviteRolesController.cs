using DataModel;
using Mewdeko.Modules.Utility.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mewdeko.Controllers;

/// <summary>
///     API controller for managing invite milestone roles.
/// </summary>
[ApiController]
[Route("botapi/[controller]/{guildId}")]
[Authorize("ApiKeyPolicy")]
public class InviteRolesController : Controller
{
    private readonly IDashboardAuditContext auditContext;
    private readonly FluxerClient client;
    private readonly InviteRoleService service;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InviteRolesController" /> class.
    /// </summary>
    /// <param name="service">The invite role service.</param>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="auditContext">Records before/after state for the dashboard audit log.</param>
    public InviteRolesController(InviteRoleService service, FluxerClient client,
        IDashboardAuditContext auditContext)
    {
        this.service = service;
        this.client = client;
        this.auditContext = auditContext;
    }

    /// <summary>
    ///     Gets a guild's invite role settings and milestone roles.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The settings and roles.</returns>
    [HttpGet]
    public async Task<IActionResult> Get(ulong guildId)
    {
        return Ok(await BuildAsync(guildId));
    }

    /// <summary>
    ///     Updates a guild's invite role settings.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="request">The settings to change.</param>
    /// <returns>The settings and roles after the change.</returns>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(ulong guildId, [FromBody] InviteRoleSettingsRequest request)
    {
        if (request.Mode is { } mode && !Enum.IsDefined(mode))
            return BadRequest("Unknown mode.");

        auditContext.RecordBefore(await BuildAsync(guildId));
        await service.UpdateSettingsAsync(guildId, request.Mode, request.RemoveOnDrop);
        var response = await BuildAsync(guildId);
        auditContext.RecordAfter(response);
        return Ok(response);
    }

    /// <summary>
    ///     Adds a milestone role or changes its milestone.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <param name="request">The milestone.</param>
    /// <returns>The settings and roles after the change.</returns>
    [HttpPut("roles/{roleId}")]
    public async Task<IActionResult> SetRole(ulong guildId, ulong roleId, [FromBody] InviteRoleRequest request)
    {
        var guild = client.GetGuild(guildId);
        if (guild is null)
            return NotFound("Guild not found.");
        if (request.RequiredInvites < 1)
            return BadRequest("The milestone has to be at least 1 invite.");
        if (!InviteRoleService.CanAssign(guild, roleId))
            return BadRequest("The bot can't give out that role. Move its highest role above it.");

        auditContext.RecordBefore(await BuildAsync(guildId));
        await service.SetRoleAsync(guildId, roleId, request.RequiredInvites);
        var response = await BuildAsync(guildId);
        auditContext.RecordAfter(response);
        return Ok(response);
    }

    /// <summary>
    ///     Removes a milestone role.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <returns>The settings and roles after the change.</returns>
    [HttpDelete("roles/{roleId}")]
    public async Task<IActionResult> RemoveRole(ulong guildId, ulong roleId)
    {
        auditContext.RecordBefore(await BuildAsync(guildId));
        if (!await service.RemoveRoleAsync(guildId, roleId))
            return NotFound("That role is not a milestone role.");

        var response = await BuildAsync(guildId);
        auditContext.RecordAfter(response);
        return Ok(response);
    }

    /// <summary>
    ///     Applies milestone roles to every member from their current invite counts.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>How many members had roles changed.</returns>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(ulong guildId)
    {
        var guild = client.GetGuild(guildId);
        if (guild is null)
            return NotFound("Guild not found.");

        auditContext.RecordBefore(null);
        var changed = await service.SyncAsync(guild);
        return Ok(new
        {
            changed
        });
    }

    private async Task<InviteRolesResponse> BuildAsync(ulong guildId)
    {
        var settings = await service.GetSettingsAsync(guildId);
        var roles = await service.GetRolesAsync(guildId);
        var guild = client.GetGuild(guildId);
        return new InviteRolesResponse
        {
            Mode = (int)settings.Mode,
            RemoveOnDrop = settings.RemoveOnDrop,
            Roles = roles.Select(r => new InviteRoleResponse
            {
                RoleId = r.RoleId,
                RoleName = guild?.GetRole(r.RoleId)?.Name,
                RequiredInvites = r.RequiredInvites
            }).ToList()
        };
    }

    /// <summary>
    ///     Request model for changing invite role settings.
    /// </summary>
    public class InviteRoleSettingsRequest
    {
        /// <summary>
        ///     Gets or sets how roles are handed out, or null to keep it.
        /// </summary>
        public InviteRoleMode? Mode { get; set; }

        /// <summary>
        ///     Gets or sets whether roles are taken back when the count drops, or null to keep it.
        /// </summary>
        public bool? RemoveOnDrop { get; set; }
    }

    /// <summary>
    ///     Request model for adding a milestone role.
    /// </summary>
    public class InviteRoleRequest
    {
        /// <summary>
        ///     Gets or sets the net invites needed to earn the role.
        /// </summary>
        public int RequiredInvites { get; set; }
    }

    /// <summary>
    ///     Response model for a guild's invite role configuration.
    /// </summary>
    public class InviteRolesResponse
    {
        /// <summary>
        ///     Gets or sets how roles are handed out.
        /// </summary>
        public int Mode { get; set; }

        /// <summary>
        ///     Gets or sets whether roles are taken back when the count drops.
        /// </summary>
        public bool RemoveOnDrop { get; set; }

        /// <summary>
        ///     Gets or sets the milestone roles.
        /// </summary>
        public List<InviteRoleResponse> Roles { get; set; } = [];
    }

    /// <summary>
    ///     Response model for one milestone role.
    /// </summary>
    public class InviteRoleResponse
    {
        /// <summary>
        ///     Gets or sets the role ID.
        /// </summary>
        public ulong RoleId { get; set; }

        /// <summary>
        ///     Gets or sets the role name, or null if the role is gone.
        /// </summary>
        public string? RoleName { get; set; }

        /// <summary>
        ///     Gets or sets the net invites needed to earn the role.
        /// </summary>
        public int RequiredInvites { get; set; }
    }
}
