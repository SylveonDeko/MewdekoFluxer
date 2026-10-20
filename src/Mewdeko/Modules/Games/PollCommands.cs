using System.Globalization;
using System.Text.Json;
using Discord.Commands;
using Humanizer;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Games.Common;
using Mewdeko.Modules.Games.Services;

namespace Mewdeko.Modules.Games;

/// <summary>
/// Text commands for creating and managing polls. Members vote by reacting to the poll message.
/// </summary>
public class PollCommands : MewdekoModuleBase<PollService>
{
    private readonly ILogger<PollCommands> logger;
    private readonly PollMessageService pollMessages;
    private readonly PollTemplateService templateService;

    /// <summary>
    /// Initializes a new instance of the PollCommands class.
    /// </summary>
    /// <param name="templateService">The template service.</param>
    /// <param name="pollMessages">The service that posts poll messages and counts reactions.</param>
    /// <param name="logger">The logger instance.</param>
    public PollCommands(PollTemplateService templateService, PollMessageService pollMessages,
        ILogger<PollCommands> logger)
    {
        this.templateService = templateService;
        this.pollMessages = pollMessages;
        this.logger = logger;
    }

    /// <summary>
    /// Creates a simple yes/no poll.
    /// </summary>
    /// <param name="question">The poll question.</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageMessages)]
    public async Task Poll([Remainder] string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            await ErrorAsync(Strings.PollQuestionRequired(ctx.Guild.Id));
            return;
        }

        var options = new List<PollOptionData>
        {
            new()
            {
                Text = Strings.PollOptionYes(ctx.Guild.Id), Emote = "✅"
            },
            new()
            {
                Text = Strings.PollOptionNo(ctx.Guild.Id), Emote = "❌"
            }
        };

