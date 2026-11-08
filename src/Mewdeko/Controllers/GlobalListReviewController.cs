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
///     API for reviewing the global list, open to bot owners and appointed reviewers. Bot wide, not guild scoped.
/// </summary>
[ApiController]
[Route("botapi/[controller]")]
[Authorize("ApiKeyPolicy")]
public class GlobalListReviewController(
    GlobalListService service,
    BotCredentials creds,
    IDashboardAuditContext auditContext) : Controller
{
    private ulong actorId;
    private bool canReview;
    private bool isOwner;

    /// <summary>
    ///     Rejects anyone who is not a bot owner or an appointed reviewer.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <param name="next">The next action in the pipeline.</param>
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var authResult = await HttpContext.AuthenticateAsync(DashJwtConstants.SchemeName);
        var userIdClaim = authResult.Principal?.FindFirst(DashJwtConstants.UserIdClaim)?.Value;

        if (!ulong.TryParse(userIdClaim, out var userId))
        {
            context.Result = Forbid();
            return;
        }

        actorId = userId;
        isOwner = creds.IsOwner(userId);
        canReview = await service.CanReviewAsync(userId);
        var asksStatus = context.ActionDescriptor.RouteValues.TryGetValue("action", out var action) &&
                         action == nameof(GetMe);
        if (!canReview && !asksStatus)
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    /// <summary>
    ///     Says whether the signed-in user can review, and whether they are an owner.
    /// </summary>
    [HttpGet("me")]
    public IActionResult GetMe()
    {
        return Ok(new GlobalListReviewerStatus
        {
            IsReviewer = canReview, IsOwner = isOwner
        });
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
    ///     Approves or rejects a pending entry. Only owners can overturn an earlier decision.
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
        var (error, entry) = await service.ReviewAsync(entryId, request.Approve, request.Note, actorId, isOwner,
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

    private IActionResult Failure(ReviewError error)
    {
        return error == ReviewError.NotFound ? NotFound() : BadRequest(error.ToString());
    }
}
