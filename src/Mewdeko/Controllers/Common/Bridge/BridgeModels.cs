using System.Text.Json.Serialization;
using Mewdeko.Modules.Bridge.Common;

namespace Mewdeko.Controllers.Common.Bridge;

/// <summary>
///     A channel bridge of a guild.
/// </summary>
public class BridgeResponse
{
    /// <summary>
    ///     The bridge id.
    /// </summary>
    public Guid BridgeId { get; set; }

    /// <summary>
    ///     The bridged channel in this guild.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     The bridged channel's name.
    /// </summary>
    public string? ChannelName { get; set; }

    /// <summary>
    ///     The community on the other platform.
    /// </summary>
    public ulong RemoteGuildId { get; set; }

    /// <summary>
    ///     The channel on the other platform.
    /// </summary>
    public ulong RemoteChannelId { get; set; }

    /// <summary>
    ///     The other community's name.
    /// </summary>
    public string RemoteGuildName { get; set; } = "";

    /// <summary>
    ///     The other channel's name.
    /// </summary>
    public string RemoteChannelName { get; set; } = "";

    /// <summary>
    ///     The platform this bridge lives on.
    /// </summary>
    public string Platform { get; set; } = "";

    /// <summary>
    ///     Whether the bridge is paused.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    ///     Whether bot messages are relayed.
    /// </summary>
    public bool AllowBots { get; set; }

    /// <summary>
    ///     Which way messages flow on this side: Both, SendOnly or ReceiveOnly.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeDirection Direction { get; set; }

    /// <summary>
    ///     The roles allowed to be relayed outward. Empty means everyone.
    /// </summary>
    public ulong[] AllowedRoleIds { get; set; } = [];

    /// <summary>
    ///     The roles that are never relayed outward.
    /// </summary>
    public ulong[] BlockedRoleIds { get; set; } = [];

    /// <summary>
    ///     The users that are never relayed outward.
    /// </summary>
    public ulong[] BlockedUserIds { get; set; } = [];

    /// <summary>
    ///     How many minutes a member must have been in the community to be relayed outward. 0 is off.
    /// </summary>
    public int MinMemberAgeMinutes { get; set; }

    /// <summary>
    ///     Whether outgoing messages with invite links are dropped.
    /// </summary>
    public bool BlockInvites { get; set; }

    /// <summary>
    ///     Whether outgoing messages caught by the word filter are dropped.
    /// </summary>
    public bool UseWordFilter { get; set; }

    /// <summary>
    ///     How incoming authors are named on this side's webhook.
    /// </summary>
    public string NameFormat { get; set; } = "";

    /// <summary>
    ///     The pair the bridge was made through, or null when it was made with a code.
    /// </summary>
    public string? PairId { get; set; }

    /// <summary>
    ///     When the bridge was made.
    /// </summary>
    public DateTime DateAdded { get; set; }
}

/// <summary>
///     Replaces all the options of a bridge. Every field is required.
/// </summary>
public class BridgeSettingsRequest
{
    /// <summary>
    ///     Which way messages flow on this side: Both, SendOnly or ReceiveOnly.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeDirection? Direction { get; set; }

    /// <summary>
    ///     The roles allowed to be relayed outward. Empty means everyone.
    /// </summary>
    public ulong[]? AllowedRoleIds { get; set; }

    /// <summary>
    ///     The roles that are never relayed outward.
    /// </summary>
    public ulong[]? BlockedRoleIds { get; set; }

    /// <summary>
    ///     The users that are never relayed outward.
    /// </summary>
    public ulong[]? BlockedUserIds { get; set; }

    /// <summary>
    ///     How many minutes a member must have been in the community to be relayed outward, 0 to 525600.
    /// </summary>
    public int? MinMemberAgeMinutes { get; set; }

    /// <summary>
    ///     Whether outgoing messages with invite links are dropped.
    /// </summary>
    public bool? BlockInvites { get; set; }

    /// <summary>
    ///     Whether outgoing messages caught by the word filter are dropped.
    /// </summary>
    public bool? UseWordFilter { get; set; }

    /// <summary>
    ///     How incoming authors are named. Must contain {name} and be at most 80 characters.
    /// </summary>
    public string? NameFormat { get; set; }
}

/// <summary>
///     Request for a link code.
/// </summary>
public class BridgeCodeRequest
{
    /// <summary>
    ///     The channel to bridge.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     The dashboard user asking.
    /// </summary>
    public ulong UserId { get; set; }
}

/// <summary>
///     Request to link a channel to a code from the other platform.
/// </summary>
public class BridgeLinkRequest
{
    /// <summary>
    ///     The code made on the other platform.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    ///     The channel to bridge.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     The dashboard user asking.
    /// </summary>
    public ulong UserId { get; set; }
}

/// <summary>
///     A created link code.
/// </summary>
public class BridgeCodeResponse
{
    /// <summary>
    ///     The code to run <c>bridge link</c> with on the other platform.
    /// </summary>
    public string Code { get; set; } = "";

    /// <summary>
    ///     When the code expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
///     Request to change whether bot messages are relayed.
/// </summary>
public class BridgeAllowBotsRequest
{
    /// <summary>
    ///     Whether to relay bot messages.
    /// </summary>
    public bool Allow { get; set; }
}
