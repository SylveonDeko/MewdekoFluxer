using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Mewdeko.Modules.Migration.Common;

/// <summary>
///     A channel as it was on Discord when the server was exported.
/// </summary>
public sealed record DiscordChannelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("position")] int Position);

/// <summary>
///     A role as it was on Discord when the server was exported.
/// </summary>
public sealed record DiscordRoleInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("color")] uint Color,
    [property: JsonPropertyName("managed")] bool Managed);

/// <summary>
///     A custom emoji as it was on Discord when the server was exported.
/// </summary>
public sealed record DiscordEmojiInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("animated")] bool Animated);

/// <summary>
///     The description of an exported Discord server: its layout and which tables were written.
/// </summary>
public sealed class MigrationMeta
{
    /// <summary>Gets or sets the format version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Gets or sets the Discord server id.</summary>
    [JsonPropertyName("discordGuildId")]
    public string DiscordGuildId { get; set; } = "0";

    /// <summary>Gets or sets the Discord server's name.</summary>
    [JsonPropertyName("guildName")]
    public string GuildName { get; set; } = "";

    /// <summary>Gets or sets the Discord user who exported the server.</summary>
    [JsonPropertyName("requestedBy")]
    public string RequestedBy { get; set; } = "0";

    /// <summary>Gets or sets when the export was made.</summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets the Discord channels.</summary>
    [JsonPropertyName("channels")]
    public List<DiscordChannelInfo> Channels { get; set; } = [];

    /// <summary>Gets or sets the Discord roles.</summary>
    [JsonPropertyName("roles")]
    public List<DiscordRoleInfo> Roles { get; set; } = [];

    /// <summary>Gets or sets the Discord custom emojis.</summary>
    [JsonPropertyName("emojis")]
    public List<DiscordEmojiInfo> Emojis { get; set; } = [];

    /// <summary>Gets or sets the names of the exported tables.</summary>
    [JsonPropertyName("tables")]
    public List<string> Tables { get; set; } = [];

    /// <summary>Gets the Discord server id as a number.</summary>
    [JsonIgnore]
    public ulong GuildId => ulong.Parse(DiscordGuildId);

    /// <summary>Gets the exporting user's id as a number.</summary>
    [JsonIgnore]
    public ulong RequestedById => ulong.Parse(RequestedBy);
}

/// <summary>
///     One exported table: its column names, their Postgres types and the rows.
/// </summary>
public sealed class MigrationTable
{
    /// <summary>Gets or sets the column names.</summary>
    [JsonPropertyName("columns")]
    public List<string> Columns { get; set; } = [];

    /// <summary>Gets or sets the Postgres type of each column, in column order.</summary>
    [JsonPropertyName("types")]
    public List<string> Types { get; set; } = [];

    /// <summary>Gets or sets the rows, each a list of values in column order.</summary>
    [JsonPropertyName("rows")]
    public List<JsonArray> Rows { get; set; } = [];
}

/// <summary>
///     Reads an exported server from Redis, where the Discord bot left it.
/// </summary>
public static class MigrationBundle
{
    /// <summary>The letters migration and link codes are made of.</summary>
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    ///     Normalizes a code the way it was typed, or returns null when it cannot be one.
    /// </summary>
    /// <param name="code">The code as typed.</param>
    /// <returns>The upper case code, or null.</returns>
    public static string? NormalizeCode(string code)
    {
        var trimmed = code.Trim().ToUpperInvariant();
        return trimmed.Length == 8 && trimmed.All(CodeAlphabet.Contains) ? trimmed : null;
    }

    /// <summary>
    ///     Reads the description of an export.
    /// </summary>
    /// <param name="redis">The shared Redis database.</param>
    /// <param name="code">The normalized code.</param>
    /// <returns>The description, or null when the code is unknown or expired.</returns>
    public static async Task<MigrationMeta?> ReadMetaAsync(IDatabase redis, string code)
    {
        var data = await redis.StringGetAsync($"mewdeko_migration:{code}:meta");
        return data.IsNullOrEmpty ? null : Deserialize<MigrationMeta>(data!);
    }

    /// <summary>
    ///     Reads one exported table.
    /// </summary>
    /// <param name="redis">The shared Redis database.</param>
    /// <param name="code">The normalized code.</param>
    /// <param name="table">The table name.</param>
    /// <returns>The table, or null when it was not exported.</returns>
    public static async Task<MigrationTable?> ReadTableAsync(IDatabase redis, string code, string table)
    {
        var data = await redis.StringGetAsync($"mewdeko_migration:{code}:table:{table}");
        return data.IsNullOrEmpty ? null : Deserialize<MigrationTable>(data!);
    }

    /// <summary>
    ///     Deletes every key of an export once it has been used.
    /// </summary>
    /// <param name="redis">The shared Redis database.</param>
    /// <param name="code">The normalized code.</param>
    /// <param name="meta">The export's description, which lists its tables.</param>
    public static async Task DeleteAsync(IDatabase redis, string code, MigrationMeta meta)
    {
        var keys = meta.Tables.Select(x => (RedisKey)$"mewdeko_migration:{code}:table:{x}")
            .Append($"mewdeko_migration:{code}:meta")
            .ToArray();
        await redis.KeyDeleteAsync(keys);
    }

    private static T Deserialize<T>(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(gzip)!;
    }
}
