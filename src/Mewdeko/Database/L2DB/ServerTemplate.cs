#nullable enable

using LinqToDB.Mapping;

namespace DataModel;

/// <summary>
///     Represents a named set of roles and channels a user saved from a community, ready to apply to another one.
/// </summary>
[Table("ServerTemplates")]
public class ServerTemplate
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the user the template belongs to.
    /// </summary>
    [Column("UserId")]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the template name.
    /// </summary>
    [Column("Name")]
    public string Name { get; set; } = "";

    /// <summary>
    ///     Gets or sets the gzip compressed JSON the template is stored as.
    /// </summary>
    [Column("Data")]
    public byte[] Data { get; set; } = [];

    /// <summary>
    ///     Gets or sets the compressed size in bytes.
    /// </summary>
    [Column("SizeBytes")]
    public int SizeBytes { get; set; }

    /// <summary>
    ///     Gets or sets how many roles the template holds.
    /// </summary>
    [Column("RoleCount")]
    public int RoleCount { get; set; }

    /// <summary>
    ///     Gets or sets how many channels and categories the template holds.
    /// </summary>
    [Column("ChannelCount")]
    public int ChannelCount { get; set; }

    /// <summary>
    ///     Gets or sets when the template was saved.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