        await PostAsync(question, options, PollType.YesNo, DefaultSettings());
    }

    /// <summary>
    /// Creates a custom poll with multiple options.
    /// </summary>
    /// <param name="input">The poll input in format: "question;option1;option2;..."</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageMessages)]
    public async Task Pollc([Remainder] string input)
    {
        if (await TryParseInputAsync(input) is not { } parsed)
            return;

        await PostAsync(parsed.Question, parsed.Options, PollType.SingleChoice, DefaultSettings());
    }

    /// <summary>
    /// Creates a multi-choice poll where users can select multiple options.
    /// </summary>
    /// <param name="input">The poll input in format: "question;option1;option2;..."</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageMessages)]
    public async Task Pollm([Remainder] string input)
    {
        if (await TryParseInputAsync(input) is not { } parsed)
            return;

        var settings = DefaultSettings();
        settings.AllowMultipleVotes = true;
        await PostAsync(parsed.Question, parsed.Options, PollType.MultiChoice, settings);
    }

    /// <summary>
    /// Creates an anonymous poll where voter identities are hidden.
    /// </summary>
    /// <param name="input">The poll input in format: "question;option1;option2;..."</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageMessages)]
    public async Task Polla([Remainder] string input)
    {
        if (await TryParseInputAsync(input) is not { } parsed)
            return;

        var settings = DefaultSettings();
        settings.IsAnonymous = true;
        await PostAsync(parsed.Question, parsed.Options, PollType.Anonymous, settings);
    }

    /// <summary>
    /// Shows the vote counts and timing of a poll.
    /// </summary>
    /// <param name="pollId">The poll ID shown in the poll footer.</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task PollStats(int pollId)
    {
        var poll = await FindManagedPollAsync(pollId);
        if (poll is null)
            return;

        var stats = await Service.GetPollStatsAsync(pollId);
        var guildId = ctx.Guild.Id;
        var total = stats?.TotalVotes ?? 0;
        var results = string.Join("\n", poll.PollOptions.OrderBy(o => o.Index).Select(o =>
        {
            var votes = stats?.OptionVotes.GetValueOrDefault(o.Index, 0) ?? 0;
            var percent = total > 0 ? (double)votes / total * 100 : 0;
            return o.Emote + " " + o.Text + ": " +
                   Strings.PollOptionVotes(guildId, votes, percent.ToString("F1", CultureInfo.InvariantCulture));
        })).TrimTo(1024);

        var embed = new EmbedBuilder()
            .WithOkColor()
            .WithTitle(Strings.PollStatsTitleWithQuestion(guildId, poll.Question).TrimTo(256))
            .AddField(Strings.PollStatsStatus(guildId),
                poll.IsActive ? Strings.PollStatsActive(guildId) : Strings.PollStatsClosed(guildId), true)
            .AddField(Strings.PollStatsTotalVotes(guildId), total.ToString(CultureInfo.InvariantCulture), true)
            .AddField(Strings.PollStatsUniqueVoters(guildId),
                (stats?.UniqueVoters ?? 0).ToString(CultureInfo.InvariantCulture), true)
            .AddField(Strings.PollStatsCreated(guildId), poll.CreatedAt.ToString("yyyy-MM-dd HH:mm"), true);

        if (poll.ClosedAt is { } closedAt)
            embed.AddField(Strings.PollStatsClosedAt(guildId), closedAt.ToString("yyyy-MM-dd HH:mm"), true);
        if (stats is { TotalVotes: > 0 })
            embed.AddField(Strings.PollStatsAverageTime(guildId), stats.AverageVoteTime.Humanize(2), true);

        embed.AddField(Strings.PollStatsResults(guildId), results);
        await ctx.Channel.SendMessageAsync(embed: embed.Build());
    }

    /// <summary>
    /// Closes a poll now and shows its final results on the poll message.
    /// </summary>
    /// <param name="pollId">The poll ID shown in the poll footer.</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task PollClose(int pollId)
    {
        var poll = await FindManagedPollAsync(pollId);
        if (poll is null)
            return;

        if (!await pollMessages.CloseAsync(pollId, ctx.User.Id))
        {
            await ErrorAsync(Strings.PollAlreadyClosed(ctx.Guild.Id));
            return;
        }

        await ConfirmAsync(Strings.PollCloseSuccess(ctx.Guild.Id, poll.Question));
    }

    /// <summary>
    /// Deletes a poll, its votes and its message after asking first.
    /// </summary>
    /// <param name="pollId">The poll ID shown in the poll footer.</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task PollDelete(int pollId)
    {
        var poll = await FindManagedPollAsync(pollId);
        if (poll is null)
            return;

        if (!await PromptUserConfirmAsync(Strings.PollDeleteConfirm(ctx.Guild.Id, poll.Question), ctx.User.Id))
            return;

        if (!await pollMessages.DeleteAsync(pollId, ctx.User.Id))
        {
            await ErrorAsync(Strings.PollDeleteFailed(ctx.Guild.Id));
            return;
        }

        await ConfirmAsync(Strings.PollDeletedSuccessfully(ctx.Guild.Id));
    }

    /// <summary>
    /// Lists all poll templates available in the guild.
    /// </summary>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task PollTemplates()
    {
        try
        {
            var templates = await templateService.GetTemplatesAsync(ctx.Guild.Id);

            if (templates.Count == 0)
            {
                await ErrorAsync(Strings.PollNoTemplates(ctx.Guild.Id));
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle(Strings.PollTemplatesTitle(ctx.Guild.Id))
                .WithColor(Color.Blue)
                .WithDescription(Strings.PollTemplatesFound(ctx.Guild.Id, templates.Count));

            foreach (var template in templates.Take(10))
            {
                var optionCount = 0;
                try
                {
                    var options = JsonSerializer.Deserialize<List<PollOptionData>>(template.Options);
                    optionCount = options?.Count ?? 0;
                }
                catch (JsonException)
                {
                    optionCount = 0;
                }

                embed.AddField(template.Name,
                    $"{Strings.PollTemplateQuestionField(ctx.Guild.Id)} {template.Question}\n" +
                    $"{Strings.PollTemplateOptionsField(ctx.Guild.Id)} {optionCount}\n" +
                    $"{Strings.PollTemplateCreatedField(ctx.Guild.Id)} {template.CreatedAt:yyyy-MM-dd}",
                    true);
            }

            if (templates.Count > 10)
                embed.WithFooter(Strings.PollShowingTemplates(ctx.Guild.Id, templates.Count));

            await ctx.Channel.SendMessageAsync(embed: embed.Build());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list poll templates in guild {GuildId}", ctx.Guild.Id);
            await ErrorAsync(Strings.PollTemplatesError(ctx.Guild.Id));
        }
    }

    /// <summary>
    /// Creates a poll from a template.
    /// </summary>
    /// <param name="templateName">The name of the template to use.</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageMessages)]
    public async Task PollTemplate([Remainder] string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            await ErrorAsync(Strings.PollTemplateNameRequired(ctx.Guild.Id));
            return;
        }

        try
        {
            var template = await templateService.GetTemplateByNameAsync(ctx.Guild.Id, templateName);
            if (template == null)
            {
                await ErrorAsync(Strings.PollTemplateNotFound(ctx.Guild.Id));
                return;
            }

            var options = JsonSerializer.Deserialize<List<PollOptionData>>(template.Options);
            if (options == null || options.Count == 0)
            {
                await ErrorAsync(Strings.PollTemplateInvalid(ctx.Guild.Id));
                return;
            }

            PollSettings? settings = null;
            if (!string.IsNullOrEmpty(template.Settings))
                settings = JsonSerializer.Deserialize<PollSettings>(template.Settings);

            settings ??= DefaultSettings();
            await PostAsync(template.Question, options, settings.DefaultType ?? PollType.SingleChoice, settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create poll from template '{TemplateName}' in guild {GuildId}", templateName,
                ctx.Guild.Id);
            await ErrorAsync(Strings.PollCreationFailed(ctx.Guild.Id));
        }
    }

    /// <summary>
    /// Lists the polls that are still open in the guild.
    /// </summary>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task PollList()
    {
        try
        {
            var polls = await Service.GetActivePollsAsync(ctx.Guild.Id);
            if (polls.Count == 0)
            {
                await ErrorAsync(Strings.PollListNoActive(ctx.Guild.Id));
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle(Strings.PollActiveTitle(ctx.Guild.Id))
                .WithOkColor()
                .WithDescription(Strings.PollActivePollsFound(ctx.Guild.Id, polls.Count));

            foreach (var poll in polls.Take(10))
            {
                var channel = await ctx.Guild.GetTextChannelAsync(poll.ChannelId);
                var stats = await Service.GetPollStatsAsync(poll.Id);
                var createdAt = ((DateTimeOffset)poll.CreatedAt).ToUnixTimeSeconds();

                embed.AddField(poll.Question.TrimTo(50),
                    $"{Strings.PollListChannelField(ctx.Guild.Id)} {channel?.Mention ?? $"<#{poll.ChannelId}>"}\n" +
                    $"{Strings.PollListVotesField(ctx.Guild.Id)} {stats?.TotalVotes ?? 0}\n" +
                    $"{Strings.PollListCreatedField(ctx.Guild.Id)} <t:{createdAt}:R>\n" +
                    $"{Strings.PollListIdField(ctx.Guild.Id)} {poll.Id}",
                    true);
            }

            if (polls.Count > 10)
                embed.WithFooter(Strings.PollShowingActive(ctx.Guild.Id, polls.Count));

            await ctx.Channel.SendMessageAsync(embed: embed.Build());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list active polls in guild {GuildId}", ctx.Guild.Id);
            await ErrorAsync(Strings.PollListRetry(ctx.Guild.Id));
        }
    }

    /// <summary>
    /// Saves a reusable poll template.
    /// </summary>
    /// <param name="input">The template in format: "name;question;option1;option2;..."</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageMessages)]
    public async Task PollTemplateCreate([Remainder] string input)
    {
        var parts = input.Split(';');
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            await ErrorAsync(Strings.PollTemplateInputRequired(ctx.Guild.Id));
            return;
        }

        var options = parts.Skip(2)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PollOptionData
            {
                Text = p
            })
            .ToList();

        switch (options.Count)
        {
            case < 2:
                await ErrorAsync(Strings.PollTemplateMinOptions(ctx.Guild.Id));
                return;
            case > PollMessageService.MaxOptions:
                await ErrorAsync(Strings.PollTemplateMaxOptions(ctx.Guild.Id));
                return;
        }

        try
        {
            var template = await templateService.CreateTemplateAsync(ctx.Guild.Id, ctx.User.Id, parts[0].Trim(),
                parts[1].Trim(), options, DefaultSettings());

            var embed = new EmbedBuilder()
                .WithTitle(Strings.PollTemplateCreatedTitle(ctx.Guild.Id))
                .WithOkColor()
                .WithDescription(Strings.PollTemplateCreatedDesc(ctx.Guild.Id, template.Name))
                .AddField(Strings.PollFieldQuestion(ctx.Guild.Id), template.Question)
                .AddField(Strings.PollFieldOptions(ctx.Guild.Id), string.Join(", ", options.Select(o => o.Text)));

            await ctx.Channel.SendMessageAsync(embed: embed.Build());
        }
        catch (InvalidOperationException ex)
        {
            await ErrorAsync(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create template '{TemplateName}' in guild {GuildId}", parts[0].Trim(),
                ctx.Guild.Id);
            await ErrorAsync(Strings.PollTemplateCreateRetry(ctx.Guild.Id));
        }
    }

    /// <summary>
    /// Deletes a poll template.
    /// </summary>
    /// <param name="templateName">The name of the template to delete.</param>
    [Cmd, Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageMessages)]
    public async Task PollTemplateDelete([Remainder] string templateName)
    {
        try
        {
            var template = await templateService.GetTemplateByNameAsync(ctx.Guild.Id, templateName);
            if (template == null)
            {
                await ErrorAsync(Strings.PollTemplateNotFound(ctx.Guild.Id));
                return;
            }

            if (template.CreatorId != ctx.User.Id && !((IGuildUser)ctx.User).GuildPermissions.ManageMessages)
            {
                await ErrorAsync(Strings.PollTemplateDeleteNoPermission(ctx.Guild.Id));
                return;
            }

            if (!await templateService.DeleteTemplateAsync(template.Id, ctx.User.Id))
            {
                await ErrorAsync(Strings.PollDeleteFailed(ctx.Guild.Id));
                return;
            }

            await ConfirmAsync(Strings.PollTemplateDeleteSuccess(ctx.Guild.Id, templateName));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete template '{TemplateName}' in guild {GuildId}", templateName,
                ctx.Guild.Id);
            await ErrorAsync(Strings.PollTemplateDeleteRetry(ctx.Guild.Id));
        }
    }

    private static PollSettings DefaultSettings()
    {
        return new PollSettings
        {
            AllowVoteChanges = true, ShowResults = true, ShowProgressBars = true
        };
    }

    private async Task<(string Question, List<PollOptionData> Options)?> TryParseInputAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            await ErrorAsync(Strings.PollInputRequired(ctx.Guild.Id));
            return null;
        }

        var parts = input.Split(';');
        var options = parts.Skip(1)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PollOptionData
            {
                Text = p
            })
            .ToList();

        if (options.Count < 2)
        {
            await ErrorAsync(Strings.PollMinimumOptions(ctx.Guild.Id));
            return null;
        }

        if (options.Count > PollMessageService.MaxOptions)
        {
            await ErrorAsync(Strings.PollMaximumOptions(ctx.Guild.Id, PollMessageService.MaxOptions));
            return null;
        }

        return (parts[0].Trim(), options);
    }

    private async Task PostAsync(string question, List<PollOptionData> options, PollType type, PollSettings settings)
    {
        if (ctx.Channel is not ITextChannel channel)
            return;

        var problem = await pollMessages.GetPostProblemAsync(channel, settings, options);
        if (problem is not null)
        {
            await ErrorAsync(problem);
            return;
        }

        try
        {
            await pollMessages.PostPollAsync(channel, ctx.User.Id, question, options, type, settings);
            await ctx.Message.DeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create a poll in guild {GuildId}", ctx.Guild.Id);
            await ErrorAsync(Strings.PollCreationFailed(ctx.Guild.Id));
        }
    }

    private async Task<DataModel.Poll?> FindManagedPollAsync(int pollId)
    {
        var poll = await Service.GetPollAsync(pollId);
        if (poll is null || poll.GuildId != ctx.Guild.Id)
        {
            await ErrorAsync(Strings.PollNotFound(ctx.Guild.Id));
            return null;
        }

        if (poll.CreatorId == ctx.User.Id || ((IGuildUser)ctx.User).GuildPermissions.ManageMessages)
            return poll;

        await ErrorAsync(Strings.PollManageNoPermission(ctx.Guild.Id));
        return null;
    }
}
