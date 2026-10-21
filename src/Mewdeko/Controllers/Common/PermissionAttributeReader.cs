using System.Reflection;

namespace Mewdeko.Controllers;

/// <summary>
///     Reads the permission a command precondition requires, which Fluxer.Net keeps private.
/// </summary>
public static class PermissionAttributeReader
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>
    ///     Gets the guild permission a precondition requires.
    /// </summary>
    /// <param name="attribute">A user or bot permission precondition.</param>
    /// <returns>The required guild permission, or null when it checks a channel permission.</returns>
    public static GuildPermission? GuildPermissionOf(object? attribute) =>
        attribute?.GetType().BaseTypeWithField("Guild")?.GetField("Guild", Flags)?.GetValue(attribute) as GuildPermission?;

    /// <summary>
    ///     Gets the channel permission a precondition requires.
    /// </summary>
    /// <param name="attribute">A user or bot permission precondition.</param>
    /// <returns>The required channel permission, or null when it checks a guild permission.</returns>
    public static ChannelPermission? ChannelPermissionOf(object? attribute) =>
        attribute?.GetType().BaseTypeWithField("Channel")?.GetField("Channel", Flags)?.GetValue(attribute) as ChannelPermission?;

    private static Type? BaseTypeWithField(this Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetField(name, Flags | BindingFlags.DeclaredOnly) != null)
                return current;
        }

        return null;
    }
}
