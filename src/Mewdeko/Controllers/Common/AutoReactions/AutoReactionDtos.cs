using System.Text.Json.Serialization;
using DataModel;

namespace Mewdeko.Controllers.Common.AutoReactions;

/// <summary>
///     An auto reaction rule.
/// </summary>
public class AutoReactionDto
{
    /// <summary>
    ///     Gets or sets the rule ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the channel the rule applies to.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the emojis, in the order they are added.
    /// </summary>
    public List<string> Emojis { get; set; } = [];

    /// <summary>
    ///     Gets or sets which messages the rule reacts to.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AutoReactionMessageType MessageTypes { get; set; }

    /// <summary>
    ///     Gets or sets whether messages from bots are skipped.
    /// </summary>
    public bool IgnoreBots { get; set; }

    /// <summary>
    ///     Gets or sets whether messages from webhooks are skipped.
    /// </summary>
    public bool IgnoreWebhooks { get; set; }

    /// <summary>
    ///     Gets or sets whether replies are skipped.
    /// </summary>
    public bool IgnoreReplies { get; set; }

    /// <summary>
    ///     Gets or sets the roles allowed to trigger the rule. Empty allows everyone.
    /// </summary>
    public List<ulong> AllowedRoleIds { get; set; } = [];

    /// <summary>
    ///     Gets or sets the roles that never trigger the rule.
    /// </summary>
    public List<ulong> IgnoredRoleIds { get; set; } = [];

    /// <summary>
    ///     Gets or sets the members that never trigger the rule.
    /// </summary>
    public List<ulong> IgnoredUserIds { get; set; } = [];

    /// <summary>
    ///     Gets or sets the words that stop the rule when a message has one.
    /// </summary>
    public List<string> SkipWords { get; set; } = [];

    /// <summary>
    ///     Gets or sets the words of which a message needs at least one.
    /// </summary>
    public List<string> RequiredWords { get; set; } = [];

    /// <summary>
    ///     Gets or sets the position of the rule in its guild.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    ///     Gets or sets whether messages that do not fit the message type are deleted.
    /// </summary>
    public bool DeleteNonMatching { get; set; }

    /// <summary>
    ///     Gets or sets whether the rule is on.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
///     A guild's rules with the limits the dashboard has to respect.
/// </summary>
public class AutoReactionListResponse
{
    /// <summary>
    ///     Gets or sets the rules, first to last.
    /// </summary>
    public List<AutoReactionDto> Rules { get; set; } = [];

    /// <summary>
    ///     Gets or sets the most emojis a rule can have.
    /// </summary>
    public int MaxEmojis { get; set; }

    /// <summary>
    ///     Gets or sets the most words a rule can have in each word list.
    /// </summary>
    public int MaxWords { get; set; }

    /// <summary>
    ///     Gets or sets the longest a word can be.
    /// </summary>
    public int MaxWordLength { get; set; }

    /// <summary>
    ///     Gets or sets the role whose members can configure auto reactions without Manage Server.
    /// </summary>
    public ulong? ManagerRoleId { get; set; }
}

/// <summary>
///     Sets the order of a guild's rules.
/// </summary>
public class AutoReactionOrderRequest
{
    /// <summary>
    ///     Gets or sets the rule IDs, first to last.
    /// </summary>
    public List<int> RuleIds { get; set; } = [];
}

/// <summary>
///     Sets or clears the auto reaction manager role.
/// </summary>
public class AutoReactionManagerRequest
{
    /// <summary>
    ///     Gets or sets the role ID, or null to clear it.
    /// </summary>
    public ulong? RoleId { get; set; }
}
