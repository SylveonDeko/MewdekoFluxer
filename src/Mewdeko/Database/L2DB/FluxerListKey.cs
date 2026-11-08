using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

[Table("FluxerListKeys")]
public class FluxerListKey
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; } // integer

    [Column("GuildId")]
    public ulong GuildId { get; set; } // numeric(20,0)

    [Column("EncryptedKey")]
    public string EncryptedKey { get; set; } = ""; // text

    [Column("LastSyncAt")]
    public DateTime? LastSyncAt { get; set; } // timestamp without time zone

    [Column("LastFullSyncAt")]
    public DateTime? LastFullSyncAt { get; set; } // timestamp without time zone

    [Column("LastError")]
    public string? LastError { get; set; } // text

    [Column("NextPollAt")]
    public DateTime NextPollAt { get; set; } // timestamp without time zone

    [Column("BackoffUntil")]
    public DateTime? BackoffUntil { get; set; } // timestamp without time zone

    [Column("ConsecutiveFailures")]
    public int ConsecutiveFailures { get; set; } // integer

    [Column("DateAdded")]
    public DateTime? DateAdded { get; set; } // timestamp without time zone
}
