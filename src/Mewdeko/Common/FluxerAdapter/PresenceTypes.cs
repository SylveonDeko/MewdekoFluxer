namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     The online status a user shows.
/// </summary>
public enum UserStatus
{
    /// <summary>The user is offline.</summary>
    Offline,

    /// <summary>The user is online.</summary>
    Online,

    /// <summary>The user is idle.</summary>
    Idle,

    /// <summary>The user does not want to be disturbed.</summary>
    DoNotDisturb,

    /// <summary>The user is online but shows as offline.</summary>
    Invisible
}

/// <summary>
///     The kind of activity a status describes. Fluxer only has custom statuses, the other kinds stay so that settings
///     and statistics saved with them still load.
/// </summary>
public enum ActivityType
{
    /// <summary>Playing a game.</summary>
    Playing = 0,

    /// <summary>Streaming.</summary>
    Streaming = 1,

    /// <summary>Listening to something.</summary>
    Listening = 2,

    /// <summary>Watching something.</summary>
    Watching = 3,

    /// <summary>A custom status.</summary>
    CustomStatus = 4,

    /// <summary>Competing in something.</summary>
    Competing = 5
}

/// <summary>
///     Conversions between Fluxer's status values and <see cref="UserStatus" />.
/// </summary>
public static class UserStatusExtensions
{
    /// <summary>
    ///     Reads a status as Fluxer sends it, such as "online" or "dnd".
    /// </summary>
    public static UserStatus ParseUserStatus(string? status) => status switch
    {
        "online" => UserStatus.Online,
        "idle" => UserStatus.Idle,
        "dnd" => UserStatus.DoNotDisturb,
        "invisible" => UserStatus.Invisible,
        _ => UserStatus.Offline
    };

    /// <summary>
    ///     The status to send to Fluxer for the bot's own presence. Offline is sent as invisible.
    /// </summary>
    public static Status ToFluxerStatus(this UserStatus status) => status switch
    {
        UserStatus.Online => Status.Online,
        UserStatus.Idle => Status.Idle,
        UserStatus.DoNotDisturb => Status.Dnd,
        _ => Status.Invisible
    };
}
