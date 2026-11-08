#nullable enable

using LinqToDB.Mapping;

namespace DataModel;

/// <summary>
///     Represents a user's own rank card background.
/// </summary>
[Table("UserXpBackgrounds")]
public class UserXpBackground
{
    /// <summary>
    ///     Gets or sets the database row identifier.
    /// </summary>
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the user ID.
    /// </summary>
    [Column("UserId")]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the image: an image URL, a preset reference, or a file name prefixed with local: for an upload.
    /// </summary>
    [Column("Image")]
    public string Image { get; set; } = "";

    /// <summary>
    ///     Gets or sets when the row was created.
    /// </summary>
    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
