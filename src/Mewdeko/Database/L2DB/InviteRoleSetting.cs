using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

/// <summary>
///     How invite milestone roles are handed out.
/// </summary>
public enum InviteRoleMode
{
    StackAll = 0,

    HighestOnly = 1
}

/// <summary>
///     A guild's invite milestone role settings.
/// </summary>
[Table("InviteRoleSettings")]
public class InviteRoleSetting
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    [Column("GuildId")]
    public ulong GuildId { get; set; }

    [Column("Mode")]
    public InviteRoleMode Mode { get; set; }

    [Column("RemoveOnDrop")]
    public bool RemoveOnDrop { get; set; }
}
