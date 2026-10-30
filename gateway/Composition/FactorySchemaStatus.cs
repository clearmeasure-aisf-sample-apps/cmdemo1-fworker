using System.Text.Json.Nodes;
using Fworker.Database;
using Microsoft.Data.SqlClient;

// Where the schema of the gateway's database stands (docs/design/fworker-database.md): the start of the Wolverine host applies the scripts of Fworker.Database and checks the catalog
// against them (WolverineHostService.RunAsync), and this holds the outcome for /ready, /api/v1/status and the services that must not touch the tables before they exist.
// - Tracked is true where a database is configured and the environment runs the Wolverine host (shadow and v2): /api/v1/status carries the schema block. Elsewhere /api/v1/status is what it was.
// - Required (W9.10o) is true only where v2 acts (Engine.V2Acts) and a database is configured: only there is the schema part of being ready. In shadow v1 acts and needs no table, so a database that
//   is down, slow to start or failing a v2-only script must not keep /ready at 503 (the operator treats a 503 as a failed release and reverts an unrelated v1 change); the schema then shows in status only.
// - Where it is required, /ready answers 503 "waitingFor":"schema" until the first good verification of this process. After that /ready no longer depends on the database: a later outage shows in
//   /api/v1/status (the schema and wolverine blocks) and does not flip /ready.
// - Nothing here is a secret: a failure is a type, a SQL error number and a script name, never a message.
sealed class FactorySchemaStatus(bool required, bool? tracked = null)
{
    readonly bool tracked = tracked ?? required;

    public const int MaxDifferences = 20;

    readonly object gate = new();
    string state = "upgrading";   // upgrading | good | failed
    bool everGood;
    int attempts;
    DateTime since = DateTime.UtcNow;
    DateTime? verifiedAt;
    string? failedScript, lastFailure;
    SchemaReport? report;

    /// <summary>A database is configured and the environment runs the Wolverine host (shadow or v2): the schema is reported in /api/v1/status.</summary>
    public bool Tracked => tracked;

    /// <summary>A database is configured and v2 acts here: the schema is part of being ready.</summary>
    public bool Required => required;

    /// <summary>The schema was verified good by this process (at least once): the tables exist, so the services that read them may.</summary>
    public bool IsGood { get { lock (gate) return everGood; } }

    public void Upgrading(int attempt)
    {
        lock (gate) { state = "upgrading"; attempts = attempt; failedScript = null; if (!everGood) since = DateTime.UtcNow; }
    }

    public void Good(SchemaReport r)
    {
        lock (gate) { state = "good"; everGood = true; report = r; verifiedAt = DateTime.UtcNow; failedScript = null; lastFailure = null; }
    }

    public void Failed(int attempt, Exception e)
    {
        lock (gate)
        {
            state = "failed"; attempts = attempt; lastFailure = Describe(e, out var script); failedScript = script;
            if (e is SchemaNotGoodException ng) report = ng.Report;
        }
    }

    /// <summary>The failure as text that is safe to show: the kind and the SQL error number, and the script when one failed.</summary>
    public static string Describe(Exception e, out string? script)
    {
        script = null;
        switch (e)
        {
            case SchemaUpgradeException u: script = u.Script; return u.InnerException is SqlException us ? $"SqlException {us.Number}" : u.InnerException?.GetType().Name ?? "SchemaUpgradeException";
            case SchemaNotGoodException: return "SchemaDifference";
            case SchemaLockTimeoutException: return "SchemaLockTimeout";
            case SqlException se: return $"SqlException {se.Number}";
            default: return e.GetType().Name;
        }
    }

    /// <summary>The 503 body of /ready while the schema is not good; null when /ready does not wait for it.</summary>
    public object? Waiting()
    {
        if (!required) return null;
        lock (gate)
        {
            if (everGood) return null;
            return new { ready = false, waitingFor = "schema", state, attempts, since = since.ToString("o"), failedScript, lastFailure };
        }
    }

    /// <summary>The "schema" block of /api/v1/status; null where the schema is not tracked.</summary>
    public JsonObject? Status()
    {
        if (!tracked) return null;
        lock (gate)
        {
            JsonArray Names(IEnumerable<string>? names) => new(names?.Select(n => (JsonNode?)n).ToArray() ?? []);
            return new JsonObject
            {
                ["state"] = state, ["journal"] = $"{SchemaNames.JournalSchema}.{SchemaNames.JournalTable}", ["shipped"] = FactorySchema.Scripts.Count, ["latest"] = FactorySchema.Scripts.LastOrDefault(),
                ["applied"] = Names(report?.Applied), ["pending"] = Names(report?.Pending), ["ahead"] = Names(report?.Ahead),
                ["differences"] = Names(report?.Differences.Take(MaxDifferences).Select(d => d.Description)), ["extra"] = report?.Extra ?? 0,
                ["attempts"] = attempts, ["since"] = since.ToString("o"), ["verifiedAt"] = verifiedAt?.ToString("o"),
                ["durationMs"] = report == null ? null : (long)report.Duration.TotalMilliseconds, ["failedScript"] = failedScript, ["lastFailure"] = lastFailure,
            };
        }
    }
}
