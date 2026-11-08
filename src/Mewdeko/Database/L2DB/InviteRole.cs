using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

/// <summary>
///     A role a member earns once their net invite count reaches <see cref="RequiredInvites" />.
/// </summary>
[Table("InviteRoles")]
public class InviteRole
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    [Column("GuildId")]
    public ulong GuildId { get; set; }

    [Column("RoleId")]
    public ulong RoleId { get; set; }

    [Column("RequiredInvites")]
    public int RequiredInvites { get; set; }

    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
