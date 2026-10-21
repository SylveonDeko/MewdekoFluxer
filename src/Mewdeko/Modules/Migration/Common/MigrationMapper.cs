using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mewdeko.Modules.Migration.Common;

/// <summary>
///     Turns Discord ids into the Fluxer ids they were matched to, in single values, lists, JSON and mentions in text.
/// </summary>
public sealed partial class MigrationMapper
{
    private readonly IReadOnlyDictionary<ulong, ulong> channels;
    private readonly IReadOnlyDictionary<ulong, ulong> roles;
    private readonly IReadOnlyDictionary<ulong, (ulong Id, string Name, bool Animated)> emojis;
    private readonly IReadOnlyDictionary<ulong, string> channelNames;
    private readonly IReadOnlyDictionary<ulong, string> roleNames;
    private readonly IReadOnlyDictionary<ulong, string> emojiNames;
    private readonly ulong discordGuildId;
    private readonly ulong fluxerGuildId;

    /// <summary>
    ///     Creates a mapper.
    /// </summary>
    /// <param name="meta">The exported server, for the names of things that found no match.</param>
    /// <param name="fluxerGuildId">The Fluxer server the data is going to.</param>
    /// <param name="channels">Discord channel ids to Fluxer channel ids.</param>
    /// <param name="roles">Discord role ids to Fluxer role ids.</param>
    /// <param name="emojis">Discord emoji ids to Fluxer emojis.</param>
    /// <param name="users">Discord user ids to Fluxer user ids, for members who linked their accounts.</param>
    public MigrationMapper(MigrationMeta meta, ulong fluxerGuildId, IReadOnlyDictionary<ulong, ulong> channels,
        IReadOnlyDictionary<ulong, ulong> roles, IReadOnlyDictionary<ulong, (ulong Id, string Name, bool Animated)> emojis,
        IReadOnlyDictionary<ulong, ulong> users)
    {
        this.channels = channels;
        this.roles = roles;
        this.emojis = emojis;
        Users = users;
        discordGuildId = meta.GuildId;
        this.fluxerGuildId = fluxerGuildId;
        channelNames = meta.Channels.ToDictionary(x => ulong.Parse(x.Id), x => x.Name);
        roleNames = meta.Roles.ToDictionary(x => ulong.Parse(x.Id), x => x.Name);
        emojiNames = meta.Emojis.ToDictionary(x => ulong.Parse(x.Id), x => x.Name);
    }

    /// <summary>Gets the Discord to Fluxer user ids known so far.</summary>
    public IReadOnlyDictionary<ulong, ulong> Users { get; private set; }

    /// <summary>
    ///     Gets a mapper that also knows the given user links.
    /// </summary>
    /// <param name="users">Discord user ids to Fluxer user ids.</param>
    /// <returns>This mapper, now using those links.</returns>
    public MigrationMapper WithUsers(IReadOnlyDictionary<ulong, ulong> users)
    {
        Users = users;
        return this;
    }

