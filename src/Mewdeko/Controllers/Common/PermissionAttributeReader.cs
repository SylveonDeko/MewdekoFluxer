namespace Mewdeko.Controllers;

/// <summary>
///     Reads the permission a command precondition requires.
/// </summary>
public static class PermissionAttributeReader
{
    /// <summary>
    ///     Gets the guild permission a precondition requires.
    /// </summary>
    /// <param name="attribute">A user or bot permission precondition.</param>
    /// <returns>The required guild permission, or null when it checks a channel permission.</returns>
    public static GuildPermission? GuildPermissionOf(object? attribute) =>
        attribute switch
        {
            RequireUserPermissionAttribute user => user.Guild,
            RequireBotPermissionAttribute bot => bot.Guild,
            _ => null
        };

    /// <summary>
    ///     Gets the channel permission a precondition requires.
    /// </summary>
    /// <param name="attribute">A user or bot permission precondition.</param>
    /// <returns>The required channel permission, or null when it checks a guild permission.</returns>
    public static ChannelPermission? ChannelPermissionOf(object? attribute) =>
        attribute switch
        {
            RequireUserPermissionAttribute user => user.Channel,
            RequireBotPermissionAttribute bot => bot.Channel,
            _ => null
        };
}
