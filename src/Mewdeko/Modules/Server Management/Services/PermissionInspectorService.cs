using Humanizer;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.Server_Management.Services;

/// <summary>
///     Explains the permissions a member, a role or the bot has in a channel.
/// </summary>
/// <param name="strings">The generated bot strings.</param>
public class PermissionInspectorService(GeneratedBotStrings strings) : INService
{
    private const int FieldLimit = 1000;

    private static readonly ulong ChannelMask =
        Enum.GetValues<ChannelPermission>().Aggregate(0UL, (raw, x) => raw | (ulong)x);

    private static string Name(ChannelPermission permission) => permission.ToString().Humanize(LetterCasing.Title);

    private static string Join(IEnumerable<string> lines)
    {
        var text = "";
        foreach (var line in lines)
        {
            if (text.Length + line.Length + 1 > FieldLimit)
                return text + "...";
            text += (text.Length > 0 ? "\n" : "") + line;
        }

        return text;
    }

    private static string OverwriteName(SocketGuild guild, PermissionOverwrite overwrite)
    {
        if (overwrite.Type == PermissionOverwriteType.Member)
            return $"<@{overwrite.Id}>";
        return overwrite.Id == guild.Id ? "@everyone" : guild.GetRole(overwrite.Id)?.Name ?? $"{overwrite.Id}";
    }

    private static List<ChannelPermission> Split(ulong raw) =>
        Enum.GetValues<ChannelPermission>().Where(x => (raw & (ulong)x) != 0 && (ulong)x != 0).ToList();

    /// <summary>
    ///     Builds the permissions a member or a role has in a channel and the overwrites that apply to them, or the steps
    ///     that decide one permission.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="user">The member, or null to check a role.</param>
    /// <param name="role">The role, used when there is no member.</param>
    /// <param name="only">One permission to trace, or null to list all of them.</param>
    public EmbedBuilder Check(Channel channel, SocketGuildMember? user, SocketRole? role, ChannelPermission? only)
    {
        var guild = channel.Guild!;
        var guildId = guild.Id;
        var everyone = guild.EveryoneRole;
        var roleIds = user is not null ? user.RoleIds.ToHashSet() : [role!.Id];
        roleIds.Add(guildId);

        var guildRaw = roleIds.Aggregate(0UL, (raw, id) => raw | (ulong)(guild.GetRole(id)?.Permissions.RawValue ?? 0));
        var admin = (guildRaw & (ulong)GuildPermission.Administrator) != 0 || user?.Id == guild.OwnerId;

        var effective = guildRaw;
        if (admin)
            effective = ChannelMask;
        else
        {
            ApplyOverwrite(channel.GetPermissionOverwrite(everyone), ref effective);
            ulong roleAllow = 0, roleDeny = 0;
            foreach (var id in roleIds.Where(x => x != guildId))
            {
                if (guild.GetRole(id) is not { } other || channel.GetPermissionOverwrite(other) is not { } overwrite)
                    continue;
                roleAllow |= (ulong)overwrite.AllowValue;
                roleDeny |= (ulong)overwrite.DenyValue;
            }

            effective = (effective & ~roleDeny) | roleAllow;
            if (user is not null)
                ApplyOverwrite(channel.GetPermissionOverwrite(user), ref effective);
        }

        var subjectName = user is not null ? user.Mention : role!.Mention;
        var embed = new EmbedBuilder().WithOkColor();

        var applying = (channel.PermissionOverwrites ?? []).Where(x =>
                x.Type == PermissionOverwriteType.Role ? roleIds.Contains(x.Id) : x.Id == user?.Id)
            .ToList();

        if (only is { } single)
        {
            var bit = (ulong)single;
            embed.WithTitle(strings.PermcheckTraceTitle(guildId, Name(single)))
                .WithDescription(strings.PermcheckTraceFor(guildId, subjectName, channel.Name));
            var steps = new List<string>();
            if (admin)
                steps.Add(strings.PermcheckStepAdmin(guildId));
            else
            {
                var granting = roleIds.Select(guild.GetRole)
                    .Where(x => x is not null && ((ulong)x.Permissions.RawValue & bit) != 0)
                    .Select(x => x!.Id == guildId ? "@everyone" : x.Name)
                    .ToList();
                steps.Add(granting.Count > 0
                    ? strings.PermcheckStepRolesGrant(guildId, string.Join(", ", granting))
                    : strings.PermcheckStepRolesNone(guildId));
                foreach (var overwrite in applying.OrderBy(x =>
                             x.Type == PermissionOverwriteType.Member ? 2 : x.Id == guildId ? 0 : 1))
                {
                    if (((ulong)overwrite.Allow.RawValue & bit) != 0)
                        steps.Add(strings.PermcheckStepAllow(guildId, OverwriteName(guild, overwrite)));
                    else if (((ulong)overwrite.Deny.RawValue & bit) != 0)
                        steps.Add(strings.PermcheckStepDeny(guildId, OverwriteName(guild, overwrite)));
                }
            }

            var allowed = (effective & bit) != 0;
            steps.Add(allowed ? strings.PermcheckResultAllowed(guildId) : strings.PermcheckResultDenied(guildId));
            embed.AddField(strings.PermcheckSteps(guildId), Join(steps));
            return allowed ? embed : embed.WithErrorColor();
        }

        embed.WithTitle(strings.PermcheckTitle(guildId, channel.Name)).WithDescription(subjectName);
        if (admin)
            embed.AddField(strings.PermcheckAllowed(guildId), strings.PermcheckAdmin(guildId));
        else
        {
            var all = Split(ChannelMask);
            var allow = all.Where(x => (effective & (ulong)x) != 0).Select(Name).ToList();
            var deny = all.Where(x => (effective & (ulong)x) == 0).Select(Name).ToList();
            embed.AddField(strings.PermcheckAllowed(guildId), allow.Count > 0 ? Join(allow) : strings.PermcheckNone(guildId));
            embed.AddField(strings.PermcheckDenied(guildId), deny.Count > 0 ? Join(deny) : strings.PermcheckNone(guildId));
        }

        var lines = new List<string>();
        foreach (var overwrite in applying)
        {
            var allows = Split((ulong)overwrite.Allow.RawValue).Select(Name).ToList();
            var denies = Split((ulong)overwrite.Deny.RawValue).Select(Name).ToList();
            var name = OverwriteName(guild, overwrite);
            if (allows.Count > 0)
                lines.Add(strings.PermcheckOverwriteAllow(guildId, name, string.Join(", ", allows)));
            if (denies.Count > 0)
                lines.Add(strings.PermcheckOverwriteDeny(guildId, name, string.Join(", ", denies)));
        }

        embed.AddField(strings.PermcheckOverwrites(guildId),
            lines.Count > 0 ? Join(lines) : strings.PermcheckNoOverwrites(guildId));
        return embed;
    }

