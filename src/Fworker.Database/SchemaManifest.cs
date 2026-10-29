using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Fworker.Database;

public sealed record ColumnInfo(string Type, int MaxLength, int Precision, int Scale, bool Nullable, bool Identity, string? Default);

/// <summary>An index or primary key. Keys are the key columns in order ("Name" or "Name desc"); the constraint name of a primary key is the index name.</summary>
public sealed record IndexInfo(bool Unique, bool Primary, IReadOnlyList<string> Keys, IReadOnlyList<string> Included, string? Filter)
{
    public bool SameAs(IndexInfo o) => Unique == o.Unique && Primary == o.Primary && Keys.SequenceEqual(o.Keys) && Included.SequenceEqual(o.Included) && Filter == o.Filter;
}

public sealed class TableInfo
{
    public Dictionary<string, ColumnInfo> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IndexInfo> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The shape of the schemas "factory" and "wolverine" read from the catalog: per table the columns (type, length, precision, scale, nullability, identity, default
/// expression) and the indexes (name, uniqueness, key columns in order, included columns, filter), and per procedure the SHA-256 of its definition. The expected
/// manifest (schema-manifest.json, embedded) is generated from a database built by the scripts, so it is by construction what the scripts produce. The comparison
/// ignores what a newer image may have added (extra tables, columns, indexes, procedures) and names objects, never values.
/// </summary>
public sealed class SchemaManifest
{
    /// <summary>Key "schema.Table".</summary>
    public Dictionary<string, TableInfo> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Key "schema.procedure", value the hash of the definition.</summary>
    public Dictionary<string, string> Procedures { get; } = new(StringComparer.OrdinalIgnoreCase);

    static readonly Lazy<SchemaManifest> embedded = new(() =>
    {
        var asm = typeof(SchemaManifest).Assembly;
        using var s = asm.GetManifestResourceStream("schema-manifest.json") ?? throw new InvalidOperationException("schema-manifest.json is not embedded");
        using var r = new StreamReader(s, Encoding.UTF8);
        return FromJson(r.ReadToEnd());
    });

    /// <summary>The manifest the scripts of this build produce.</summary>
    public static SchemaManifest Expected => embedded.Value;

    // one batch, one round trip: the catalog of the two schemas
    const string Catalog = """
        DECLARE @schemas TABLE (name sysname PRIMARY KEY);
        INSERT @schemas VALUES (N'factory'), (N'wolverine');
        SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name IN (SELECT name FROM @schemas);
        SELECT s.name, t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, dc.definition
          FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
          JOIN sys.types ty ON ty.user_type_id = c.user_type_id
          LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
          WHERE s.name IN (SELECT name FROM @schemas);
        SELECT s.name, t.name, i.name, i.is_unique, i.is_primary_key, i.filter_definition, i.index_id
          FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
          WHERE i.index_id > 0 AND s.name IN (SELECT name FROM @schemas);
        SELECT s.name, t.name, i.index_id, ic.key_ordinal, ic.is_included_column, c.name, ic.is_descending_key
          FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
          JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
          WHERE i.index_id > 0 AND s.name IN (SELECT name FROM @schemas);
        SELECT s.name, o.name, m.definition FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
          WHERE o.type = 'P' AND s.name IN (SELECT name FROM @schemas);
        """;

    /// <summary>Reads the catalog of the database the connection string names. Never writes.</summary>
    public static async Task<SchemaManifest> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        return await ReadAsync(c, ct);
    }

    public static async Task<SchemaManifest> ReadAsync(SqlConnection c, CancellationToken ct = default)
    {
        var m = new SchemaManifest();
        await using var cmd = new SqlCommand(Catalog, c) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) m.Tables[r.GetString(0) + "." + r.GetString(1)] = new TableInfo();
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            m.Tables[r.GetString(0) + "." + r.GetString(1)].Columns[r.GetString(2)] = new ColumnInfo(r.GetString(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6), r.GetBoolean(7), r.GetBoolean(8),
                r.IsDBNull(9) ? null : NormalizeDefault(r.GetString(9)));
        await r.NextResultAsync(ct);
        var indexes = new Dictionary<(string, int), (string Table, string Name, bool Unique, bool Primary, string? Filter)>();
        while (await r.ReadAsync(ct))
            indexes[(r.GetString(0) + "." + r.GetString(1), r.GetInt32(6))] = (r.GetString(0) + "." + r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetString(5));
        await r.NextResultAsync(ct);
        var cols = new List<(string Table, int Index, int Ordinal, bool Included, string Column, bool Desc)>();
        while (await r.ReadAsync(ct)) cols.Add((r.GetString(0) + "." + r.GetString(1), r.GetInt32(2), r.GetByte(3), r.GetBoolean(4), r.GetString(5), r.GetBoolean(6)));
        foreach (var (key, ix) in indexes)
        {
            var mine = cols.Where(x => x.Table == key.Item1 && x.Index == key.Item2).ToList();
            var keys = mine.Where(x => !x.Included).OrderBy(x => x.Ordinal).Select(x => x.Desc ? x.Column + " desc" : x.Column).ToList();
            var inc = mine.Where(x => x.Included).Select(x => x.Column).OrderBy(x => x, StringComparer.Ordinal).ToList();
            m.Tables[ix.Table].Indexes[ix.Name] = new IndexInfo(ix.Unique, ix.Primary, keys, inc, ix.Filter);
        }
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct)) m.Procedures[r.GetString(0) + "." + r.GetString(1)] = Hash(r.GetString(2));
        return m;
    }

    /// <summary>A default expression without whitespace and parentheses: ((0)) and (0) are the same, and the name of the constraint is not part of it.</summary>
    internal static string NormalizeDefault(string definition) => new(definition.Where(ch => !char.IsWhiteSpace(ch) && ch != '(' && ch != ')').ToArray());

    internal static string Hash(string definition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.Replace("\r\n", "\n").TrimEnd()))).ToLowerInvariant();

    /// <summary>What this (actual) manifest lacks or has different from the expected one, and how many objects it has beyond it.</summary>
    public (List<SchemaDifference> Differences, int Extra) Compare(SchemaManifest expected)
    {
        var diff = new List<SchemaDifference>();
        var extra = 0;
        foreach (var (name, et) in expected.Tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!Tables.TryGetValue(name, out var at)) { diff.Add(new($"missing table {name}")); continue; }
            foreach (var (col, ec) in et.Columns.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (!at.Columns.TryGetValue(col, out var ac)) diff.Add(new($"missing column {name}.{col}"));
                else if (ac != ec) diff.Add(new($"column {name}.{col} differs"));
            }
            extra += at.Columns.Keys.Count(k => !et.Columns.ContainsKey(k));
            foreach (var (ix, ei) in et.Indexes.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (!at.Indexes.TryGetValue(ix, out var ai)) diff.Add(new($"missing index {name}.{ix}"));
                else if (!ai.SameAs(ei)) diff.Add(new($"index {name}.{ix} differs"));
            }
            extra += at.Indexes.Keys.Count(k => !et.Indexes.ContainsKey(k));
        }
        extra += Tables.Keys.Count(k => !expected.Tables.ContainsKey(k));
        foreach (var (name, hash) in expected.Procedures.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!Procedures.TryGetValue(name, out var ah)) diff.Add(new($"missing procedure {name}"));
            else if (ah != hash) diff.Add(new($"procedure {name} differs"));
        }
        extra += Procedures.Keys.Count(k => !expected.Procedures.ContainsKey(k));
        return (diff, extra);
    }

    // ---- the file ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>The JSON of schema-manifest.json: everything in ordinal order, so that a rewrite of an unchanged schema changes no byte.</summary>
    public string ToJson()
    {
        var tables = new JsonObject();
        foreach (var (tn, t) in Tables.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var cols = new JsonObject();
            foreach (var (cn, c) in t.Columns.OrderBy(x => x.Key, StringComparer.Ordinal))
                cols[cn] = new JsonObject { ["type"] = c.Type, ["maxLength"] = c.MaxLength, ["precision"] = c.Precision, ["scale"] = c.Scale, ["nullable"] = c.Nullable, ["identity"] = c.Identity, ["default"] = c.Default };
            var ixs = new JsonObject();
            foreach (var (iname, i) in t.Indexes.OrderBy(x => x.Key, StringComparer.Ordinal))
                ixs[iname] = new JsonObject { ["unique"] = i.Unique, ["primary"] = i.Primary, ["keys"] = new JsonArray(i.Keys.Select(k => (JsonNode?)k).ToArray()),
                    ["included"] = new JsonArray(i.Included.Select(k => (JsonNode?)k).ToArray()), ["filter"] = i.Filter };
            tables[tn] = new JsonObject { ["columns"] = cols, ["indexes"] = ixs };
        }
        var procs = new JsonObject();
        foreach (var (pn, h) in Procedures.OrderBy(x => x.Key, StringComparer.Ordinal)) procs[pn] = h;
        var root = new JsonObject { ["tables"] = tables, ["procedures"] = procs };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    public static SchemaManifest FromJson(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var m = new SchemaManifest();
        foreach (var (tn, tv) in root["tables"]!.AsObject())
        {
            var t = new TableInfo();
            foreach (var (cn, cv) in tv!["columns"]!.AsObject())
                t.Columns[cn] = new ColumnInfo(cv!["type"]!.GetValue<string>(), cv["maxLength"]!.GetValue<int>(), cv["precision"]!.GetValue<int>(), cv["scale"]!.GetValue<int>(),
                    cv["nullable"]!.GetValue<bool>(), cv["identity"]!.GetValue<bool>(), cv["default"]?.GetValue<string>());
            foreach (var (iname, iv) in tv["indexes"]!.AsObject())
                t.Indexes[iname] = new IndexInfo(iv!["unique"]!.GetValue<bool>(), iv["primary"]!.GetValue<bool>(), iv["keys"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
                    iv["included"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(), iv["filter"]?.GetValue<string>());
            m.Tables[tn] = t;
        }
        foreach (var (pn, h) in root["procedures"]!.AsObject()) m.Procedures[pn] = h!.GetValue<string>();
        return m;
    }
}
