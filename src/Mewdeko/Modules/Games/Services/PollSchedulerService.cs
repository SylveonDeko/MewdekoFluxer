using System.Text.Json;
using System.Threading;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Modules.Games.Common;

namespace Mewdeko.Modules.Games.Services;

/// <summary>
/// Service that manages scheduled poll creation and execution.
/// </summary>
public class PollSchedulerService : INService, IReadyExecutor, IDisposable
{
    private readonly DiscordShardedClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ILogger<PollSchedulerService> logger;
    private readonly PollMessageService pollMessages;
    private readonly PollService pollService;
    private readonly Timer schedulerTimer;
    private readonly SemaphoreSlim semaphore = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the PollSchedulerService class.
    /// </summary>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="pollService">The poll service.</param>
    /// <param name="pollMessages">The service that posts poll messages.</param>
    /// <param name="client">The Discord client.</param>
    /// <param name="logger">The logger instance.</param>
    public PollSchedulerService(IDataConnectionFactory dbFactory, PollService pollService,
        PollMessageService pollMessages,
        DiscordShardedClient client, ILogger<PollSchedulerService> logger)
    {
        this.dbFactory = dbFactory;
        this.pollService = pollService;
        this.pollMessages = pollMessages;
        this.client = client;
        this.logger = logger;

        // Check for scheduled polls every minute
        schedulerTimer = new Timer(ProcessScheduledPolls, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Starts the scheduler service when the bot is ready.
    /// </summary>
    public async Task OnReadyAsync()
    {
        logger.LogInformation("Poll scheduler service is ready");
        await Task.CompletedTask;
        // Perform an initial check for scheduled polls
        ProcessScheduledPolls(null);
    }

    /// <summary>
    /// Schedules a poll to be created at a specific time.
    /// </summary>
    /// <param name="guildId">The Discord guild ID.</param>
    /// <param name="channelId">The Discord channel ID.</param>
    /// <param name="creatorId">The Discord user ID of the creator.</param>
    /// <param name="question">The poll question.</param>
    /// <param name="options">The poll options.</param>
    /// <param name="pollType">The poll type.</param>
    /// <param name="settings">The poll settings.</param>
    /// <param name="scheduledFor">When to create the poll.</param>
    /// <param name="durationMinutes">Poll duration in minutes.</param>
    /// <returns>The scheduled poll entity.</returns>
    public async Task<ScheduledPoll> SchedulePollAsync(ulong guildId, ulong channelId, ulong creatorId,
        string question, List<PollOptionData> options, PollType pollType, PollSettings settings,
        DateTime scheduledFor, int? durationMinutes = null)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            var scheduledPoll = new ScheduledPoll
            {
                GuildId = guildId,
                ChannelId = channelId,
                CreatorId = creatorId,
                Question = question,
                Options = JsonSerializer.Serialize(options),
                Type = (int)pollType,
                Settings = JsonSerializer.Serialize(settings),
                ScheduledFor = scheduledFor,
                DurationMinutes = durationMinutes,
                ScheduledAt = DateTime.UtcNow
            };

            scheduledPoll.Id = await db.InsertWithInt32IdentityAsync(scheduledPoll);

            logger.LogInformation("Scheduled poll {ScheduledPollId} for {ScheduledFor} in guild {GuildId}",
                scheduledPoll.Id, scheduledFor, guildId);

            return scheduledPoll;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule poll for guild {GuildId}", guildId);
            throw;
        }
    }

    /// <summary>
    /// Gets all scheduled polls for a guild.
    /// </summary>
    /// <param name="guildId">The Discord guild ID.</param>
    /// <param name="includeCancelled">Whether to include cancelled polls.</param>
    /// <param name="includeExecuted">Whether to include executed polls.</param>
    /// <returns>List of scheduled polls.</returns>
    public async Task<List<ScheduledPoll>> GetScheduledPollsAsync(ulong guildId,
        bool includeCancelled = false, bool includeExecuted = false)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            var query = db.ScheduledPolls
                .Where(sp => sp.GuildId == guildId);

            if (!includeCancelled)
                query = query.Where(sp => !sp.IsCancelled);

            if (!includeExecuted)
                query = query.Where(sp => !sp.IsExecuted);

