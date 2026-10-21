using System.IO;
using System.Text.Json.Nodes;

namespace Mewdeko.Modules.Migration.Common;

/// <summary>
///     What a column holds, as far as moving it from Discord to Fluxer goes.
/// </summary>
public enum IdKind
{
    /// <summary>Not an id; the value is copied as it is.</summary>
    None,

    /// <summary>A channel id.</summary>
    Channel,

    /// <summary>A role id.</summary>
    Role,

    /// <summary>A user id.</summary>
    User,

    /// <summary>A message id, which never exists on Fluxer.</summary>
    Message,

    /// <summary>A custom emoji id.</summary>
    Emoji,

    /// <summary>The server id.</summary>
    Guild,

    /// <summary>Free text that may hold channel, role, user or emoji mentions.</summary>
    Text,

    /// <summary>A value that has no meaning on Fluxer and is emptied.</summary>
    Clear,

    /// <summary>A value that is copied as it is, used inside type dependent rules.</summary>
    Skip
}

/// <summary>
///     How one column of a table is rewritten.
/// </summary>
/// <param name="Kind">What a single value holds.</param>
/// <param name="ListFormat">For text columns holding several ids, how they are joined; null for single values.</param>
/// <param name="TypeColumn">For ids whose meaning depends on another column, that column's name.</param>
/// <param name="TypeMap">For type dependent ids, what each value of the type column means.</param>
/// <param name="JsonPaths">
///     For JSON columns, the rule for the value found at each path. A path is a dot separated list of property
///     names, where <c>$</c> is the root and <c>[*]</c> after a name means every item of that array.
/// </param>
public sealed record ColumnRule(
    IdKind Kind,
    string? ListFormat = null,
    string? TypeColumn = null,
    IReadOnlyDictionary<string, IdKind>? TypeMap = null,
    IReadOnlyDictionary<string, ColumnRule>? JsonPaths = null);

/// <summary>
///     How a table's rows are moved over.
/// </summary>
public sealed class TableRule
{
    /// <summary>Gets or sets the table name.</summary>
    public required string Table { get; init; }

    /// <summary>Gets or sets whether the table is copied at all.</summary>
    public bool Copy { get; init; }

    /// <summary>Gets or sets how rows belong to a server: guild, parent or channel.</summary>
    public string Scope { get; init; } = "guild";

    /// <summary>Gets or sets the column holding the server id, for server scoped tables.</summary>
    public string? GuildColumn { get; init; }

    /// <summary>Gets or sets the generated primary key column, if any.</summary>
    public string? Key { get; init; }

    /// <summary>Gets or sets the foreign key columns and the tables they point at.</summary>
    public IReadOnlyDictionary<string, string> Parents { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets or sets the column naming the member who owns the row, if any.</summary>
    public string? Member { get; init; }

    /// <summary>Gets or sets how each id holding column is rewritten.</summary>
    public IReadOnlyDictionary<string, ColumnRule> Columns { get; init; } = new Dictionary<string, ColumnRule>();

    /// <summary>Gets or sets the columns that must find a match on Fluxer for the row to be kept.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>Gets or sets what happens when a member's row clashes with one already on Fluxer: skip, replace or sum.</summary>
    public string OnMemberConflict { get; init; } = "skip";

    /// <summary>Gets or sets the numeric columns added together when rows clash and the rule is sum.</summary>
    public IReadOnlyList<string> SumColumns { get; init; } = [];
}

/// <summary>
///     The rules for every table, read from data/migration/tables.json.
/// </summary>
public sealed class MigrationSpec
{
    private MigrationSpec(IReadOnlyDictionary<string, TableRule> tables)
    {
        Tables = tables;
    }

    /// <summary>Gets the rules keyed by table name.</summary>
    public IReadOnlyDictionary<string, TableRule> Tables { get; }

