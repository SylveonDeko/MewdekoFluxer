using Mewdeko.Common.Attributes.TextCommands;

namespace Mewdeko.Modules.Help.Common;

/// <summary>
///     Reads the permissions a command requires from its permission attributes.
/// </summary>
public static class RequiredPermissions
{
    /// <summary>
    ///     Gets the guild permission required by a user permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required guild permission, or null when it requires a channel permission.</returns>
    public static GuildPermission? Guild(UserPermAttribute? attribute)
    {
        return attribute?.UserPermissionAttribute.Guild;
    }

    /// <summary>
    ///     Gets the channel permission required by a user permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required channel permission, or null when it requires a guild permission.</returns>
    public static ChannelPermission? Channel(UserPermAttribute? attribute)
    {
        return attribute?.UserPermissionAttribute.Channel;
    }

    /// <summary>
    ///     Gets the guild permission required by a bot permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required guild permission, or null when it requires a channel permission.</returns>
    public static GuildPermission? Guild(BotPermAttribute? attribute)
    {
        return attribute?.Guild;
    }

    /// <summary>
    ///     Gets the channel permission required by a bot permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required channel permission, or null when it requires a guild permission.</returns>
    public static ChannelPermission? Channel(BotPermAttribute? attribute)
    {
        return attribute?.Channel;
    }
}
