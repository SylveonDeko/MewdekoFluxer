using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

/// <summary>
///     A member's row from a migrated server, kept until that member links their Discord account and it can be
///     written with their Fluxer id.
/// </summary>
[Table("MigrationPendingRows")]
public class MigrationPendingRow
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public long Id { get; set; }

    [Column("MigrationId")]
    public int MigrationId { get; set; }

    [Column("DiscordUserId")]
    public ulong DiscordUserId { get; set; }

    [Column("TableName")]
    public string TableName { get; set; } = null!;

    [Column("OldKey")]
    public string? OldKey { get; set; }

    [Column("Row", DataType = LinqToDB.DataType.BinaryJson)]
    public string Row { get; set; } = null!;

    [Column("Position")]
    public int Position { get; set; }
}
