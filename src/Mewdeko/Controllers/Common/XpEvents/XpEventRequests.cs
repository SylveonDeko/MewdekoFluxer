namespace Mewdeko.Controllers.Common.XpEvents;

/// <summary>
///     A request to reset every member's XP in a server.
/// </summary>
public class XpResetRequest
{
    /// <summary>
    ///     Whether bonus XP is wiped as well.
    /// </summary>
    public bool ResetBonusXp { get; set; }
}

/// <summary>
///     A request to add a custom level-up message.
/// </summary>
public class XpLevelUpMessageRequest
{
    /// <summary>
    ///     The message template.
    /// </summary>
    public string Message { get; set; } = "";
}

/// <summary>
///     A request to set the XP multiplier of a channel or role.
/// </summary>
public class XpMultiplierRequest
{
    /// <summary>
    ///     The channel or role ID.
    /// </summary>
    public ulong Id { get; set; }

    /// <summary>
    ///     The multiplier.
    /// </summary>
    public double Multiplier { get; set; }
}

/// <summary>
///     A request to start a timed XP boost.
/// </summary>
public class XpBoostRequest
{
    /// <summary>
    ///     The boost name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     The XP multiplier.
    /// </summary>
    public double Multiplier { get; set; }

    /// <summary>
    ///     How long the boost runs, in minutes.
    /// </summary>
    public int DurationMinutes { get; set; }

    /// <summary>
    ///     The channels the boost is limited to, empty for all.
    /// </summary>
    public List<ulong> ChannelIds { get; set; } = [];

    /// <summary>
    ///     The roles the boost is limited to, empty for all.
    /// </summary>
    public List<ulong> RoleIds { get; set; } = [];
}

/// <summary>
///     A request to start an XP competition.
/// </summary>
public class XpCompetitionRequest
{
    /// <summary>
    ///     The competition name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     The competition type, 0 most gained, 1 reach level, 2 highest total.
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    ///     How long the competition runs, in minutes.
    /// </summary>
    public int DurationMinutes { get; set; }

    /// <summary>
    ///     The level to reach for reach-level competitions.
    /// </summary>
    public int TargetLevel { get; set; }

    /// <summary>
    ///     The channel the end is announced in, 0 for none.
    /// </summary>
    public ulong AnnouncementChannelId { get; set; }

    /// <summary>
    ///     The placement rewards.
    /// </summary>
    public List<XpCompetitionRewardRequest> Rewards { get; set; } = [];
}

/// <summary>
///     A placement reward of a new competition.
/// </summary>
public class XpCompetitionRewardRequest
{
    /// <summary>
    ///     The placement the reward is for.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    ///     The role given, 0 for none.
    /// </summary>
    public ulong RoleId { get; set; }

    /// <summary>
    ///     The bonus XP given.
    /// </summary>
    public int XpAmount { get; set; }

    /// <summary>
    ///     The currency given.
    /// </summary>
    public long CurrencyAmount { get; set; }
}
