using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Modules.Migration.Common;
using Npgsql;
using NpgsqlTypes;

namespace Mewdeko.Modules.Migration.Services;

/// <summary>
///     Why a migration could not be prepared.
/// </summary>
public enum MigrationProblem
{
    /// <summary>Nothing went wrong.</summary>
    None,

    /// <summary>The code is not one Mewdeko hands out.</summary>
    BadCode,

    /// <summary>No export exists under the code, or it expired.</summary>
    NotFound,

    /// <summary>The export was made by a newer version of the Discord bot.</summary>
    UnsupportedVersion
}

/// <summary>
///     A migration that has been read and matched, waiting for confirmation.
/// </summary>
public sealed class MigrationPlan
{
    /// <summary>Gets the code the export was stored under.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the exported server.</summary>
    public required MigrationMeta Meta { get; init; }

    /// <summary>Gets the Fluxer server receiving the data.</summary>
    public required ulong GuildId { get; init; }

    /// <summary>Gets the channel, role and emoji pairs.</summary>
    public required MigrationMatch Match { get; init; }

    /// <summary>Gets the exported tables that will be written, in write order.</summary>
    public required List<(TableRule Rule, MigrationTable Data)> Tables { get; init; }

    /// <summary>Gets the Discord ids of every member who owns rows, and whether each is linked already.</summary>
    public required Dictionary<ulong, bool> Members { get; init; }

    /// <summary>Gets how many rows of server settings will be written.</summary>
    public int SettingRows => Tables.Where(x => x.Rule.Member is null).Sum(x => x.Data.Rows.Count);

    /// <summary>Gets how many rows of member data were exported.</summary>
    public int MemberRows => Tables.Where(x => x.Rule.Member is not null).Sum(x => x.Data.Rows.Count);
}

/// <summary>
///     What a migration did.
/// </summary>
/// <param name="Written">Rows written now.</param>
/// <param name="Held">Rows kept until their member links an account.</param>
/// <param name="Dropped">Rows left out because something they need found no Fluxer match.</param>
/// <param name="Failed">Rows the database refused.</param>
/// <param name="WaitingMembers">Members whose data is waiting for them to link.</param>
/// <param name="Reposted">Panel and menu messages posted again in their channels.</param>
public sealed record MigrationOutcome(int Written, int Held, int Dropped, int Failed, int WaitingMembers,
    int Reposted = 0);

/// <summary>
///     What linking an account did.
/// </summary>
/// <param name="Linked">Whether the link was made.</param>
/// <param name="AlreadyLinkedElsewhere">Whether the Discord account belongs to another Fluxer account already.</param>
/// <param name="Rows">Rows moved over for the member.</param>
/// <param name="Servers">How many servers those rows were in.</param>
public sealed record LinkOutcome(bool Linked, bool AlreadyLinkedElsewhere, int Rows, int Servers);

