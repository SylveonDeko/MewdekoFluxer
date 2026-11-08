using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Common.ModuleBehaviors;

namespace Mewdeko.Modules.Server_Management.Services;

/// <summary>
///     Gives members access to a text channel while they are in the voice channel it is linked to.
/// </summary>
public class VoiceTextLinkService : INService, IReadyExecutor
{
    private static readonly OverwritePermissions Grant = new OverwritePermissions().Modify(
        viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, readMessageHistory: PermValue.Allow);

    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ConcurrentDictionary<ulong, VoiceTextLink> links = new();
    private readonly ILogger<VoiceTextLinkService> logger;
    private readonly ConcurrentDictionary<ulong, ulong> temporaryHubs = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="VoiceTextLinkService" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="eventHandler">The event handler.</param>
    /// <param name="logger">The logger instance.</param>
    public VoiceTextLinkService(FluxerClient client, IDataConnectionFactory dbFactory,
        EventHandler eventHandler, ILogger<VoiceTextLinkService> logger)
    {
        this.client = client;
        this.dbFactory = dbFactory;
        this.logger = logger;
        eventHandler.UserVoiceStateUpdated.Subscribe("VoiceTextLinkService", OnUserVoiceStateUpdated);
        eventHandler.ChannelDestroyed.Subscribe("VoiceTextLinkService", OnChannelDestroyed);
    }

