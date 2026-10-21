using Humanizer;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Moderation.Services;
using Mewdeko.Modules.Utility.Services;

namespace Mewdeko.Modules.Utility;

public partial class Utility
{
    /// <summary>
    ///     Displays the banner of a specified user or the command invoker.
    /// </summary>
    /// <param name="usr">Optional. The user whose banner to display.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task Banner([Remainder] SocketGuildMember? usr = null)
    {
        usr ??= ctx.Member!;
        var bannerUrl = usr.GetBannerUrl(2048);
        if (bannerUrl == null)
        {
            await ReplyErrorAsync(Strings.AvatarNone(ctx.Guild.Id, usr.ToString())).ConfigureAwait(false);
            return;
        }

        await ctx.Channel.SendAsync(embed: new EmbedBuilder()
                .WithOkColor()
                .AddField(efb => efb.WithName("Username").WithValue(usr.ToString()).WithIsInline(true))
                .AddField(efb =>
                    efb.WithName("Guild Banner Url")
                        .WithValue($"[Link]({bannerUrl})").WithIsInline(true))
                .WithImageUrl(bannerUrl).Build())
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Commands for displaying information about various entities within the guild.
    /// </summary>
    /// <param name="client">The Discord client used for fetching information.</param>
    /// <param name="muteService">The mute service for fetching mute information.</param>
    [Group]
    public class InfoCommands(FluxerClient client, MuteService muteService) : MewdekoSubmodule<UtilityService>
    {
        /// <summary>
        ///     Displays information about a specified role within the guild.
        /// </summary>
        /// <param name="role">The role to gather information about.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task RInfo(SocketRole role)
        {
            var eb = new EmbedBuilder().WithTitle(role.Name)
                .AddField("Users in role",
                    (await ctx.Guild.GetUsersAsync().ConfigureAwait(false)).Count(x => x.RoleIds.Contains(role.Id)))
                .AddField("Is Mentionable", role.IsMentionable)
                .AddField("Is Hoisted", role.IsHoisted).AddField("Color", role.Color)
                .AddField("Permissions", string.Join(",", role.Permissions))
                .AddField("Creation Date", TimestampTag.FromDateTimeOffset(role.CreatedAt))
                .AddField("Position", role.Position)
                .AddField("ID", role.Id)
                .WithColor(role.Colour);
            await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        /// <summary>
        ///     Displays information about a specified voice channel or the user's current voice channel.
        /// </summary>
        /// <param name="channel">The voice channel to gather information about, optional.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task VInfo([Remainder] VoiceChannel? channel = null)
        {
            var voiceChannel = ctx.Member!.VoiceChannel;
            var eb = new EmbedBuilder();
            switch (voiceChannel)
            {
                case null when channel == null:
                    await ctx.Channel.SendErrorAsync(
                            Strings.NotInVoice(ctx.Guild.Id),
                            Config)
                        .ConfigureAwait(false);
                    return;
                // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                case null when channel is not null:
                    eb.WithTitle(channel.Name);
                    eb.AddField("Users", channel.ConnectedUsers.Count());
                    eb.AddField("Created On", channel.CreatedAt);
                    eb.AddField("Bitrate", channel.Bitrate);
                    eb.AddField("User Limit", channel.UserLimit == null ? "Infinite" : channel.UserLimit);
                    eb.AddField("Channel ID", channel.Id);
                    eb.WithOkColor();
                    await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
                    break;
            }

            if (voiceChannel is not null && channel is not null)
            {
                eb.WithTitle(channel.Name);
                eb.AddField("Users", channel.ConnectedUsers.Count());
                eb.AddField("Created On", channel.CreatedAt);
                eb.AddField("Bitrate", channel.Bitrate);
                eb.AddField("User Limit", channel.UserLimit == null ? "Infinite" : channel.UserLimit);
                eb.AddField("Channel ID", channel.Id);
                eb.WithOkColor();
                await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
            }

            if (voiceChannel is not null && channel is null)
            {
                eb.WithTitle(voiceChannel.Name);
                eb.AddField("Users", voiceChannel.ConnectedUsers.Count());
                eb.AddField("Created On", voiceChannel.CreatedAt);
                eb.AddField("Bitrate", voiceChannel.Bitrate);
                eb.AddField("User Limit", voiceChannel.UserLimit == null ? "Infinite" : voiceChannel.UserLimit);
                eb.AddField("Channel ID", voiceChannel.Id);
                eb.WithOkColor();
                await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Fetches and displays information about a user by their Discord ID.
        /// </summary>
        /// <param name="id">The Discord ID of the user.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task Fetch(ulong id)
        {
            var usr = await client.Rest.GetUserAsync(id).ConfigureAwait(false);
            if (usr is null)
            {
                await ctx.Channel.SendErrorAsync(
                    Strings.UserNotFoundId(ctx.Guild.Id), Config);
            }
            else
            {
                var embed = new EmbedBuilder()
                    .WithTitle(Strings.FetchedUserInfo(ctx.Guild.Id))
                    .AddField("Username", usr)
                    .AddField("Created At", TimestampTag.FromDateTimeOffset(usr.CreatedAt))
                    .AddField("Public Flags", usr.Flags)
                    .WithImageUrl(usr.RealAvatarUrl().ToString())
                    .WithOkColor();
                await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Displays detailed information about the server.
        /// </summary>
        /// <param name="guildName">Optional. The name of the guild to display information about.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task ServerInfo(string? guildName = null)
        {
            var channel = (Channel)ctx.Channel;
            guildName = guildName?.ToUpperInvariant();
            SocketGuild guild;
            if (string.IsNullOrWhiteSpace(guildName))
            {
                guild = (SocketGuild)channel.Guild;
            }
            else
            {
                guild = client.Guilds.FirstOrDefault(g =>
                    string.Equals(g.Name, guildName, StringComparison.InvariantCultureIgnoreCase));
            }

            if (guild == null)
                return;
            var ownername = guild.GetUser(guild.OwnerId);

            var embed = new EmbedBuilder()
                .WithAuthor(eab => eab.WithName(Strings.ServerInfo(ctx.Guild.Id)))
                .WithTitle(guild.Name)
                .AddField("Id", guild.Id.ToString())
                .AddField("Owner", ownername.Mention)
                .AddField("Total Users", guild.Users.Count.ToString())
                .AddField("Created On", TimestampTag.FromDateTimeOffset(guild.CreatedAt))
                .WithColor(Mewdeko.OkColor);
            if (guild.GetInviteSplashUrl(2048) is { } splashUrl)
                embed.WithImageUrl(splashUrl);
            if (Uri.IsWellFormedUriString(guild.IconUrl, UriKind.Absolute))
                embed.WithThumbnailUrl(guild.IconUrl);
            if (guild.Emotes.Count > 0)
            {
                embed.AddField(fb =>
                    fb.WithName($"{Strings.CustomEmojis(ctx.Guild.Id)}({guild.Emotes.Count})")
                        .WithValue(string.Join(" ", guild.Emotes
                                .SecureShuffle()
                                .Take(30)
                                .Select(e => $"{e}"))
                            .TrimTo(1024)));
            }

            await ctx.Channel.SendAsync(embed: embed.Build())
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     Displays information about a specified text channel or the current channel.
        /// </summary>
        /// <param name="channel">Optional. The text channel to gather information about.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task ChannelInfo(TextChannel? channel = null)
        {
            var ch = channel ?? (Channel)ctx.Channel;
            var embed = new EmbedBuilder()
                .WithTitle(ch.Name)
                .AddField(Strings.Id(ctx.Guild.Id), ch.Id.ToString())
                .AddField(Strings.CreatedAt(ctx.Guild.Id), TimestampTag.FromDateTimeOffset(ch.CreatedAt))
                .AddField(Strings.Users(ctx.Guild.Id),
                    ch.Guild?.Users.Count(x => x.GetPermissions(ch).ViewChannel) ?? 0)
                .AddField("NSFW", ch.IsNsfw)
                .AddField("Slowmode Interval", TimeSpan.FromSeconds(ch.SlowModeInterval).Humanize())
                .AddField("Default Thread Archive Duration", ch.DefaultAutoArchiveDuration)
                .WithColor(Mewdeko.OkColor);
            if (!string.IsNullOrWhiteSpace(ch.Topic))
                embed.WithDescription(ch.Topic);
            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
        }

        /// <summary>
        ///     Displays detailed information about a specified user or the command invoker.
        /// </summary>
        /// <param name="usr">Optional. The user to gather information about.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task UserInfo(SocketGuildMember? usr = null)
        {
            var user = usr ?? ctx.Member!;
            var userbanner = user.GetBannerUrl(2048);
            var serverUserType = user.GuildPermissions.Administrator ? "Administrator" : "Regular User";
            var embed = new EmbedBuilder()
                .AddField("Username", user.ToString())
                .WithColor(user.AccentColor is { } accent ? new Color((uint)accent) : Mewdeko.OkColor);

            if (!string.IsNullOrWhiteSpace(user.Nickname))
                embed.AddField("Nickname", user.Nickname);

            embed.AddField("User Id", user.Id)
                .AddField("User Type", serverUserType)
                .AddField("Joined Server", TimestampTag.FromDateTimeOffset(user.JoinedAt))
                .AddField("Joined Fluxer", TimestampTag.FromDateTimeOffset(user.CreatedAt))
                .AddField("Role Count", user.Roles.Count(r => r.Id != r.Guild.EveryoneRole.Id));

            if (FluxerContext.Snapshots.GetPresence(user.Id)?.CustomStatusText is { Length: > 0 } customStatus)
                embed.AddField("Status", customStatus);

            var av = user.RealAvatarUrl();
            if (av.IsAbsoluteUri)
            {
                if (userbanner is not null)
                {
                    embed.WithThumbnailUrl(av.ToString());
                    embed.WithImageUrl(userbanner);
                }
                else
                {
                    embed.WithImageUrl(av.ToString());
                }
            }

            var moreInfo = Emoji.FromUnicode("ℹ️");
            embed.WithFooter($"{moreInfo} {Strings.MoreInfo(ctx.Guild.Id)}");
            var msg = await ctx.Channel.SendAsync(embed: embed.Build()).ConfigureAwait(false);
            var input = await Interactive.WaitForReactionAsync(msg, ctx.User.Id, [moreInfo]).ConfigureAwait(false);
            if (input == 0)
            {
                embed.Footer = null;
                if (user.Roles.Any(x => x.Id != ctx.Guild.EveryoneRole.Id))
                {
                    embed.AddField("Roles",
                        string.Join("", user.Roles.OrderBy(x => x.Position).Select(x => x.Mention)));
                }

                embed.AddField("Deafened", user.IsDeaf);
                embed.AddField("Is VC Muted", user.IsMute);
                embed.AddField("Is Server Muted",
                    user.Roles.Contains(await muteService.GetMuteRole(ctx.Guild).ConfigureAwait(false)));
                await msg.ModifyAsync(x => x.Embed = embed.Build()).ConfigureAwait(false);
            }

            try
            {
                await msg.RemoveAllReactionsAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        /// <summary>
        ///     Displays the avatar of a specified user or the command invoker. Has a button to view the guild avatar if available.
        /// </summary>
        /// <param name="usr">Optional. The user whose avatar to display.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task Avatar([Remainder] SocketGuildMember? usr = null)
        {
            usr ??= ctx.Member!;
            var avatarUrl = usr.GetAvatarUrl(2048);

            if (avatarUrl == null)
            {
                await ReplyErrorAsync(Strings.AvatarNone(ctx.Guild.Id, usr.ToString())).ConfigureAwait(false);
                return;
            }

            await ctx.Channel.SendAsync(embed: new EmbedBuilder()
                    .WithOkColor()
                    .AddField(efb => efb.WithName("Username").WithValue(usr.ToString()).WithIsInline(true))
                    .AddField(efb =>
                        efb.WithName($"{(usr.AvatarHash is null ? "" : "Guild")} Avatar Url")
                            .WithValue($"[Link]({avatarUrl})").WithIsInline(true))
                    .WithImageUrl(avatarUrl).Build())
                .ConfigureAwait(false);
        }
    }
}