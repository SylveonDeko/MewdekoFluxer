using System.Diagnostics;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Modules.Automations.Common;

namespace Mewdeko.Modules.Automations.Services;

/// <summary>
///     An automation with its conditions and actions already read from JSON.
/// </summary>
/// <param name="Row">The stored automation.</param>
/// <param name="Conditions">The conditions.</param>
/// <param name="Actions">The actions.</param>
public sealed record CompiledAutomation(Automation Row, IReadOnlyList<AutomationNode> Conditions,
    IReadOnlyList<AutomationNode> Actions);

/// <summary>
///     What came of saving an automation.
/// </summary>
/// <param name="Automation">The saved automation, or null when it was not saved.</param>
/// <param name="Errors">Everything wrong with it.</param>
/// <param name="NotFound">Whether the automation to change does not exist.</param>
public sealed record AutomationSaveResult(Automation? Automation, List<AutomationValidationError> Errors,
    bool NotFound = false);

/// <summary>
///     What came of creating automations from a template.
/// </summary>
/// <param name="Created">The automations created.</param>
/// <param name="Error">Why nothing was created.</param>
/// <param name="Detail">The parameter at fault, or the first validation problem.</param>
public sealed record AutomationTemplateOutcome(List<Automation> Created, AutomationTemplateError Error,
    string? Detail);

/// <summary>
///     Runs automations when their triggers happen, and stores them.
/// </summary>
public class AutomationService : INService
{
    private readonly FluxerClient client;
    private readonly ConcurrentDictionary<string, DateTime> cooldowns = new();
    private readonly IDataConnectionFactory dbFactory;
    private readonly ConcurrentDictionary<ulong, Lazy<Task<GuildAutomations>>> guilds = new();
    private readonly ILogger<AutomationService> logger;
    private readonly ConcurrentDictionary<ulong, Queue<DateTime>> runStarts = new();
    private readonly AutomationRunner runner;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AutomationService" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="eventHandler">The event handler.</param>
    /// <param name="runner">Performs the actions.</param>
    /// <param name="logger">The logger instance.</param>
    public AutomationService(FluxerClient client, IDataConnectionFactory dbFactory,
        EventHandler eventHandler, AutomationRunner runner, ILogger<AutomationService> logger)
    {
        this.client = client;
        this.dbFactory = dbFactory;
        this.runner = runner;
        this.logger = logger;

        const string module = "AutomationService";
        eventHandler.MessageReceived.Subscribe(module, OnMessageReceived);
        eventHandler.MessageUpdated.Subscribe(module, OnMessageUpdated);
        eventHandler.MessageDeleted.Subscribe(module, OnMessageDeleted);
        eventHandler.ReactionAdded.Subscribe(module, OnReactionAdded);
        eventHandler.ReactionRemoved.Subscribe(module, OnReactionRemoved);
        eventHandler.UserJoined.Subscribe(module, OnUserJoined);
        eventHandler.UserLeft.Subscribe(module, OnUserLeft);
        eventHandler.GuildMemberUpdated.Subscribe(module, OnGuildMemberUpdated);
        eventHandler.UserVoiceStateUpdated.Subscribe(module, OnVoiceStateUpdated);
        eventHandler.ThreadCreated.Subscribe(module, OnThreadCreated);
        eventHandler.LeftGuild.Subscribe(module, OnLeftGuild);
    }

    /// <summary>
    ///     Gets every automation of a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    public async Task<IReadOnlyList<Automation>> GetAllAsync(ulong guildId) =>
        (await LoadAsync(guildId)).All.Select(x => x.Row).ToList();

    /// <summary>
    ///     Gets one automation of a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="id">The automation ID.</param>
    public async Task<Automation?> GetAsync(ulong guildId, int id) =>
        (await LoadAsync(guildId)).All.FirstOrDefault(x => x.Row.Id == id)?.Row;

    /// <summary>
    ///     Creates an automation after checking it.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="creatorId">The ID of the member creating it.</param>
    /// <param name="input">The settings.</param>
    public async Task<AutomationSaveResult> CreateAsync(ulong guildId, ulong creatorId, AutomationInput input)
    {
        var errors = Check(guildId, input);
        if (errors.Count > 0)
            return new AutomationSaveResult(null, errors);

        var now = DateTime.UtcNow;
        var row = new Automation
        {
            GuildId = guildId, CreatorId = creatorId, DateAdded = now, DateModified = now
        };
        Apply(row, input);

        await using var db = await dbFactory.CreateConnectionAsync();
        row.Id = await db.InsertWithInt32IdentityAsync(row);
        await ReloadAsync(guildId);
        return new AutomationSaveResult(row, errors);
    }

