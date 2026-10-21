using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Mewdeko.Modules.Migration.Common;

/// <summary>
///     A column of a table in the Fluxer database.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="UdtName">The Postgres type name, for example numeric, int4 or _int8.</param>
/// <param name="Nullable">Whether the column accepts null.</param>
/// <param name="Sequence">Whether Postgres numbers the column itself, from an identity or a sequence.</param>
/// <param name="HasDefault">Whether Postgres fills the column with a default when it is left out.</param>
public sealed record TargetColumn(string Name, string UdtName, bool Nullable, bool Sequence, bool HasDefault);

/// <summary>
///     A table of the Fluxer database.
/// </summary>
/// <param name="Name">The table name.</param>
/// <param name="Columns">The columns by name.</param>
/// <param name="UniqueKeys">The column sets of every unique index, the primary key included.</param>
public sealed record TargetTable(string Name, IReadOnlyDictionary<string, TargetColumn> Columns,
    IReadOnlyList<string[]> UniqueKeys);

/// <summary>
///     Reads the Fluxer database's tables and turns exported values into typed parameters for them.
/// </summary>
public static class MigrationSchema
{
    /// <summary>
    ///     Reads every table, column and unique index of the public schema.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>The tables by name.</returns>
    public static async Task<Dictionary<string, TargetTable>> LoadAsync(NpgsqlConnection connection)
    {
        var columns = new Dictionary<string, Dictionary<string, TargetColumn>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
                         """
                         select c.table_name, c.column_name, c.udt_name, c.is_nullable = 'YES',
                                c.is_identity = 'YES' or coalesce(c.column_default, '') like 'nextval(%',
                                c.is_identity = 'YES' or c.column_default is not null
                         from information_schema.columns c
                         join information_schema.tables t
                           on t.table_schema = c.table_schema and t.table_name = c.table_name
                         where c.table_schema = 'public' and t.table_type = 'BASE TABLE'
                         """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var set))
                    columns[table] = set = new Dictionary<string, TargetColumn>(StringComparer.Ordinal);

                set[reader.GetString(1)] = new TargetColumn(reader.GetString(1), reader.GetString(2),
                    reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5));
            }
        }

        var keys = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
                         """
                         select t.relname, array_agg(a.attname order by k.ordinality)
                         from pg_index i
                         join pg_class t on t.oid = i.indrelid
                         join pg_namespace n on n.oid = t.relnamespace and n.nspname = 'public'
                         cross join lateral unnest(i.indkey) with ordinality as k(attnum, ordinality)
                         join pg_attribute a on a.attrelid = t.oid and a.attnum = k.attnum
                         where i.indisunique and i.indpred is null
                         group by i.indexrelid, t.relname
                         """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0);
                if (!keys.TryGetValue(table, out var list))
                    keys[table] = list = [];

                list.Add(reader.GetFieldValue<string[]>(1));
            }
        }

        return columns.ToDictionary(x => x.Key,
            x => new TargetTable(x.Key, x.Value, keys.GetValueOrDefault(x.Key) ?? []), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Builds a parameter holding an exported value in the column's type.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="column">The column the value goes into.</param>
    /// <param name="value">The value as exported.</param>
    /// <returns>The parameter.</returns>
    public static NpgsqlParameter ToParameter(string name, TargetColumn column, JsonNode? value)
    {
        if (column.UdtName.StartsWith('_'))
        {
            var elementType = column.UdtName[1..];
            var (dbType, clrType) = Describe(elementType);
            var items = value is JsonArray array ? array.ToList() : [];
            var typed = Array.CreateInstance(clrType, items.Count);
            for (var i = 0; i < items.Count; i++)
                typed.SetValue(Convert(elementType, items[i]) ?? Default(clrType), i);

            return new NpgsqlParameter(name, NpgsqlDbType.Array | dbType)
            {
                Value = value is null ? DBNull.Value : typed
            };
        }

        var converted = Convert(column.UdtName, value);
        if (converted is null && !column.Nullable)
            converted = Default(Describe(column.UdtName).ClrType);

        return new NpgsqlParameter(name, Describe(column.UdtName).DbType) { Value = converted ?? DBNull.Value };
    }

    /// <summary>
    ///     Converts an exported value to the CLR value Npgsql writes for a type.
    /// </summary>
    /// <param name="udtName">The Postgres type name.</param>
    /// <param name="value">The value as exported.</param>
    /// <returns>The value, or null.</returns>
    public static object? Convert(string udtName, JsonNode? value)
    {
        if (value is null)
            return null;

        var text = MigrationMapper.NodeText(value);
        var invariant = CultureInfo.InvariantCulture;
        return udtName switch
        {
            "numeric" => decimal.Parse(text, NumberStyles.Float, invariant),
            "int8" => (long)decimal.Parse(text, NumberStyles.Float, invariant),
            "int4" => (int)decimal.Parse(text, NumberStyles.Float, invariant),
            "int2" => (short)decimal.Parse(text, NumberStyles.Float, invariant),
            "float8" => double.Parse(text, NumberStyles.Float, invariant),
            "float4" => float.Parse(text, NumberStyles.Float, invariant),
            "bool" => value is JsonValue flag && flag.TryGetValue<bool>(out var b)
                ? b
                : text is "1" or "true" or "True",
            "timestamp" => DateTime.SpecifyKind(
                DateTime.Parse(text, invariant, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTimeKind.Unspecified),
            "timestamptz" => DateTime.Parse(text, invariant,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            "date" => DateTime.Parse(text, invariant, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                .Date,
            "interval" => TimeSpan.TryParseExact(text, "c", invariant, out var span)
                ? span
                : TimeSpan.Parse(text, invariant),
            "uuid" => Guid.Parse(text),
            "bytea" => System.Convert.FromBase64String(text),
            _ => text
        };
    }

    private static (NpgsqlDbType DbType, Type ClrType) Describe(string udtName)
    {
        return udtName switch
        {
            "numeric" => (NpgsqlDbType.Numeric, typeof(decimal)),
            "int8" => (NpgsqlDbType.Bigint, typeof(long)),
            "int4" => (NpgsqlDbType.Integer, typeof(int)),
            "int2" => (NpgsqlDbType.Smallint, typeof(short)),
            "float8" => (NpgsqlDbType.Double, typeof(double)),
            "float4" => (NpgsqlDbType.Real, typeof(float)),
            "bool" => (NpgsqlDbType.Boolean, typeof(bool)),
            "timestamp" => (NpgsqlDbType.Timestamp, typeof(DateTime)),
            "timestamptz" => (NpgsqlDbType.TimestampTz, typeof(DateTime)),
            "date" => (NpgsqlDbType.Date, typeof(DateTime)),
            "interval" => (NpgsqlDbType.Interval, typeof(TimeSpan)),
            "uuid" => (NpgsqlDbType.Uuid, typeof(Guid)),
            "bytea" => (NpgsqlDbType.Bytea, typeof(byte[])),
            "jsonb" => (NpgsqlDbType.Jsonb, typeof(string)),
            "json" => (NpgsqlDbType.Json, typeof(string)),
            "varchar" => (NpgsqlDbType.Varchar, typeof(string)),
            _ => (NpgsqlDbType.Text, typeof(string))
        };
    }

    private static object Default(Type type)
    {
        if (type == typeof(string))
            return "";

        if (type == typeof(DateTime))
            return DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        if (type == typeof(byte[]))
            return Array.Empty<byte>();

        return Activator.CreateInstance(type)!;
    }
}
