using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using DataModel;
using LinqToDB;
using Mewdeko.Common.Collections;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Database.Enums;
using Mewdeko.Modules.Chat_Triggers.Services;
using Mewdeko.Modules.Help.Services;
using Mewdeko.Modules.Permissions.Common;
using Mewdeko.Modules.Permissions.Services;
using Mewdeko.Services.Analytics;
using Mewdeko.Services.Impl;
using Mewdeko.Services.Settings;
using Mewdeko.Services.Strings;
using Microsoft.Extensions.DependencyInjection;

namespace Mewdeko.Services;

/// <summary>
///     Handles command parsing and execution, integrating with various services to process Discord interactions and
///     messages.
/// </summary>
public class CommandHandler : INService
{
    private const int GlobalCommandsCooldown = 750;

    private const float OneThousandth = 1.0f / 1000;

    /// <summary>
    ///     Services stuffs
    /// </summary>
    public readonly IServiceProvider Services;

    private readonly Mewdeko bot;
    private readonly BotConfigService bss;
    private readonly IDataCache cache;

    // ReSharper disable once NotAccessedField.Local
    private readonly Timer clearUsersOnShortCooldown;
    private readonly FluxerClient client;
    private readonly IAnalyticsCollector collector;
    private readonly CommandService commandService;
    private readonly IDataConnectionFactory dbFactory;
    private readonly GuildSettingsService gss;
    private readonly Localization localization;
    private readonly ILogger<CommandHandler> logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandHandler" /> class.
    /// </summary>
    /// <param name="client">The Discord client.</param>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="commandService">The service for handling commands.</param>
    /// <param name="bss">The bot configuration service.</param>
    /// <param name="bot">The bot instance.</param>
    /// <param name="services">The service provider for dependency injection.</param>
    /// <param name="gss">The guild settings service.</param>
    /// <param name="eventHandler">The event handler for discord events.</param>
    /// <param name="cache">The data cache service.</param>
    /// <param name="logger">The logger instance for structured logging.</param>
    /// <param name="collector">The analytics collector that records command executions.</param>
    /// <param name="localization">Resolves the guild locale recorded with each command.</param>
    public CommandHandler(FluxerClient client, IDataConnectionFactory dbFactory, CommandService commandService,
        BotConfigService bss, Mewdeko bot, IServiceProvider services,
        GuildSettingsService gss, EventHandler eventHandler, IDataCache cache, ILogger<CommandHandler> logger,
        IAnalyticsCollector collector, Localization localization)
    {
        this.gss = gss;
        this.cache = cache;
        this.logger = logger;
        this.collector = collector;
        this.localization = localization;
        this.client = client;
        this.commandService = commandService;
        this.bss = bss;
        this.bot = bot;
        this.dbFactory = dbFactory;
        this.Services = services;
        clearUsersOnShortCooldown = new Timer(_ => UsersOnShortCooldown.Clear(), null, GlobalCommandsCooldown,
            GlobalCommandsCooldown);
        eventHandler.MessageReceived.Subscribe("CommandHandler", MessageReceivedHandler);
    }

    /// <summary>
    ///     Nonces of messages the bot sent through <see cref="ExecuteExternal" />, which run as commands even though
    ///     the bot wrote them.
    /// </summary>
    private NonBlocking.ConcurrentDictionary<string, byte> ExternalCommandNonces { get; } = new();

    /// <summary>
    ///     A thread-safe dictionary mapping channel IDs to command parse queues.
    /// </summary>
    private NonBlocking.ConcurrentDictionary<ulong, ConcurrentQueue<SocketMessage>> CommandParseQueue { get; } = new();

    /// <summary>
    ///     A thread-safe dictionary indicating whether a command parse lock is active for a channel.
    /// </summary>
    private NonBlocking.ConcurrentDictionary<ulong, bool> CommandParseLock { get; } = new();

    private ConcurrentHashSet<ulong> UsersOnShortCooldown { get; } = [];