    /// <summary>
    ///     Reads the rules file.
    /// </summary>
    /// <param name="path">Path to tables.json.</param>
    /// <returns>The parsed rules.</returns>
    public static MigrationSpec Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var tables = new Dictionary<string, TableRule>(StringComparer.Ordinal);
        foreach (var (name, node) in root)
        {
            if (node is not JsonObject entry)
                continue;
            tables[name] = ParseTable(name, entry);
        }

        return new MigrationSpec(tables);
    }

    private static TableRule ParseTable(string name, JsonObject entry)
    {
        var copy = string.Equals((string?)entry["action"], "copy", StringComparison.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry["parents"] is JsonObject parentNode)
        {
            foreach (var (column, parent) in parentNode)
            {
                if (parent is not null)
                    parents[column] = (string)parent!;
            }
        }

        var columns = new Dictionary<string, ColumnRule>(StringComparer.Ordinal);
        if (entry["columns"] is JsonObject columnNode)
        {
            foreach (var (column, rule) in columnNode)
            {
                var parsed = ParseColumn(rule);
                if (parsed is not null)
                    columns[column] = parsed;
            }
        }

        var required = entry["required"] is JsonArray requiredNode
            ? requiredNode.Select(x => (string)x!).ToList()
            : [];

        var conflict = "skip";
        var sums = new List<string>();
        switch (entry["onMemberConflict"])
        {
            case JsonObject conflictNode when conflictNode["sum"] is JsonArray sumNode:
                conflict = "sum";
                sums.AddRange(sumNode.Select(x => (string)x!));
                break;
            case JsonValue conflictValue:
                conflict = (string)conflictValue!;
                break;
        }

        return new TableRule
        {
            Table = name,
            Copy = copy,
            Scope = (string?)entry["scope"] ?? "guild",
            GuildColumn = (string?)entry["guildColumn"],
            Key = (string?)entry["key"],
            Parents = parents,
            Member = (string?)entry["member"],
            Columns = columns,
            Required = required,
            OnMemberConflict = conflict,
            SumColumns = sums
        };
    }

    private static ColumnRule? ParseColumn(JsonNode? rule)
    {
        switch (rule)
        {
            case JsonValue value:
                return new ColumnRule(ParseKind((string?)value));
            case JsonObject list when list["list"] is not null:
                return new ColumnRule(ParseKind((string?)list["list"]), (string?)list["format"] ?? "comma");
            case JsonObject byType when byType["by"] is not null:
                var map = new Dictionary<string, IdKind>(StringComparer.OrdinalIgnoreCase);
                if (byType["map"] is JsonObject mapNode)
                {
                    foreach (var (key, kind) in mapNode)
                        map[key] = ParseKind((string?)kind);
                }

                return new ColumnRule(IdKind.None, TypeColumn: (string)byType["by"]!, TypeMap: map);
            case JsonObject json when json["json"] is not null:
                var paths = new Dictionary<string, ColumnRule>(StringComparer.Ordinal);
                switch (json["json"])
                {
                    case JsonObject pathNode:
                        foreach (var (path, inner) in pathNode)
                        {
                            if (ParseColumn(inner) is { } parsed)
                                paths[path] = parsed;
                        }

                        break;
                    case JsonValue whole:
                        paths["*"] = new ColumnRule(ParseKind((string?)whole));
                        break;
                }

                return new ColumnRule(IdKind.Text, JsonPaths: paths);
            default:
                return null;
        }
    }

    private static IdKind ParseKind(string? kind)
    {
        return kind?.ToLowerInvariant() switch
        {
            "channel" => IdKind.Channel,
            "role" => IdKind.Role,
            "user" => IdKind.User,
            "message" => IdKind.Message,
            "emoji" => IdKind.Emoji,
            "guild" => IdKind.Guild,
            "text" => IdKind.Text,
            "clear" => IdKind.Clear,
            "skip" => IdKind.Skip,
            _ => IdKind.None
        };
    }
}
