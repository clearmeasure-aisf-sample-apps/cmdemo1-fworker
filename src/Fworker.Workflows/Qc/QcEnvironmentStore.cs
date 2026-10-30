using System.Text.Json.Nodes;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Qc;

/// <summary>One row of factory.QcEnvironments: the key "owner/repo!N" (lower case), the repository, the item the pull request belongs to, the revision of the row, the state of the record and the record itself.</summary>
public sealed record QcEnvironmentEntry(string Key, string Repository, string? ItemKey, int Revision, string Status, JsonObject State, DateTimeOffset UpdatedAt);

/// <summary>
/// The saga's own copy of the QC records, one per (repository, pull request). The record of the work item's state comment is what v1 reads (the rollback path, the sweep, the merge gate), so a verb saves
/// that first and this table after it; this is the copy the list of "Waiting for PR QC" and the index of live items read without opening a state comment, and the record a verb adopts when the comment has none.
/// </summary>
public interface IQcEnvironmentStore
{
    Task<QcEnvironmentEntry?> Load(string key, CancellationToken ct);

    /// <summary>
    /// Stores the record: inserts the row or replaces it, and keeps the newer when the row already holds a record of the same attempt with a higher revision (a verb that lost a race to a later one must not take the
    /// row back). Another writer at the same moment is told by the revision of the row; the save runs again on the row that won.
    /// </summary>
    Task Save(string key, string repository, string? itemKey, JsonObject record, CancellationToken ct);

    /// <summary>The rows of one repository that are not torn down (a failed environment is still on the list of "Waiting for PR QC"; the live states are <see cref="QcRecords.IsLive"/>).</summary>
    Task<IReadOnlyList<QcEnvironmentEntry>> Live(string repository, CancellationToken ct);
}

public static class QcKeys
{
    /// <summary>"owner/repo!N", lower case: the correlation key of a pull request.</summary>
    public static string For(string owner, string repo, int pr) => $"{owner}/{repo}!{pr}".ToLowerInvariant();
    public static string ItemKey(string owner, string repo, int item) => $"{owner}/{repo}#{item}".ToLowerInvariant();
}

public sealed class SqlQcEnvironmentStore(Func<FactoryDbContext> open) : IQcEnvironmentStore
{
    const int Attempts = 12;

    public async Task<QcEnvironmentEntry?> Load(string key, CancellationToken ct)
    {
        await using var db = open();
        var row = await db.QcEnvironments.AsNoTracking().FirstOrDefaultAsync(x => x.PullRequestKey == key, ct);
        return row is null ? null : Entry(row);
    }

    public async Task Save(string key, string repository, string? itemKey, JsonObject record, CancellationToken ct)
    {
        var status = Status(record);
        for (var attempt = 1; ; attempt++)
        {
            await using var db = open();
            var row = await db.QcEnvironments.FirstOrDefaultAsync(x => x.PullRequestKey == key, ct);
            try
            {
                if (row is null)
                {
                    db.QcEnvironments.Add(new QcEnvironmentRow { PullRequestKey = key, Repository = repository, ItemKey = itemKey, Revision = 1, Status = status, StateJson = record.ToJsonString(), UpdatedAt = DateTimeOffset.UtcNow });
                }
                else
                {
                    if (Newer(ParseState(row.StateJson), record)) return;
                    row.Repository = repository; row.ItemKey = itemKey ?? row.ItemKey; row.Status = status; row.StateJson = record.ToJsonString();
                    row.Revision += 1; row.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (Exception e) when ((e is DbUpdateException) && attempt < Attempts) { /* another writer was first (the key, or the revision): look again */ }
        }
    }

    public async Task<IReadOnlyList<QcEnvironmentEntry>> Live(string repository, CancellationToken ct)
    {
        await using var db = open();
        var rows = await db.QcEnvironments.AsNoTracking().Where(x => x.Repository == repository && x.Status != "torn-down").ToListAsync(ct);
        return rows.Select(Entry).ToList();
    }

    public static string Status(JsonObject record)
    {
        var s = QcJson.Str(record, "state");
        return s.Length > 16 ? s[..16] : s;
    }

    internal static JsonObject ParseState(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); } catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    /// <summary>The stored record is a later one of the same attempt than the one being saved.</summary>
    internal static bool Newer(JsonObject stored, JsonObject incoming) =>
        QcJson.Str(stored, "sha") == QcJson.Str(incoming, "sha") && QcJson.Int(stored, "attempt") == QcJson.Int(incoming, "attempt") && QcJson.Int(stored, "rev") > QcJson.Int(incoming, "rev");

    static QcEnvironmentEntry Entry(QcEnvironmentRow row) => new(row.PullRequestKey, row.Repository, row.ItemKey, row.Revision, row.Status, ParseState(row.StateJson), row.UpdatedAt);
}

/// <summary>The same in memory: the tests, and a gateway without a database.</summary>
public sealed class InMemoryQcEnvironmentStore : IQcEnvironmentStore
{
    readonly Dictionary<string, QcEnvironmentEntry> rows = [];
    public IReadOnlyDictionary<string, QcEnvironmentEntry> Rows { get { lock (rows) return new Dictionary<string, QcEnvironmentEntry>(rows); } }
    /// <summary>A fault to throw from Save (a test of the verb that must not fail for the copy).</summary>
    public Exception? SaveFault { get; set; }

    public Task<QcEnvironmentEntry?> Load(string key, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryGetValue(key, out var e) ? e with { State = (JsonObject)e.State.DeepClone() } : null);
    }

    public Task Save(string key, string repository, string? itemKey, JsonObject record, CancellationToken ct)
    {
        if (SaveFault is { } f) throw f;
        lock (rows)
        {
            if (rows.TryGetValue(key, out var old))
            {
                if (SqlQcEnvironmentStore.Newer(old.State, record)) return Task.CompletedTask;
                rows[key] = old with { Repository = repository, ItemKey = itemKey ?? old.ItemKey, Status = SqlQcEnvironmentStore.Status(record), State = (JsonObject)record.DeepClone(), Revision = old.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            }
            else rows[key] = new QcEnvironmentEntry(key, repository, itemKey, 1, SqlQcEnvironmentStore.Status(record), (JsonObject)record.DeepClone(), DateTimeOffset.UtcNow);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QcEnvironmentEntry>> Live(string repository, CancellationToken ct)
    {
        lock (rows) return Task.FromResult<IReadOnlyList<QcEnvironmentEntry>>(rows.Values.Where(x => x.Repository == repository && x.Status != "torn-down").ToList());
    }
}
