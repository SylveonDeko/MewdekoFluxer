using System.Net.Http;
using System.Text.RegularExpressions;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Services.Settings;

namespace Mewdeko.Modules.Server_Management;

/// <summary>
///     Contains commands related to server management.
/// </summary>
/// <param name="factory">The factory factory.</param>
/// <param name="config">The bot configuration settings.</param>
public partial class ServerManagement(IHttpClientFactory factory, BotConfigService config)
    : MewdekoModule
{
    /// <summary>
    ///     Displays the list of allowed permissions for the invoking user.
    /// </summary>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    public async Task PermView()
    {
        var perms = ctx.Member!.GuildPermissions;
        var eb = new EmbedBuilder();
        eb.WithTitle(Strings.ListAllowedPerms(ctx.Guild.Id));
        eb.WithOkColor();
        var allowed = Enum.GetValues<GuildPermission>()
            .Where(perms.Has)
            .Select(i => $"**{i}**");

        eb.WithDescription(string.Join("\n", allowed));
        await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
    }

    /// <summary>
    ///     Displays the list of allowed permissions for the specified user.
    /// </summary>
    /// <param name="user">The user whose permissions will be displayed.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [Priority(0)]
    public async Task PermView(SocketGuildMember user)
    {
        var perms = user.GuildPermissions;
        var eb = new EmbedBuilder();
        eb.WithTitle($"{Strings.ListAllowedPerms(ctx.Guild.Id)} for {user}");
        eb.WithOkColor();
        var allowed = Enum.GetValues<GuildPermission>()
            .Where(perms.Has)
            .Select(i => $"**{i}**");

        eb.WithDescription(string.Join("\n", allowed));
        await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
    }

    /// <summary>
    ///     Displays the list of allowed permissions for the specified role.
    /// </summary>
    /// <param name="user">The role whose permissions will be displayed.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [Priority(1)]
    public async Task PermView(SocketRole user)
    {
        var perms = user.Permissions;
        var eb = new EmbedBuilder();
        eb.WithTitle($"{Strings.ListAllowedPerms(ctx.Guild.Id)} for {user}");
        eb.WithOkColor();
        var allowed = Enum.GetValues<GuildPermission>()
            .Where(perms.Has)
            .Select(i => $"**{i}**");

        eb.WithDescription(string.Join("\n", allowed));
        await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the splash image of the server.
    /// </summary>
    /// <param name="img">The URL of the new splash image.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.Administrator)]
    public async Task SetSplash(string img)
    {
        var guild = ctx.Guild;
        var uri = new Uri(img);
        using var http = factory.CreateClient();
        using var sr = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var imgStream = imgData.ToStream();
        await using var _ = imgStream.ConfigureAwait(false);
        var request = new UpdateGuildRequest();
        request.SplashFromStream(imgStream);
        await guild.ModifyAsync(request).ConfigureAwait(false);
        await ctx.Channel.SendConfirmAsync(Strings.SplashImageSet(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the icon of the server.
    /// </summary>
    /// <param name="img">The URL of the new server icon.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.Administrator)]
    public async Task SetIcon(string img)
    {
        var guild = ctx.Guild;
        var uri = new Uri(img);
        using var http = factory.CreateClient();
        using var sr = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var imgStream = imgData.ToStream();
        await using var _ = imgStream.ConfigureAwait(false);
        var request = new UpdateGuildRequest();
        request.IconFromStream(imgStream);
        await guild.ModifyAsync(request).ConfigureAwait(false);
        await ctx.Channel.SendConfirmAsync(Strings.ServerIconSet(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the banner of the server.
    /// </summary>
    /// <param name="img">The URL of the new server banner.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.Administrator)]
    public async Task SetBanner(string img)
    {
        var guild = ctx.Guild;
        var uri = new Uri(img);
        using var http = factory.CreateClient();
        using var sr = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var imgStream = imgData.ToStream();
        await using var _ = imgStream.ConfigureAwait(false);
        var request = new UpdateGuildRequest();
        request.BannerFromStream(imgStream);
        await guild.ModifyAsync(request).ConfigureAwait(false);
        await ctx.Channel.SendConfirmAsync(Strings.ServerBannerSet(ctx.Guild.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sets the name of the server.
    /// </summary>
    /// <param name="name">The new name for the server.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.Administrator)]
    public async Task SetServerName([Remainder] string name)
    {
        var guild = ctx.Guild;
        await guild.ModifyAsync(new UpdateGuildRequest { Name = name }).ConfigureAwait(false);
        await ctx.Channel.SendConfirmAsync(Strings.ServerNameSet(ctx.Guild.Id, name)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds a new emote to the server.
    /// </summary>
    /// <param name="name">The name of the emote.</param>
    /// <param name="url">The URL of the emote image. If not provided, the image will be taken from the message attachments.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageExpressions)]
    [BotPerm(GuildPermission.ManageExpressions)]
    [Priority(0)]
    public async Task AddEmote(string name, string? url = null)
    {
        string acturl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var tags = ctx.Message.Attachments?.FirstOrDefault();
            acturl = tags?.Url ?? string.Empty;
        }
        else if (url.StartsWith("<"))
        {
            var result = EmoteAdapter.GetMessageEmotes(url).Select(EmoteAdapter.GetEmoteUrl);
            acturl = string.Join("", result);
        }
        else
        {
            acturl = url;
        }

        var uri = new Uri(acturl);
        using var http = factory.CreateClient();
        using var sr = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var imgStream = imgData.ToStream();
        await using var _ = imgStream.ConfigureAwait(false);
        try
        {
            var emoteRequest = new CreateGuildEmojiRequest { Name = name };
            emoteRequest.ImageFromStream(imgStream);
            var emote = await ctx.Guild.CreateEmojiAsync(emoteRequest).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.EmoteCreated(ctx.Guild.Id, emote, Format.Code(name)))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ctx.Channel.SendErrorAsync(
                    Strings.EmoteAddFailed(ctx.Guild.Id),
                    Config)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Removes an emote from the server.
    /// </summary>
    /// <param name="_">Placeholder parameter to satisfy command signature requirements.</param>
    [Cmd]
    [Aliases]
    [UserPerm(GuildPermission.ManageExpressions)]
    [BotPerm(GuildPermission.ManageExpressions)]
    [RequireContext(ContextType.Guild)]
    public async Task RemoveEmote(string _)
    {
        var tags = EmoteAdapter.GetMessageEmotes(ctx.Message.Content)
            .FirstOrDefault();
        try
        {
            var emote1 = ctx.Guild.Emotes.FirstOrDefault(x => x.Id == tags?.Id);
            if (emote1 is null)
            {
                await ctx.Channel.SendErrorAsync(Strings.EmoteNotFromGuild(ctx.Guild.Id), Config).ConfigureAwait(false);
                return;
            }
            await ctx.Guild.DeleteEmojiAsync(emote1).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.EmoteDeleted(ctx.Guild.Id, emote1)).ConfigureAwait(false);
        }
        catch (FluxerApiException)
        {
            await ctx.Channel.SendErrorAsync(Strings.EmoteNotFromGuild(ctx.Guild.Id), Config).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Renames an existing emote on the server.
    /// </summary>
    /// <param name="emote">The existing emote to rename.</param>
    /// <param name="name">The new name for the emote.</param>
    [Cmd]
    [Aliases]
    [UserPerm(GuildPermission.ManageExpressions)]
    [BotPerm(GuildPermission.ManageExpressions)]
    [RequireContext(ContextType.Guild)]
    public async Task RenameEmote(string emote, string name)
    {
        if (name.StartsWith("<"))
        {
            await ctx.Channel.SendErrorAsync(Strings.EmoteInvalidName(ctx.Guild.Id), Config).ConfigureAwait(false);
            return;
        }

        var tags = EmoteAdapter.GetMessageEmotes(ctx.Message.Content)
            .FirstOrDefault();
        try
        {
            var emote1 = ctx.Guild.Emotes.FirstOrDefault(x => x.Id == tags?.Id);
            if (emote1 is null)
            {
                await ctx.Channel.SendErrorAsync(Strings.EmoteWrongGuild(ctx.Guild.Id), Config).ConfigureAwait(false);
                return;
            }
            var ogname = emote1.Name;
            await ctx.Guild.ModifyEmojiAsync(emote1, name).ConfigureAwait(false);
            var emote2 = (await ctx.Guild.GetEmojisAsync().ConfigureAwait(false)).First(x => x.Id == tags.Id);
            await ctx.Channel.SendConfirmAsync(
                    Strings.EmoteRenamed(ctx.Guild.Id, emote1, Format.Code(ogname), Format.Code(emote2.Name)))
                .ConfigureAwait(false);
        }
        catch (FluxerApiException)
        {
            await ctx.Channel.SendErrorAsync(Strings.EmoteWrongGuild(ctx.Guild.Id), Config).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Steals emotes and adds them to the server. They are taken from the text, a message link or ID, or the
    ///     message being replied to.
    /// </summary>
    /// <param name="e">Emotes, or a message link or ID. Leave it out when replying to a message.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageExpressions)]
    [BotPerm(GuildPermission.ManageExpressions)]
    [Priority(1)]
    public async Task StealEmotes([Remainder] string? e = null)
    {
        var tags = EmoteAdapter.GetMessageEmotes(e);
        if (tags.Count == 0 && await FindEmoteSourceAsync(e).ConfigureAwait(false) is { } source)
            tags = EmoteAdapter.GetMessageEmotes(source.Content);

        if (tags.Count == 0)
        {
            await ctx.Channel.SendErrorAsync(Strings.NoEmotes(ctx.Guild.Id), Config).ConfigureAwait(false);
            return;
        }

        var eb = new EmbedBuilder
        {
            Description = Strings.AddingEmotes(ctx.Guild.Id, config.Data.LoadingEmote), Color = Mewdeko.OkColor
        };
        var errored = new List<string>();
        var emotes = new List<string>();
        var msg = await ctx.Channel.SendAsync(embed: eb.Build()).ConfigureAwait(false);
        foreach (var i in tags)
        {
            using var http = factory.CreateClient();
            using var sr = await http.GetAsync(EmoteAdapter.GetEmoteUrl(i), HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var imgStream = imgData.ToStream();
            await using var _ = imgStream.ConfigureAwait(false);
            {
                try
                {
                    var emoteRequest = new CreateGuildEmojiRequest { Name = i.Name };
                    emoteRequest.ImageFromStream(imgStream);
                    var emote = await ctx.Guild.CreateEmojiAsync(emoteRequest).ConfigureAwait(false);
                    emotes.Add($"{emote} {Format.Code(emote.Name)}");
                }
                catch (Exception)
                {
                    errored.Add($"{i.Name}\n{EmoteAdapter.GetEmoteUrl(i)}");
                }
            }
        }

        var b = new EmbedBuilder
        {
            Color = Mewdeko.OkColor
        };
        if (emotes.Count > 0) b.WithDescription(Strings.AddedEmotes(ctx.Guild.Id, string.Join("\n", emotes)));
        if (errored.Count > 0) b.AddField(Strings.ErroredEmotes(ctx.Guild.Id, errored.Count), string.Join("\n\n", errored));
        await msg.ModifyAsync(x => x.Embed = b.Build()).ConfigureAwait(false);
    }

    private async Task<Message?> FindEmoteSourceAsync(string? text)
    {
        var link = MessageLinkRegex().Match(text ?? string.Empty);
        if (link.Success)
            return await GetMessageAsync(ulong.Parse(link.Groups["channel"].Value),
                ulong.Parse(link.Groups["message"].Value)).ConfigureAwait(false);

        if (ulong.TryParse(text?.Trim(), out var messageId))
            return await GetMessageAsync(ctx.Channel.Id, messageId).ConfigureAwait(false);

        if (ctx.Message.MessageReference is { } reference)
            return await GetMessageAsync(reference.ChannelId, reference.MessageId).ConfigureAwait(false);

        return null;
    }

    private static async Task<Message?> GetMessageAsync(ulong channelId, ulong messageId)
    {
        if (await FluxerContext.Client.Gateway.GetCachedMessageAsync(messageId).ConfigureAwait(false) is { } cached)
            return cached;

        if (FluxerContext.Client.GetChannel(channelId) is not { } channel)
            return null;

        try
        {
            return await channel.GetMessageAsync(messageId).ConfigureAwait(false);
        }
        catch (FluxerApiException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"/channels/(?:\d+|@me)/(?<channel>\d+)/(?<message>\d+)")]
    private static partial Regex MessageLinkRegex();
}
