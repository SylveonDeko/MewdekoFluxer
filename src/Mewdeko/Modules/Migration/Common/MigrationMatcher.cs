namespace Mewdeko.Modules.Migration.Common;

/// <summary>
///     The channels, roles and emojis of a Discord server paired with the ones in the Fluxer server made from its
///     template.
/// </summary>
/// <param name="Channels">Discord channel ids to Fluxer channel ids.</param>
/// <param name="Roles">Discord role ids to Fluxer role ids.</param>
/// <param name="Emojis">Discord emoji ids to Fluxer emojis.</param>
/// <param name="UnmatchedChannels">Discord channels with no Fluxer counterpart.</param>
/// <param name="UnmatchedRoles">Discord roles with no Fluxer counterpart.</param>
public sealed record MigrationMatch(
    Dictionary<ulong, ulong> Channels,
    Dictionary<ulong, ulong> Roles,
    Dictionary<ulong, (ulong Id, string Name, bool Animated)> Emojis,
    List<DiscordChannelInfo> UnmatchedChannels,
    List<DiscordRoleInfo> UnmatchedRoles);

/// <summary>
///     Pairs a Discord server's layout with a Fluxer server's. A template keeps names, kinds, categories and order,
///     so channels are paired by kind, name and category, then by kind and name, then by a looser name, taking
///     duplicates in position order. Roles are paired by name the same way.
/// </summary>
public static class MigrationMatcher
{
    private const int Text = 0;
    private const int Voice = 2;
    private const int Category = 4;

