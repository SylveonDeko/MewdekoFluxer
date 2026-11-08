using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Services.Strings;

namespace Mewdeko.Modules.Utility.Services;

/// <summary>
///     Hands out and takes back roles earned at a number of net invites.
/// </summary>
public class InviteRoleService : INService
{
    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ILogger<InviteRoleService> logger;
    private readonly GeneratedBotStrings strings;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InviteRoleService" /> class.
    /// </summary>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="strings">Localized strings.</param>
    /// <param name="logger">The logger.</param>
    public InviteRoleService(IDataConnectionFactory dbFactory, FluxerClient client, GeneratedBotStrings strings,
        ILogger<InviteRoleService> logger)
    {
        this.dbFactory = dbFactory;
        this.client = client;
        this.strings = strings;
        this.logger = logger;
    }

    /// <summary>
    ///     Re-applies a member's milestone roles after their invite count changed.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="userId">The inviter's user ID.</param>
    /// <param name="count">The inviter's new net count.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task OnInviteCountChanged(ulong guildId, ulong userId, int count)
    {
        try
        {
            var guild = client.GetGuild(guildId);
            var member = guild?.GetUser(userId);
            if (guild is null || member is null || member.IsBot)
                return;

            await using var db = await dbFactory.CreateConnectionAsync();
            var roles = await db.InviteRoles.Where(x => x.GuildId == guildId).ToListAsync();
            if (roles.Count == 0)
                return;

            var settings = await db.InviteRoleSettings.FirstOrDefaultAsync(x => x.GuildId == guildId);
            await ApplyAsync(guild, member, count, roles, settings ?? new InviteRoleSetting());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update invite roles for {UserId} in {GuildId}", userId, guildId);
        }
    }

    /// <summary>
    ///     Gets a guild's settings, or the defaults when none are saved.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The settings.</returns>
    public async Task<InviteRoleSetting> GetSettingsAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.InviteRoleSettings.FirstOrDefaultAsync(x => x.GuildId == guildId)
               ?? new InviteRoleSetting
               {
                   GuildId = guildId
               };
    }

    /// <summary>
    ///     Changes a guild's settings. Values left null stay as they are.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="mode">The new mode, or null.</param>
    /// <param name="removeOnDrop">Whether roles are taken back when the count drops, or null.</param>
    /// <returns>The saved settings.</returns>
    public async Task<InviteRoleSetting> UpdateSettingsAsync(ulong guildId, InviteRoleMode? mode = null,
        bool? removeOnDrop = null)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.InviteRoleSettings.FirstOrDefaultAsync(x => x.GuildId == guildId);
        var isNew = row is null;
        row ??= new InviteRoleSetting
        {
            GuildId = guildId
        };

        row.Mode = mode ?? row.Mode;
        row.RemoveOnDrop = removeOnDrop ?? row.RemoveOnDrop;
        if (isNew)
            row.Id = await db.InsertWithInt32IdentityAsync(row);
        else
            await db.UpdateAsync(row);
        return row;
    }

    /// <summary>
    ///     Gets a guild's milestone roles, lowest milestone first.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <returns>The milestone roles.</returns>
    public async Task<List<InviteRole>> GetRolesAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.InviteRoles
            .Where(x => x.GuildId == guildId)
            .OrderBy(x => x.RequiredInvites)
            .ThenBy(x => x.Id)
            .ToListAsync();
    }

    /// <summary>
    ///     Adds a milestone role or moves an existing one to a new milestone.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <param name="requiredInvites">The net invites needed to earn the role.</param>
    /// <returns>The saved milestone role.</returns>
    public async Task<InviteRole> SetRoleAsync(ulong guildId, ulong roleId, int requiredInvites)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.InviteRoles.FirstOrDefaultAsync(x => x.GuildId == guildId && x.RoleId == roleId);
        if (row is null)
        {
            row = new InviteRole
            {
                GuildId = guildId,
                RoleId = roleId,
                RequiredInvites = requiredInvites,
                DateAdded = DateTime.UtcNow
            };
            row.Id = await db.InsertWithInt32IdentityAsync(row);
            return row;
        }

        row.RequiredInvites = requiredInvites;
        await db.UpdateAsync(row);
        return row;
    }

    /// <summary>
    ///     Removes a milestone role.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <returns>True if the role was a milestone role.</returns>
    public async Task<bool> RemoveRoleAsync(ulong guildId, ulong roleId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.InviteRoles.Where(x => x.GuildId == guildId && x.RoleId == roleId).DeleteAsync() > 0;
    }

    /// <summary>
    ///     Applies milestone roles to every member of a guild from their current counts.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <returns>How many members had roles changed.</returns>
    public async Task<int> SyncAsync(SocketGuild guild)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var roles = await db.InviteRoles.Where(x => x.GuildId == guild.Id).ToListAsync();
        if (roles.Count == 0)
            return 0;

        var settings = await db.InviteRoleSettings.FirstOrDefaultAsync(x => x.GuildId == guild.Id)
                       ?? new InviteRoleSetting();
        var counts = await db.InviteCounts
            .Where(x => x.GuildId == guild.Id)
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        var changed = 0;
        foreach (var member in guild.Users.Where(u => !u.IsBot).ToList())
        {
            if (await ApplyAsync(guild, member, counts.GetValueOrDefault(member.Id), roles, settings))
                changed++;
        }

        return changed;
    }

    private async Task<bool> ApplyAsync(SocketGuild guild, SocketGuildMember member, int count,
        IReadOnlyList<InviteRole> roles, InviteRoleSetting settings)
    {
        var held = roles.Where(r => member.Roles.Any(x => x.Id == r.RoleId)).ToList();
        var pool = roles.Where(r => r.RequiredInvites <= count).ToList();
        if (!settings.RemoveOnDrop)
            pool = pool.Union(held).ToList();

        var wanted = pool;
        if (settings.Mode == InviteRoleMode.HighestOnly && pool.Count > 0)
        {
            var top = pool.Max(r => r.RequiredInvites);
            wanted = pool.Where(r => r.RequiredInvites == top).ToList();
        }

        var wantedIds = wanted.Select(r => r.RoleId).ToHashSet();
        var give = wanted.Where(r => held.All(h => h.RoleId != r.RoleId))
            .Select(r => r.RoleId)
            .Where(id => CanAssign(guild, id))
            .Distinct()
            .ToList();
        var take = held.Where(r => !wantedIds.Contains(r.RoleId))
            .Select(r => r.RoleId)
            .Where(id => CanAssign(guild, id))
            .Distinct()
            .ToList();
        if (give.Count == 0 && take.Count == 0)
            return false;

        var options = new RequestOptions
        {
            AuditLogReason = strings.InviteRoleAuditReason(guild.Id)
        };
        try
        {
            foreach (var id in give)
                await member.AddRoleAsync(id, options);
            foreach (var id in take)
                await member.RemoveRoleAsync(id, options);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update invite roles for {UserId} in {GuildId}", member.Id, guild.Id);
            return false;
        }
    }

    /// <summary>
    ///     Checks whether the bot is able to give out a role.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="roleId">The role ID.</param>
    /// <returns>True if the role exists, is not managed and sits below the bot's highest role.</returns>
    public static bool CanAssign(SocketGuild guild, ulong roleId)
    {
        var role = guild.GetRole(roleId);
        if (role is null || role.Id == guild.Id)
            return false;
        var bot = guild.CurrentUser;
        return bot.GuildPermissions.ManageRoles && bot.Hierarchy > role.Position;
    }
}