    /// <summary>
    ///     Event that occurs when a command is executed.
    /// </summary>
    public event Func<Message, CommandInfo, Task> CommandExecuted = delegate { return Task.CompletedTask; };

    /// <summary>
    ///     Event that occurs when a command is errored.w
    /// </summary>
    public event Func<CommandInfo, Channel, string, IUser?, Task> CommandErrored = delegate
    {
        return Task.CompletedTask;
    };

    /// <summary>
    ///     Used for xp, for some reason.
    /// </summary>
    public event Func<Message, Task> OnMessageNoTrigger = delegate { return Task.CompletedTask; };

    /// <summary>
    ///     Executes an external command within a specific guild and channel context.
    /// </summary>
    /// <param name="guildId">The ID of the guild.</param>
    /// <param name="channelId">The ID of the channel.</param>
    /// <param name="commandText">The text of the command to execute.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ExecuteExternal(ulong? guildId, ulong channelId, string commandText)
    {
        if (guildId != null)
        {
            var guild = client.GetGuild(guildId.Value);
            if (guild?.GetChannel(channelId) is not Channel channel)
            {
                logger.LogWarning("Channel for external execution not found");
                return;
            }

            try
            {
                var nonce = Guid.NewGuid().ToString("N");
                ExternalCommandNonces[nonce] = 0;
                await channel.SendMessageAsync(commandText, nonce: nonce).ConfigureAwait(false);
            }
            catch
            {
                //exclude
            }
        }
    }

