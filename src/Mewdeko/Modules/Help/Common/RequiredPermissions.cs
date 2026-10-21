using System.Reflection;
using Mewdeko.Common.Attributes.TextCommands;

namespace Mewdeko.Modules.Help.Common;

/// <summary>
///     Reads the permissions a command requires from its permission attributes.
/// </summary>
public static class RequiredPermissions
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>
    ///     Gets the guild permission required by a user permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required guild permission, or null when it requires a channel permission.</returns>
    public static GuildPermission? Guild(UserPermAttribute? attribute)
    {
        return Read<GuildPermission>(attribute?.UserPermissionAttribute, "Guild");
    }

    /// <summary>
    ///     Gets the channel permission required by a user permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required channel permission, or null when it requires a guild permission.</returns>
    public static ChannelPermission? Channel(UserPermAttribute? attribute)
    {
        return Read<ChannelPermission>(attribute?.UserPermissionAttribute, "Channel");
    }

    /// <summary>
    ///     Gets the guild permission required by a bot permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required guild permission, or null when it requires a channel permission.</returns>
    public static GuildPermission? Guild(BotPermAttribute? attribute)
    {
        return Read<GuildPermission>(attribute, "Guild");
    }

    /// <summary>
    ///     Gets the channel permission required by a bot permission attribute.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The required channel permission, or null when it requires a guild permission.</returns>
    public static ChannelPermission? Channel(BotPermAttribute? attribute)
    {
        return Read<ChannelPermission>(attribute, "Channel");
    }

    private static T? Read<T>(object? attribute, string fieldName) where T : struct
    {
        return attribute?.GetType().BaseTypeOrSelf(fieldName)?.GetField(fieldName, Flags)?.GetValue(attribute) as T?;
    }

    private static Type? BaseTypeOrSelf(this Type type, string fieldName)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.GetField(fieldName, Flags | BindingFlags.DeclaredOnly) != null)
                return current;

        return null;
    }
}
