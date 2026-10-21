using System.Net.Http;
using System.Text.Json;
using DataModel;
using Lavalink4NET;
using Lavalink4NET.DiscordNet;
using Lavalink4NET.Players;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Music.CustomPlayer;
using Mewdeko.Modules.Music.Services;

namespace Mewdeko.Modules.Music;

public partial class Music
{
    /// <summary>
    ///     Submodule for TTS (text-to-speech) commands. Manages per-voice-channel TTS settings,
    ///     user voices, blocking, and guild-wide TTS configuration.
    /// </summary>
    public class MusicTts(
        IAudioService service,
        GuildSettingsService guildSettingsService,
        IDataConnectionFactory dbFactory,
        InteractiveService interactiveService,
        TtsService ttsService) : MewdekoSubmodule
    {
        /// <summary>
        ///     Enables or disables TTS for the voice channel you are currently in.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsEnable()
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var vcId = player.VoiceChannelId;
            var existing = await player.GetTtsVcSettingAsync(vcId);

            if (existing is { Enabled: true })
            {
                await player.UpsertTtsVcSettingAsync(new TtsVoiceChannelSetting
                {
                    VoiceChannelId = vcId,
                    Enabled = false,
                    LinkedTextChannelId = existing.LinkedTextChannelId,
                    AnnounceJoinLeave = existing.AnnounceJoinLeave,
                    JoinFormat = existing.JoinFormat,
                    LeaveFormat = existing.LeaveFormat,
                    AutoJoin = existing.AutoJoin
                });
                await ReplyConfirmAsync(Strings.TtsDisabled(ctx.Guild.Id));
            }
            else
            {
                await player.UpsertTtsVcSettingAsync(new TtsVoiceChannelSetting
                {
                    VoiceChannelId = vcId,
                    Enabled = true,
                    LinkedTextChannelId = existing?.LinkedTextChannelId,
                    AnnounceJoinLeave = existing?.AnnounceJoinLeave ?? false,
                    JoinFormat = existing?.JoinFormat,
                    LeaveFormat = existing?.LeaveFormat,
                    AutoJoin = existing?.AutoJoin ?? false
                });
                await ReplyConfirmAsync(Strings.TtsEnabled(ctx.Guild.Id));
            }
        }

        /// <summary>
        ///     Links a text channel to the current voice channel for TTS.
        ///     Messages in the linked text channel will be read aloud.
        ///     Use without arguments to unlink.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsLink(Channel channel = null)
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var vcId = player.VoiceChannelId;
            var existing = await player.GetTtsVcSettingAsync(vcId);

            await player.UpsertTtsVcSettingAsync(new TtsVoiceChannelSetting
            {
                VoiceChannelId = vcId,
                Enabled = existing?.Enabled ?? true,
                LinkedTextChannelId = channel?.Id,
                AnnounceJoinLeave = existing?.AnnounceJoinLeave ?? false,
                JoinFormat = existing?.JoinFormat,
                LeaveFormat = existing?.LeaveFormat,
                AutoJoin = existing?.AutoJoin ?? false
            });