    private async Task MessageReceivedHandler(SocketMessage usrMsg)
    {
        try
        {
            var isExternal = usrMsg.Nonce is { } nonce && ExternalCommandNonces.TryRemove(nonce, out _);
            if ((usrMsg.Author.IsBot && !isExternal) ||
                !bot.Ready.Task.IsCompleted) //no bots, wait until bot connected and initialized
            {
                return;
            }

            AddCommandToParseQueue(usrMsg);
            await ExecuteCommandsInChannelAsync(usrMsg.Channel.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error in CommandHandler");
            if (ex.InnerException != null)
                logger.LogWarning(ex.InnerException, "Inner Exception of the error in CommandHandler");
        }
    }

    /// <summary>
    ///     Adds a command to the parse queue for a given channel.
    /// </summary>
    /// <param name="usrMsg">The user message to add to the queue.</param>
    public void AddCommandToParseQueue(SocketMessage usrMsg)
    {
        CommandParseQueue.AddOrUpdate(usrMsg.Channel.Id,
            _ => new ConcurrentQueue<SocketMessage>(new List<SocketMessage>
            {
                usrMsg
            }), (_, y) =>
            {
                y.Enqueue(usrMsg);
                return y;
            });
    }

    /// <summary>
    ///     Attempts to execute commands in the parse queue for a given channel.
    /// </summary>
    /// <param name="channelId">The ID of the channel.</param>
    /// <returns>A task that represents the asynchronous operation, returning true if commands were executed.</returns>
    public async Task<bool> ExecuteCommandsInChannelAsync(ulong channelId)
    {
        if (CommandParseLock.GetValueOrDefault(channelId, false) ||
            CommandParseQueue.GetValueOrDefault(channelId)?.IsEmpty != false)
            return false;

        CommandParseLock[channelId] = true;
        try
        {
            while (CommandParseQueue[channelId].TryDequeue(out var msg))
            {
                try
                {
                    await TryRunCommand(msg.Guild, msg.Channel, msg).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    collector.Error(e, "CommandHandler.ExecuteCommandsInChannelAsync", null,
                        msg.Channel.GuildId);
                    logger.LogError("Error occured in the handler: {E}", e);
                }
            }

            CommandParseQueue[channelId] = new ConcurrentQueue<SocketMessage>();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CommandParseLock[channelId] = false;
        }
    }

    private async Task TryRunCommand(SocketGuild? guild, Channel channel, SocketMessage usrMsg)
    {
        var execTime = Environment.TickCount;

        var lateExecutors = Services.GetServices<ILateExecutor>();
        var inputTransformers = Services.GetServices<IInputTransformer>();
        var earlyBehaviors = Services.GetServices<IEarlyBehavior>().ToArray();

        foreach (var beh in earlyBehaviors)
        {
            if (!await beh.RunBehavior(client, guild, usrMsg).ConfigureAwait(false)) continue;
            if (ShouldLogEarlyBehavior(beh))
            {
                logger.LogInformation(
                    "Executed {BehaviorType} behavior: {BehaviorName} for user: {User} in: {Guild}",
                    beh.BehaviorType, beh.GetType().Name, $"{usrMsg.Author} | {usrMsg.Id}", $"{guild} | {guild.Id}");
            }

            return;
        }

        var messageContent = usrMsg.Content;
        foreach (var exec in inputTransformers)
        {
            messageContent = await exec.TransformInput(guild, usrMsg.Channel, usrMsg.Author, messageContent)
                .ConfigureAwait(false);
            if (messageContent != usrMsg.Content) break;
        }

        var prefix = await gss.GetPrefix(guild);
        if (prefix == null) return;

        var prefixLength = GetPrefixLength(messageContent, prefix);
        if (prefixLength == 0)
        {
            OnMessageNoTrigger?.Invoke(usrMsg).ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var (success, error, info, errorClass) = await ExecuteCommandAsync(new CommandContext(client, usrMsg),
            messageContent, prefixLength, MultiMatchHandling.Best).ConfigureAwait(false);
        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        execTime = Environment.TickCount - execTime;

        var optedOut = await UpdateCommandStats(guild, channel, usrMsg, info).ConfigureAwait(false);
        if (info is not null)
        {
            var socketGuild = guild as SocketGuild;
            collector.Command(new CommandSample("text", info.Module.Name, info.Name, success,
                success ? null : errorClass, success ? null : error, durationMs, null, guild?.Id,
                socketGuild?.MemberCount, socketGuild is null ? null : client.GetShardIdFor(socketGuild),
                localization.GetCultureInfo(guild?.Id)?.Name, optedOut));
        }

        if (success)
        {
            await LogCommandExecution(usrMsg, channel as Channel, info, true, execTime).ConfigureAwait(false);
            await CommandExecuted(usrMsg, info).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(error))
        {
            if (info is null)
            {
                var serv = Services.GetRequiredService<HelpService>();
                if (channel is Channel)
                    await serv.BadCommand(client, null, usrMsg);
                return;
            }

            await LogCommandExecution(usrMsg, channel as Channel, info, false, execTime, error)
                .ConfigureAwait(false);
            if (guild != null)
            {
                var permissionService = Services.GetService<PermissionService>();
                var pc = await permissionService.GetCacheFor(guild.Id).ConfigureAwait(false);
                if (pc?.Permissions.CheckPermissions(usrMsg, info?.Name, info?.Module.Name, out _) ?? true)
                    await CommandErrored(info, channel as Channel, error, usrMsg.Author).ConfigureAwait(false);
            }
        }

        foreach (var exec in lateExecutors)
        {
            await exec.LateExecute(client, guild, usrMsg).ConfigureAwait(false);
        }
    }

    private bool ShouldLogEarlyBehavior(IEarlyBehavior beh)
    {
        return beh switch
        {
            BlacklistService => bss.Data.LogBlacklistedAttempts,
            FilterService => bss.Data.LogFilteredMessages,
            ChatTriggersService => bss.Data.LogChatTriggerFires,
            _ => true
        };
    }

    private async Task<(bool Success, string Error, CommandInfo? Info, string? ErrorClass)> ExecuteCommandAsync(
        CommandContext context, string input, int argPos,
        MultiMatchHandling multiMatchHandling = MultiMatchHandling.Exception)
    {
        var searchResult = commandService.Search(context, input[argPos..]);
        if (!searchResult.IsSuccess)
            return (false, searchResult.ErrorReason, null, searchResult.Error?.ToString());

        var lateBlockers = Services.GetServices<ILateBlocker>().ToArray();

        var commands = searchResult.Commands;
        var preconditionResults = await Task.WhenAll(commands.Select(async match =>
            (match, await match.Command.CheckPreconditionsAsync(context, Services).ConfigureAwait(false))));

        var successfulPreconditions = preconditionResults.Where(x => x.Item2.IsSuccess).ToArray();

        if (successfulPreconditions.Length == 0)
        {
            var bestCandidate = preconditionResults
                .OrderByDescending(x => x.match.Command.Priority)
                .FirstOrDefault(x => !x.Item2.IsSuccess);
            return (false, bestCandidate.Item2.ErrorReason, commands[0].Command,
                bestCandidate.Item2.Error?.ToString());
        }

        var parseResults = await Task.WhenAll(successfulPreconditions.Select(async x =>
        {
            var parseResult = await x.match.ParseAsync(context, searchResult, x.Item2, Services).ConfigureAwait(false);
            return (x.match, parseResult);
        }));

        var successfulParses = parseResults
            .Where(x => x.parseResult.IsSuccess)
            .OrderByDescending(x => x.match.Command.Priority)
            .ThenByDescending(x => x.parseResult.ArgValues.Sum(y => y.Values.Sum(z => z.Score)))
            .ToArray();

        if (successfulParses.Length == 0)
        {
            var bestMatch = parseResults.FirstOrDefault(x => !x.parseResult.IsSuccess);
            return (false, bestMatch.parseResult.ErrorReason, commands[0].Command,
                bestMatch.parseResult.Error?.ToString());
        }

        var cmd = successfulParses[0].match.Command;

        if (!UsersOnShortCooldown.Add(context.User.Id))
            return (false, "You are on a short cooldown.", cmd, "Cooldown");

        var chosenOverload = successfulParses[0];

        foreach (var i in lateBlockers)
        {
            var blocked = await i.TryBlockLate(client, context,
                chosenOverload.match.Command.Module.GetTopLevelModule().Name,
                chosenOverload.match.Command);
            if (blocked)
                return (false, "lateblocker", null, "Blocked");
        }

        var result = await chosenOverload.match.ExecuteAsync(context, chosenOverload.parseResult, Services)
            .ConfigureAwait(false);

        if (result is not ExecuteResult executeResult)
            return (result.IsSuccess, result.ErrorReason, cmd, result.Error?.ToString());
        if (executeResult.Exception != null && executeResult.Exception is not FluxerApiException
            {
                ErrorCode: FluxerErrorCodes.MissingPermissions
            })
        {
            var guild = context.Guild as SocketGuild;
            collector.Error(executeResult.Exception, "CommandHandler.ExecuteCommandAsync", cmd.Module.Name,
                guild?.Id, guild is null ? null : client.GetShardIdFor(guild));
            logger.LogWarning(executeResult.Exception, "Command execution error");
        }

        return (executeResult.IsSuccess, executeResult.ErrorReason, cmd,
            executeResult.Exception is null
                ? executeResult.Error?.ToString()
                : Innermost(executeResult.Exception).GetType().Name);
    }

    private async Task LogCommandExecution(Message usrMsg, Channel? channel, CommandInfo? commandInfo,
        bool success, int executionTime, string errorMessage = null)
    {
        var logBuilder = new StringBuilder()
            .AppendLine(success ? "Command Executed" : "Command Errored")
            .AppendLine($"User: {usrMsg.Author} [{usrMsg.Author.Id}]")
            .AppendLine($"Server: {(channel == null ? "PRIVATE" : $"{channel.Guild.Name} [{channel.Guild.Id}]")}")
            .AppendLine($"Channel: {(channel == null ? "PRIVATE" : $"{channel.Name} [{channel.Id}]")}")
            .AppendLine($"Message: {usrMsg.Content}")
            .AppendLine($"Execution Time: {executionTime}ms");

        if (!success && !string.IsNullOrEmpty(errorMessage))
            logBuilder.AppendLine($"Error: {errorMessage}");

        if (bss.Data.LogCommandExecutions)
        {
            if (success)
                logger.LogInformation(logBuilder.ToString());
            else
                logger.LogWarning(logBuilder.ToString());
        }


        var embed = new EmbedBuilder()
            .WithColor(success ? Mewdeko.OkColor : Mewdeko.ErrorColor)
            .WithTitle(success ? "Command Executed" : "Command Errored")
            .AddField("User", $"{usrMsg.Author.Mention} {usrMsg.Author} {usrMsg.Author.Id}")
            .AddField("Guild", channel == null ? "PRIVATE" : $"{channel.Guild.Name} `{channel.Guild.Id}`")
            .AddField("Channel", channel == null ? "PRIVATE" : $"{channel.Name} `{channel.Id}`")
            .AddField("Message", usrMsg.Content.TrimTo(1000))
            .AddField("Execution Time", $"{executionTime}ms");

        if (!success && !string.IsNullOrEmpty(errorMessage))
            embed.AddField("Error", errorMessage);

        if (commandInfo != null)
            embed.AddField("Command", $"{commandInfo.Module.Name} | {commandInfo.Name}");

        if (bss.Data.CommandLogChannel > 0)
        {
            if (await client.Rest.GetChannelAsync(bss.Data.CommandLogChannel) is Channel logChannel)
                await logChannel.SendAsync(embed: embed.Build()).ConfigureAwait(false);
        }

        if (channel?.GuildId is not { } guildId)
            return;

        var gc = await gss.GetGuildConfig(guildId);
        if (gc.CommandLogChannel != 0)
        {
            if (await client.Rest.GetChannelAsync(gc.CommandLogChannel) is Channel commandLogChannel)
                await commandLogChannel.SendAsync(embed: embed.Build()).ConfigureAwait(false);
        }
    }

    private static Exception Innermost(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
            current = current.InnerException;
        return current;
    }

    private int GetPrefixLength(string content, string prefix)
    {
        // Check if content starts with the custom prefix
        if (content.StartsWith(prefix, StringComparison.InvariantCulture))
            return prefix.Length;

        // Get possible mention formats
        var mentions = new[]
        {
            client.CurrentUser.Mention, // e.g., "@BotName"
            $"<@{client.CurrentUser.Id}>", // e.g., "<@1234567890>"
            $"<@!{client.CurrentUser.Id}>" // e.g., "<@!1234567890>" (for nicknames)
        };

        // Find the longest matching mention at the start
        return (from mention in mentions
            where content.StartsWith(mention + " ", StringComparison.InvariantCulture)
            select mention.Length + 1).FirstOrDefault();
    }

    private async Task<bool> UpdateCommandStats(SocketGuild? guild, Channel channel, Message usrMsg,
        CommandInfo? info)
    {
        if (guild == null || info == null) return false;

        var guildConfig = await gss.GetGuildConfig(guild.Id).ConfigureAwait(false);
        if (guildConfig.StatsOptOut) return true;

        await using var dbContext = await dbFactory.CreateConnectionAsync().ConfigureAwait(false);
        var user = await dbContext.GetOrCreateUser(usrMsg.Author).ConfigureAwait(false);
        if (user.StatsOptOut) return true;

        var commandStats = new CommandStat
        {
            ChannelId = channel.Id,
            GuildId = guild.Id,
            IsSlash = false,
            NameOrId = info.Name,
            UserId = usrMsg.Author.Id,
            Module = info.Module.Name
        };
        await dbContext.InsertAsync(commandStats).ConfigureAwait(false);
        return false;
    }
}