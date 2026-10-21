using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

/// <summary>
///     Ties a Fluxer account to the Discord account its owner proved they hold, so data moved over from Discord can
///     follow them.
/// </summary>
[Table("DiscordAccountLinks")]
public class DiscordAccountLink
{
    [Column("FluxerUserId", IsPrimaryKey = true)]
    public ulong FluxerUserId { get; set; }

    [Column("DiscordUserId")]
    public ulong DiscordUserId { get; set; }

    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
