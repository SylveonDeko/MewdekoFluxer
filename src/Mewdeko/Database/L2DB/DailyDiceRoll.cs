#nullable enable

using LinqToDB.Mapping;

namespace DataModel;

/// <summary>
///     Represents one member's daily dice roll.
/// </summary>
[Table("DailyDiceRolls")]
public class DailyDiceRoll
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the guild ID.
    /// </summary>
    [Column("GuildId")]
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets the member's user ID.
    /// </summary>
    [Column("UserId")]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the UTC day the roll counts for.
    /// </summary>
    [Column("RollDate", DataType = LinqToDB.DataType.Date)]
    public DateTime RollDate { get; set; }

    /// <summary>
    ///     Gets or sets the rolled number.
    /// </summary>
    [Column("Roll")]
    public int Roll { get; set; }

    /// <summary>
    ///     Gets or sets when the roll was made.
    /// </summary>
    [Column("RolledAt")]
    public DateTime RolledAt { get; set; }
}
