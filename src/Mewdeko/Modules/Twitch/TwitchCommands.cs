using Discord.Commands;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Twitch.Common;
using Mewdeko.Modules.Twitch.Services;

namespace Mewdeko.Modules.Twitch;

/// <summary>
///     Text commands for configuring the Twitch bot integration of a server.
/// </summary>
public class TwitchCommands : MewdekoModuleBase<TwitchService>
{
    /// <summary>
    ///     Sets the Twitch channel the bot joins and enables the command processor for this server.
    /// </summary>
    /// <param name="twitchChannel">The Twitch channel name, with or without the # prefix.</param>
    /// <param name="prefix">The command prefix used in Twitch chat.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchSet(string twitchChannel, string prefix = "!")
    {
        await Service.JoinChannelAsync(ctx.Guild.Id, twitchChannel, prefix).ConfigureAwait(false);
        var channel = twitchChannel.TrimStart('#').ToLowerInvariant();
        await ConfirmAsync(Strings.TwitchChannelSet(ctx.Guild.Id, channel, prefix)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes the Twitch channel configuration of this server and leaves the channel.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRemove()
    {
        var config = await Service.GetConfigAsync(ctx.Guild.Id).ConfigureAwait(false);
        if (config is null)
        {
            await ErrorAsync(Strings.TwitchNotSet(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        await Service.LeaveChannelAsync(ctx.Guild.Id).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchChannelRemoved(ctx.Guild.Id, config.TwitchChannel)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Enables the Twitch integration of this server.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchEnable()
    {
        var ok = await Service.SetEnabledAsync(ctx.Guild.Id, true).ConfigureAwait(false);
        await ReplyResultAsync(ok, Strings.TwitchEnabled(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Disables the Twitch integration of this server without deleting its configuration.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchDisable()
    {
        var ok = await Service.SetEnabledAsync(ctx.Guild.Id, false).ConfigureAwait(false);
        await ReplyResultAsync(ok, Strings.TwitchDisabled(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Shows the Twitch configuration of this server.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchConfig()
    {
        var config = await Service.GetConfigAsync(ctx.Guild.Id).ConfigureAwait(false);
        if (config is null)
        {
            await ErrorAsync(Strings.TwitchNotSet(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        var goLiveValue = config.GoLiveChannelId.HasValue
            ? $"<#{config.GoLiveChannelId.Value}>"
            : Strings.None(ctx.Guild.Id);

        var embed = new EmbedBuilder()
            .WithOkColor()
            .WithTitle(Strings.TwitchConfigTitle(ctx.Guild.Id))
            .AddField(Strings.TwitchConfigChannel(ctx.Guild.Id), $"#{config.TwitchChannel}", true)
            .AddField(Strings.TwitchConfigPrefix(ctx.Guild.Id), $"`{config.CommandPrefix}`", true)
            .AddField(Strings.TwitchConfigEnabled(ctx.Guild.Id),
                config.Enabled ? Strings.Yes(ctx.Guild.Id) : Strings.No(ctx.Guild.Id), true)
            .AddField(Strings.TwitchConfigGolive(ctx.Guild.Id), goLiveValue, true);

        if (!string.IsNullOrWhiteSpace(config.Language))
            embed.AddField(Strings.TwitchConfigLanguage(ctx.Guild.Id), config.Language, true);

        await ctx.Channel.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
    }

    /// <summary>
    ///     Shows Twitch integration health and configuration status.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchStatus()
    {
        var status = await Service.GetStatusSummaryAsync(ctx.Guild.Id).ConfigureAwait(false);
        var embed = new EmbedBuilder()
            .WithOkColor()
            .WithTitle(Strings.TwitchStatusTitle(ctx.Guild.Id))
            .WithDescription($"```text\n{status}\n```");
        await ctx.Channel.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
    }

    /// <summary>
    ///     Changes the command prefix used in Twitch chat.
    /// </summary>
    /// <param name="prefix">The new prefix.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchPrefix(string prefix)
    {
        var config = await Service.GetConfigAsync(ctx.Guild.Id).ConfigureAwait(false);
        if (config is null)
        {
            await ErrorAsync(Strings.TwitchNotSet(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        await Service.JoinChannelAsync(ctx.Guild.Id, config.TwitchChannel, prefix).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchPrefixSet(ctx.Guild.Id, prefix)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the language of Twitch chat responses, overriding the server locale.
    /// </summary>
    /// <param name="languageTag">A language tag such as en-US or de-DE. Leave empty to reset to the server default.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchLanguage([Remainder] string? languageTag = null)
    {
        var config = await Service.GetConfigAsync(ctx.Guild.Id).ConfigureAwait(false);
        if (config is null)
        {
            await ErrorAsync(Strings.TwitchNotSet(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        var tag = languageTag?.Trim();
        await Service.SetChannelLanguageAsync(ctx.Guild.Id, string.IsNullOrWhiteSpace(tag) ? null : tag)
            .ConfigureAwait(false);

        await ConfirmAsync(string.IsNullOrWhiteSpace(tag)
            ? Strings.TwitchLanguageCleared(ctx.Guild.Id)
            : Strings.TwitchLanguageSet(ctx.Guild.Id, tag)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Updates the title and category of the Twitch stream.
    /// </summary>
    /// <param name="title">The new stream title, or nothing to keep the current one.</param>
    /// <param name="category">The new category name, or nothing to keep the current one.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchStreamInfo(string? title = null, string? category = null)
    {
        var ok = await Service.UpdateStreamInfoAsync(ctx.Guild.Id, title, category).ConfigureAwait(false);
        await ReplyResultAsync(ok, Strings.TwitchStreamInfoUpdated(ctx.Guild.Id),
            Strings.TwitchStreamInfoFailed(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates a clip of the current Twitch stream.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchClip()
    {
        var clipUrl = await Service.CreateClipAsync(ctx.Guild.Id).ConfigureAwait(false);
        var ok = !string.IsNullOrWhiteSpace(clipUrl);
        await ReplyResultAsync(ok, ok ? Strings.TwitchClipCreated(ctx.Guild.Id, clipUrl) : string.Empty,
            Strings.TwitchClipFailed(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Links a member to their Twitch account so the bot recognises them in Twitch chat.
    /// </summary>
    /// <param name="user">The member to link.</param>
    /// <param name="twitchUsername">Their Twitch login name.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchLink(IGuildUser user, string twitchUsername)
    {
        await Service.LinkAccountAsync(ctx.Guild.Id, user.Id, twitchUsername).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchLinkSet(ctx.Guild.Id, user.Mention, twitchUsername.ToLowerInvariant()))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes the Twitch account link of a member.
    /// </summary>
    /// <param name="user">The member whose link is removed.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchUnlink(IGuildUser user)
    {
        var existing = await Service.GetLinkedTwitchUsernameAsync(ctx.Guild.Id, user.Id).ConfigureAwait(false);
        if (existing is null)
        {
            await ErrorAsync(Strings.TwitchLinkNotFound(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        await Service.UnlinkAccountAsync(ctx.Guild.Id, user.Id).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchLinkRemoved(ctx.Guild.Id, user.Mention)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists the member to Twitch account links of this server.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchLinks()
    {
        var links = await Service.GetAllLinksAsync(ctx.Guild.Id).ConfigureAwait(false);
        List<string> lines = links.Count == 0
            ? [Strings.TwitchLinksEmpty(ctx.Guild.Id)]
            : links.Select(l =>
                Strings.TwitchLinkEntry(ctx.Guild.Id, $"<@{l.DiscordUserId}>", l.TwitchUsername)).ToList();

        await SendListAsync(Strings.TwitchLinksTitle(ctx.Guild.Id), lines).ConfigureAwait(false);
    }

    /// <summary>
    ///     Claims a link code generated by the link command in Twitch chat.
    /// </summary>
    /// <param name="code">The claim code.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchClaim(string code)
    {
        var result = await Service.ClaimLinkCodeAsync(ctx.Guild.Id, ctx.User.Id, code).ConfigureAwait(false);
        var failure = result.ErrorKey == "twitch_claim_expired"
            ? Strings.TwitchClaimExpired(ctx.Guild.Id)
            : Strings.TwitchClaimInvalid(ctx.Guild.Id);
        await ReplyResultAsync(result.Success, Strings.TwitchClaimSuccess(ctx.Guild.Id, result.TwitchUsername),
            failure).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds or updates a custom Twitch chat command.
    /// </summary>
    /// <param name="name">The command name. Put the response in quotes when it has spaces.</param>
    /// <param name="response">The text the command answers with.</param>
    /// <param name="permission">Who may run it: Everyone, Subscriber, Vip, Moderator or Broadcaster.</param>
    /// <param name="cooldownSeconds">Seconds before the command can be used again.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchCommandAdd(string name, string response,
        TwitchCommandPermission permission = TwitchCommandPermission.Everyone, int cooldownSeconds = 0)
    {
        var command = await Service.UpsertCustomCommandAsync(ctx.Guild.Id, name, response, permission,
            cooldownSeconds).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchCommandSaved(ctx.Guild.Id, command.Name)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a custom Twitch chat command.
    /// </summary>
    /// <param name="name">The command name.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchCommandRemove(string name)
    {
        var removed = await Service.RemoveCustomCommandAsync(ctx.Guild.Id, name).ConfigureAwait(false);
        await ReplyResultAsync(removed, Strings.TwitchCommandRemoved(ctx.Guild.Id, name),
            Strings.TwitchCommandNotFound(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists the custom Twitch chat commands.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchCommandList()
    {
        var commands = await Service.GetCustomCommandsAsync(ctx.Guild.Id).ConfigureAwait(false);
        List<string> lines = commands.Count == 0
            ? [Strings.TwitchCommandsEmpty(ctx.Guild.Id)]
            : commands.Select(c =>
                Strings.TwitchCommandEntry(ctx.Guild.Id, c.Name,
                    ((TwitchCommandPermission)c.PermissionLevel).ToString(),
                    c.CooldownSeconds, c.UseCount)).ToList();

        await SendListAsync(Strings.TwitchCommandsTitle(ctx.Guild.Id), lines).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the value of a Twitch counter.
    /// </summary>
    /// <param name="name">The counter name.</param>
    /// <param name="value">The new value.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchCounterSet(string name, int value)
    {
        var updated = await Service.SetCounterAsync(ctx.Guild.Id, name, value).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchCounterSet(ctx.Guild.Id, name, updated)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists the Twitch counters.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchCounterList()
    {
        var counters = await Service.GetCountersAsync(ctx.Guild.Id).ConfigureAwait(false);
        List<string> lines = counters.Count == 0
            ? [Strings.TwitchCountersEmpty(ctx.Guild.Id)]
            : counters.Select(c => Strings.TwitchCounterEntry(ctx.Guild.Id, c.Name, c.Value)).ToList();

        await SendListAsync(Strings.TwitchCountersTitle(ctx.Guild.Id), lines).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the answer of the Twitch schedule chat command.
    /// </summary>
    /// <param name="message">The schedule message.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchScheduleSet([Remainder] string message)
    {
        await Service.SetScheduleMessageAsync(ctx.Guild.Id, message).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchScheduleSet(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Clears the answer of the Twitch schedule chat command.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchScheduleClear()
    {
        await Service.SetScheduleMessageAsync(ctx.Guild.Id, null).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchScheduleCleared(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the answer of the Twitch socials chat command.
    /// </summary>
    /// <param name="message">The socials message.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchSocialsSet([Remainder] string message)
    {
        await Service.SetSocialsMessageAsync(ctx.Guild.Id, message).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchSocialsSet(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Clears the answer of the Twitch socials chat command.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchSocialsClear()
    {
        await Service.SetSocialsMessageAsync(ctx.Guild.Id, null).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchSocialsCleared(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds or updates a repeating Twitch chat message timer.
    /// </summary>
    /// <param name="name">The timer name.</param>
    /// <param name="messages">The message to send. Put it in quotes and separate several messages with new lines.</param>
    /// <param name="intervalMinutes">Minutes between messages.</param>
    /// <param name="minChatMessages">Chat messages needed between timer messages.</param>
    /// <param name="onlineOnly">Only send while the stream is live.</param>
    /// <param name="randomizeMessages">Pick messages at random instead of in order.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchTimerAdd(string name, string messages, int intervalMinutes = 10,
        int minChatMessages = 5, bool onlineOnly = true, bool randomizeMessages = false)
    {
        var timer = await Service.UpsertTimerAsync(ctx.Guild.Id, name, messages, intervalMinutes, minChatMessages,
            onlineOnly, randomizeMessages, true).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchTimerSaved(ctx.Guild.Id, timer.Name)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a repeating Twitch chat message timer.
    /// </summary>
    /// <param name="name">The timer name.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchTimerRemove(string name)
    {
        var removed = await Service.RemoveTimerAsync(ctx.Guild.Id, name).ConfigureAwait(false);
        await ReplyResultAsync(removed, Strings.TwitchTimerRemoved(ctx.Guild.Id, name),
            Strings.TwitchTimerNotFound(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists the repeating Twitch chat message timers.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchTimerList()
    {
        var timers = await Service.GetTimersAsync(ctx.Guild.Id).ConfigureAwait(false);
        List<string> lines = timers.Count == 0
            ? [Strings.TwitchTimersEmpty(ctx.Guild.Id)]
            : timers.Select(t => Strings.TwitchTimerEntry(ctx.Guild.Id, t.Name, t.IntervalMinutes,
                t.MinChatMessages,
                t.OnlineOnly ? Strings.TwitchTimerOnlineOnly(ctx.Guild.Id) : Strings.TwitchTimerAlways(ctx.Guild.Id),
                t.Enabled ? Strings.TwitchTimerEnabled(ctx.Guild.Id) : Strings.TwitchTimerDisabled(ctx.Guild.Id))).ToList();

        await SendListAsync(Strings.TwitchTimersTitle(ctx.Guild.Id), lines).ConfigureAwait(false);
    }

    /// <summary>
    ///     Enables a repeating Twitch chat message timer.
    /// </summary>
    /// <param name="name">The timer name.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public Task TwitchTimerEnable(string name)
    {
        return SetTimerStateAsync(name, true);
    }

    /// <summary>
    ///     Disables a repeating Twitch chat message timer.
    /// </summary>
    /// <param name="name">The timer name.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public Task TwitchTimerDisable(string name)
    {
        return SetTimerStateAsync(name, false);
    }

    /// <summary>
    ///     Sends a repeating Twitch chat message timer to chat right away.
    /// </summary>
    /// <param name="name">The timer name.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchTimerTest(string name)
    {
        var sent = await Service.TestTimerAsync(ctx.Guild.Id, name).ConfigureAwait(false);
        await ReplyResultAsync(sent is not null, sent is null ? string.Empty : Strings.TwitchTimerSent(ctx.Guild.Id, sent),
            Strings.TwitchTimerNotFound(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the channel for go-live notifications, with an optional custom message.
    /// </summary>
    /// <param name="channel">The channel to post in.</param>
    /// <param name="message">A message that can use %streamer%, %title%, %game% and %url%.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchGoLive(ITextChannel channel, [Remainder] string? message = null)
    {
        var config = await Service.GetConfigAsync(ctx.Guild.Id).ConfigureAwait(false);
        if (config is null)
        {
            await ErrorAsync(Strings.TwitchNotSet(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        await Service.SetGoLiveChannelAsync(ctx.Guild.Id, channel.Id, message).ConfigureAwait(false);
        await ConfirmAsync(message is not null
            ? Strings.TwitchGoliveMessageSet(ctx.Guild.Id, channel.Mention)
            : Strings.TwitchGoliveSet(ctx.Guild.Id, channel.Mention)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Clears the go-live notification channel.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchGoLiveClear()
    {
        var config = await Service.GetConfigAsync(ctx.Guild.Id).ConfigureAwait(false);
        if (config is null)
        {
            await ErrorAsync(Strings.TwitchNotSet(ctx.Guild.Id)).ConfigureAwait(false);
            return;
        }

        await Service.SetGoLiveChannelAsync(ctx.Guild.Id, 0, null).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchGoliveCleared(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the channel for Twitch subscription notifications.
    /// </summary>
    /// <param name="channel">The channel to post in.</param>
    /// <param name="message">An optional custom message.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchSubChannel(ITextChannel channel, [Remainder] string? message = null)
    {
        await Service.SetSubNotificationChannelAsync(ctx.Guild.Id, channel.Id, message).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchSubSet(ctx.Guild.Id, channel.Mention)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Disables Twitch subscription notifications.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchSubClear()
    {
        await Service.SetSubNotificationChannelAsync(ctx.Guild.Id, 0, null).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchSubCleared(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the channel for Twitch raid notifications.
    /// </summary>
    /// <param name="channel">The channel to post in.</param>
    /// <param name="message">An optional custom message.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRaidChannel(ITextChannel channel, [Remainder] string? message = null)
    {
        await Service.SetRaidNotificationChannelAsync(ctx.Guild.Id, channel.Id, message).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchRaidSet(ctx.Guild.Id, channel.Mention)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Disables Twitch raid notifications.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRaidClear()
    {
        await Service.SetRaidNotificationChannelAsync(ctx.Guild.Id, 0, null).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchRaidCleared(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the channel for end of stream recaps.
    /// </summary>
    /// <param name="channel">The channel to post in.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRecapChannel([Remainder] ITextChannel channel)
    {
        await Service.SetStreamRecapChannelAsync(ctx.Guild.Id, channel.Id, true).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchRecapSet(ctx.Guild.Id, channel.Mention)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Disables stream recap posts.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRecapClear()
    {
        await Service.SetStreamRecapChannelAsync(ctx.Guild.Id, 0, false).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchRecapCleared(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Syncs a Twitch rank to a Discord role.
    /// </summary>
    /// <param name="twitchRank">The Twitch rank: Everyone, Subscriber, Vip, Moderator or Broadcaster.</param>
    /// <param name="role">The role members with that rank receive.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRoleSyncAdd(TwitchPermissionLevel twitchRank, IRole role)
    {
        await Service.UpsertRoleSyncMappingAsync(ctx.Guild.Id, twitchRank, role.Id).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchRolesyncSaved(ctx.Guild.Id, twitchRank.ToString(), role.Mention))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a Twitch rank to Discord role sync.
    /// </summary>
    /// <param name="twitchRank">The Twitch rank.</param>
    /// <param name="role">The synced role.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRoleSyncRemove(TwitchPermissionLevel twitchRank, IRole role)
    {
        var removed = await Service.RemoveRoleSyncMappingAsync(ctx.Guild.Id, twitchRank, role.Id)
            .ConfigureAwait(false);
        await ReplyResultAsync(removed,
            Strings.TwitchRolesyncRemoved(ctx.Guild.Id, twitchRank.ToString(), role.Mention),
            Strings.TwitchRolesyncNotFound(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists the Twitch rank to Discord role syncs.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchRoleSyncList()
    {
        var mappings = await Service.GetRoleSyncMappingsAsync(ctx.Guild.Id).ConfigureAwait(false);
        List<string> lines = mappings.Count == 0
            ? [Strings.TwitchRolesyncEmpty(ctx.Guild.Id)]
            : mappings.Select(m =>
                Strings.TwitchRolesyncEntry(ctx.Guild.Id, ((TwitchPermissionLevel)m.PermissionLevel).ToString(),
                    $"<@&{m.RoleId}>")).ToList();

        await SendListAsync(Strings.TwitchRolesyncTitle(ctx.Guild.Id), lines).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds or updates a channel point redemption action.
    /// </summary>
    /// <param name="rewardTitle">The title of the channel point reward. Put it in quotes when it has spaces.</param>
    /// <param name="twitchResponse">What the bot answers in Twitch chat.</param>
    /// <param name="discordChannel">A channel to announce the redemption in.</param>
    /// <param name="discordMessage">The announcement text.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRedemptionAdd(string rewardTitle, string? twitchResponse = null,
        ITextChannel? discordChannel = null, [Remainder] string? discordMessage = null)
    {
        var action = await Service.UpsertRedemptionActionAsync(ctx.Guild.Id, rewardTitle, twitchResponse,
            discordChannel?.Id, discordMessage).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchRedemptionSaved(ctx.Guild.Id, action.RewardTitle)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a channel point redemption action.
    /// </summary>
    /// <param name="rewardTitle">The title of the channel point reward.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRedemptionRemove([Remainder] string rewardTitle)
    {
        var removed = await Service.RemoveRedemptionActionAsync(ctx.Guild.Id, rewardTitle).ConfigureAwait(false);
        await ReplyResultAsync(removed, Strings.TwitchRedemptionRemoved(ctx.Guild.Id, rewardTitle),
            Strings.TwitchRedemptionNotFound(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists the channel point redemption actions.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchRedemptionList()
    {
        var actions = await Service.GetRedemptionActionsAsync(ctx.Guild.Id).ConfigureAwait(false);
        List<string> lines = actions.Count == 0
            ? [Strings.TwitchRedemptionsEmpty(ctx.Guild.Id)]
            : actions.Select(a => Strings.TwitchRedemptionEntry(ctx.Guild.Id, a.RewardTitle,
                a.DiscordChannelId.HasValue ? $"<#{a.DiscordChannelId.Value}>" : "-",
                string.IsNullOrWhiteSpace(a.TwitchResponse) ? "-" : Strings.Yes(ctx.Guild.Id))).ToList();

        await SendListAsync(Strings.TwitchRedemptionsTitle(ctx.Guild.Id), lines).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds or updates a Twitch raid target suggestion.
    /// </summary>
    /// <param name="twitchLogin">The Twitch login of the channel.</param>
    /// <param name="note">An optional note about the target.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRaidTargetAdd(string twitchLogin, [Remainder] string? note = null)
    {
        var target = await Service.UpsertRaidTargetAsync(ctx.Guild.Id, twitchLogin, note).ConfigureAwait(false);
        await ConfirmAsync(Strings.TwitchRaidtargetSaved(ctx.Guild.Id, target.TwitchLogin)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a Twitch raid target suggestion.
    /// </summary>
    /// <param name="twitchLogin">The Twitch login of the channel.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task TwitchRaidTargetRemove(string twitchLogin)
    {
        var removed = await Service.RemoveRaidTargetAsync(ctx.Guild.Id, twitchLogin).ConfigureAwait(false);
        await ReplyResultAsync(removed, Strings.TwitchRaidtargetRemoved(ctx.Guild.Id, twitchLogin),
            Strings.TwitchRaidtargetNotFound(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists the Twitch raid target suggestions.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task TwitchRaidTargetList()
    {
        var targets = await Service.GetRaidTargetsAsync(ctx.Guild.Id).ConfigureAwait(false);
        List<string> lines = targets.Count == 0
            ? [Strings.TwitchRaidtargetsEmpty(ctx.Guild.Id)]
            : targets.Select(t => Strings.TwitchRaidtargetEntry(ctx.Guild.Id, t.TwitchLogin,
                string.IsNullOrWhiteSpace(t.Note) ? "-" : t.Note)).ToList();

        await SendListAsync(Strings.TwitchRaidtargetsTitle(ctx.Guild.Id), lines).ConfigureAwait(false);
    }

    private async Task SetTimerStateAsync(string name, bool enabled)
    {
        var updated = await Service.SetTimerEnabledAsync(ctx.Guild.Id, name, enabled).ConfigureAwait(false);
        var state = enabled ? Strings.TwitchTimerEnabled(ctx.Guild.Id) : Strings.TwitchTimerDisabled(ctx.Guild.Id);
        await ReplyResultAsync(updated, Strings.TwitchTimerState(ctx.Guild.Id, state, name),
            Strings.TwitchTimerNotFound(ctx.Guild.Id)).ConfigureAwait(false);
    }

    private async Task ReplyResultAsync(bool ok, string success, string? failure = null)
    {
        if (ok)
            await ConfirmAsync(success).ConfigureAwait(false);
        else
            await ErrorAsync(failure ?? Strings.TwitchNotSet(ctx.Guild.Id)).ConfigureAwait(false);
    }

    private async Task SendListAsync(string title, List<string> lines)
    {
        const int perPage = 10;
        var totalPages = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)perPage));

        var paginator = new LazyPaginatorBuilder()
            .AddUser(ctx.User)
            .WithPageFactory(GeneratePage)
            .WithMaxPageIndex(totalPages - 1)
            .WithFooter(PaginatorFooter.PageNumber)
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
            .WithActionOnTimeout(ActionOnStop.DisableInput)
            .Build();

        await Interactive.SendPaginatorAsync(paginator, ctx.Channel, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        return;

        PageBuilder GeneratePage(int page)
        {
            return new PageBuilder()
                .WithOkColor()
                .WithTitle(title)
                .WithDescription(string.Join('\n', lines.Skip(page * perPage).Take(perPage)));
        }
    }
}
