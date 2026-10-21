namespace Mewdeko.Modules.ServerStats.Services;

/// <summary>
///     Answers live questions about members currently in a game or app. Fluxer presences carry only a status and a
///     custom status, so nothing is recorded any more: totals already stored keep showing, and nobody counts as
///     currently playing.
/// </summary>
public class ActivityTrackingService : INService
{
    /// <summary>
    ///     Gets the seconds a member has accrued in their open sessions, keyed by activity name.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="userId">The member.</param>
    /// <returns>Open seconds per activity name, which is always empty.</returns>
    public Dictionary<string, int> GetOpenSessionSeconds(ulong guildId, ulong userId)
    {
        return new Dictionary<string, int>();
    }

    /// <summary>
    ///     Counts members currently in a tracked activity, optionally one by name.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="name">An activity name, or null for any.</param>
    /// <returns>The member count, which is always zero.</returns>
    public int CountActiveNow(ulong guildId, string? name = null)
    {
        return 0;
    }
}
