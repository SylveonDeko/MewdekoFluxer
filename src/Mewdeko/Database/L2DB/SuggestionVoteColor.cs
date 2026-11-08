using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

/// <summary>
///     A guild's suggestion vote colour setting: how many votes one side has to lead by to recolour the embed.
/// </summary>
[Table("SuggestionVoteColors")]
public class SuggestionVoteColor
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    [Column("GuildId")]
    public ulong GuildId { get; set; }

    [Column("Threshold")]
    public int Threshold { get; set; }

    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
