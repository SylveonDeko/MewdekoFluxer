namespace Mewdeko.Extensions;

/// <summary>
///     Extension methods for the SocketRole interface.
/// </summary>
public static class RoleExtensions
{
    /// <summary>
    ///     Checks if a role can be managed by a guild user.
    /// </summary>
    /// <param name="role">The role to check.</param>
    /// <param name="user">The user attempting to manage the role.</param>
    /// <returns>True if the user can manage the role, false otherwise.</returns>
    public static bool CanManageRole(this SocketRole role, SocketGuildMember user)
    {
        if (user.Guild.OwnerId == user.Id)
            return role.Id != user.Guild.EveryoneRole.Id;

        if (!user.GuildPermissions.Has(GuildPermission.ManageRoles) &&
            !user.GuildPermissions.Has(GuildPermission.Administrator))
            return false;

        return role.Position < user.GetRoles().Max(x => x.Position) &&
               role.Id != user.Guild.EveryoneRole.Id;
    }
}