            if (channel is null)
                await ReplyConfirmAsync(Strings.TtsUnlinked(ctx.Guild.Id));
            else
                await ReplyConfirmAsync(Strings.TtsLinked(ctx.Guild.Id, channel.Id));
        }

        /// <summary>
        ///     Toggles join/leave announcements for the current voice channel.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsAnnounce()
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var vcId = player.VoiceChannelId;
            var existing = await player.GetTtsVcSettingAsync(vcId);
            var newVal = !(existing?.AnnounceJoinLeave ?? false);

            await player.UpsertTtsVcSettingAsync(new TtsVoiceChannelSetting
            {
                VoiceChannelId = vcId,
                Enabled = existing?.Enabled ?? true,
                LinkedTextChannelId = existing?.LinkedTextChannelId,
                AnnounceJoinLeave = newVal,
                JoinFormat = existing?.JoinFormat,
                LeaveFormat = existing?.LeaveFormat,
                AutoJoin = existing?.AutoJoin ?? false
            });

            await ReplyConfirmAsync(newVal
                ? Strings.TtsAnnounceEnabled(ctx.Guild.Id)
                : Strings.TtsAnnounceDisabled(ctx.Guild.Id));
        }

        /// <summary>
        ///     Toggles auto-join for the voice channel you are currently in. When on, the bot joins that channel
        ///     by itself as soon as a member enters it (if it is not already in voice) and leaves again once it
        ///     is empty. The bot does not need to be connected to use this command.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsAutoJoin()
        {
            var vc = (ctx.User as SocketGuildMember)?.VoiceChannel;
            if (vc is null)
            {
                await ReplyErrorAsync(Strings.MusicNotInChannel(ctx.Guild.Id));
                return;
            }

            var newVal = await ttsService.ToggleAutoJoinAsync(ctx.Guild.Id, vc.Id);

            await ReplyConfirmAsync(newVal
                ? Strings.TtsAutojoinEnabled(ctx.Guild.Id, vc.Id)
                : Strings.TtsAutojoinDisabled(ctx.Guild.Id, vc.Id));
        }

        /// <summary>
        ///     Sets the join announcement format for the current voice channel.
        ///     Supports %user.name%, %user.mention%, and %server.name% placeholders.
        ///     Use without arguments to reset to default.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsJoinFormat([Remainder] string format = null)
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var vcId = player.VoiceChannelId;
            var existing = await player.GetTtsVcSettingAsync(vcId);

            await player.UpsertTtsVcSettingAsync(new TtsVoiceChannelSetting
            {
                VoiceChannelId = vcId,
                Enabled = existing?.Enabled ?? true,
                LinkedTextChannelId = existing?.LinkedTextChannelId,
                AnnounceJoinLeave = existing?.AnnounceJoinLeave ?? false,
                JoinFormat = format,
                LeaveFormat = existing?.LeaveFormat,
                AutoJoin = existing?.AutoJoin ?? false
            });

            await ReplyConfirmAsync(string.IsNullOrWhiteSpace(format)
                ? Strings.TtsJoinFormatReset(ctx.Guild.Id)
                : Strings.TtsJoinFormatSet(ctx.Guild.Id, format));
        }

        /// <summary>
        ///     Sets the leave announcement format for the current voice channel.
        ///     Supports %user.name%, %user.mention%, and %server.name% placeholders.
        ///     Use without arguments to reset to default.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsLeaveFormat([Remainder] string format = null)
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var vcId = player.VoiceChannelId;
            var existing = await player.GetTtsVcSettingAsync(vcId);

            await player.UpsertTtsVcSettingAsync(new TtsVoiceChannelSetting
            {
                VoiceChannelId = vcId,
                Enabled = existing?.Enabled ?? true,
                LinkedTextChannelId = existing?.LinkedTextChannelId,
                AnnounceJoinLeave = existing?.AnnounceJoinLeave ?? false,
                JoinFormat = existing?.JoinFormat,
                LeaveFormat = format,
                AutoJoin = existing?.AutoJoin ?? false
            });

            await ReplyConfirmAsync(string.IsNullOrWhiteSpace(format)
                ? Strings.TtsLeaveFormatReset(ctx.Guild.Id)
                : Strings.TtsLeaveFormatSet(ctx.Guild.Id, format));
        }

        /// <summary>
        ///     Sets the TTS volume (0-100). Separate from music volume.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsVolume(int volume)
        {
            if (volume is < 0 or > 100)
            {
                await ReplyErrorAsync(Strings.TtsVolumeInvalid(ctx.Guild.Id));
                return;
            }

            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            await player.UpdateTtsGuildSettingAsync(s => s.TtsVolume = volume);
            await ReplyConfirmAsync(Strings.TtsVolumeSet(ctx.Guild.Id, volume));
        }

        /// <summary>
        ///     Sets the TTS playback speed (0.5-2.0).
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsSpeed(float speed)
        {
            if (speed is < 0.5f or > 2.0f)
            {
                await ReplyErrorAsync(Strings.TtsSpeedInvalid(ctx.Guild.Id));
                return;
            }

            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            await player.UpdateTtsGuildSettingAsync(s => s.TtsSpeed = speed);
            await ReplyConfirmAsync(Strings.TtsSpeedSet(ctx.Guild.Id, speed.ToString("F1")));
        }

        /// <summary>
        ///     Sets the default TTS voice for the guild. Use without arguments to reset.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsDefaultVoice([Remainder] string voice = null)
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            await player.UpdateTtsGuildSettingAsync(s => s.TtsDefaultVoice = voice);

            await ReplyConfirmAsync(string.IsNullOrWhiteSpace(voice)
                ? Strings.TtsDefaultVoiceReset(ctx.Guild.Id)
                : Strings.TtsDefaultVoiceSet(ctx.Guild.Id, voice));
        }

        /// <summary>
        ///     Sets your personal TTS voice for this server. Use without arguments to reset.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task TtsVoice([Remainder] string voice = null)
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            await player.UpsertTtsUserSettingAsync(ctx.User.Id, s => s.Voice = voice);

            await ReplyConfirmAsync(string.IsNullOrWhiteSpace(voice)
                ? Strings.TtsVoiceReset(ctx.Guild.Id)
                : Strings.TtsVoiceSet(ctx.Guild.Id, voice));
        }

        /// <summary>
        ///     Searches available TTS voices by name, language, or gender.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task TtsVoices([Remainder] string search = null)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                await ReplyErrorAsync(Strings.TtsVoicesSearchRequired(ctx.Guild.Id));
                return;
            }

            List<FloweryVoice> voices;
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mewdeko/1.0");
                var json = await http.GetStringAsync("https://api.flowery.pw/v1/tts/voices");
                var response = JsonSerializer.Deserialize<FloweryVoiceResponse>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                voices = response?.Voices ?? [];
            }
            catch
            {
                await ReplyErrorAsync(Strings.TtsVoicesFetchFailed(ctx.Guild.Id));
                return;
            }

            var results = voices
                .Where(v => v.Source is not "SAM")
                .Where(v =>
                    v.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (v.Gender?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (v.Language?.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (v.Language?.Code?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (v.Source?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

            if (results.Count == 0)
            {
                await ReplyErrorAsync(Strings.TtsVoicesNoResults(ctx.Guild.Id, search));
                return;
            }

            const int voicesPerPage = 15;
            var totalPages = Math.Max(1, (int)Math.Ceiling((double)results.Count / voicesPerPage));

            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(GeneratePage)
                .WithMaxPageIndex(totalPages - 1)
                .WithFooter(PaginatorFooter.PageNumber)
                .WithActionOnCancellation(ActionOnStop.DeleteMessage)
                .WithActionOnTimeout(ActionOnStop.DisableInput)
                .Build();

            await interactiveService.SendPaginatorAsync(paginator, ctx.Channel, TimeSpan.FromMinutes(5));

            PageBuilder GeneratePage(int page)
            {
                var startIndex = page * voicesPerPage;
                var lines = results.Skip(startIndex).Take(voicesPerPage).Select((v, i) =>
                    Strings.TtsVoicesEntry(ctx.Guild.Id, startIndex + i + 1, v.Name, v.Gender, v.Language?.Name, v.Source));

                return new PageBuilder()
                    .WithOkColor()
                    .WithTitle(Strings.TtsVoicesTitle(ctx.Guild.Id, search, results.Count))
                    .WithDescription(string.Join("\n", lines));
            }
        }

        /// <summary>
        ///     Blocks or unblocks a user from using TTS in this server.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsBlock(SocketGuildMember user)
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var isBlocked = await player.IsTtsUserBlockedAsync(user.Id);
            await player.UpsertTtsUserSettingAsync(user.Id, s => s.IsBlocked = !isBlocked);

            await ReplyConfirmAsync(!isBlocked
                ? Strings.TtsUserBlocked(ctx.Guild.Id, user.DisplayName)
                : Strings.TtsUserUnblocked(ctx.Guild.Id, user.DisplayName));
        }

        /// <summary>
        ///     Lists all users blocked from TTS in this server.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsBlockList()
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            var blockedUsers = await db.GetTable<TtsUserSetting>()
                .Where(x => x.GuildId == ctx.Guild.Id && x.IsBlocked)
                .ToListAsync();

            if (blockedUsers.Count == 0)
            {
                await ReplyConfirmAsync(Strings.TtsBlockListEmpty(ctx.Guild.Id));
                return;
            }

            const int usersPerPage = 10;
            var totalPages = Math.Max(1, (int)Math.Ceiling((double)blockedUsers.Count / usersPerPage));

            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(GeneratePage)
                .WithMaxPageIndex(totalPages - 1)
                .WithFooter(PaginatorFooter.PageNumber)
                .WithActionOnCancellation(ActionOnStop.DeleteMessage)
                .WithActionOnTimeout(ActionOnStop.DisableInput)
                .Build();

            await interactiveService.SendPaginatorAsync(paginator, ctx.Channel, TimeSpan.FromMinutes(5));

            PageBuilder GeneratePage(int page)
            {
                var startIndex = page * usersPerPage;
                var lines = blockedUsers.Skip(startIndex).Take(usersPerPage).Select((x, i) =>
                    $"`{startIndex + i + 1}.` <@{x.UserId}>");

                return new PageBuilder()
                    .WithOkColor()
                    .WithTitle(Strings.TtsBlockListTitle(ctx.Guild.Id, blockedUsers.Count))
                    .WithDescription(string.Join("\n", lines));
            }
        }

        /// <summary>
        ///     Sets the role required to use TTS. Use without arguments to remove the restriction.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsRole(SocketRole role = null)
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            await player.UpdateTtsGuildSettingAsync(s => s.TtsRoleId = role?.Id);

            await ReplyConfirmAsync(role is null
                ? Strings.TtsRoleRemoved(ctx.Guild.Id)
                : Strings.TtsRoleSet(ctx.Guild.Id, role.Name));
        }

        /// <summary>
        ///     Toggles whether TTS reads reply context (e.g. "replying to User").
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsReplyContext()
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var settings = await player.GetTtsGuildSettings();
            var newVal = !settings.TtsReplyContext;
            await player.UpdateTtsGuildSettingAsync(s => s.TtsReplyContext = newVal);

            await ReplyConfirmAsync(newVal
                ? Strings.TtsReplyContextOn(ctx.Guild.Id)
                : Strings.TtsReplyContextOff(ctx.Guild.Id));
        }

        /// <summary>
        ///     Toggles whether TTS narrates attachments (e.g. "sent an image").
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsAttachmentNarration()
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var settings = await player.GetTtsGuildSettings();
            var newVal = !settings.TtsAttachmentNarration;
            await player.UpdateTtsGuildSettingAsync(s => s.TtsAttachmentNarration = newVal);

            await ReplyConfirmAsync(newVal
                ? Strings.TtsAttachmentNarrationOn(ctx.Guild.Id)
                : Strings.TtsAttachmentNarrationOff(ctx.Guild.Id));
        }

        /// <summary>
        ///     Toggles whether consecutive messages from the same user skip the name prefix.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsConsecutiveGrouping()
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var settings = await player.GetTtsGuildSettings();
            var newVal = !settings.TtsConsecutiveGrouping;
            await player.UpdateTtsGuildSettingAsync(s => s.TtsConsecutiveGrouping = newVal);

            await ReplyConfirmAsync(newVal
                ? Strings.TtsConsecutiveGroupingOn(ctx.Guild.Id)
                : Strings.TtsConsecutiveGroupingOff(ctx.Guild.Id));
        }

        /// <summary>
        ///     Sets the maximum TTS queue size (1-50).
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsMaxQueue(int size)
        {
            if (size is < 1 or > 50)
            {
                await ReplyErrorAsync(Strings.TtsMaxQueueInvalid(ctx.Guild.Id));
                return;
            }

            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            await player.UpdateTtsGuildSettingAsync(s => s.TtsMaxQueueSize = size);
            await ReplyConfirmAsync(Strings.TtsMaxQueueSet(ctx.Guild.Id, size));
        }

        /// <summary>
        ///     Shows the current TTS settings for this guild and voice channel.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task TtsSettings()
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            var settings = await player.GetTtsGuildSettings();
            var vcSetting = await player.GetTtsVcSettingAsync(player.VoiceChannelId);
            var userSetting = await player.GetTtsUserSettingAsync(ctx.User.Id);

            var guildId = ctx.Guild.Id;
            var yes = Strings.TtsYes(guildId);
            var no = Strings.TtsNo(guildId);
            var vcEnabled = vcSetting?.Enabled == true ? yes : no;
            var linkedChannel = vcSetting?.LinkedTextChannelId.HasValue == true
                ? $"<#{vcSetting.LinkedTextChannelId.Value}>"
                : Strings.TtsSettingsNoLinkedChannel(guildId);
            var announce = vcSetting?.AnnounceJoinLeave == true ? yes : no;
            var autoJoin = vcSetting?.AutoJoin == true ? yes : no;
            var joinFmt = vcSetting?.JoinFormat ?? TtsService.DefaultJoinFormat;
            var leaveFmt = vcSetting?.LeaveFormat ?? TtsService.DefaultLeaveFormat;
            var defaultVoice = string.IsNullOrWhiteSpace(settings.TtsDefaultVoice)
                ? Strings.TtsSystemDefault(guildId)
                : settings.TtsDefaultVoice;
            var yourVoice = string.IsNullOrWhiteSpace(userSetting.Voice)
                ? Strings.TtsServerDefault(guildId)
                : userSetting.Voice;
            var roleText = settings.TtsRoleId.HasValue
                ? $"<@&{settings.TtsRoleId.Value}>"
                : Strings.TtsSettingsNoRole(guildId);

            var eb = new EmbedBuilder()
                .WithOkColor()
                .WithTitle(Strings.TtsSettingsTitle(guildId))
                .WithDescription(
                    $"{Strings.TtsSettingsChannel(guildId, vcEnabled, linkedChannel, announce, joinFmt, leaveFmt, autoJoin)}\n\n" +
                    Strings.TtsSettingsGuild(guildId, settings.TtsVolume, settings.TtsSpeed.ToString("F1"),
                        defaultVoice, yourVoice, roleText, settings.TtsReplyContext ? yes : no,
                        settings.TtsAttachmentNarration ? yes : no, settings.TtsConsecutiveGrouping ? yes : no,
                        settings.TtsMaxQueueSize));

            await ctx.Channel.SendAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);
        }

        /// <summary>
        ///     Removes all TTS settings for the current voice channel.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageGuild)]
        public async Task TtsRemove()
        {
            var (player, result) = await GetPlayerAsync(false);
            if (result is not null)
            {
                await SendErrorAsync(result);
                return;
            }

            await player.RemoveTtsVcSettingAsync(player.VoiceChannelId);
            await ReplyConfirmAsync(Strings.TtsRemoved(ctx.Guild.Id));
        }

        private async Task SendErrorAsync(string message)
        {
            var eb = new EmbedBuilder()
                .WithErrorColor()
                .WithTitle(Strings.MusicPlayerError(ctx.Guild.Id))
                .WithDescription(message);

            await ctx.Channel.SendAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);
        }

        private async ValueTask<(MewdekoPlayer, string?)> GetPlayerAsync(bool connectToVoiceChannel = true)
        {
            try
            {
                var channelBehavior = connectToVoiceChannel
                    ? PlayerChannelBehavior.Join
                    : PlayerChannelBehavior.None;

                var retrieveOptions = new PlayerRetrieveOptions(channelBehavior);

                var options = new MewdekoPlayerOptions
                {
                    Channel = ctx.Channel as Channel
                };

                var result = await service.Players
                    .RetrieveAsync<MewdekoPlayer, MewdekoPlayerOptions>(Context, CreatePlayerAsync, options,
                        retrieveOptions)
                    .ConfigureAwait(false);

                await result.Player.SetVolumeAsync(await result.Player.GetVolume() / 100f).ConfigureAwait(false);

                if (result.IsSuccess) return (result.Player, null);
                var errorMessage = result.Status switch
                {
                    PlayerRetrieveStatus.UserNotInVoiceChannel => Strings.MusicNotInChannel(ctx.Guild.Id),
                    PlayerRetrieveStatus.BotNotConnected => Strings.MusicBotNotConnect(ctx.Guild.Id,
                        await guildSettingsService.GetPrefix(ctx.Guild)),
                    PlayerRetrieveStatus.VoiceChannelMismatch => Strings.MusicVoiceChannelMismatch(ctx.Guild.Id),
                    PlayerRetrieveStatus.Success => null,
                    PlayerRetrieveStatus.UserInSameVoiceChannel => null,
                    PlayerRetrieveStatus.PreconditionFailed => null,
                    _ => throw new ArgumentOutOfRangeException()
                };
                return (null, errorMessage);
            }
            catch (TimeoutException)
            {
                return (null, Strings.MusicLavalinkDisconnected(ctx.Guild.Id));
            }
        }
    }
}

file class FloweryVoiceResponse
{
    public List<FloweryVoice> Voices { get; set; } = [];
}

file class FloweryVoice
{
    public string Name { get; } = "";
    public string? Gender { get; set; }
    public string? Source { get; set; }
    public FloweryLanguage? Language { get; set; }
}

file class FloweryLanguage
{
    public string? Name { get; set; }
    public string? Code { get; set; }
}