    /// <summary>
    ///     Loads the links and fixes access that changed while the bot was offline.
    /// </summary>
    public async Task OnReadyAsync()
    {
        List<VoiceTextLink> all;
        await using (var db = await dbFactory.CreateConnectionAsync())
            all = await db.VoiceTextLinks.ToListAsync();

        foreach (var link in all)
            links[link.VoiceChannelId] = link;

        _ = Task.Run(async () =>
        {
            foreach (var link in all)
            {
                try
                {
                    await SyncAsync(link);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not sync the voice text link of {VoiceChannelId}",
                        link.VoiceChannelId);
                }
            }
        });
    }

    /// <summary>
    ///     Lists the links in a guild.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    public List<VoiceTextLink> GetLinks(ulong guildId) =>
        links.Values.Where(x => x.GuildId == guildId).OrderBy(x => x.VoiceChannelId).ToList();

    /// <summary>
    ///     Links a voice channel to a text channel, replacing any earlier link.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="voiceChannelId">The voice channel ID.</param>
    /// <param name="textChannelId">The text channel ID.</param>
    public async Task LinkAsync(ulong guildId, ulong voiceChannelId, ulong textChannelId)
    {
        var old = links.GetValueOrDefault(voiceChannelId);
        await using var db = await dbFactory.CreateConnectionAsync();
        await db.VoiceTextLinks.Where(x => x.VoiceChannelId == voiceChannelId).DeleteAsync();
        var link = new VoiceTextLink
        {
            GuildId = guildId,
            VoiceChannelId = voiceChannelId,
            TextChannelId = textChannelId,
            DateAdded = DateTime.UtcNow
        };
        await db.InsertAsync(link);
        links[voiceChannelId] = link;

        if (old is not null && old.TextChannelId != textChannelId)
            await RevokeAllAsync(old);
        await SyncAsync(link);
    }

    /// <summary>
    ///     Removes a link and takes back the access it gave.
    /// </summary>
    /// <param name="voiceChannelId">The voice channel ID.</param>
    /// <returns>False if the voice channel had no link.</returns>
    public async Task<bool> UnlinkAsync(ulong voiceChannelId)
    {
        if (!links.TryRemove(voiceChannelId, out var link))
            return false;

        await using var db = await dbFactory.CreateConnectionAsync();
        await db.VoiceTextLinks.Where(x => x.VoiceChannelId == voiceChannelId).DeleteAsync();
        await RevokeAllAsync(link);
        return true;
    }

    private static bool IsGrant(OverwritePermissions perms) =>
        perms.AllowValue == Grant.AllowValue && perms.DenyValue == 0;

    private async Task<ulong?> GetHubIdAsync(ulong guildId, ulong channelId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var channel = await db.CustomVoiceChannels.FirstOrDefaultAsync(x => x.ChannelId == channelId);
        if (channel is null)
            return null;

        if (channel.HubVoiceChannelId is { } hubId and not 0)
            return hubId;

        var config = await db.CustomVoiceConfigs.Where(x => x.GuildId == guildId && x.HubVoiceChannelId != 0)
            .OrderBy(x => x.Id).FirstOrDefaultAsync();
        return config?.HubVoiceChannelId;
    }

    private async Task<List<ulong>> GetTemporaryChannelIdsAsync(ulong guildId, ulong hubId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        if (!await db.CustomVoiceConfigs.AnyAsync(x => x.GuildId == guildId && x.HubVoiceChannelId == hubId))
            return [];

        var firstHubId = await db.CustomVoiceConfigs.Where(x => x.GuildId == guildId && x.HubVoiceChannelId != 0)
            .OrderBy(x => x.Id).Select(x => x.HubVoiceChannelId).FirstOrDefaultAsync();
        return await db.CustomVoiceChannels
            .Where(x => x.GuildId == guildId && (x.HubVoiceChannelId == hubId ||
                                                 (x.HubVoiceChannelId == null && firstHubId == hubId)))
            .Select(x => x.ChannelId).ToListAsync();
    }

    private async Task SyncAsync(VoiceTextLink link)
    {
        var guild = client.GetGuild(link.GuildId);
        var text = guild?.GetChannel(link.TextChannelId);
        if (guild is null || text is null)
            return;

        var channelIds = new List<ulong> { link.VoiceChannelId };
        channelIds.AddRange(await GetTemporaryChannelIdsAsync(link.GuildId, link.VoiceChannelId));

        var inside = channelIds.Select(x => guild.GetChannel(x)).OfType<Channel>()
            .SelectMany(x => x.ConnectedUsers).Where(x => !x.User.IsBot).DistinctBy(x => x.Id).ToList();
        var insideIds = inside.Select(x => x.Id).ToHashSet();

        foreach (var overwrite in (text.PermissionOverwrites ?? []).ToList())
        {
            if (overwrite.Type != PermissionOverwriteType.Member || insideIds.Contains(overwrite.Id)
                                                                 || !IsGrant(OverwritePermissions.From(overwrite)))
                continue;

            var user = guild.GetUser(overwrite.Id);
            if (user is not null && !await IsLinkedToAsync(user, link))
                await text.RemovePermissionOverwriteAsync(user);
        }

        foreach (var user in inside)
            await GrantAsync(text, user);
    }

    private async Task<bool> IsLinkedToAsync(SocketGuildMember user, VoiceTextLink link) =>
        (await ResolveAsync(user.VoiceChannel))?.TextChannelId == link.TextChannelId;

    private async Task<VoiceTextLink?> ResolveAsync(Channel? channel)
    {
        if (channel?.GuildId is not { } guildId)
            return null;

        if (links.TryGetValue(channel.Id, out var direct))
            return direct;

        if (!links.Values.Any(x => x.GuildId == guildId))
            return null;

        if (!temporaryHubs.TryGetValue(channel.Id, out var hubId))
        {
            var found = await GetHubIdAsync(guildId, channel.Id);
            if (found is not { } hub)
                return null;

            hubId = temporaryHubs[channel.Id] = hub;
        }

        return links.GetValueOrDefault(hubId);
    }

    private async Task RevokeAllAsync(VoiceTextLink link)
    {
        var guild = client.GetGuild(link.GuildId);
        var text = guild?.GetChannel(link.TextChannelId);
        if (guild is null || text is null)
            return;

        foreach (var overwrite in (text.PermissionOverwrites ?? []).ToList())
        {
            if (overwrite.Type != PermissionOverwriteType.Member || !IsGrant(OverwritePermissions.From(overwrite)))
                continue;

            var user = guild.GetUser(overwrite.Id);
            if (user is not null && !await IsLinkedToAsync(user, link))
                await text.RemovePermissionOverwriteAsync(user);
        }
    }

    private static async Task GrantAsync(Channel text, SocketGuildMember user)
    {
        if (text.GetPermissionOverwrite(user) is not null)
            return;

        await text.AddPermissionOverwriteAsync(user, Grant);
    }

    private static async Task RevokeAsync(Channel text, SocketGuildMember user)
    {
        if (text.GetPermissionOverwrite(user) is { } current && IsGrant(current))
            await text.RemovePermissionOverwriteAsync(user);
    }

    private async Task OnUserVoiceStateUpdated(SocketGuildMember member, VoiceStateSnapshot before,
        VoiceStateSnapshot after)
    {
        if (member.User.IsBot || before.ChannelId == after.ChannelId)
            return;

        try
        {
            var left = await ResolveAsync(before.ChannelId is { } beforeId ? member.Guild.GetChannel(beforeId) : null);
            var joined = await ResolveAsync(after.ChannelId is { } afterId ? member.Guild.GetChannel(afterId) : null);
            if (left is not null && left.TextChannelId != joined?.TextChannelId
                                 && member.Guild.GetChannel(left.TextChannelId) is { } leftText)
                await RevokeAsync(leftText, member);

            if (joined is not null && member.Guild.GetChannel(joined.TextChannelId) is { } joinedText)
                await GrantAsync(joinedText, member);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update the voice text access of {UserId}", member.Id);
        }
    }

    private async Task OnChannelDestroyed(Channel channel)
    {
        if (temporaryHubs.ContainsKey(channel.Id))
        {
            _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => temporaryHubs.TryRemove(channel.Id, out var removed));
        }

        try
        {
            var gone = links.Values.Where(x => x.VoiceChannelId == channel.Id || x.TextChannelId == channel.Id)
                .ToList();
            if (gone.Count == 0)
                return;

            await using var db = await dbFactory.CreateConnectionAsync();
            foreach (var link in gone)
            {
                links.TryRemove(link.VoiceChannelId, out _);
                await db.VoiceTextLinks.Where(x => x.VoiceChannelId == link.VoiceChannelId).DeleteAsync();
                if (link.VoiceChannelId == channel.Id)
                    await RevokeAllAsync(link);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the voice text links of {ChannelId}", channel.Id);
        }
    }
}