    /// <summary>
    ///     Maps one id.
    /// </summary>
    /// <param name="kind">What the id is.</param>
    /// <param name="id">The Discord id.</param>
    /// <param name="mapped">The Fluxer id, or null when the value should be emptied.</param>
    /// <returns>False when the id has no Fluxer match and the value cannot be kept.</returns>
    public bool TryMapId(IdKind kind, ulong id, out ulong? mapped)
    {
        mapped = id;
        if (id == 0)
            return true;

        switch (kind)
        {
            case IdKind.Channel when channels.TryGetValue(id, out var channel):
                mapped = channel;
                return true;
            case IdKind.Channel when id == discordGuildId:
                mapped = fluxerGuildId;
                return true;
            case IdKind.Role when id == discordGuildId:
                mapped = fluxerGuildId;
                return true;
            case IdKind.Role when roles.TryGetValue(id, out var role):
                mapped = role;
                return true;
            case IdKind.Emoji when emojis.TryGetValue(id, out var emoji):
                mapped = emoji.Id;
                return true;
            case IdKind.Guild:
                mapped = fluxerGuildId;
                return id == discordGuildId;
            case IdKind.User:
                mapped = Users.TryGetValue(id, out var user) ? user : id;
                return true;
            case IdKind.Message or IdKind.Clear:
                mapped = null;
                return true;
            case IdKind.Channel or IdKind.Role or IdKind.Emoji:
                mapped = null;
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    ///     Maps a column value by its rule.
    /// </summary>
    /// <param name="rule">How the column is rewritten.</param>
    /// <param name="value">The value as exported.</param>
    /// <param name="sibling">Reads another column of the same row, for ids whose meaning depends on it.</param>
    /// <param name="missing">Set when a single id had no Fluxer match.</param>
    /// <returns>The value to write.</returns>
    public JsonNode? MapValue(ColumnRule rule, JsonNode? value, Func<string, JsonNode?> sibling, out bool missing)
    {
        missing = false;
        if (value is null)
            return null;

        if (rule.JsonPaths is not null)
            return MapJsonColumn(rule, value);

        if (rule.ListFormat is not null)
            return MapList(rule.Kind, rule.ListFormat, value);

        var kind = rule.Kind;
        if (rule.TypeColumn is not null)
        {
            var type = sibling(rule.TypeColumn);
            kind = type is not null && rule.TypeMap!.TryGetValue(NodeText(type), out var byType) ? byType : IdKind.Skip;
        }

        return MapSingle(kind, value, out missing);
    }

    /// <summary>
    ///     Rewrites channel, role, user and emoji mentions in text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with Fluxer ids, or names where nothing matched.</returns>
    public string RewriteText(string text)
    {
        if (text.IndexOf('<') < 0)
            return text;

        return MentionRegex().Replace(text, match =>
        {
            var id = ulong.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            switch (match.Groups["kind"].Value)
            {
                case "#":
                    return TryMapId(IdKind.Channel, id, out var channel) && channel is not null
                        ? $"<#{channel}>"
                        : $"#{channelNames.GetValueOrDefault(id, "deleted-channel")}";
                case "@&":
                    return TryMapId(IdKind.Role, id, out var role) && role is not null
                        ? $"<@&{role}>"
                        : $"@{roleNames.GetValueOrDefault(id, "deleted-role")}";
                case "@" or "@!":
                    return Users.TryGetValue(id, out var user) ? $"<@{user}>" : match.Value;
                default:
                    var name = match.Groups["name"].Value;
                    return emojis.TryGetValue(id, out var emoji)
                        ? $"<{(emoji.Animated ? "a" : "")}:{emoji.Name}:{emoji.Id}>"
                        : $":{emojiNames.GetValueOrDefault(id, name)}:";
            }
        });
    }

    private JsonNode? MapSingle(IdKind kind, JsonNode value, out bool missing)
    {
        missing = false;
        switch (kind)
        {
            case IdKind.None or IdKind.Skip:
                return value.DeepClone();
            case IdKind.Text:
                return value is JsonValue text && text.TryGetValue<string>(out var s)
                    ? JsonValue.Create(RewriteText(s))
                    : value.DeepClone();
            case IdKind.Clear or IdKind.Message:
                return null;
        }

        if (!TryReadId(value, out var id))
            return kind == IdKind.Emoji ? JsonValue.Create(RewriteText(NodeText(value))) : value.DeepClone();

        if (!TryMapId(kind, id, out var mapped))
        {
            missing = true;
            return null;
        }

        return mapped is null ? null : WriteIdLike(value, mapped.Value);
    }

    private JsonNode? MapList(IdKind kind, string format, JsonNode value)
    {
        switch (format)
        {
            case "pgarray" when value is JsonArray array:
                return new JsonArray(array.Select(x => x is null ? null : MapListItem(kind, x))
                    .Where(x => x is not null).ToArray());
            case "json":
                var raw = NodeText(value);
                if (string.IsNullOrWhiteSpace(raw))
                    return value.DeepClone();

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(raw);
                }
                catch (JsonException)
                {
                    return value.DeepClone();
                }

                if (parsed is not JsonArray items)
                    return value.DeepClone();

                var mapped = new JsonArray(items.Select(x => x is null ? null : MapListItem(kind, x))
                    .Where(x => x is not null).ToArray());
                return JsonValue.Create(mapped.ToJsonString());
            default:
                var separator = format switch
                {
                    "comma" => ",",
                    "space" => " ",
                    "semicolon" => ";",
                    _ => format
                };
                var source = NodeText(value);
                if (string.IsNullOrWhiteSpace(source))
                    return value.DeepClone();

                var parts = source.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part =>
                    {
                        if (!ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                            return kind == IdKind.Emoji ? RewriteText(part) : part;

                        if (id == 0)
                            return part;

                        return TryMapId(kind, id, out var mappedId) && mappedId is not null
                            ? mappedId.Value.ToString(CultureInfo.InvariantCulture)
                            : null;
                    })
                    .Where(x => x is not null);
                return JsonValue.Create(string.Join(separator, parts));
        }
    }

    private JsonNode? MapListItem(IdKind kind, JsonNode item)
    {
        if (!TryReadId(item, out var id))
            return item.DeepClone();

        return TryMapId(kind, id, out var mapped) && mapped is not null ? WriteIdLike(item, mapped.Value) : null;
    }

    private JsonNode? MapJsonColumn(ColumnRule rule, JsonNode value)
    {
        var raw = NodeText(value);
        if (rule.JsonPaths!.TryGetValue("*", out var whole))
            return whole.Kind == IdKind.Text ? JsonValue.Create(RewriteText(raw)) : value.DeepClone();

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return value.DeepClone();
        }

        if (document is null)
            return value.DeepClone();

        foreach (var (path, inner) in rule.JsonPaths)
            ApplyPath(document, path.Split('.'), 0, inner);

        return JsonValue.Create(document.ToJsonString());
    }

