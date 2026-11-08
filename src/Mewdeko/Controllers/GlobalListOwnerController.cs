using DataModel;
using Mewdeko.AuthHandlers;
using Mewdeko.Controllers.Common.GlobalList;
using Mewdeko.Modules.GlobalList.Common;
using Mewdeko.Modules.GlobalList.Services;
using Mewdeko.Services.Impl;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Mewdeko.Controllers;

/// <summary>
///     Owner only API for reviewing the global list. Bot wide, not guild scoped.
/// </summary>
[ApiController]
[Route("botapi/[controller]")]
[Authorize("ApiKeyPolicy")]
public class GlobalListOwnerController(
    GlobalListService service,
    BotCredentials creds,
    IDashboardAuditContext auditContext) : Controller
{
    private ulong actorId;

    /// <summary>
    ///     Rejects anyone who is not a bot owner.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <param name="next">The next action in the pipeline.</param>
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var authResult = await HttpContext.AuthenticateAsync(DashJwtConstants.SchemeName);
        var userIdClaim = authResult.Principal?.FindFirst(DashJwtConstants.UserIdClaim)?.Value;

        if (!ulong.TryParse(userIdClaim, out var userId) || !creds.IsOwner(userId))
        {
            context.Result = Forbid();
            return;
        }

        actorId = userId;
        await next();
    }

    /// <summary>
    ///     Lists the pending entries and open appeals.
    /// </summary>
    [HttpGet("queue")]
    public async Task<IActionResult> GetQueue()
    {
        return Ok(await service.GetQueueAsync());
    }

    /// <summary>
    ///     Lists the live entries.
    /// </summary>
    /// <param name="userId">Only this user, if given.</param>
    [HttpGet("live")]
    public async Task<IActionResult> GetLive([FromQuery] ulong? userId = null)
    {
        return Ok(await service.GetLiveAsync(userId));
    }

    /// <summary>
    ///     Gets an entry with its reports, votes and appeals.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    [HttpGet("entries/{entryId:int}")]
    public async Task<IActionResult> GetEntry(int entryId)
    {
        var detail = await service.GetDetailAsync(entryId, null);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>
    ///     Gets a screenshot.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="imageId">The screenshot ID.</param>
    [HttpGet("entries/{entryId:int}/images/{imageId:int}")]
    public async Task<IActionResult> GetImage(int entryId, int imageId)
    {
        var bytes = await service.GetImageAsync(entryId, imageId);
        if (bytes is null)
            return NotFound();

        return Ok(new GlobalListImageResponse
        {
            Image = "data:image/png;base64," + Convert.ToBase64String(bytes)
        });
    }

    /// <summary>
    ///     Approves or rejects an entry. Owners can overturn an earlier decision.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="request">The decision.</param>
    [HttpPost("entries/{entryId:int}/review")]
    public async Task<IActionResult> Review(int entryId, [FromBody] GlobalListReviewRequest request)
    {
        auditContext.RecordBefore(new
        {
            entryId
        });
        var (error, entry) = await service.ReviewAsync(entryId, request.Approve, request.Note, actorId, true,
            request.Category);
        if (error != ReviewError.None)
            return Failure(error);

        auditContext.RecordAfter(new
        {
            entryId, entry!.Status, entry.UserId
        });
        return Ok();
    }

    /// <summary>
    ///     Removes a live entry.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="request">The note.</param>
    [HttpPost("entries/{entryId:int}/remove")]
    public async Task<IActionResult> Remove(int entryId, [FromBody] GlobalListNoteRequest request)
    {
        auditContext.RecordBefore(new
        {
            entryId
        });
        var (error, entry) = await service.RemoveAsync(entryId, request.Note, actorId);
        if (error != ReviewError.None)
            return Failure(error);

        auditContext.RecordAfter(new
        {
            entryId, entry!.Status, entry.UserId
        });
        return Ok();
    }

    /// <summary>
    ///     Approves or rejects an appeal. Approving removes the entry.
    /// </summary>
    /// <param name="appealId">The appeal ID.</param>
    /// <param name="request">The decision.</param>
    [HttpPost("appeals/{appealId:int}/review")]
    public async Task<IActionResult> ReviewAppeal(int appealId, [FromBody] GlobalListReviewRequest request)
    {
        auditContext.RecordBefore(new
        {
            appealId
        });
        var error = await service.ReviewAppealAsync(appealId, request.Approve, request.Note, actorId);
        if (error != ReviewError.None)
            return Failure(error);

        auditContext.RecordAfter(new
        {
            appealId, request.Approve
        });
        return Ok();
    }

    /// <summary>
    ///     Lists the appointed reviewers.
    /// </summary>
    [HttpGet("reviewers")]
    public async Task<IActionResult> GetReviewers()
    {
        return Ok(await service.GetReviewersAsync());
    }

    /// <summary>
    ///     Appoints a reviewer.
    /// </summary>
    /// <param name="request">The user.</param>
    [HttpPost("reviewers")]
    public async Task<IActionResult> AddReviewer([FromBody] GlobalListReviewerRequest request)
    {
        if (request.UserId == 0)
            return BadRequest("InvalidUser");

        auditContext.RecordBefore(null);
        if (!await service.AddReviewerAsync(request.UserId, actorId))
            return BadRequest("AlreadyReviewer");

        auditContext.RecordAfter(new
        {
            request.UserId
        });
        return Ok(await service.GetReviewersAsync());
    }

    /// <summary>
    ///     Removes a reviewer.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    [HttpDelete("reviewers/{userId}")]
    public async Task<IActionResult> RemoveReviewer(ulong userId)
    {
        auditContext.RecordBefore(new
        {
            userId
        });
        if (!await service.RemoveReviewerAsync(userId))
            return NotFound();

        return Ok(await service.GetReviewersAsync());
    }

    /// <summary>
    ///     Lists the communities that have a reporting record.
    /// </summary>
    [HttpGet("standing")]
    public async Task<IActionResult> GetStanding()
    {
        return Ok(await service.ListStandingsAsync());
    }

    /// <summary>
    ///     Clears a community's reporting record.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    [HttpPost("standing/{guildId}/reset")]
    public async Task<IActionResult> ResetStanding(ulong guildId)
    {
        auditContext.RecordBefore(new
        {
            guildId
        });
        if (!await service.ResetStandingAsync(guildId))
            return NotFound();

        return Ok(await service.ListStandingsAsync());
    }

    private IActionResult Failure(ReviewError error)
    {
        return error == ReviewError.NotFound ? NotFound() : BadRequest(error.ToString());
    }
}
