using DataModel;
using LinqToDB.Async;
using Mewdeko.Controllers.Common.DashboardAccess;
using Mewdeko.Modules.Giveaways.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mewdeko.Controllers;

/// <summary>
///     API Controller for managing and interacting with Giveaways.
/// </summary>
[ApiController]
[Route("botapi/[controller]")]
[Authorize("ApiKeyPolicy")]
public class GiveawaysController : Controller
{
    private readonly IDashboardAuditContext auditContext;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ILogger<GiveawaysController> logger;
    private readonly GiveawayService service;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GiveawaysController" /> class.
    /// </summary>
    /// <param name="service">The giveaway service instance.</param>
    /// <param name="dbFactory">The factory for creating database connections.</param>
    /// <param name="logger">Logger for this class.</param>
    /// <param name="auditContext">Records before/after state for the dashboard audit log.</param>
    public GiveawaysController(
        GiveawayService service,
        IDataConnectionFactory dbFactory, ILogger<GiveawaysController> logger,
        IDashboardAuditContext auditContext)
    {
        this.service = service;
        this.dbFactory = dbFactory;
        this.logger = logger;
        this.auditContext = auditContext;
    }

    /// <summary>
    ///     Gets a specific giveaway by its unique ID.
    /// </summary>
    /// <param name="giveawayId">The integer ID of the giveaway.</param>
    /// <returns>
    ///     An <see cref="OkObjectResult" /> containing the giveaway details if found, otherwise potentially null or an
    ///     error depending on service implementation.
    /// </returns>
    [HttpGet("{giveawayId:int}")]
    public async Task<IActionResult> GetGiveaway(int giveawayId)
    {
        var giveaway = await service.GetGiveawayById(giveawayId);
        return Ok(giveaway);
    }

    /// <summary>
    ///     Gets all active or relevant giveaways for a specific guild.
    /// </summary>
    /// <param name="guildId">The ID of the guild.</param>
    /// <returns>
    ///     An <see cref="OkObjectResult" /> containing a list of giveaways, or <see cref="NotFoundResult" /> if an error
    ///     occurs.
    /// </returns>
    [HttpGet("guild/{guildId:ulong}")]
    public async Task<IActionResult> GetGiveawaysForGuild(ulong guildId)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            var giveaways = await db.Giveaways
                .Where(x => x.ServerId == guildId)
                .ToListAsync();
            return Ok(giveaways);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve giveaways for guild {GuildId}", guildId);
            return NotFound(
                "Failed to retrieve giveaways for the specified guild.");
        }
    }

    /// <summary>
    ///     Creates a new giveaway initiated from an external source (e.g., dashboard).
    /// </summary>
    /// <param name="guildId">The ID of the guild where the giveaway will run.</param>
    /// <param name="model">The giveaway details.</param>
    /// <returns>
    ///     An <see cref="OkObjectResult" /> containing the created giveaway details, or
    ///     <see cref="BadRequestObjectResult" /> if creation fails.
    /// </returns>
    [HttpPost("{guildId:ulong}")]
    public async Task<IActionResult> CreateGiveaway(ulong guildId, [FromBody] Giveaway model)
    {
        try
        {
            var createdGiveaway = await service.CreateGiveawayFromDashboard(guildId, model);
            auditContext.RecordAfter(createdGiveaway);
            return Ok(createdGiveaway);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create dashboard giveaway for guild {GuildId}", guildId);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Manually ends a specific giveaway.
    /// </summary>
    /// <param name="guildId">The ID of the guild (used for routing, not directly in logic shown).</param>
    /// <param name="giveawayId">The ID of the giveaway to end.</param>
    /// <returns>
    ///     An <see cref="OkResult" /> if ending is initiated, <see cref="NotFoundObjectResult" /> if giveaway doesn't
    ///     exist, or <see cref="BadRequestObjectResult" /> on error.
    /// </returns>
    [HttpPatch("{guildId:ulong}/{giveawayId:int}")]
    public async Task<IActionResult> EndGiveaway(ulong guildId, int giveawayId)
    {
        try
        {
            var gway = await service.GetGiveawayById(giveawayId);
            if (gway is null)
                return NotFound("That giveaway doesnt exist.");

            auditContext.RecordBefore(gway);
            await service.GiveawayTimerAction(gway);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to end giveaway {GiveawayId} for guild {GuildId}", giveawayId, guildId);
            return BadRequest(ex.Message);
        }
    }
}