            return await query.OrderBy(sp => sp.ScheduledFor).ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get scheduled polls for guild {GuildId}", guildId);
            return new List<ScheduledPoll>();
        }
    }

    /// <summary>
    /// Cancels a scheduled poll.
    /// </summary>
    /// <param name="scheduledPollId">The ID of the scheduled poll.</param>
    /// <param name="cancelledBy">The Discord user ID of who cancelled it.</param>
    /// <returns>True if the poll was successfully cancelled.</returns>
    public async Task<bool> CancelScheduledPollAsync(int scheduledPollId, ulong cancelledBy)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            var scheduledPoll = await db.GetTable<ScheduledPoll>()
                .FirstOrDefaultAsync(sp => sp.Id == scheduledPollId);

            if (scheduledPoll == null || scheduledPoll.IsExecuted || scheduledPoll.IsCancelled)
                return false;

            scheduledPoll.IsCancelled = true;
            scheduledPoll.CancelledAt = DateTime.UtcNow;
            scheduledPoll.CancelledBy = cancelledBy;

            await db.UpdateAsync(scheduledPoll);

            logger.LogInformation("Cancelled scheduled poll {ScheduledPollId} by user {UserId}",
                scheduledPollId, cancelledBy);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel scheduled poll {ScheduledPollId}", scheduledPollId);
            return false;
        }
    }

    /// <summary>
    /// Gets a scheduled poll by ID.
    /// </summary>
    /// <param name="scheduledPollId">The scheduled poll ID.</param>
    /// <returns>The scheduled poll or null if not found.</returns>
    public async Task<ScheduledPoll?> GetScheduledPollAsync(int scheduledPollId)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            return await db.GetTable<ScheduledPoll>()
                .FirstOrDefaultAsync(sp => sp.Id == scheduledPollId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get scheduled poll {ScheduledPollId}", scheduledPollId);
            return null;
        }
    }

    /// <summary>
    /// Timer callback that processes scheduled polls that are ready to be created.
    /// </summary>
    /// <param name="state">Timer state (unused).</param>
    private async void ProcessScheduledPolls(object? state)
    {
        if (!await semaphore.WaitAsync(100)) // Don't block if already processing
            return;

        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            var now = DateTime.UtcNow;
            var duePolls = await db.GetTable<ScheduledPoll>()
                .Where(sp => !sp.IsExecuted && !sp.IsCancelled && sp.ScheduledFor <= now)
                .ToListAsync();

            if (duePolls.Count == 0)
                return;

            logger.LogInformation("Found {Count} scheduled polls ready for execution", duePolls.Count);

            foreach (var scheduledPoll in duePolls)
            {
                try
                {
                    await ExecuteScheduledPoll(scheduledPoll);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to execute scheduled poll {ScheduledPollId}", scheduledPoll.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during scheduled poll processing");
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Executes a scheduled poll by creating the actual poll.
    /// </summary>
    /// <param name="scheduledPoll">The scheduled poll to execute.</param>
    private async Task ExecuteScheduledPoll(ScheduledPoll scheduledPoll)
    {
        try
        {
            // Deserialize the stored data
            var options = JsonSerializer.Deserialize<List<PollOptionData>>(scheduledPoll.Options);
            var settings = !string.IsNullOrEmpty(scheduledPoll.Settings)
                ? JsonSerializer.Deserialize<PollSettings>(scheduledPoll.Settings)
                : new PollSettings();

            if (options == null)
            {
                logger.LogWarning("Scheduled poll {ScheduledPollId} has invalid options data", scheduledPoll.Id);
                await MarkScheduledPollAsExecuted(scheduledPoll.Id, null);
                return;
            }

            // Verify the guild and channel still exist
            var guild = client.GetGuild(scheduledPoll.GuildId);
            if (guild == null)
            {
                logger.LogWarning("Guild {GuildId} not found for scheduled poll {ScheduledPollId}",
                    scheduledPoll.GuildId, scheduledPoll.Id);
                await MarkScheduledPollAsExecuted(scheduledPoll.Id, null);
                return;
            }

            var channel = guild.GetTextChannel(scheduledPoll.ChannelId);
            if (channel == null)
            {
                logger.LogWarning("Channel {ChannelId} not found for scheduled poll {ScheduledPollId}",
                    scheduledPoll.ChannelId, scheduledPoll.Id);
                await MarkScheduledPollAsExecuted(scheduledPoll.Id, null);
                return;
            }

            settings ??= new PollSettings();
            var problem = await pollMessages.GetPostProblemAsync(channel, settings, options);
            if (problem is not null)
            {
                logger.LogWarning("Scheduled poll {ScheduledPollId} cannot be posted: {Problem}", scheduledPoll.Id,
                    problem);
                await MarkScheduledPollAsExecuted(scheduledPoll.Id, null);
                return;
            }

            var poll = await pollMessages.PostPollAsync(channel, scheduledPoll.CreatorId, scheduledPoll.Question,
                options, (PollType)scheduledPoll.Type, settings);

            if (scheduledPoll.DurationMinutes.HasValue)
            {
                await pollService.SetPollExpirationAsync(poll.Id,
                    TimeSpan.FromMinutes(scheduledPoll.DurationMinutes.Value));
                await pollMessages.RefreshPollMessageAsync(poll.Id);
            }

            // Mark the scheduled poll as executed
            await MarkScheduledPollAsExecuted(scheduledPoll.Id, poll.Id);

            logger.LogInformation("Successfully executed scheduled poll {ScheduledPollId}, created poll {PollId}",
                scheduledPoll.Id, poll.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute scheduled poll {ScheduledPollId}", scheduledPoll.Id);
            await MarkScheduledPollAsExecuted(scheduledPoll.Id, null);
        }
    }

    /// <summary>
    /// Marks a scheduled poll as executed.
    /// </summary>
    /// <param name="scheduledPollId">The scheduled poll ID.</param>
    /// <param name="createdPollId">The created poll ID (if successful).</param>
    private async Task MarkScheduledPollAsExecuted(int scheduledPollId, int? createdPollId)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            var scheduledPoll = await db.GetTable<ScheduledPoll>()
                .FirstOrDefaultAsync(sp => sp.Id == scheduledPollId);

            if (scheduledPoll != null)
            {
                scheduledPoll.IsExecuted = true;
                scheduledPoll.ExecutedAt = DateTime.UtcNow;
                scheduledPoll.CreatedPollId = createdPollId;

                await db.UpdateAsync(scheduledPoll);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark scheduled poll {ScheduledPollId} as executed", scheduledPollId);
        }
    }

    /// <summary>
    /// Disposes the timer when the service is disposed.
    /// </summary>
    public void Dispose()
    {
        schedulerTimer.Dispose();
        semaphore.Dispose();
    }
}