/// <summary>
///     Brings a server's Mewdeko data over from Discord: reads the export the Discord bot left in Redis, pairs its
///     channels and roles with the Fluxer server, rewrites every id, and writes it in one transaction. Rows that
///     belong to members who have not linked their accounts are held and written when they link.
/// </summary>
public sealed class DiscordMigrationService(
    FluxerClient client,
    IDataConnectionFactory dbFactory,
    IDataCache cache,
    MigrationRefresher refresher,
    ILogger<DiscordMigrationService> logger) : INService
{
    private const int SupportedVersion = 1;
    private const string PendingMarker = "$pending";
    private static readonly Lazy<MigrationSpec> Spec = new(() => MigrationSpec.Load("data/migration/tables.json"));
    private readonly SemaphoreSlim migrationLock = new(1, 1);

    /// <summary>
    ///     Reads an export and matches it against a Fluxer server.
    /// </summary>
    /// <param name="guild">The Fluxer server.</param>
    /// <param name="rawCode">The code as typed.</param>
    /// <returns>The plan, or why there is none.</returns>
    public async Task<(MigrationPlan? Plan, MigrationProblem Problem)> PrepareAsync(SocketGuild guild, string rawCode)
    {
        if (MigrationBundle.NormalizeCode(rawCode) is not { } code)
            return (null, MigrationProblem.BadCode);

        var redis = cache.Redis.GetDatabase();
        var meta = await MigrationBundle.ReadMetaAsync(redis, code);
        if (meta is null)
            return (null, MigrationProblem.NotFound);

        if (meta.Version > SupportedVersion)
            return (null, MigrationProblem.UnsupportedVersion);

        await using var connection = await OpenAsync();
        var schema = await MigrationSchema.LoadAsync(connection);
        var match = MigrationMatcher.Match(meta, guild);

        var tables = new List<(TableRule Rule, MigrationTable Data)>();
        foreach (var name in OrderTables(meta.Tables.Where(x =>
                     Spec.Value.Tables.TryGetValue(x, out var rule) && rule.Copy && schema.ContainsKey(x))))
        {
            var data = await MigrationBundle.ReadTableAsync(redis, code, name);
            if (data is not null && data.Rows.Count > 0)
                tables.Add((Spec.Value.Tables[name], data));
        }

        var links = await LoadLinksAsync();
        var members = new Dictionary<ulong, bool>();
        foreach (var (rule, data) in tables.Where(x => x.Rule.Member is not null))
        {
            var index = data.Columns.IndexOf(rule.Member!);
            if (index < 0)
                continue;

            foreach (var row in data.Rows)
            {
                if (row[index] is { } node && ulong.TryParse(MigrationMapper.NodeText(node), out var id) && id != 0)
                    members[id] = links.ContainsKey(id);
            }
        }

        return (new MigrationPlan
        {
            Code = code,
            Meta = meta,
            GuildId = guild.Id,
            Match = match,
            Tables = tables,
            Members = members
        }, MigrationProblem.None);
    }

    /// <summary>
    ///     Writes a prepared migration, replacing the server's current Mewdeko settings.
    /// </summary>
    /// <param name="plan">The plan from <see cref="PrepareAsync" />.</param>
    /// <param name="invokerId">The Fluxer user who confirmed it, linked to the Discord user who exported.</param>
    /// <returns>What was written.</returns>
    public async Task<MigrationOutcome> ApplyAsync(MigrationPlan plan, ulong invokerId)
    {
        await migrationLock.WaitAsync();
        try
        {
            var outcome = await ApplyLockedAsync(plan, invokerId);
            await MigrationBundle.DeleteAsync(cache.Redis.GetDatabase(), plan.Code, plan.Meta);
            await refresher.RefreshGuildAsync(plan.GuildId);
            var reposted = await refresher.RepostPanelsAsync(plan.GuildId);
            return outcome with { Reposted = reposted };
        }
        finally
        {
            migrationLock.Release();
        }
    }

    /// <summary>
    ///     Links a Fluxer account to the Discord account behind a link code, and writes the member's held rows.
    /// </summary>
    /// <param name="fluxerUserId">The Fluxer user.</param>
    /// <param name="rawCode">The code as typed.</param>
    /// <returns>What happened, or null when the code is unknown or expired.</returns>
    public async Task<LinkOutcome?> LinkAsync(ulong fluxerUserId, string rawCode)
    {
        if (MigrationBundle.NormalizeCode(rawCode) is not { } code)
            return null;

        var value = await cache.Redis.GetDatabase().StringGetDeleteAsync($"mewdeko_link:{code}");
        if (value.IsNullOrEmpty || !ulong.TryParse(value.ToString(), out var discordUserId))
            return null;

        await migrationLock.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (!await SaveLinkAsync(connection, fluxerUserId, discordUserId))
            {
                await transaction.RollbackAsync();
                return new LinkOutcome(false, true, 0, 0);
            }

            var (rows, servers) = await ApplyHeldRowsAsync(connection, transaction, discordUserId);
            await transaction.CommitAsync();
            foreach (var guildId in servers)
                await refresher.RefreshGuildAsync(guildId);

            return new LinkOutcome(true, false, rows, servers.Count);
        }
        finally
        {
            migrationLock.Release();
        }
    }

    private async Task<MigrationOutcome> ApplyLockedAsync(MigrationPlan plan, ulong invokerId)
    {
        await using var connection = await OpenAsync();
        var schema = await MigrationSchema.LoadAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        await SaveLinkAsync(connection, invokerId, plan.Meta.RequestedById);
        var links = await LoadLinksAsync(connection);
        var mapper = new MigrationMapper(plan.Meta, plan.GuildId, plan.Match.Channels, plan.Match.Roles,
            plan.Match.Emojis, links);

        var guild = client.GetGuild(plan.GuildId);
        var fluxerChannels = guild?.Channels.Keys.Select(x => (decimal)x).ToArray() ?? [];
        await DeleteExistingAsync(connection, plan, schema, fluxerChannels);

        var migrationId = await InsertMigrationAsync(connection, plan, invokerId);
        var keyMaps = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.Ordinal);
        var heldKeys = new Dictionary<string, Dictionary<string, ulong>>(StringComparer.Ordinal);
        int written = 0, held = 0, dropped = 0, failed = 0;
        var waiting = new HashSet<ulong>();

        for (var position = 0; position < plan.Tables.Count; position++)
        {
            var (rule, data) = plan.Tables[position];
            var before = (written, held, dropped, failed);
            if (!schema.TryGetValue(rule.Table, out var target))
                continue;

            var keyMap = keyMaps[rule.Table] = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var heldMap = heldKeys[rule.Table] = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var selfReferences = new List<(JsonNode NewKey, string Column, JsonNode OldValue)>();

            foreach (var source in data.Rows)
            {
                var row = ToRow(data, source);
                var oldKey = rule.Key is not null && row.TryGetValue(rule.Key, out var keyNode) && keyNode is not null
                    ? MigrationMapper.NodeText(keyNode)
                    : null;

                ulong? holder = null;
                var pending = new JsonObject();
                var ownReferences = new List<(string Column, JsonNode OldValue)>();
                var lost = false;
                foreach (var (column, parent) in rule.Parents)
                {
                    if (!row.TryGetValue(column, out var reference) || reference is null)
                        continue;

                    var referenceText = MigrationMapper.NodeText(reference);
                    if (parent == rule.Table)
                    {
                        ownReferences.Add((column, reference));
                        row[column] = null;
                        continue;
                    }

                    if (keyMaps.TryGetValue(parent, out var parentKeys) &&
                        parentKeys.TryGetValue(referenceText, out var newReference))
                    {
                        row[column] = newReference?.DeepClone();
                        continue;
                    }

                    if (heldKeys.TryGetValue(parent, out var parentHeld) &&
                        parentHeld.TryGetValue(referenceText, out var owner))
                    {
                        holder = owner;
                        pending[column] = parent;
                        continue;
                    }

                    if (!keyMaps.ContainsKey(parent) && !heldKeys.ContainsKey(parent))
                    {
                        row[column] = null;
                        continue;
                    }

                    lost = true;
                }

                var member = ReadMember(rule, row);
                if (lost || !MapRow(rule, row, mapper))
                {
                    dropped++;
                    continue;
                }

                if (holder is null && member is { } memberId && !links.ContainsKey(memberId))
                    holder = memberId;

                if (holder is { } heldFor)
                {
                    if (pending.Count > 0)
                        row[PendingMarker] = pending;

                    await HoldRowAsync(connection, migrationId, heldFor, rule.Table, oldKey, row, position);
                    if (oldKey is not null)
                        heldMap[oldKey] = heldFor;

                    waiting.Add(heldFor);
                    held++;
                    continue;
                }

                var (inserted, newKey) = await InsertRowAsync(connection, transaction, target, rule, row,
                    rule.Member is not null);
                if (!inserted)
                {
                    failed++;
                    continue;
                }

                written++;
                if (oldKey is not null)
                    keyMap[oldKey] = newKey;

                if (newKey is not null)
                    selfReferences.AddRange(ownReferences.Select(x => (newKey, x.Column, x.OldValue)));
            }

            await FixSelfReferencesAsync(connection, target, rule, keyMap, selfReferences);
            logger.LogDebug("Migrated {Table}: {Written} written, {Held} held, {Dropped} dropped, {Failed} failed",
                rule.Table, written - before.written, held - before.held, dropped - before.dropped,
                failed - before.failed);
        }

        await transaction.CommitAsync();
        logger.LogInformation(
            "Migrated Discord guild {DiscordGuildId} into {GuildId}: {Written} written, {Held} held, {Dropped} dropped, {Failed} failed",
            plan.Meta.GuildId, plan.GuildId, written, held, dropped, failed);
        return new MigrationOutcome(written, held, dropped, failed, waiting.Count);
    }

    private async Task<(int Rows, HashSet<ulong> Servers)> ApplyHeldRowsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ulong discordUserId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var heldRows = await db.MigrationPendingRows
            .Where(x => x.DiscordUserId == discordUserId)
            .OrderBy(x => x.MigrationId).ThenBy(x => x.Position).ThenBy(x => x.Id)
            .ToListAsync();
        if (heldRows.Count == 0)
            return (0, []);

        var migrations = await db.MigratedGuilds
            .Where(x => heldRows.Select(r => r.MigrationId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);
        var schema = await MigrationSchema.LoadAsync(connection);
        var links = await LoadLinksAsync(connection);
        var keyMaps = new Dictionary<(int Migration, string Table), Dictionary<string, JsonNode?>>();
        var servers = new HashSet<ulong>();
        var rows = 0;

        foreach (var heldRow in heldRows)
        {
            if (!migrations.TryGetValue(heldRow.MigrationId, out var migration) ||
                !Spec.Value.Tables.TryGetValue(heldRow.TableName, out var rule) ||
                !schema.TryGetValue(heldRow.TableName, out var target) ||
                JsonNode.Parse(heldRow.Row) is not JsonObject json)
                continue;

            var row = json.ToDictionary(x => x.Key, x => x.Value?.DeepClone(), StringComparer.Ordinal);
            if (row.Remove(PendingMarker, out var pendingNode) && pendingNode is JsonObject pending)
            {
                var lost = false;
                foreach (var (column, parentNode) in pending)
                {
                    var parent = (string)parentNode!;
                    if (row.GetValueOrDefault(column) is { } reference &&
                        keyMaps.TryGetValue((heldRow.MigrationId, parent), out var parentKeys) &&
                        parentKeys.TryGetValue(MigrationMapper.NodeText(reference), out var newReference))
                        row[column] = newReference?.DeepClone();
                    else
                        lost = true;
                }

                if (lost)
                    continue;
            }

            var userColumns = rule.Columns.Where(x => x.Value.Kind == IdKind.User).Select(x => x.Key);
            if (rule.Member is not null)
                userColumns = userColumns.Append(rule.Member).Distinct();

            foreach (var column in userColumns)
            {
                if (row.GetValueOrDefault(column) is { } node &&
                    ulong.TryParse(MigrationMapper.NodeText(node), out var id) && links.TryGetValue(id, out var linked))
                    row[column] = WriteLike(node, linked);
            }

            var (inserted, newKey) = await InsertRowAsync(connection, transaction, target, rule, row, true);
            if (!inserted)
                continue;

            rows++;
            servers.Add(migration.GuildId);
            if (heldRow.OldKey is null)
                continue;

            if (!keyMaps.TryGetValue((heldRow.MigrationId, heldRow.TableName), out var map))
                keyMaps[(heldRow.MigrationId, heldRow.TableName)] = map = new Dictionary<string, JsonNode?>();

            map[heldRow.OldKey] = newKey;
        }

        await using (var delete = new NpgsqlCommand(
                         """delete from "MigrationPendingRows" where "DiscordUserId" = @user""", connection))
        {
            delete.Parameters.AddWithValue("user", (decimal)discordUserId);
            await delete.ExecuteNonQueryAsync();
        }

        return (rows, servers);
    }

    private static bool MapRow(TableRule rule, Dictionary<string, JsonNode?> row, MigrationMapper mapper)
    {
        if (rule.GuildColumn is not null && row.ContainsKey(rule.GuildColumn))
        {
            var guild = mapper.MapValue(new ColumnRule(IdKind.Guild), row[rule.GuildColumn], _ => null, out var foreign);
            if (foreign)
                return false;

            row[rule.GuildColumn] = guild;
        }

        var columns = rule.Member is not null && !rule.Columns.ContainsKey(rule.Member)
            ? rule.Columns.Append(KeyValuePair.Create(rule.Member, new ColumnRule(IdKind.User)))
            : rule.Columns;
        foreach (var (column, columnRule) in columns)
        {
            if (!row.TryGetValue(column, out var value))
                continue;

            var mapped = mapper.MapValue(columnRule, value, other => row.GetValueOrDefault(other), out var missing);
            if (missing && rule.Required.Contains(column))
                return false;

            row[column] = mapped;
        }

        return rule.Required.All(x => !row.ContainsKey(x) || row[x] is not null);
    }

    private static ulong? ReadMember(TableRule rule, Dictionary<string, JsonNode?> row)
    {
        if (rule.Member is null || row.GetValueOrDefault(rule.Member) is not { } node)
            return null;

        return ulong.TryParse(MigrationMapper.NodeText(node), out var id) && id != 0 ? id : null;
    }

    private static Dictionary<string, JsonNode?> ToRow(MigrationTable data, JsonArray source)
    {
        var row = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        for (var i = 0; i < data.Columns.Count && i < source.Count; i++)
            row[data.Columns[i]] = source[i]?.DeepClone();

        return row;
    }

    private static async Task<(bool Inserted, JsonNode? NewKey)> InsertRowAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, TargetTable target, TableRule rule, Dictionary<string, JsonNode?> row,
        bool memberConflicts)
    {
        var columns = new List<TargetColumn>();
        var values = new List<JsonNode?>();
        foreach (var (name, value) in row)
        {
            if (!target.Columns.TryGetValue(name, out var column))
                continue;

            if (column.Sequence)
                continue;

            if (value is null && column.HasDefault)
                continue;

            columns.Add(column);
            values.Add(value);
        }

        if (columns.Count == 0)
            return (false, null);

        var returning = rule.Key is not null && target.Columns.ContainsKey(rule.Key) ? Quote(rule.Key) : "1";
        var sql = new StringBuilder()
            .Append("insert into ").Append(Quote(rule.Table)).Append(" (")
            .AppendJoin(", ", columns.Select(x => Quote(x.Name)))
            .Append(") values (")
            .AppendJoin(", ", columns.Select((_, i) => $"@p{i}"))
            .Append(") on conflict do nothing returning ").Append(returning)
            .ToString();

        await using var savepoint = new SavepointScope(transaction);
        await savepoint.BeginAsync();
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            for (var i = 0; i < columns.Count; i++)
                command.Parameters.Add(MigrationSchema.ToParameter($"p{i}", columns[i], values[i]));

            var result = await command.ExecuteScalarAsync();
            var outcome = result is not null
                ? (true, JsonValue.Create(System.Convert.ToString(result, CultureInfo.InvariantCulture)))
                : memberConflicts && rule.Member is not null
                    ? await ResolveMemberConflictAsync(connection, transaction, target, rule, columns, values)
                    : (false, (JsonNode?)null);

            await savepoint.ReleaseAsync();
            return outcome;
        }
        catch (PostgresException ex)
        {
            await savepoint.RollbackAsync();
            LogRefused(rule.Table, ex);
            return (false, null);
        }
    }

    private static async Task<(bool Inserted, JsonNode? NewKey)> ResolveMemberConflictAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, TargetTable target, TableRule rule,
        List<TargetColumn> columns, List<JsonNode?> values)
    {
        var key = target.UniqueKeys.FirstOrDefault(x => x.Contains(rule.Member!) &&
                                                       x.All(c => columns.Any(col => col.Name == c)))
                  ?? target.UniqueKeys.FirstOrDefault(x => x.All(c => columns.Any(col => col.Name == c)));
        if (key is null || rule.OnMemberConflict == "skip")
            return (false, null);

        var where = string.Join(" and ", key.Select((c, i) => $"{Quote(c)} = @k{i}"));
        var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        await using (command)
        {
            for (var i = 0; i < key.Length; i++)
            {
                var index = columns.FindIndex(x => x.Name == key[i]);
                command.Parameters.Add(MigrationSchema.ToParameter($"k{i}", columns[index], values[index]));
            }

            switch (rule.OnMemberConflict)
            {
                case "sum":
                    var sums = rule.SumColumns.Where(x => columns.Any(c => c.Name == x)).ToList();
                    if (sums.Count == 0)
                        return (false, null);

                    for (var i = 0; i < sums.Count; i++)
                    {
                        var index = columns.FindIndex(x => x.Name == sums[i]);
                        command.Parameters.Add(MigrationSchema.ToParameter($"s{i}", columns[index], values[index]));
                    }

                    command.CommandText =
                        $"update {Quote(rule.Table)} set {string.Join(", ", sums.Select((c, i) => $"{Quote(c)} = {Quote(c)} + @s{i}"))} where {where}";
                    return (await command.ExecuteNonQueryAsync() > 0, null);
                case "replace":
                    command.CommandText = $"delete from {Quote(rule.Table)} where {where}";
                    await command.ExecuteNonQueryAsync();
                    break;
                default:
                    return (false, null);
            }
        }

        var row = columns.Select((c, i) => (c.Name, values[i])).ToDictionary(x => x.Name, x => x.Item2);
        return await InsertRowAsync(connection, transaction, target, rule, row, false);
    }

    private static async Task DeleteExistingAsync(NpgsqlConnection connection, MigrationPlan plan,
        IReadOnlyDictionary<string, TargetTable> schema, decimal[] fluxerChannels)
    {
        var copied = Spec.Value.Tables.Values.Where(x => x.Copy).ToDictionary(x => x.Table);
        var exported = plan.Tables.Select(x => x.Rule.Table).ToHashSet(StringComparer.Ordinal);
        var replaced = new Dictionary<string, string>(StringComparer.Ordinal);

        string? Selector(TableRule rule, string column)
        {
            if (replaced.TryGetValue(rule.Table, out var existing))
                return $"select {Quote(column)} from {Quote(rule.Table)} where {existing}";

            return null;
        }

        foreach (var name in OrderTables(copied.Keys.Where(schema.ContainsKey)))
        {
            var rule = copied[name];
            string? condition = null;
            switch (rule.Scope)
            {
                case "guild" when rule.Member is null && rule.GuildColumn is not null && exported.Contains(name):
                    condition = $"{Quote(rule.GuildColumn)} = @guild";
                    break;
                case "channel" when rule.Member is null && exported.Contains(name):
                    var channelColumn = rule.Columns.FirstOrDefault(x => x.Value.Kind == IdKind.Channel).Key;
                    if (channelColumn is not null)
                        condition = $"{Quote(channelColumn)} = any(@channels)";
                    break;
                case "parent":
                    var parts = rule.Parents
                        .Where(x => x.Value != name && copied.TryGetValue(x.Value, out var parent) && parent.Key is not null)
                        .Select(x => (Column: x.Key, Select: Selector(copied[x.Value], copied[x.Value].Key!)))
                        .Where(x => x.Select is not null)
                        .Select(x => $"{Quote(x.Column)} in ({x.Select})")
                        .ToList();
                    if (parts.Count > 0)
                        condition = string.Join(" or ", parts);
                    break;
            }

            if (condition is not null)
                replaced[name] = condition;
        }

        foreach (var name in OrderTables(replaced.Keys).Reverse())
        {
            await using var command = new NpgsqlCommand($"delete from {Quote(name)} where {replaced[name]}", connection);
            command.Parameters.AddWithValue("guild", (decimal)plan.GuildId);
            command.Parameters.Add(new NpgsqlParameter("channels", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = fluxerChannels
            });
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task FixSelfReferencesAsync(NpgsqlConnection connection, TargetTable table, TableRule rule,
        Dictionary<string, JsonNode?> keyMap, List<(JsonNode NewKey, string Column, JsonNode OldValue)> references)
    {
        if (rule.Key is null || !table.Columns.TryGetValue(rule.Key, out var keyColumn))
            return;

        foreach (var (newKey, column, oldValue) in references)
        {
            if (!keyMap.TryGetValue(MigrationMapper.NodeText(oldValue), out var referenced) || referenced is null ||
                !table.Columns.TryGetValue(column, out var referenceColumn))
                continue;

            await using var command = new NpgsqlCommand(
                $"update {Quote(rule.Table)} set {Quote(column)} = @target where {Quote(rule.Key)} = @key", connection);
            command.Parameters.Add(MigrationSchema.ToParameter("target", referenceColumn, referenced));
            command.Parameters.Add(MigrationSchema.ToParameter("key", keyColumn, newKey));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> InsertMigrationAsync(NpgsqlConnection connection, MigrationPlan plan,
        ulong invokerId)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into "MigratedGuilds" ("GuildId", "DiscordGuildId", "MigratedBy", "ChannelMap", "RoleMap")
            values (@guild, @discord, @by, @channels, @roles) returning "Id"
            """, connection);
        command.Parameters.AddWithValue("guild", (decimal)plan.GuildId);
        command.Parameters.AddWithValue("discord", (decimal)plan.Meta.GuildId);
        command.Parameters.AddWithValue("by", (decimal)invokerId);
        command.Parameters.Add(new NpgsqlParameter("channels", NpgsqlDbType.Jsonb)
        {
            Value = ToJsonMap(plan.Match.Channels)
        });
        command.Parameters.Add(new NpgsqlParameter("roles", NpgsqlDbType.Jsonb) { Value = ToJsonMap(plan.Match.Roles) });
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task HoldRowAsync(NpgsqlConnection connection, int migrationId, ulong discordUserId,
        string table, string? oldKey, Dictionary<string, JsonNode?> row, int position)
    {
        var json = new JsonObject(row.Select(x => KeyValuePair.Create(x.Key, x.Value?.DeepClone())));
        await using var command = new NpgsqlCommand(
            """
            insert into "MigrationPendingRows" ("MigrationId", "DiscordUserId", "TableName", "OldKey", "Row", "Position")
            values (@migration, @user, @table, @key, @row, @position)
            """, connection);
        command.Parameters.AddWithValue("migration", migrationId);
        command.Parameters.AddWithValue("user", (decimal)discordUserId);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("key", (object?)oldKey ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("row", NpgsqlDbType.Jsonb) { Value = json.ToJsonString() });
        command.Parameters.AddWithValue("position", position);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> SaveLinkAsync(NpgsqlConnection connection, ulong fluxerUserId,
        ulong discordUserId)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into "DiscordAccountLinks" ("FluxerUserId", "DiscordUserId") values (@fluxer, @discord)
            on conflict ("FluxerUserId") do update set "DiscordUserId" = excluded."DiscordUserId", "DateAdded" = now() at time zone 'utc'
            where not exists (select 1 from "DiscordAccountLinks" where "DiscordUserId" = excluded."DiscordUserId" and "FluxerUserId" <> excluded."FluxerUserId")
            """, connection);
        command.Parameters.AddWithValue("fluxer", (decimal)fluxerUserId);
        command.Parameters.AddWithValue("discord", (decimal)discordUserId);
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }

        await using var check = new NpgsqlCommand(
            """select count(*) from "DiscordAccountLinks" where "FluxerUserId" = @fluxer and "DiscordUserId" = @discord""",
            connection);
        check.Parameters.AddWithValue("fluxer", (decimal)fluxerUserId);
        check.Parameters.AddWithValue("discord", (decimal)discordUserId);
        return (long)(await check.ExecuteScalarAsync())! > 0;
    }

    private async Task<Dictionary<ulong, ulong>> LoadLinksAsync()
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.DiscordAccountLinks.ToDictionaryAsync(x => x.DiscordUserId, x => x.FluxerUserId);
    }

    private static async Task<Dictionary<ulong, ulong>> LoadLinksAsync(NpgsqlConnection connection)
    {
        var links = new Dictionary<ulong, ulong>();
        await using var command = new NpgsqlCommand(
            """select "DiscordUserId", "FluxerUserId" from "DiscordAccountLinks" """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            links[(ulong)reader.GetDecimal(0)] = (ulong)reader.GetDecimal(1);

        return links;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        string connectionString;
        await using (var db = await dbFactory.CreateConnectionAsync())
            connectionString = db.ConnectionString!;

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static IEnumerable<string> OrderTables(IEnumerable<string> names)
    {
        var set = names.ToHashSet(StringComparer.Ordinal);
        var ordered = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string name)
        {
            if (done.Contains(name) || !visiting.Add(name))
                return;

            if (Spec.Value.Tables.TryGetValue(name, out var rule))
            {
                foreach (var parent in rule.Parents.Values.Where(x => x != name && set.Contains(x))
                             .OrderBy(x => x, StringComparer.Ordinal))
                    Visit(parent);
            }

            visiting.Remove(name);
            done.Add(name);
            ordered.Add(name);
        }

        foreach (var name in set.OrderBy(x => x, StringComparer.Ordinal))
            Visit(name);

        return ordered;
    }

    private static string ToJsonMap(Dictionary<ulong, ulong> map)
    {
        return new JsonObject(map.Select(x =>
                KeyValuePair.Create(x.Key.ToString(CultureInfo.InvariantCulture),
                    (JsonNode?)JsonValue.Create(x.Value.ToString(CultureInfo.InvariantCulture)))))
            .ToJsonString();
    }

    private static JsonNode WriteLike(JsonNode original, ulong id)
    {
        return original is JsonValue value && value.TryGetValue<string>(out _)
            ? JsonValue.Create(id.ToString(CultureInfo.InvariantCulture))
            : JsonValue.Create(id);
    }

    private static string Quote(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static void LogRefused(string table, PostgresException ex)
    {
        Serilog.Log.Debug("Migration row refused by {Table}: {Message}", table, ex.MessageText);
    }

    private sealed class SavepointScope(NpgsqlTransaction? transaction) : IAsyncDisposable
    {
        private const string Name = "migration_row";
        private bool open;

        public async Task BeginAsync()
        {
            if (transaction is null)
                return;

            await transaction.SaveAsync(Name);
            open = true;
        }

        public async Task ReleaseAsync()
        {
            if (!open)
                return;

            await transaction!.ReleaseAsync(Name);
            open = false;
        }

        public async Task RollbackAsync()
        {
            if (!open)
                return;

            await transaction!.RollbackAsync(Name);
            open = false;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