    /// <summary>
    ///     Changes an automation after checking it.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="id">The automation ID.</param>
    /// <param name="input">The new settings.</param>
    public async Task<AutomationSaveResult> UpdateAsync(ulong guildId, int id, AutomationInput input)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.Automations.FirstOrDefaultAsync(x => x.GuildId == guildId && x.Id == id);
        if (row is null)
            return new AutomationSaveResult(null, [], true);

        var errors = Check(guildId, input);
        if (errors.Count > 0)
            return new AutomationSaveResult(null, errors);

        Apply(row, input);
        row.DateModified = DateTime.UtcNow;
        await db.UpdateAsync(row);
        await ReloadAsync(guildId);
        return new AutomationSaveResult(row, errors);
    }

    /// <summary>
    ///     Turns an automation on or off.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="id">The automation ID.</param>
    /// <param name="enabled">Whether it should run.</param>
    /// <returns>The automation, or null when it does not exist.</returns>
    public async Task<Automation?> SetEnabledAsync(ulong guildId, int id, bool enabled)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var updated = await db.Automations.Where(x => x.GuildId == guildId && x.Id == id)
            .Set(x => x.Enabled, enabled)
            .Set(x => x.DateModified, DateTime.UtcNow)
            .UpdateAsync();

        if (updated == 0)
            return null;

        await ReloadAsync(guildId);
        return await GetAsync(guildId, id);
    }

    /// <summary>
    ///     Deletes an automation and its run log.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="id">The automation ID.</param>
    /// <returns>Whether it existed.</returns>
    public async Task<bool> DeleteAsync(ulong guildId, int id)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        await db.AutomationRuns.Where(x => x.GuildId == guildId && x.AutomationId == id).DeleteAsync();
        var deleted = await db.Automations.Where(x => x.GuildId == guildId && x.Id == id).DeleteAsync();
        if (deleted == 0)
            return false;

        await ReloadAsync(guildId);
        return true;
    }

    /// <summary>
    ///     Gets the latest runs of an automation, newest first.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="id">The automation ID.</param>
    /// <param name="limit">The most runs to return.</param>
    public async Task<List<AutomationRun>> GetRunsAsync(ulong guildId, int id, int limit = AutomationLimits.RunsKept)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.AutomationRuns.Where(x => x.GuildId == guildId && x.AutomationId == id)
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, AutomationLimits.RunsKept))
            .ToListAsync();
    }

    /// <summary>
    ///     Creates the automations of a template.
    /// </summary>
    /// <param name="templateId">The template ID.</param>
    /// <param name="guild">The guild.</param>
    /// <param name="creatorId">The ID of the member creating them.</param>
    /// <param name="values">The template parameters, keyed by name.</param>
    public async Task<AutomationTemplateOutcome> CreateFromTemplateAsync(string templateId, SocketGuild guild,
        ulong creatorId, IReadOnlyDictionary<string, ulong> values)
    {
        var (result, error, detail) = AutomationTemplates.Build(templateId, guild, values);
        if (result is null)
            return new AutomationTemplateOutcome([], error, detail);

        foreach (var input in result.Automations)
        {
            var errors = Check(guild.Id, input);
            if (errors.Count > 0)
                return new AutomationTemplateOutcome([], AutomationTemplateError.Invalid, errors[0].Message);
        }

        var created = new List<Automation>();
        foreach (var input in result.Automations)
        {
            var saved = await CreateAsync(guild.Id, creatorId, input);
            if (saved.Automation is not null)
                created.Add(saved.Automation);
        }

        return new AutomationTemplateOutcome(created, AutomationTemplateError.None, null);
    }

    /// <summary>
    ///     Runs the actions of an automation once by hand, ignoring its conditions and cooldown.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="id">The automation ID.</param>
    /// <param name="invoker">The member who asked for the test.</param>
    /// <param name="channel">The channel the test happens in, or null when there is none.</param>
    /// <param name="message">A message to use as the trigger message, if there is one.</param>
    /// <returns>How the run ended, or null when the automation does not exist.</returns>
    public async Task<AutomationRunResult?> RunTestAsync(SocketGuild guild, int id, SocketGuildMember invoker,
        Channel? channel, Message? message)
    {
        var compiled = (await LoadAsync(guild.Id)).All.FirstOrDefault(x => x.Row.Id == id);
        if (compiled is null)
            return null;

        var (channelId, parentId, categoryId) = ChannelInfo(guild, channel);
        var data = new AutomationEventData
        {
            Trigger = compiled.Row.Trigger,
            GuildId = guild.Id,
            ChannelId = channelId,
            ParentChannelId = parentId,
            CategoryId = categoryId,
            UserId = invoker.Id,
            IsBot = invoker.IsBot,
            RoleIds = invoker.RoleIds,
            MessageId = message?.Id ?? 0,
            MessageKnown = message is not null,
            Content = message?.Content,
            IsReply = IsReply(message)
        };

        var context = new AutomationRunContext
        {
            Automation = compiled.Row, Data = data, Guild = guild, User = invoker.User, Channel = channel,
            Message = message, IsTest = true
        };

        return await ExecuteAsync(compiled, context);
    }

    private async Task<GuildAutomations> LoadAsync(ulong guildId)
    {
        var lazy = guilds.GetOrAdd(guildId, id => new Lazy<Task<GuildAutomations>>(() => ReadAsync(id)));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            guilds.TryRemove(guildId, out _);
            throw;
        }
    }

    private async Task<GuildAutomations> ReadAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var rows = await db.Automations.Where(x => x.GuildId == guildId).OrderBy(x => x.Id).ToListAsync();
        return new GuildAutomations(rows.Select(x =>
            new CompiledAutomation(x, AutomationNode.ParseList(x.Conditions), AutomationNode.ParseList(x.Actions)))
            .ToList());
    }

    private async Task ReloadAsync(ulong guildId)
    {
        var loaded = await ReadAsync(guildId);
        guilds[guildId] = new Lazy<Task<GuildAutomations>>(Task.FromResult(loaded));
    }

    private List<AutomationValidationError> Check(ulong guildId, AutomationInput input)
    {
        var errors = AutomationValidator.Validate(input.Name, input.Trigger, input.Conditions, input.Actions,
            input.CooldownSeconds);

        if (errors.Count == 0 && client.GetGuild(guildId) is { } guild)
            errors.AddRange(AutomationValidator.ValidateAgainstGuild(guild, input.Conditions, input.Actions));

        return errors;
    }

    private static void Apply(Automation row, AutomationInput input)
    {
        row.Name = input.Name.Trim();
        row.Enabled = input.Enabled;
        row.Trigger = input.Trigger;
        row.Conditions = AutomationNode.ToJson(input.Conditions);
        row.Actions = AutomationNode.ToJson(input.Actions);
        row.CooldownSeconds = input.CooldownSeconds;
        row.CooldownScope = input.CooldownScope;
        row.RetryOnFailure = input.RetryOnFailure;
        row.IgnoreBots = input.IgnoreBots;
        row.AllowMassMentions = input.AllowMassMentions;
    }

    private async Task<IReadOnlyList<CompiledAutomation>> ForTriggerAsync(ulong guildId,
        AutomationTriggerType trigger)
    {
        try
        {
            return (await LoadAsync(guildId)).For(trigger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load the automations of {GuildId}", guildId);
            return [];
        }
    }

    private Task DispatchAsync(IReadOnlyList<CompiledAutomation> automations, AutomationEventData data,
        Func<Automation, AutomationRunContext> make)
    {
        foreach (var compiled in automations)
        {
            try
            {
                var row = compiled.Row;
                if (row.IgnoreBots && data.IsBot)
                    continue;

                if (AutomationConditionEvaluator.FirstFailure(compiled.Conditions, data) >= 0)
                    continue;

                if (IsCoolingDown(row, data) || !TryStartRun(data.GuildId))
                    continue;

                var context = make(row);
                _ = Task.Run(() => ExecuteAsync(compiled, context));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Automation {Id} could not be started in {GuildId}", compiled.Row.Id,
                    data.GuildId);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<AutomationRunResult> ExecuteAsync(CompiledAutomation compiled, AutomationRunContext context)
    {
        var started = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        AutomationRunResult result;
        try
        {
            result = await runner.RunActionsAsync(context, compiled.Actions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automation {Id} crashed in {GuildId}", compiled.Row.Id, context.Guild.Id);
            result = new AutomationRunResult(false, null, null, "Something went wrong.");
        }

        watch.Stop();
        await LogRunAsync(compiled.Row, context, result, started, (int)watch.ElapsedMilliseconds);
        return result;
    }

    private async Task LogRunAsync(Automation row, AutomationRunContext context, AutomationRunResult result,
        DateTime started, int durationMs)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            await db.InsertAsync(new AutomationRun
            {
                AutomationId = row.Id,
                GuildId = row.GuildId,
                Success = result.Success,
                IsTest = context.IsTest,
                FailedActionIndex = result.FailedIndex,
                FailedActionType = result.FailedType,
                Reason = result.Reason?.TrimTo(500),
                UserId = context.Data.UserId,
                ChannelId = context.Data.ChannelId,
                DurationMs = durationMs,
                RanAt = started
            });

            var oldest = await db.AutomationRuns.Where(x => x.AutomationId == row.Id)
                .OrderByDescending(x => x.Id)
                .Skip(AutomationLimits.RunsKept - 1)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync();

            if (oldest is { } keepFrom)
                await db.AutomationRuns.Where(x => x.AutomationId == row.Id && x.Id < keepFrom).DeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not log a run of automation {Id}", row.Id);
        }
    }

    private bool IsCoolingDown(Automation row, AutomationEventData data)
    {
        if (row.CooldownSeconds <= 0)
            return false;

        var now = DateTime.UtcNow;
        var scope = row.CooldownScope == AutomationCooldownScope.Channel ? data.ChannelId : data.UserId;
        var key = $"{row.Id}:{scope}";

        if (cooldowns.Count > 5000)
        {
            foreach (var (stale, expires) in cooldowns.Where(x => x.Value < now).ToList())
                cooldowns.TryRemove(stale, out _);
        }

        if (cooldowns.TryGetValue(key, out var until) && until > now)
            return true;

        cooldowns[key] = now.AddSeconds(row.CooldownSeconds);
        return false;
    }

    private bool TryStartRun(ulong guildId)
    {
        var starts = runStarts.GetOrAdd(guildId, _ => new Queue<DateTime>());
        var now = DateTime.UtcNow;
        lock (starts)
        {
            while (starts.Count > 0 && now - starts.Peek() > TimeSpan.FromMinutes(1))
                starts.Dequeue();

            if (starts.Count >= AutomationLimits.MaxRunsPerMinute)
                return false;

            starts.Enqueue(now);
            return true;
        }
    }

    private static (ulong ChannelId, ulong ParentId, ulong CategoryId) ChannelInfo(SocketGuild guild, Channel? channel)
    {
        if (channel is null)
            return (0, 0, 0);

        if (AutomationRunner.IsThread(channel))
        {
            var parent = channel.ParentId is { } parentId ? guild.GetChannel(parentId) : null;
            return (channel.Id, channel.ParentId ?? 0, parent?.ParentId ?? 0);
        }

        return (channel.Id, 0, channel.ParentId ?? 0);
    }

    private static bool IsReply(Message? message) =>
        message?.MessageReference is { Type: MessageReferenceType.Default };

    private static List<string> AttachmentKinds(IEnumerable<Attachment>? attachments) =>
        (attachments ?? []).Select(x =>
        {
            var type = x.ContentType ?? "";
            if (type.StartsWith("image/"))
                return "Image";
            if (type.StartsWith("video/"))
                return "Video";
            return type.StartsWith("audio/") ? "Audio" : "File";
        }).Distinct().ToList();

    private AutomationEventData MessageData(AutomationTriggerType trigger, SocketGuild guild, Message? message,
        Channel channel, User? user, IReadOnlyCollection<ulong>? roleIds, ulong userId, string? emojiKey = null,
        int reactionCount = 0)
    {
        var (channelId, parentId, categoryId) = ChannelInfo(guild, channel);
        return new AutomationEventData
        {
            Trigger = trigger,
            GuildId = guild.Id,
            ChannelId = channelId,
            ParentChannelId = parentId,
            CategoryId = categoryId,
            UserId = user?.Id ?? userId,
            IsBot = user?.IsBot == true || message?.WebhookId is not null,
            RoleIds = roleIds ?? [],
            MessageId = message?.Id ?? 0,
            MessageKnown = message is not null,
            Content = message?.Content,
            AttachmentTypes = message is null ? [] : AttachmentKinds(message.Attachments),
            IsReply = IsReply(message),
            EmojiKey = emojiKey,
            ReactionCount = reactionCount
        };
    }

    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message.Guild is not { } guild || message.Author is not { } author ||
            author.Id == client.CurrentUser.Id || message.Type is not (MessageType.Default or MessageType.Reply))
            return;

        var list = await ForTriggerAsync(guild.Id, AutomationTriggerType.MessageSent);
        if (list.Count == 0)
            return;

        var member = message.Member ?? guild.GetUser(author.Id);
        var data = MessageData(AutomationTriggerType.MessageSent, guild, message, message.Channel, author,
            member?.RoleIds, author.Id);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = author, Channel = message.Channel, Message = message
        });
    }

    private async Task OnMessageUpdated(Message? before, SocketMessage after)
    {
        if (after.Guild is not { } guild || after.Author is not { } author || author.Id == client.CurrentUser.Id ||
            after.EditedAt is null)
            return;

        if (before is not null && before.Content == after.Content)
            return;

        var list = await ForTriggerAsync(guild.Id, AutomationTriggerType.MessageEdited);
        if (list.Count == 0)
            return;

        var member = after.Member ?? guild.GetUser(author.Id);
        var data = MessageData(AutomationTriggerType.MessageEdited, guild, after, after.Channel, author,
            member?.RoleIds, author.Id);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = author, Channel = after.Channel, Message = after
        });
    }

    private async Task OnMessageDeleted(Cacheable<Message> message, Channel channel)
    {
        if (runner.IsSuppressed(AutomationRunner.MessageKey(message.Id)))
            return;

        if (channel.Guild is not { } guild)
            return;

        var cached = message.Value;
        if (cached?.Author?.Id == client.CurrentUser.Id)
            return;

        var list = await ForTriggerAsync(guild.Id, AutomationTriggerType.MessageDeleted);
        if (list.Count == 0)
            return;

        var member = cached?.Author is { } author ? guild.GetUser(author.Id) : null;
        var data = MessageData(AutomationTriggerType.MessageDeleted, guild, cached, channel, cached?.Author,
            member?.RoleIds, cached?.Author?.Id ?? 0);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = cached?.Author, Channel = channel, Message = cached
        });
    }

    private Task OnReactionAdded(Cacheable<Message> message, Channel channel, SocketReaction reaction) =>
        OnReactionAsync(AutomationTriggerType.ReactionAdded, message, channel, reaction);

    private Task OnReactionRemoved(Cacheable<Message> message, Channel channel, SocketReaction reaction) =>
        OnReactionAsync(AutomationTriggerType.ReactionRemoved, message, channel, reaction);

    private async Task OnReactionAsync(AutomationTriggerType trigger, Cacheable<Message> message, Channel channel,
        SocketReaction reaction)
    {
        if (reaction.UserId == client.CurrentUser.Id || channel.Guild is not { } guild)
            return;

        var key = AutomationEmoji.Normalize(reaction.Emoji);
        if (trigger == AutomationTriggerType.ReactionRemoved &&
            runner.IsSuppressed(AutomationRunner.ReactionKey(message.Id, reaction.UserId, key)))
            return;

        var list = await ForTriggerAsync(guild.Id, trigger);
        if (list.Count == 0)
            return;

        Message? loaded = null;
        try
        {
            loaded = message.Value ?? await message.GetOrDownloadAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read message {MessageId} for a reaction", message.Id);
        }

        var member = reaction.Member ?? guild.GetUser(reaction.UserId);
        var count = loaded?.Reactions?.FirstOrDefault(x => x.Emoji.Matches(reaction.Emoji))?.Count
                    ?? (trigger == AutomationTriggerType.ReactionAdded ? 1 : 0);

        var data = MessageData(trigger, guild, loaded, channel, member?.User, member?.RoleIds, reaction.UserId, key,
            count);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = member?.User, Channel = channel, Message = loaded,
            Emote = Emoji.From(reaction.Emoji)
        });
    }

    private async Task OnUserJoined(SocketGuildMember member)
    {
        if (member.Id == client.CurrentUser.Id)
            return;

        var guild = member.Guild;
        var list = await ForTriggerAsync(guild.Id, AutomationTriggerType.MemberJoined);
        if (list.Count == 0)
            return;

        var data = MemberData(AutomationTriggerType.MemberJoined, guild, member.User, member.RoleIds);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = member.User
        });
    }

    private async Task OnUserLeft(SocketGuild guild, User user, MemberSnapshot? snapshot)
    {
        if (user.Id == client.CurrentUser.Id)
            return;

        var list = await ForTriggerAsync(guild.Id, AutomationTriggerType.MemberLeft);
        if (list.Count == 0)
            return;

        var data = MemberData(AutomationTriggerType.MemberLeft, guild, user, snapshot?.RoleIds);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = user
        });
    }

    private async Task OnGuildMemberUpdated(MemberSnapshot? before, SocketGuildMember after)
    {
        if (before is null || after.Id == client.CurrentUser.Id)
            return;

        var gained = after.RoleIds.Except(before.RoleIds).ToList();
        var lost = before.RoleIds.Except(after.RoleIds).ToList();
        if (gained.Count == 0 && lost.Count == 0)
            return;

        await DispatchRoleChangesAsync(AutomationTriggerType.RoleGained, after, gained);
        await DispatchRoleChangesAsync(AutomationTriggerType.RoleLost, after, lost);
    }

    private async Task DispatchRoleChangesAsync(AutomationTriggerType trigger, SocketGuildMember member,
        List<ulong> roleIds)
    {
        if (roleIds.Count == 0)
            return;

        var guild = member.Guild;
        var list = await ForTriggerAsync(guild.Id, trigger);
        foreach (var roleId in roleIds)
        {
            if (list.Count == 0 || runner.IsSuppressed(AutomationRunner.RoleKey(guild.Id, member.Id, roleId,
                    trigger == AutomationTriggerType.RoleGained)))
                continue;

            var role = guild.GetRole(roleId);
            var data = MemberData(trigger, guild, member.User, member.RoleIds, roleId);
            await DispatchAsync(list, data, row => new AutomationRunContext
            {
                Automation = row, Data = data, Guild = guild, User = member.User, Role = role
            });
        }
    }

    private async Task OnVoiceStateUpdated(SocketGuildMember member, VoiceStateSnapshot before,
        VoiceStateSnapshot after)
    {
        if (member.Id == client.CurrentUser.Id || before.ChannelId == after.ChannelId)
            return;

        if (before.VoiceChannel is { } left)
            await DispatchVoiceAsync(AutomationTriggerType.VoiceLeft, member, left);

        if (after.VoiceChannel is { } joined)
            await DispatchVoiceAsync(AutomationTriggerType.VoiceJoined, member, joined);
    }

    private async Task DispatchVoiceAsync(AutomationTriggerType trigger, SocketGuildMember member, Channel channel)
    {
        var guild = member.Guild;
        var list = await ForTriggerAsync(guild.Id, trigger);
        if (list.Count == 0)
            return;

        var data = MemberData(trigger, guild, member.User, member.RoleIds, channel: channel);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = member.User, Channel = channel
        });
    }

    private async Task OnThreadCreated(ThreadChannel thread)
    {
        if (thread.Guild is not { } guild || thread.OwnerId == client.CurrentUser.Id)
            return;

        var list = await ForTriggerAsync(guild.Id, AutomationTriggerType.ThreadCreated);
        if (list.Count == 0)
            return;

        var member = thread.OwnerId is { } ownerId ? guild.GetUser(ownerId) : null;
        var data = MemberData(AutomationTriggerType.ThreadCreated, guild, member?.User, member?.RoleIds,
            channel: thread);
        await DispatchAsync(list, data, row => new AutomationRunContext
        {
            Automation = row, Data = data, Guild = guild, User = member?.User, Channel = thread,
            TriggerThread = thread
        });
    }

    private Task OnLeftGuild(SocketGuild guild)
    {
        guilds.TryRemove(guild.Id, out _);
        runStarts.TryRemove(guild.Id, out _);
        return Task.CompletedTask;
    }

    private static AutomationEventData MemberData(AutomationTriggerType trigger, SocketGuild guild, User? user,
        IReadOnlyCollection<ulong>? roleIds, ulong changedRoleId = 0, Channel? channel = null)
    {
        var (channelId, parentId, categoryId) = ChannelInfo(guild, channel);
        return new AutomationEventData
        {
            Trigger = trigger,
            GuildId = guild.Id,
            ChannelId = channelId,
            ParentChannelId = parentId,
            CategoryId = categoryId,
            UserId = user?.Id ?? 0,
            IsBot = user?.IsBot == true,
            RoleIds = roleIds ?? [],
            ChangedRoleId = changedRoleId
        };
    }

    private sealed class GuildAutomations(List<CompiledAutomation> all)
    {
        private readonly Dictionary<AutomationTriggerType, IReadOnlyList<CompiledAutomation>> byTrigger = all
            .Where(x => x.Row.Enabled)
            .GroupBy(x => x.Row.Trigger)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<CompiledAutomation>)x.ToList());

        public IReadOnlyList<CompiledAutomation> All { get; } = all;

        public IReadOnlyList<CompiledAutomation> For(AutomationTriggerType trigger) =>
            byTrigger.TryGetValue(trigger, out var list) ? list : [];
    }
}
