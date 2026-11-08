using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

[Table("FluxerListVoters")]
public class FluxerListVoter
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; } // integer

    [Column("GuildId")]
    public ulong GuildId { get; set; } // numeric(20,0)

    [Column("UserId")]
    public ulong UserId { get; set; } // numeric(20,0)

    [Column("VotedAt")]
    public DateTime VotedAt { get; set; } // timestamp without time zone

    [Column("DateAdded")]
    public DateTime? DateAdded { get; set; } // timestamp without time zone
}