    private static void ApplyOverwrite(OverwritePermissions? overwrite, ref ulong raw)
    {
        if (overwrite is not { } value)
            return;
        raw = (raw & ~(ulong)value.DenyValue) | (ulong)value.AllowValue;
    }

    /// <summary>
    ///     Builds the reasons the bot can or cannot assign a role, or what it cannot assign at all when no role is given.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="role">The role to check, or null for an overview.</param>
    public EmbedBuilder Diagnose(SocketGuild guild, SocketRole? role)
    {
        var guildId = guild.Id;
        var me = guild.CurrentUser;
        var topRole = me.RoleIds.Select(guild.GetRole).Where(x => x is not null).OrderByDescending(x => x!.Position)
            .FirstOrDefault();
        var hierarchy = topRole?.Position ?? 0;
        var myPerms = me.RoleIds.Append(guildId).Aggregate(0UL,
            (raw, id) => raw | (ulong)(guild.GetRole(id)?.Permissions.RawValue ?? 0));
        var problems = new List<string>();

        if ((myPerms & ((ulong)GuildPermission.ManageRoles | (ulong)GuildPermission.Administrator)) == 0)
            problems.Add(strings.RoledxNoManageRoles(guildId));

        var top = topRole?.Mention ?? "@everyone";
        if (role is null)
        {
            var roles = guild.RoleList.Where(x => x.Id != guildId).ToList();
            var above = roles.Count(x => x.Position >= hierarchy);
            var fine = roles.Count - above;
            var embed = new EmbedBuilder().WithTitle(strings.RoledxOverviewTitle(guildId))
                .WithDescription(strings.RoledxOverviewTop(guildId, top))
                .AddField(strings.RoledxOverviewCounts(guildId),
                    strings.RoledxOverviewCountsText(guildId, fine, above, 0));
            if (problems.Count > 0)
                embed.AddField(strings.RoledxProblems(guildId), Join(problems));
            return problems.Count > 0 || above > 0 ? embed.WithErrorColor() : embed.WithOkColor();
        }

        if (role.Id == guildId)
            problems.Add(strings.RoledxEveryone(guildId));
        else if (role.Position >= hierarchy)
            problems.Add(role.Position == hierarchy
                ? strings.RoledxSamePosition(guildId, role.Mention)
                : strings.RoledxAbove(guildId, role.Mention, top));

        var result = new EmbedBuilder().WithTitle(strings.RoledxTitle(guildId, role.Name));
        if (problems.Count == 0)
            return result.WithOkColor().WithDescription(strings.RoledxOk(guildId, role.Mention));

        return result.WithErrorColor().WithDescription(Join(problems));
    }
}
