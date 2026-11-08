#nullable enable

using LinqToDB.Mapping;

namespace DataModel;

/// <summary>
///     Represents a compressed snapshot of a community's settings, roles, channels and emojis that its owner can
///     restore into the same community or another one.
/// </summary>
[Table("ServerBackups")]
public class ServerBackup
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the user the backup belongs to.
    /// </summary>
    [Column("UserId")]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the community the backup was taken from.
    /// </summary>
    [Column("SourceGuildId")]
    public ulong SourceGuildId { get; set; }

    /// <summary>
    ///     Gets or sets the name the community had when the backup was taken.
    /// </summary>
    [Column("SourceGuildName")]
    public string SourceGuildName { get; set; } = "";

    /// <summary>
    ///     Gets or sets the gzip compressed JSON the backup is stored as.
    /// </summary>
    [Column("Data")]
    public byte[] Data { get; set; } = [];

    /// <summary>
    ///     Gets or sets the compressed size in bytes.
    /// </summary>
    [Column("SizeBytes")]
    public int SizeBytes { get; set; }

    /// <summary>
    ///     Gets or sets how many roles the backup holds.
    /// </summary>
    [Column("RoleCount")]
    public int RoleCount { get; set; }

    /// <summary>
    ///     Gets or sets how many channels and categories the backup holds.
    /// </summary>
    [Column("ChannelCount")]
    public int ChannelCount { get; set; }

    /// <summary>
    ///     Gets or sets how many emojis the backup holds.
    /// </summary>
    [Column("EmojiCount")]
    public int EmojiCount { get; set; }

    /// <summary>
    ///     Gets or sets how many messages the backup holds.
    /// </summary>
    [Column("MessageCount")]
    public int MessageCount { get; set; }

    /// <summary>
    ///     Gets or sets when the backup was taken.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
