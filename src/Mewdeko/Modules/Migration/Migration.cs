using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Migration.Services;

namespace Mewdeko.Modules.Migration;

/// <summary>
///     Commands for bringing a server and its members over from Mewdeko on Discord.
/// </summary>
public class Migration : MewdekoModuleBase<DiscordMigrationService>
{
    private const int ListedNames = 15;

    /// <summary>
    ///     Brings this server's Mewdeko setup over from Discord, using the code from <c>.fluxermigrate</c> there.
    ///     Shows what was matched and asks before writing anything.
    /// </summary>
    /// <param name="code">The code the Discord bot sent.</param>
    /// <example>.migrate ABCD2345</example>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.Administrator)]
    public async Task Migrate(string code)
    {
        var guildId = ctx.Guild!.Id;
        MigrationPlan? plan;
        MigrationProblem problem;
        using (ctx.Channel.EnterTypingState())
            (plan, problem) = await Service.PrepareAsync(ctx.Guild, code);

        switch (problem)
        {
            case MigrationProblem.BadCode:
                await ReplyErrorAsync(Strings.MigrationBadCode(guildId));
                return;
            case MigrationProblem.NotFound:
                await ReplyErrorAsync(Strings.MigrationNotFound(guildId));
                return;
            case MigrationProblem.UnsupportedVersion:
                await ReplyErrorAsync(Strings.MigrationUnsupported(guildId));
                return;
        }

        var match = plan!.Match;
        var linked = plan.Members.Count(x => x.Value);
        var preview = new EmbedBuilder()
            .WithOkColor()
            .WithTitle(Strings.MigrationPreviewTitle(guildId, plan.Meta.GuildName))
            .WithDescription(Strings.MigrationPreviewDescription(guildId))
            .AddField(Strings.MigrationPreviewChannels(guildId),
                $"{match.Channels.Count}/{plan.Meta.Channels.Count}", true)
            .AddField(Strings.MigrationPreviewRoles(guildId),
                $"{match.Roles.Count - 1}/{plan.Meta.Roles.Count(x => !x.Managed) - 1}", true)
            .AddField(Strings.MigrationPreviewEmojis(guildId), $"{match.Emojis.Count}/{plan.Meta.Emojis.Count}", true)
            .AddField(Strings.MigrationPreviewData(guildId),
                Strings.MigrationPreviewDataValue(guildId, plan.SettingRows, plan.Tables.Count, plan.MemberRows,
                    plan.Members.Count, linked));

        if (match.UnmatchedChannels.Count > 0)
            preview.AddField(Strings.MigrationPreviewUnmatchedChannels(guildId),
                ListNames(match.UnmatchedChannels.Select(x => $"#{x.Name}").ToList(), guildId));

        if (match.UnmatchedRoles.Count > 0)
            preview.AddField(Strings.MigrationPreviewUnmatchedRoles(guildId),
                ListNames(match.UnmatchedRoles.Select(x => $"@{x.Name}").ToList(), guildId));

        if (!await PromptUserConfirmAsync(preview, ctx.User.Id))
        {
            await ReplyConfirmAsync(Strings.MigrationCancelled(guildId));
            return;
        }

        var working = await LoadingLocalizedAsync(Strings.MigrationWorking(guildId));
        MigrationOutcome outcome;
        try
        {
            using (ctx.Channel.EnterTypingState())
                outcome = await Service.ApplyAsync(plan, ctx.User.Id);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Migration into guild {GuildId} failed", guildId);
            await working.DeleteAsync();
            await ReplyErrorAsync(Strings.MigrationFailed(guildId));
            return;
        }

        await working.DeleteAsync();
        await ReplyConfirmAsync(Strings.MigrationDone(guildId, outcome.Written, outcome.Held, outcome.WaitingMembers,
            outcome.Dropped, outcome.Failed, outcome.Reposted));
    }

    /// <summary>
    ///     Links your Fluxer account to your Discord account with the code from <c>.fluxerlink</c> on Discord, and
    ///     brings over your XP, balance and the rest from every server that moved to Fluxer.
    /// </summary>
    /// <param name="code">The code the Discord bot sent.</param>
    /// <example>.linkdiscord ABCD2345</example>
    [Cmd]
    [Aliases]
    public async Task LinkDiscord(string code)
    {
        var guildId = ctx.Guild?.Id ?? 0;
        var outcome = await Service.LinkAsync(ctx.User.Id, code);
        switch (outcome)
        {
            case null:
                await ReplyErrorAsync(Strings.MigrationLinkNotFound(guildId));
                break;
            case { AlreadyLinkedElsewhere: true }:
                await ReplyErrorAsync(Strings.MigrationLinkTaken(guildId));
                break;
            case { Rows: 0 }:
                await ReplyConfirmAsync(Strings.MigrationLinkedNothing(guildId));
                break;
            default:
                await ReplyConfirmAsync(Strings.MigrationLinked(guildId, outcome.Rows, outcome.Servers));
                break;
        }
    }

    private string ListNames(List<string> names, ulong guildId)
    {
        var shown = string.Join(", ", names.Take(ListedNames));
        return names.Count > ListedNames
            ? $"{shown} {Strings.MigrationAndMore(guildId, names.Count - ListedNames)}"
            : shown;
    }
}
