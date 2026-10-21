namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     The kinds of mention a message may ping.
/// </summary>
[Flags]
public enum AllowedMentionTypes
{
    /// <summary>No mention kind.</summary>
    None = 0,

    /// <summary>User mentions.</summary>
    Users = 1,

    /// <summary>Role mentions.</summary>
    Roles = 2,

    /// <summary>The everyone and here mentions.</summary>
    Everyone = 4
}

/// <summary>
///     Which mentions in a message actually ping, either by kind or by listing users and roles.
/// </summary>
public sealed class AllowedMentions
{
    /// <summary>
    ///     Creates a set that pings the given kinds of mention.
    /// </summary>
    public AllowedMentions(AllowedMentionTypes allowedTypes = AllowedMentionTypes.None)
    {
        AllowedTypes = allowedTypes;
    }

    /// <summary>A set that pings nobody.</summary>
    public static AllowedMentions None => new();

    /// <summary>A set that pings every mention written in the message.</summary>
    public static AllowedMentions All =>
        new(AllowedMentionTypes.Users | AllowedMentionTypes.Roles | AllowedMentionTypes.Everyone);

    /// <summary>The kinds of mention that ping.</summary>
    public AllowedMentionTypes AllowedTypes { get; set; }

    /// <summary>Specific users who are pinged, used instead of the user kind.</summary>
    public List<ulong> UserIds { get; set; } = [];

    /// <summary>Specific roles that are pinged, used instead of the role kind.</summary>
    public List<ulong> RoleIds { get; set; } = [];

    /// <summary>Whether a reply pings the author of the message it replies to.</summary>
    public bool? MentionRepliedUser { get; set; }

    /// <summary>
    ///     Builds the request Fluxer expects. Kinds are left out whenever specific users or roles are listed, since
    ///     Fluxer refuses a parse list and ids in the same request.
    /// </summary>
    public AllowedMentionsRequest ToRequest()
    {
        var parse = new HashSet<AllowedMentionParseType>();
        if (UserIds.Count == 0 && RoleIds.Count == 0)
        {
            if (AllowedTypes.HasFlag(AllowedMentionTypes.Users))
                parse.Add(AllowedMentionParseType.USERS);
            if (AllowedTypes.HasFlag(AllowedMentionTypes.Roles))
                parse.Add(AllowedMentionParseType.ROLES);
            if (AllowedTypes.HasFlag(AllowedMentionTypes.Everyone))
                parse.Add(AllowedMentionParseType.EVERYONE);
        }

        return new AllowedMentionsRequest
        {
            Parse = parse,
            Users = UserIds.Count > 0 ? [..UserIds] : null,
            Roles = RoleIds.Count > 0 ? [..RoleIds] : null,
            RepliedUser = MentionRepliedUser
        };
    }

    /// <summary>
    ///     Builds the request Fluxer expects.
    /// </summary>
    public static implicit operator AllowedMentionsRequest(AllowedMentions mentions) => mentions.ToRequest();
}
