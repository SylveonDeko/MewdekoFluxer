using Mewdeko.Controllers.Common.Chat;
using Mewdeko.Modules.Utility.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

// ReSharper disable PossibleInvalidOperationException

namespace Mewdeko.Controllers;

/// <summary>
///     Controller for chat message management
/// </summary>
[ApiController]
[Route("botapi/[controller]/{guildId}")]
[Authorize("ApiKeyPolicy")]
public class ChatController(
    FluxerClient client,
    ChatLogService chatLogService,
    IDashboardAuditContext auditContext) : Controller
{
    /// <summary>
    ///     Gets chat messages from a channel within a specified time range
    /// </summary>
    [HttpGet("{channelId}/messages")]
    public async Task<IActionResult> GetChatMessages(ulong guildId, ulong channelId, [FromQuery] string after)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null) return NotFound("Guild not found");

        var channel = guild.GetTextChannel(channelId);
        if (channel == null) return NotFound("Channel not found");

        if (!DateTime.TryParse(after, out var afterDate))
            return BadRequest("Invalid date format");

        const int pageSize = 100;
        const int maxMessages = 5000;
        var messagesResult = new List<Message>();
        ulong? beforeId = null;
        while (messagesResult.Count < maxMessages)
        {
            var page = (await channel.GetMessagesAsync(pageSize, beforeId).FlattenAsync()).ToList();
            if (page.Count == 0) break;

            messagesResult.AddRange(page.Where(m => m.Timestamp >= afterDate));

            if (page.Count < pageSize || page.Min(m => m.Timestamp) < afterDate) break;

            beforeId = page.OrderBy(m => m.Timestamp).First().Id;
        }

        // Sort messages by timestamp (oldest first)
        messagesResult = messagesResult.OrderBy(m => m.Timestamp).ToList();

        var messageData = messagesResult.Select(m => new
        {
            Id = m.Id.ToString(),
            m.Content,
            Author = new
            {
                Id = m.Author.Id.ToString(),
                m.Author.Username,
                AvatarUrl = m.Author.GetAvatarOrDefaultUrl()
            },
            Timestamp = m.Timestamp.ToString("o"),
            Attachments = (m.Attachments ?? []).Select(a => new
            {
                a.Url, a.ProxyUrl, a.Filename, FileSize = a.Size
            }),
            Embeds = (m.Embeds ?? []).Select(e => new
            {
                Type = e.RawType,
                e.Title,
                e.Description,
                e.Url,
                Thumbnail = e.Thumbnail?.Url,
                Author = e.Author != null
                    ? new
                    {
                        e.Author.Name, e.Author.IconUrl
                    }
                    : null
            })
        });

        return Ok(messageData);
    }

    /// <summary>
    ///     Gets all saved chat logs for a guild
    /// </summary>
    [HttpGet("logs")]
    public async Task<IActionResult> GetChatLogs(ulong guildId)
    {
        // Check if guild exists
        var guild = client.GetGuild(guildId);
        if (guild == null) return NotFound("Guild not found");

        var logs = await chatLogService.GetChatLogsForGuildAsync(guildId);

        var response = logs.Select(l => new
        {
            Id = l.Id.ToString(),
            ChannelId = l.ChannelId.ToString(),
            l.ChannelName,
            l.Name,
            CreatedBy = l.CreatedBy.ToString(),
            Timestamp = l.DateAdded.Value.ToString("o"),
            l.MessageCount
        });

        return Ok(response);
    }

    /// <summary>
    ///     Gets a specific chat log by id
    /// </summary>
    [HttpGet("logs/{logId}")]
    public async Task<IActionResult> GetChatLog(ulong guildId, int logId)
    {
        // Check if guild exists
        var guild = client.GetGuild(guildId);
        if (guild == null) return NotFound("Guild not found");

        var log = await chatLogService.GetChatLogAsync(logId);
        if (log == null) return NotFound("Log not found");

        // Verify this log belongs to the requested guild
        if (log.GuildId != guildId)
            return Forbid("This log does not belong to the specified guild");

        var messages = JsonConvert.DeserializeObject<List<ChatLogMessageDto>>(log.Messages);

        var response = new
        {
            Id = log.Id.ToString(),
            GuildId = log.GuildId.ToString(),
            ChannelId = log.ChannelId.ToString(),
            log.ChannelName,
            log.Name,
            CreatedBy = log.CreatedBy.ToString(),
            Timestamp = log.DateAdded.Value.ToString("o"),
            log.MessageCount,
            Messages = messages
        };

        return Ok(response);
    }

    /// <summary>
    ///     Saves a chat log
    /// </summary>
    [HttpPost("logs")]
    public async Task<IActionResult> SaveChatLog(ulong guildId, [FromBody] SaveChatLogRequest? request)
    {
        if (request == null) return BadRequest("Invalid request data");

        // Check if guild exists
        var guild = client.GetGuild(guildId);
        if (guild == null) return NotFound("Guild not found");

        // Check if channel exists
        var channel = guild.GetTextChannel(request.ChannelId);
        if (channel == null) return NotFound("Channel not found");

        try
        {
            var logId = await chatLogService.SaveChatLogAsync(
                guildId,
                request.ChannelId,
                channel.Name,
                request.Name,
                request.CreatedBy,
                request.Messages
            );

            return Ok(new
            {
                Id = logId
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error saving chat log: {ex.Message}");
        }
    }

    /// <summary>
    ///     Updates a chat log's name
    /// </summary>
    [HttpPatch("logs/{logId}")]
    public async Task<IActionResult> UpdateChatLogName(ulong guildId, int logId,
        [FromBody] UpdateChatLogNameRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Invalid request data");

        // Check if log exists
        var log = await chatLogService.GetChatLogAsync(logId);
        if (log == null) return NotFound("Log not found");

        // Verify this log belongs to the requested guild
        if (log.GuildId != guildId)
            return Forbid("This log does not belong to the specified guild");

        auditContext.RecordBefore(log);
        await chatLogService.UpdateChatLogNameAsync(logId, request.Name);
        return Ok();
    }

    /// <summary>
    ///     Deletes a chat log
    /// </summary>
    [HttpDelete("logs/{logId}")]
    public async Task<IActionResult> DeleteChatLog(ulong guildId, int logId)
    {
        // Check if log exists
        var log = await chatLogService.GetChatLogAsync(logId);
        if (log == null) return NotFound("Log not found");

        // Verify this log belongs to the requested guild
        if (log.GuildId != guildId)
            return Forbid("This log does not belong to the specified guild");

        auditContext.RecordBefore(log);
        await chatLogService.DeleteChatLogAsync(logId);
        return Ok();
    }
}