    /// <summary>
    ///     Matches the exported server against a Fluxer server.
    /// </summary>
    /// <param name="meta">The exported Discord server.</param>
    /// <param name="guild">The Fluxer server.</param>
    /// <param name="channelOverrides">Pairs chosen by hand, Discord id to Fluxer id, applied before matching.</param>
    /// <param name="roleOverrides">Role pairs chosen by hand, applied before matching.</param>
    /// <returns>The pairs and what found no match.</returns>
    public static MigrationMatch Match(MigrationMeta meta, SocketGuild guild,
        IReadOnlyDictionary<ulong, ulong>? channelOverrides = null,
        IReadOnlyDictionary<ulong, ulong>? roleOverrides = null)
    {
        var channels = new Dictionary<ulong, ulong>();
        var discordChannels = meta.Channels.Select(x => (Info: x, Id: ulong.Parse(x.Id))).ToList();
        var fluxerChannels = guild.Channels.Values.ToList();
        var takenChannels = new HashSet<ulong>();

        foreach (var (discordId, fluxerId) in channelOverrides ?? new Dictionary<ulong, ulong>())
        {
            if (guild.Channels.ContainsKey(fluxerId) && takenChannels.Add(fluxerId))
                channels[discordId] = fluxerId;
        }

        void PairChannels(Func<DiscordChannelInfo, string?> discordKey, Func<Channel, string?> fluxerKey)
        {
            var left = discordChannels.Where(x => !channels.ContainsKey(x.Id))
                .GroupBy(x => discordKey(x.Info))
                .Where(x => x.Key is not null)
                .ToDictionary(x => x.Key!, x => x.OrderBy(c => c.Info.Position).ToList());
            var right = fluxerChannels.Where(x => !takenChannels.Contains(x.Id))
                .GroupBy(fluxerKey)
                .Where(x => x.Key is not null)
                .ToDictionary(x => x.Key!, x => x.OrderBy(c => c.Position ?? 0).ToList());
            foreach (var (key, group) in left)
            {
                if (!right.TryGetValue(key, out var candidates))
                    continue;

                foreach (var (discord, fluxer) in group.Zip(candidates))
                {
                    channels[discord.Id] = fluxer.Id;
                    takenChannels.Add(fluxer.Id);
                }
            }
        }

        string? ParentKey(string? discordParent)
        {
            if (discordParent is null)
                return "none";

            return channels.TryGetValue(ulong.Parse(discordParent), out var parent) ? parent.ToString() : null;
        }

        PairChannels(x => DiscordKind(x.Type) == Category ? Strict(x.Name) : null,
            x => FluxerKind(x.Type) == Category ? Strict(x.Name) : null);
        PairChannels(x => DiscordKind(x.Type) == Category ? Loose(x.Name) : null,
            x => FluxerKind(x.Type) == Category ? Loose(x.Name) : null);
        PairChannels(
            x => DiscordKind(x.Type) == Category || ParentKey(x.ParentId) is not { } parent
                ? null
                : $"{DiscordKind(x.Type)}|{Strict(x.Name)}|{parent}",
            x => FluxerKind(x.Type) == Category
                ? null
                : $"{FluxerKind(x.Type)}|{Strict(x.Name)}|{x.ParentId?.ToString() ?? "none"}");
        PairChannels(x => DiscordKind(x.Type) == Category ? null : $"{DiscordKind(x.Type)}|{Strict(x.Name)}",
            x => FluxerKind(x.Type) == Category ? null : $"{FluxerKind(x.Type)}|{Strict(x.Name)}");
        PairChannels(x => DiscordKind(x.Type) == Category ? null : $"{DiscordKind(x.Type)}|{Loose(x.Name)}",
            x => FluxerKind(x.Type) == Category ? null : $"{FluxerKind(x.Type)}|{Loose(x.Name)}");

        var roles = new Dictionary<ulong, ulong> { [meta.GuildId] = guild.Id };
        var discordRoles = meta.Roles.Select(x => (Info: x, Id: ulong.Parse(x.Id)))
            .Where(x => x.Id != meta.GuildId && !x.Info.Managed)
            .ToList();
        var fluxerRoles = guild.Roles.Values.Where(x => x.Id != guild.Id).ToList();
        var takenRoles = new HashSet<ulong>();

        foreach (var (discordId, fluxerId) in roleOverrides ?? new Dictionary<ulong, ulong>())
        {
            if (guild.Roles.ContainsKey(fluxerId) && takenRoles.Add(fluxerId))
                roles[discordId] = fluxerId;
        }

        void PairRoles(Func<string, string> key)
        {
            var left = discordRoles.Where(x => !roles.ContainsKey(x.Id))
                .GroupBy(x => key(x.Info.Name))
                .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.Info.Position).ToList());
            var right = fluxerRoles.Where(x => !takenRoles.Contains(x.Id))
                .GroupBy(x => key(x.Name))
                .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.Position).ToList());
            foreach (var (name, group) in left)
            {
                if (name.Length == 0 || !right.TryGetValue(name, out var candidates))
                    continue;

                foreach (var (discord, fluxer) in group.Zip(candidates))
                {
                    roles[discord.Id] = fluxer.Id;
                    takenRoles.Add(fluxer.Id);
                }
            }
        }

        PairRoles(Strict);
        PairRoles(Loose);

        var emojis = new Dictionary<ulong, (ulong Id, string Name, bool Animated)>();
        var fluxerEmojis = (guild.Emojis?.Values ?? Enumerable.Empty<GuildEmoji>()).ToList();
        foreach (var emoji in meta.Emojis)
        {
            var match = fluxerEmojis.FirstOrDefault(x => x.Name == emoji.Name)
                        ?? fluxerEmojis.FirstOrDefault(x =>
                            string.Equals(x.Name, emoji.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                emojis[ulong.Parse(emoji.Id)] = (match.Id, match.Name, match.IsAnimated);
        }

        var unmatchedChannels = discordChannels.Where(x => !channels.ContainsKey(x.Id)).Select(x => x.Info).ToList();
        var unmatchedRoles = discordRoles.Where(x => !roles.ContainsKey(x.Id)).Select(x => x.Info).ToList();
        return new MigrationMatch(channels, roles, emojis, unmatchedChannels, unmatchedRoles);
    }

    private static int DiscordKind(int type)
    {
        return type switch
        {
            4 => Category,
            2 or 13 => Voice,
            _ => Text
        };
    }

    private static int FluxerKind(ChannelType type)
    {
        return type switch
        {
            ChannelType.GuildCategory => Category,
            ChannelType.GuildVoice or ChannelType.GuildStageVoice => Voice,
            _ => Text
        };
    }

    private static string Strict(string? name)
    {
        return (name ?? "").Trim().ToLowerInvariant();
    }

    private static string Loose(string? name)
    {
        return new string((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
