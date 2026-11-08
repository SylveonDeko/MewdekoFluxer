using DataModel;
using Mewdeko.Controllers.Common.DashboardAccess;
using Mewdeko.Controllers.Common.GlobalList;
using Mewdeko.Database.Enums;
using Mewdeko.Modules.GlobalList.Common;
using Mewdeko.Modules.GlobalList.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mewdeko.Controllers;

/// <summary>
///     API Controller for a community's global list settings, reports and votes.
/// </summary>
[ApiController]
[Route("botapi/[controller]/{guildId}")]
[Authorize("ApiKeyPolicy")]
public class GlobalListController(
    GlobalListService service,
    FluxerClient client,
    DashboardAccessService dashboardAccessService,
    IDashboardAuditContext auditContext) : Controller
{
    private const string Section = "GlobalList";

    /// <summary>
    ///     Gets the community's settings and reporting standing.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    [HttpGet]
    public async Task<IActionResult> Get(ulong guildId)
    {
        if (client.GetGuild(guildId) is null)
            return NotFound("Guild not found.");

        return Ok(new GlobalListOverview
        {
            Settings = await service.GetSettingsAsync(guildId), Standing = await service.GetStandingAsync(guildId)
        });
    }

    /// <summary>
    ///     Changes the community's settings.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="patch">The change.</param>
    [HttpPost]
    public async Task<IActionResult> Update(ulong guildId, [FromBody] GlobalListSettingsPatch? patch)
    {
        if (patch is null)
            return BadRequest("InvalidBody");

        var guild = client.GetGuild(guildId);
        if (guild is null)
            return NotFound("Guild not found.");

        if (patch.LogChannelId is { } channel && channel != 0 && guild.GetTextChannel(channel) is null)
            return BadRequest("InvalidChannel");

        if (patch.QuarantineRoleId is { } role && role != 0 && guild.GetRole(role) is null)
            return BadRequest("InvalidRole");

        auditContext.RecordBefore(await service.GetSettingsAsync(guildId));
        var updated = await service.UpdateSettingsAsync(guildId, patch);
        auditContext.RecordAfter(updated);
        return Ok(new GlobalListOverview
        {
            Settings = updated, Standing = await service.GetStandingAsync(guildId)
        });
    }

    /// <summary>
    ///     Reports a user. Only members with moderation access can.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="request">The report.</param>
    [HttpPost("reports")]
    public async Task<IActionResult> Report(ulong guildId, [FromBody] GlobalListReportRequest request)
    {
        if (!await IsStaffAsync(guildId))
            return Forbid();

        var screenshots = new List<byte[]>();
        foreach (var data in request.Screenshots.Take(GlobalListRules.MaxScreenshots + 1))
        {
            if (GlobalListService.DecodeImage(data) is not { } bytes)
                return BadRequest(ReportError.InvalidImage.ToString());

            screenshots.Add(bytes);
        }

        auditContext.RecordBefore(null);
        var (error, entryId) = await service.ReportAsync(guildId, request.TargetUserId, request.Category,
            request.Reason, request.Links, screenshots);
        if (error != ReportError.None)
            return BadRequest(error.ToString());

        auditContext.RecordAfter(new
        {
            entryId, request.TargetUserId, request.Category
        });
        return Ok(new
        {
            entryId
        });
    }

    /// <summary>
    ///     Lists the pending entries, with the ones this community reported marked.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    [HttpGet("open")]
    public async Task<IActionResult> GetOpen(ulong guildId)
    {
        if (!(await service.GetSettingsAsync(guildId)).Enabled)
            return Ok(new List<GlobalListEntryItem>());

        return Ok(await service.GetOpenAsync(guildId));
    }

    /// <summary>
    ///     Gets a pending entry with its evidence and votes.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="entryId">The entry ID.</param>
    [HttpGet("entries/{entryId:int}")]
    public async Task<IActionResult> GetEntry(ulong guildId, int entryId)
    {
        var detail = await service.GetDetailAsync(entryId, guildId);
        if (detail is null || detail.Entry.Status != GlobalListStatus.Pending ||
            !(await service.GetSettingsAsync(guildId)).Enabled)
            return NotFound();

        detail.Appeals = [];
        return Ok(detail);
    }

    /// <summary>
    ///     Gets a screenshot from a pending entry.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="imageId">The screenshot ID.</param>
    [HttpGet("entries/{entryId:int}/images/{imageId:int}")]
    public async Task<IActionResult> GetImage(ulong guildId, int entryId, int imageId)
    {
        var detail = await service.GetDetailAsync(entryId, guildId);
        if (detail is null || detail.Entry.Status != GlobalListStatus.Pending ||
            !(await service.GetSettingsAsync(guildId)).Enabled)
            return NotFound();

        var bytes = await service.GetImageAsync(entryId, imageId);
        if (bytes is null)
            return NotFound();

        return Ok(new GlobalListImageResponse
        {
            Image = "data:image/png;base64," + Convert.ToBase64String(bytes)
        });
    }

    /// <summary>
    ///     Votes on a pending entry. Only staff of other opted-in communities can, once each.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="request">The vote.</param>
    [HttpPost("entries/{entryId:int}/vote")]
    public async Task<IActionResult> Vote(ulong guildId, int entryId, [FromBody] GlobalListVoteRequest request)
    {
        if (!await IsStaffAsync(guildId))
            return Forbid();

        auditContext.RecordBefore(null);
        var error = await service.VoteAsync(guildId, entryId, request.Confirm, request.Note);
        if (error != VoteError.None)
            return BadRequest(error.ToString());

        auditContext.RecordAfter(new
        {
            entryId, request.Confirm
        });
        return Ok();
    }

    private async Task<bool> IsStaffAsync(ulong guildId)
    {
        var userId = await HttpContext.GetDashboardUserIdAsync();
        var guild = client.GetGuild(guildId);
        var member = userId is { } id ? guild?.GetUser(id) : null;
        if (guild is null || member is null || userId is not { } actor)
            return false;

        if (GlobalListService.IsStaff(guild, member))
            return true;

        return await dashboardAccessService.GetSectionAccessAsync(guildId, actor,
            member.Roles.Select(role => role.Id).ToList(), Section) >= DashboardAccessLevel.Manage;
    }
}
