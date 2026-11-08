namespace Mewdeko.Modules.Giveaways.Common;

/// <summary>
///     The prize a giveaway pays its winners automatically.
/// </summary>
public enum GiveawayPrizeType
{
    /// <summary>
    ///     No automatic prize.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Experience points.
    /// </summary>
    Xp = 1,

    /// <summary>
    ///     Server currency.
    /// </summary>
    Coins = 2
}

/// <summary>
///     The values of a giveaway's <c>Ended</c> column.
/// </summary>
public static class GiveawayStatus
{
    /// <summary>
    ///     The giveaway is running.
    /// </summary>
    public const int Active = 0;

    /// <summary>
    ///     The giveaway has ended.
    /// </summary>
    public const int Ended = 1;

    /// <summary>
    ///     The giveaway is saved but not started.
    /// </summary>
    public const int Draft = 2;
}

/// <summary>
///     Parses and applies the roles that grant extra giveaway entries.
/// </summary>
public static class GiveawayExtraEntries
{
    /// <summary>
    ///     Parses space separated <c>roleId:extraEntries</c> pairs, skipping invalid ones.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The extra entries by role ID.</returns>
    public static Dictionary<ulong, int> Parse(string? value)
    {
        var result = new Dictionary<ulong, int>();

        if (string.IsNullOrWhiteSpace(value))
            return result;

        foreach (var pair in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');

            if (parts.Length == 2 && ulong.TryParse(parts[0], out var roleId) &&
                int.TryParse(parts[1], out var extra) && extra > 0)
                result[roleId] = extra;
        }

        return result;
    }

    /// <summary>
    ///     Formats extra entries for storage.
    /// </summary>
    /// <param name="entries">The extra entries by role ID.</param>
    /// <returns>The stored value, or null when there are none.</returns>
    public static string? Format(IReadOnlyDictionary<ulong, int> entries)
    {
        return entries.Count == 0 ? null : string.Join(' ', entries.Select(x => $"{x.Key}:{x.Value}"));
    }

    /// <summary>
    ///     Gets how many entries a member's roles are worth, counting the base entry.
    /// </summary>
    /// <param name="roleIds">The member's role IDs.</param>
    /// <param name="entries">The extra entries by role ID.</param>
    public static int WeightFor(IEnumerable<ulong> roleIds, IReadOnlyDictionary<ulong, int> entries)
    {
        return 1 + roleIds.Where(entries.ContainsKey).Sum(x => entries[x]);
    }

    /// <summary>
    ///     Picks distinct winners, each candidate weighted by their entries.
    /// </summary>
    /// <param name="candidates">The candidates and their weights.</param>
    /// <param name="count">How many winners to pick.</param>
    /// <typeparam name="T">The candidate type.</typeparam>
    public static List<T> PickWinners<T>(IEnumerable<(T Candidate, int Weight)> candidates, int count)
    {
        var pool = candidates.Select(x => (x.Candidate, Weight: Math.Max(1, x.Weight))).ToList();
        var winners = new List<T>();

        while (winners.Count < count && pool.Count > 0)
        {
            var roll = Random.Shared.Next(pool.Sum(x => x.Weight));
            var index = 0;

            while (roll >= pool[index].Weight)
            {
                roll -= pool[index].Weight;
                index++;
            }

            winners.Add(pool[index].Candidate);
            pool.RemoveAt(index);
        }

        return winners;
    }
}
