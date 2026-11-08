
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Confessions.Services;

namespace Mewdeko.Modules.Confessions;

/// <summary>
///     Module for managing confessions.
/// </summary>
/// <param name="guildSettings">The guild settings service</param>
public class Confessions(GuildSettingsService guildSettings)
    : MewdekoModuleBase<ConfessionService>
{
    /// <summary>
    ///     Allows users to confess anonymously.
    /// </summary>
    /// <param name="serverId">The ID of the community.</param>
    /// <param name="confession">The confession message</param>
    /// <example>.confess 1234567890 falafel.</example>
    [Cmd]
    [Aliases]
    [Priority(1)]
    public async Task Confess(ulong serverId, [Remainder] string confession)
    {
        if (ctx.Guild is not null)
        {
            await ReplyHowToConfessAsync().ConfigureAwait(false);
            return;
        }

        await PostConfessionAsync(serverId, confession).ConfigureAwait(false);
    }

    /// <summary>
    ///     Allows users to confess anonymously without an ID when they share one community with confessions enabled.
    /// </summary>
    /// <param name="confession">The confession message</param>
    /// <example>.confess falafel.</example>
    [Cmd]
    [Aliases]
    [Priority(0)]
    public async Task Confess([Remainder] string confession)
    {
        if (ctx.Guild is not null)
        {
            await ReplyHowToConfessAsync().ConfigureAwait(false);
            return;
        }

        var user = (SocketUser)ctx.User;
        var enabled = new List<(string Name, ulong Id, string Prefix)>();
        foreach (var guild in FluxerContext.Client.Guilds.Where(x => x.Members.ContainsKey(user.Id)))
        {
            if ((await guildSettings.GetGuildConfig(guild.Id)).ConfessionChannel is 0)
                continue;

            enabled.Add((guild.Name, guild.Id, await guildSettings.GetPrefix(guild)));
        }

        switch (enabled.Count)
        {
            case 0:
                await ErrorAsync(Strings.ConfessionsNoneAny(null)).ConfigureAwait(false);
                return;
            case 1:
                await PostConfessionAsync(enabled[0].Id, confession).ConfigureAwait(false);
                return;
            default:
                var lines = string.Join("\n",
                    enabled.Select(x => Strings.ConfessionsPickLine(null, x.Name, x.Prefix, x.Id)));
                await ConfirmAsync(Strings.ConfessionsPick(null, lines)).ConfigureAwait(false);
                return;
        }
    }

    private async Task ReplyHowToConfessAsync()
    {
        var guild = ctx.Guild!;
        if ((await guildSettings.GetGuildConfig(guild.Id)).ConfessionChannel is 0)
        {
            await ErrorAsync(Strings.ConfessionsNone(guild.Id)).ConfigureAwait(false);
            return;
        }

        try
        {
            await ctx.Message.DeleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // the bot may lack Manage Messages
        }

        try
        {
            await ctx.User.SendConfirmAsync(Strings.ConfessionsDmHowto(guild.Id,
                await guildSettings.GetPrefix(guild), guild.Id)).ConfigureAwait(false);
        }
        catch
        {
            (await ErrorAsync(Strings.ConfessionsDmClosed(guild.Id)).ConfigureAwait(false)).DeleteAfter(8);
        }
    }

    private async Task PostConfessionAsync(ulong serverId, string confession)
    {
        var gc = await guildSettings.GetGuildConfig(serverId);
        var attachment = ctx.Message.Attachments.FirstOrDefault()?.Url;
        var user = (SocketUser)ctx.User;
        if (!FluxerContext.Client.Guilds.Where(x => x.Members.ContainsKey(user.Id)).Select(x => x.Id)
                .Contains(serverId))
        {
            await ErrorAsync(Strings.ConfessionsNoneAny(null)).ConfigureAwait(false);
            return;
        }

        if (gc.ConfessionChannel is 0)
        {
            await ErrorAsync(Strings.ConfessionsNone(serverId)).ConfigureAwait(false);
            return;
        }

        if (await Service.IsBlacklistedAsync(serverId, ctx.User.Id).ConfigureAwait(false))
        {
            await ErrorAsync(Strings.ConfessionsBlacklisted(serverId)).ConfigureAwait(false);
            return;
        }

        await Service.SendConfession(serverId, ctx.User, confession, ctx.Channel, attachment)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the confession channel for anonymous confessions.
    /// </summary>
    /// <param name="channel">The confession channel (optional).</param>
    /// <example>.confessionchannel #confessions</example>
    [Cmd]
    [Aliases]
    [UserPerm(GuildPermission.ManageChannels)]
    [RequireContext(ContextType.Guild)]
    public async Task ConfessionChannel(TextChannel? channel = null)
    {
        if (channel is null)
        {
            await Service.SetConfessionChannel(ctx.Guild, 0).ConfigureAwait(false);
            await ConfirmAsync(Strings.ConfessionsDisabled(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        var currentUser = await ctx.Guild.GetUserAsync(ctx.Client.CurrentUser.Id).ConfigureAwait(false);
        var perms = currentUser.GetPermissions(channel);
        if (!perms.SendMessages || !perms.EmbedLinks)
        {
            await ErrorAsync(Strings.ConfessionsInvalidPerms(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        await Service.SetConfessionChannel(ctx.Guild, channel.Id).ConfigureAwait(false);
        await ConfirmAsync(Strings.ConfessionsChannelSet(ctx.Guild.Id, channel.Mention)).ConfigureAwait(false);
    }


    /// <summary>
    ///     Sets the confession log channel for logging confessions. Misuse of this feature will end up in me being 2m away
    ///     from your house.
    /// </summary>
    /// <param name="channel">The confession log channel (optional).</param>
    /// <example>.confessionlogchannel #confessions</example>
    [Cmd]
    [Aliases]
    [UserPerm(GuildPermission.Administrator)]
    [RequireContext(ContextType.Guild)]
    public async Task ConfessionLogChannel(TextChannel? channel = null)
    {
        if (channel is null)
        {
            await Service.SetConfessionLogChannel(ctx.Guild, 0).ConfigureAwait(false);
            await ConfirmAsync(Strings.ConfessionsLoggingDisabled(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        var currentUser = await ctx.Guild.GetUserAsync(ctx.Client.CurrentUser.Id).ConfigureAwait(false);
        var perms = currentUser.GetPermissions(channel);
        if (!perms.SendMessages || !perms.EmbedLinks)
        {
            await ErrorAsync(Strings.ConfessionsInvalidPerms(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        await Service.SetConfessionLogChannel(ctx.Guild, channel.Id).ConfigureAwait(false);
        await ConfirmAsync(Strings.ConfessionsSpleen(ctx.Guild.Id, channel.Mention)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds or removes a user from the confession blacklist.
    /// </summary>
    /// <param name="user">The user to add or remove from the blacklist.</param>
    /// <example>.confessionblacklist @user</example>
    [Cmd]
    [Aliases]
    [UserPerm(GuildPermission.ManageChannels)]
    [RequireContext(ContextType.Guild)]
    public async Task ConfessionBlacklist(IUser user)
    {
        var blacklists = (await guildSettings.GetGuildConfig(ctx.Guild.Id)).ConfessionBlacklist.Split(" ");
        if (blacklists.Length > 0)
        {
            if (blacklists.Contains(user.Id.ToString()))
            {
                await ErrorAsync(Strings.ConfessionsBlacklistedAlready(ctx.Guild.Id)).ConfigureAwait(false);
                return;
            }

            await Service.ToggleUserBlacklistAsync(ctx.Guild.Id, user.Id).ConfigureAwait(false);
            await ConfirmAsync(Strings.ConfessionsBlacklistedAdded(ctx.Guild.Id, user.Mention));
        }
    }

    /// <summary>
    ///     Removes a user from the confession blacklist.
    /// </summary>
    /// <param name="user">The user to remove from the blacklist.</param>
    /// <example>.confessionunblacklist @user</example>
    [Cmd]
    [Aliases]
    [UserPerm(GuildPermission.ManageChannels)]
    [RequireContext(ContextType.Guild)]
    public async Task ConfessionUnblacklist(IUser user)
    {
        var blacklists = (await guildSettings.GetGuildConfig(ctx.Guild.Id)).ConfessionBlacklist.Split(" ");
        if (blacklists.Length > 0)
        {
            if (!blacklists.Contains(user.Id.ToString()))
            {
                await ErrorAsync(Strings.ConfessionsBlacklistedNot(ctx.Guild.Id)).ConfigureAwait(false);
                return;
            }

            await Service.ToggleUserBlacklistAsync(ctx.Guild.Id, user.Id).ConfigureAwait(false);
            await ConfirmAsync(Strings.ConfessionsBlacklistedRemoved(ctx.Guild.Id, user.Mention));
        }
    }

    /// <summary>
    ///     Reports a server for misuse of confessions.
    /// </summary>
    /// <param name="serverId">The ID of the server abusing confessions.</param>
    /// <param name="how">How they are abusing confessions, with image links if possible.</param>
    /// <example>.confessreport 1234567890 They are abusing confessions.</example>
    [Cmd]
    [Aliases]
    public async Task ConfessReport(ulong serverId, [Remainder] string how)
    {
        var guildId = ctx.Guild?.Id;
        var reportedGuild = await ((FluxerClient)ctx.Client).Rest.GetGuildAsync(serverId).ConfigureAwait(false);
        if (reportedGuild is null)
        {
            await ctx.Channel.SendErrorAsync(Strings.ConfessionsInvalidId(guildId), Config).ConfigureAwait(false);
            return;
        }

        if (Config.ConfessionReportChannel is 0)
            return;

        if (await ((FluxerClient)ctx.Client).Rest.GetChannelAsync(Config.ConfessionReportChannel)
                .ConfigureAwait(false) is not Channel channel)
            return;

        var eb = new EmbedBuilder().WithErrorColor().WithTitle(Strings.ConfessionsReportReceived(guildId))
            .AddField(Strings.ConfessionsReport(guildId), how)
            .AddField(Strings.ConfessionsReportUser(guildId), $"{ctx.User} | {ctx.User.Id}")
            .AddField(Strings.ConfessionsServerId(guildId), serverId);
        try
        {
            var invites = await reportedGuild.GetInvitesAsync().ConfigureAwait(false);
            eb.AddField(Strings.ConfessionsServerInvite(guildId), $"{FluxerContext.Client.Config.InviteUrl}/{invites.First().Code}");
        }
        catch
        {
            eb.AddField(Strings.ConfessionsServerInvite(guildId), Strings.ConfessionsMissingInvite(guildId));
        }

        await channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
        await ctx.Channel.SendErrorAsync(Strings.ConfessionsReportSent(guildId), Config).ConfigureAwait(false);
    }
}