    private void ApplyPath(JsonNode node, string[] segments, int index, ColumnRule rule)
    {
        if (index == segments.Length)
            return;

        var segment = segments[index];
        var each = segment.EndsWith("[*]", StringComparison.Ordinal);
        var name = each ? segment[..^3] : segment;
        var last = index == segments.Length - 1;

        JsonNode? target = name is "$" or "" ? node : (node as JsonObject)?[name];
        if (target is null)
            return;

        if (each)
        {
            if (target is not JsonArray array)
                return;

            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not { } item)
                    continue;

                if (last)
                    array[i] = MapValue(rule, item, column => null, out _);
                else
                    ApplyPath(item, segments, index + 1, rule);
            }

            return;
        }

        if (!last)
        {
            ApplyPath(target, segments, index + 1, rule);
            return;
        }

        if (node is not JsonObject parent)
            return;

        parent[name] = MapValue(rule, target, column => parent[column], out _);
    }

    private static bool TryReadId(JsonNode node, out ulong id)
    {
        id = 0;
        if (node is not JsonValue value)
            return false;

        if (value.TryGetValue<string>(out var text))
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);

        if (value.TryGetValue<ulong>(out id))
            return true;

        if (value.TryGetValue<long>(out var signed) && signed >= 0)
        {
            id = (ulong)signed;
            return true;
        }

        if (value.TryGetValue<decimal>(out var number) && number >= 0 && number == decimal.Truncate(number))
        {
            id = (ulong)number;
            return true;
        }

        return false;
    }

    private static JsonNode WriteIdLike(JsonNode original, ulong id)
    {
        return original is JsonValue value && value.TryGetValue<string>(out _)
            ? JsonValue.Create(id.ToString(CultureInfo.InvariantCulture))
            : JsonValue.Create(id);
    }

    /// <summary>
    ///     Reads a value as text, whatever JSON type it was exported as.
    /// </summary>
    /// <param name="node">The value.</param>
    /// <returns>Its text.</returns>
    public static string NodeText(JsonNode node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
    }

    [GeneratedRegex(@"<(?<kind>#|@&|@!|@)(?<id>\d{15,21})>|<a?:(?<name>\w{1,64}):(?<id>\d{15,21})>")]
    private static partial Regex MentionRegex();
}
