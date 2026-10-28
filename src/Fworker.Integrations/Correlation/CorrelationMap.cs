using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;

namespace Fworker.Integrations.Correlation;

// W4.1: the correlation map (docs/design/wolverine-factory.md 4.2) with its writers. A later event that names only a pull request, a head commit, a task or an agent
// finds its item through this map. The table is factory.Correlations of Fworker.Persistence (made by the migration the Wolverine host runs at its start); a writer
// adds a row if the (kind, key) is not there and never overwrites one, so the first source that knew the mapping stays the word on it.

/// <summary>One mapping: a thing outside the factory to the item (and generation) it belongs to.</summary>
public sealed record CorrelationRef(string Kind, string Key, string ItemKey, int Generation, string Source);

public interface ICorrelationMap
{
    /// <summary>Adds the mapping when its (kind, key) is new. False when the key was already mapped (to this item or another): nothing is changed.</summary>
    bool TryAdd(CorrelationRef r);
    /// <summary>The item key a thing maps to, or null.</summary>
    string? Resolve(string kind, string key);
}

public sealed class InMemoryCorrelationMap : ICorrelationMap
{
    readonly ConcurrentDictionary<(string, string), CorrelationRef> rows = new();
    public bool TryAdd(CorrelationRef r) => rows.TryAdd((r.Kind, r.Key), r);
    public string? Resolve(string kind, string key) => rows.TryGetValue((kind, key), out var r) ? r.ItemKey : null;
}

public sealed class SqlCorrelationMap(string connectionString) : ICorrelationMap
{
    public bool TryAdd(CorrelationRef r)
    {
        using var c = new SqlConnection(connectionString); c.Open();
        using var cmd = new SqlCommand("""
            INSERT INTO factory.Correlations (Kind, [Key], ItemKey, Generation, Source, CreatedAt)
            SELECT @kind, @key, @item, @gen, @src, SYSDATETIMEOFFSET()
            WHERE NOT EXISTS (SELECT 1 FROM factory.Correlations WITH (UPDLOCK, HOLDLOCK) WHERE Kind = @kind AND [Key] = @key)
            """, c);
        Bind(cmd, r.Kind, r.Key);
        cmd.Parameters.Add("@item", SqlDbType.NVarChar, 300).Value = r.ItemKey;
        cmd.Parameters.Add("@gen", SqlDbType.Int).Value = r.Generation;
        cmd.Parameters.Add("@src", SqlDbType.NVarChar, 64).Value = r.Source.Length <= 64 ? r.Source : r.Source[..64];
        return cmd.ExecuteNonQuery() == 1;
    }

    public string? Resolve(string kind, string key)
    {
        using var c = new SqlConnection(connectionString); c.Open();
        using var cmd = new SqlCommand("SELECT ItemKey FROM factory.Correlations WHERE Kind = @kind AND [Key] = @key", c);
        Bind(cmd, kind, key);
        return cmd.ExecuteScalar() as string;
    }

    static void Bind(SqlCommand cmd, string kind, string key)
    {
        cmd.Parameters.Add("@kind", SqlDbType.NVarChar, 32).Value = kind;
        cmd.Parameters.Add("@key", SqlDbType.NVarChar, 400).Value = key;
    }
}
