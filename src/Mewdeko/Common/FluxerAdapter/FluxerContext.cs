namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Holds the bot's Fluxer client so adapter members can reach the gateway cache and the API from an entity.
/// </summary>
public static class FluxerContext
{
    /// <summary>
    ///     The client the bot runs on, set once at startup.
    /// </summary>
    public static FluxerClient Client { get; internal set; } = null!;

    /// <summary>
    ///     The earlier states the gateway no longer has, set once at startup.
    /// </summary>
    public static GatewaySnapshotCache Snapshots { get; internal set; } = new();

    /// <summary>
    ///     The web address messages and channels link to.
    /// </summary>
    public static string WebUrl { get; internal set; } = "https://web.fluxer.app";

    /// <summary>
    ///     Whether the gateway connection is currently up.
    /// </summary>
    public static bool IsConnected { get; internal set; }
}
