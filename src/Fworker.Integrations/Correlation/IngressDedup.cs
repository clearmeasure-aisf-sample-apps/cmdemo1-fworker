using System.Collections.Concurrent;
using System.Data;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Microsoft.Data.SqlClient;

namespace Fworker.Integrations.Correlation;

// W4.1: the deduplication of the ingress, in two layers. The logical key is the delivery (provider and delivery id): the same webhook delivered twice, or a
// redelivery by GitHub. The business key is the fact (event, item, pull request, commit): the same transition seen by a webhook and by the poll of the board
// reconciler, or by a repository hook and an organization hook. A key is claimed by a message id: the same message claiming again (Wolverine redelivers after a
// crash between the claim and the end of the handler) goes on, a different message is a duplicate. The table is created when first used, as the command stores are.

/// <summary>What the inbox takes in: the event as the rule verb returned it, and where it came from ("webhook" or "poll").</summary>
public sealed record IngressInboxMessage(string Source, string EventJson);

public static class IngressKeys
{
    /// <summary>"provider|ref", or null for an event that carries no delivery id (a poll's event, whose ref starts with "poll:").</summary>
    public static string? Logical(JsonObject ev)
    {
        var r = Str(ev["ref"]);
        return string.IsNullOrEmpty(r) || r.StartsWith("poll:", StringComparison.Ordinal) ? null : $"{Str(ev["provider"]) ?? "github"}|{r}";
    }

    /// <summary>
    /// The fact: "event|item|pr|sha" plus what tells two occurrences of one commit apart, or null when the event cannot be told apart (then only its delivery deduplicates it).
    /// A sha is required (a pull request number alone is not a fact: it is drafted, reopened, closed again). A CI event (build.*) needs its run id and carries the attempt,
    /// so a re-run of the same commit is a new fact; a review carries its author and state; any event carries its conclusion or status when it has one (W4.1 follow-up, #485).
    /// </summary>
    public static string? Business(JsonObject ev)
    {
        var name = Str(ev["event"]); if (string.IsNullOrEmpty(name)) return null;
        var sha = (Str(ev["sha"]) ?? "").ToLowerInvariant();
        if (sha == "") return null;
        var run = Scalar(ev["run_id"]);
        if (name.StartsWith("build.", StringComparison.Ordinal) && run == "") return null;   // a CI event without its run: a re-run would look like the first
        var key = $"{name}|{Num(ev["item"])}|{Num(ev["pr"])}|{sha}";
        foreach (var f in Discriminators)
            if (Scalar(ev[f]) is { Length: > 0 } v) key += $"|{f}={v.ToLowerInvariant()}";
        return key;
    }

    static readonly string[] Discriminators = ["run_id", "run_attempt", "conclusion", "status", "state", "login", "workflow"];

    static string Scalar(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : "";

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static string Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) && i > 0 ? i.ToString() : "";
}

public interface IIngressDedup
{
    /// <summary>True when the key is new or was claimed by this same message; false when another message holds it.</summary>
    bool Claim(string layer, string key, Guid messageId);
}

public sealed class InMemoryIngressDedup : IIngressDedup
{
    readonly ConcurrentDictionary<(string, string), Guid> held = new();
    public bool Claim(string layer, string key, Guid messageId) => held.GetOrAdd((layer, key), messageId) == messageId;
}

public sealed class SqlIngressDedup(string connectionString) : IIngressDedup
{
    const string Ddl = """
        IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory]');
        IF OBJECT_ID(N'factory.IngressDedup', N'U') IS NULL
            CREATE TABLE factory.IngressDedup (
                Layer nvarchar(16) NOT NULL, [Key] nvarchar(400) NOT NULL, MessageId uniqueidentifier NOT NULL, ClaimedAt datetime2 NOT NULL,
                CONSTRAINT PK_IngressDedup PRIMARY KEY (Layer, [Key]));
        """;
    readonly object gate = new();
    bool ready;

    public bool Claim(string layer, string key, Guid messageId)
    {
        using var c = new SqlConnection(connectionString); c.Open();
        EnsureTable(c);
        using var cmd = new SqlCommand("""
            INSERT INTO factory.IngressDedup (Layer, [Key], MessageId, ClaimedAt)
            SELECT @l, @k, @m, SYSUTCDATETIME()
            WHERE NOT EXISTS (SELECT 1 FROM factory.IngressDedup WITH (UPDLOCK, HOLDLOCK) WHERE Layer = @l AND [Key] = @k);
            SELECT CASE WHEN MessageId = @m THEN 1 ELSE 0 END FROM factory.IngressDedup WHERE Layer = @l AND [Key] = @k;
            """, c);
        cmd.Parameters.Add("@l", SqlDbType.NVarChar, 16).Value = layer;
        cmd.Parameters.Add("@k", SqlDbType.NVarChar, 400).Value = key.Length <= 400 ? key : key[..400];
        cmd.Parameters.Add("@m", SqlDbType.UniqueIdentifier).Value = messageId;
        return cmd.ExecuteScalar() is int one && one == 1;
    }

    /// <summary>Forgets claims older than the window (for a recurring cleanup job); returns the rows removed.</summary>
    public int Purge(TimeSpan olderThan)
    {
        using var c = new SqlConnection(connectionString); c.Open();
        EnsureTable(c);
        using var cmd = new SqlCommand("DELETE FROM factory.IngressDedup WHERE ClaimedAt < @t", c);
        cmd.Parameters.Add("@t", SqlDbType.DateTime2).Value = DateTime.UtcNow - olderThan;
        return cmd.ExecuteNonQuery();
    }

    void EnsureTable(SqlConnection c)
    {
        if (ready) return;
        lock (gate)
        {
            if (ready) return;
            using var cmd = new SqlCommand(Ddl, c); cmd.ExecuteNonQuery(); ready = true;
        }
    }
}

/// <summary>The decision of the inbox for one message.</summary>
public sealed record IngressInboxOutcome(bool Accepted, string Reason, int CorrelationsAdded);

/// <summary>Takes one inbox message in: deduplicates it (delivery, then fact) and writes the correlations of the event. A redelivery of the same message gives the same answer.</summary>
public sealed class IngressInboxProcessor(IIngressDedup dedup, ICorrelationMap map, RepoId home)
{
    public RepoId Home => home;

    public IngressInboxOutcome Process(IngressInboxMessage m, Guid messageId)
    {
        if (JsonNode.Parse(m.EventJson) is not JsonObject ev) return new(false, "not-an-object", 0);
        if (IngressKeys.Logical(ev) is { } logical && !dedup.Claim("delivery", logical, messageId)) return new(false, "duplicate-delivery", 0);
        if (IngressKeys.Business(ev) is { } fact && !dedup.Claim("fact", fact, messageId)) return new(false, "duplicate-fact", 0);
        var added = 0;
        foreach (var r in CorrelationWriters.FromEvent(ev, home, m.Source)) if (map.TryAdd(r)) added++;
        return new(true, "accepted", added);
    }
}
