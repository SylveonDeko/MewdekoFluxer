using DataModel;
using Fluxer.Net.Rest;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Modules.Bridge.Common;

namespace Mewdeko.Modules.Bridge.Services;

public sealed partial class BridgeService
{
    string IBridgePairHost.Platform => ThisPlatform;

    bool IBridgePairHost.HasGuild(ulong guildId) => client.GetGuild(guildId) is not null;

    string IBridgePairHost.GuildName(ulong guildId) => client.GetGuild(guildId)?.Name ?? "";

    IReadOnlyList<BridgeChannelInfo> IBridgePairHost.TextChannels(ulong guildId)
    {
        var guild = client.GetGuild(guildId);
        if (guild is null)
            return [];

        return guild.Channels.Values
            .Where(x => x.Type == ChannelType.GuildText)
            .Select(x => (Channel: x, Parent: x.ParentId is { } parentId ? guild.GetChannel(parentId) : null))
            .OrderBy(x => x.Parent?.Position ?? -1)
            .ThenBy(x => x.Channel.Position ?? 0)
            .Select(x => new BridgeChannelInfo(x.Channel.Id, x.Channel.Name ?? "", x.Parent?.Name ?? "",
                IsPublic(guild, x.Channel)))
            .ToList();
    }

    bool IBridgePairHost.IsBridged(ulong channelId) => byChannel.ContainsKey(channelId);

    bool IBridgePairHost.CanManageWebhooks(ulong guildId, ulong channelId) =>
        client.GetGuild(guildId) is { } guild && guild.GetChannel(channelId) is { } channel
                                              && guild.CurrentMember.GetPermissions(channel).ManageWebhooks;

    async Task<bool> IBridgePairHost.CanManageAsync(ulong guildId, ulong userId)
    {
        if (client.GetGuild(guildId) is not { } guild)
            return false;

        var member = guild.Members.GetValueOrDefault(userId) ?? await guild.GetMemberAsync(userId);
        return member is not null && member.HasPermission(GuildPermission.ManageGuild);
    }

    IReadOnlyList<ChannelBridge> IBridgePairHost.BridgesOfPair(Guid pairId) =>
        byId.Values.Where(x => x.Row.PairId == pairId).Select(x => x.Row).ToList();

    async Task<ChannelBridge?> IBridgePairHost.CreateBridgeAsync(ulong guildId, ulong channelId, ulong userId,
        Guid pairId, ulong remoteGuildId, string remoteGuildName, ulong remoteChannelId, string remoteChannelName)
    {
        if (CheckChannel(guildId, channelId, out _) != BridgeResult.Ok || client.GetGuild(guildId)?.GetChannel(channelId)
                is not { } channel)
            return null;

        string url;
        try
        {
            (url, _) = await EnsureWebhookAsync(channel);
        }
        catch (FluxerApiException ex)
        {
            logger.LogInformation(ex, "Could not create the bridge webhook in channel {ChannelId}", channelId);
            return null;
        }

        var row = NewRow(Guid.NewGuid(), guildId, channelId, url, userId, remoteGuildId, remoteGuildName,
            remoteChannelId, remoteChannelName, pairId);
        await using (var db = await dbFactory.CreateConnectionAsync())
            await db.InsertAsync(row);

        Track(row);
        return row;
    }

    async Task IBridgePairHost.RemoveBridgeAsync(ChannelBridge bridge)
    {
        if (!byId.TryGetValue(bridge.BridgeId, out var state))
            return;

        await RemoveAsync(state);
        await PublishAsync(new BridgeEnvelope
        {
            Type = BridgeEnvelopeType.Unlinked, BridgeId = bridge.BridgeId
        });
    }

    async Task IBridgePairHost.UnlinkLocalAsync(ChannelBridge bridge)
    {
        if (!byId.TryGetValue(bridge.BridgeId, out var state))
            return;

        await RemoveAsync(state);
        await NoticeAsync(bridge.GuildId, bridge.ChannelId, strings.BridgeRemovedRemote(bridge.GuildId));
    }

    async Task IBridgePairHost.ClearPairAsync(Guid pairId)
    {
        foreach (var state in byId.Values.Where(x => x.Row.PairId == pairId).ToList())
            state.Row.PairId = null;

        await using var db = await dbFactory.CreateConnectionAsync();
        await db.ChannelBridges.Where(x => x.PairId == pairId).Set(x => x.PairId, (Guid?)null).UpdateAsync();
    }

    Task IBridgePairHost.PublishAsync(BridgeEnvelope envelope) => PublishAsync(envelope);

    Task IBridgePairHost.NoticeAsync(ulong guildId, ulong channelId, string text) =>
        NoticeAsync(guildId, channelId, text);

    Task IBridgePairHost.NoticeLinkedAsync(ChannelBridge bridge) => NoticeAsync(bridge.GuildId, bridge.ChannelId,
        strings.BridgeLinked(bridge.GuildId, bridge.RemoteChannelName, bridge.RemoteGuildName));

    private static bool IsPublic(SocketGuild guild, Channel channel)
    {
        var permissions = guild.EveryoneRole?.Permissions.RawValue ?? 0;
        if (permissions.HasFlag(GuildPermission.Administrator))
            return true;

        var view = permissions.HasFlag(GuildPermission.ViewChannel);
        var overwrite = channel.PermissionOverwrites?.FirstOrDefault(x => x.Id == guild.Id
                                                                         && x.Type == PermissionOverwriteType.Role);
        if (overwrite is null)
            return view;

        if (overwrite.Allow.RawValue.HasFlag(ChannelPermission.ViewChannel))
            return true;

        return overwrite.Deny.RawValue.HasFlag(ChannelPermission.ViewChannel) ? false : view;
    }

    private Task OnChannelCreated(Channel channel) => ChannelChanged(channel);

    private Task OnChannelDestroyed(Channel channel) => ChannelChanged(channel);

    private Task OnChannelUpdated(Channel before, Channel after) => ChannelChanged(after);

    private Task ChannelChanged(Channel channel)
    {
        if (channel.GuildId is { } guildId)
            Pairs.ChannelsChanged(guildId);

        return Task.CompletedTask;
    }

    private Task OnRoleUpdated(SocketRole before, SocketRole after)
    {
        if (after.Id == after.Guild.Id)
            Pairs.ChannelsChanged(after.Guild.Id);

        return Task.CompletedTask;
    }
}
