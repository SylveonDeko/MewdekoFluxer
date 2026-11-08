namespace Mewdeko.Controllers.Common.Giveaways;

/// <summary>
///     Request that changes the extra entries and automatic prize of a giveaway.
/// </summary>
public class GiveawayOptionsRequest
{
    /// <summary>
    ///     Gets or sets the roles that grant extra entries, as space separated roleId:extra pairs.
    /// </summary>
    public string? ExtraEntryRoles { get; set; }

    /// <summary>
    ///     Gets or sets the prize type, 0 none, 1 xp, 2 coins.
    /// </summary>
    public int PrizeType { get; set; }

    /// <summary>
    ///     Gets or sets the prize amount.
    /// </summary>
    public long PrizeAmount { get; set; }
}
