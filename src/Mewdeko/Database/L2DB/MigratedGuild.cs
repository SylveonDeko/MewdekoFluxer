using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace DataModel;

/// <summary>
///     A server whose Mewdeko data was brought over from Discord, with the channel and role matches used, so member
///     data that arrives later is mapped the same way.
/// </summary>
[Table("MigratedGuilds")]
public class MigratedGuild
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
    public int Id { get; set; }

    [Column("GuildId")]
    public ulong GuildId { get; set; }

    [Column("DiscordGuildId")]
    public ulong DiscordGuildId { get; set; }

    [Column("MigratedBy")]
    public ulong MigratedBy { get; set; }

    [Column("ChannelMap", DataType = LinqToDB.DataType.BinaryJson)]
    public string ChannelMap { get; set; } = "{}";

    [Column("RoleMap", DataType = LinqToDB.DataType.BinaryJson)]
    public string RoleMap { get; set; } = "{}";

    [Column("DateAdded")]
    public DateTime DateAdded { get; set; }
}
