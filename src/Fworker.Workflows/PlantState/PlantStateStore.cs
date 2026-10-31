using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.PlantState;

// W9.10k: the plant's hand-over in the database (factory.PlantAssignments, PlantHolders, PlantDrains and PlantState, script 240). The plant (gateway/Plant.cs) is in memory; a release, a scale-in or a replica move restarts the process, and
// until now what it would lose went to a ref of the work-orders repository (PlantSnapshot.cs, the rule script plant-snapshot-save). Where the engine acts the same snapshot is written here instead: the assignments and offers (the ids the workers'
// item tokens name), the owners, the bot holders and the drains as rows, the rest (the manager's mode, the day of its last summary, the digest's carry, the leases the last sweep saw, the Copilot and Cursor owners) as one document
// with the epoch of the term that wrote it. Splitting and joining are pure (<see cref="PlantStateSplit"/>): the snapshot that goes in is the snapshot that comes out, so the plant's own restore and its validation (names, numbers, codes)
// are the same for both stores. The durable truth stays the board, the item state comments and the claim refs: what is read here is corrected at start (the gateway's PlantRestoreCorrection) and by the first sweeps.

/// <summary>What was stored: the snapshot, the epoch and holder of the term that wrote it and when.</summary>
public sealed record PlantStateRead(JsonObject Snapshot, long Epoch, string Holder, DateTimeOffset SavedAt);

public interface IPlantStateStore
{
    /// <summary>Replaces the stored plant by <paramref name="snapshot"/> in one transaction. False when a term of a later epoch has written already (this writer is stale). Throws when the database does not take it.</summary>
    Task<bool> Save(JsonObject snapshot, long epoch, string holder, CancellationToken ct = default);

    /// <summary>The stored plant, or null when nothing was ever stored. Throws when the database does not answer.</summary>
    Task<PlantStateRead?> Load(CancellationToken ct = default);
}

/// <summary>The rows of one snapshot, and the document with the rest of it.</summary>
public sealed record PlantRows(List<PlantAssignmentRow> Assignments, List<PlantHolderRow> Holders, List<PlantDrainRow> Drains, JsonObject Document);

public static class PlantStateSplit
{
    public const string Row = "plant";
    public const string Owner = "owner", Bot = "bot", Operator = "operator", Manager = "manager";
    // the keys of the snapshot that live in rows; everything else is the document
    static readonly string[] RowKeys = ["assignments", "owners", "bots", "drained", "managerDrained"];

    static string S(JsonNode? n, int max = 100)
    {
        var s = n is JsonValue v && v.TryGetValue<string>(out var t) ? t : n?.ToString() ?? "";
        return s.Length > max ? s[..max] : s;
    }

    static int I(JsonNode? n) => int.TryParse(n?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    static bool B(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static DateTimeOffset? T(JsonNode? n) => DateTimeOffset.TryParse(n?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) && d.Year > 1 ? d : null;

    public static PlantRows Split(JsonObject snapshot)
    {
        var assignments = new List<PlantAssignmentRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in snapshot["assignments"] as JsonArray ?? [])
        {
            if (n is not JsonObject o || S(o["id"], 60) is not { Length: > 0 } id || I(o["item"]) <= 0 || !seen.Add(id)) continue;
            assignments.Add(new PlantAssignmentRow
            {
                Id = id, Item = I(o["item"]), Kind = S(o["kind"], 20), Role = S(o["role"], 60), BoardColumn = S(o["column"]), Mode = S(o["mode"], 60), Reason = S(o["reason"], 60), Worker = S(o["worker"]),
                PullRequest = I(o["pr"]), Pin = S(o["pin"]), Accepted = B(o["accepted"]), AcceptedAt = T(o["acceptedAt"]), CreatedAt = T(o["createdAt"]) ?? DateTimeOffset.UtcNow,
                SlotId = S(o["slot"]), SlotRuntime = S(o["runtime"]), ArmId = S(o["arm"])
            });
        }
        var holders = new List<PlantHolderRow>();
        foreach (var (key, kind) in new[] { ("owners", Owner), ("bots", Bot) })
            foreach (var kv in snapshot[key] as JsonObject ?? [])
                if (I(JsonValue.Create(kv.Key)) is var item and > 0 && S(kv.Value) is { Length: > 0 } holder) holders.Add(new PlantHolderRow { Item = item, Kind = kind, Holder = holder });
        var drains = new List<PlantDrainRow>();
        foreach (var (key, kind) in new[] { ("drained", Operator), ("managerDrained", Manager) })
            foreach (var d in snapshot[key] as JsonArray ?? [])
                if (S(d) is { Length: > 0 } name && !drains.Any(x => x.Name == name && x.Kind == kind)) drains.Add(new PlantDrainRow { Name = name, Kind = kind });
        var document = new JsonObject();
        foreach (var kv in snapshot) if (!RowKeys.Contains(kv.Key)) document[kv.Key] = kv.Value?.DeepClone();
        return new PlantRows(assignments, holders, drains, document);
    }

    /// <summary>The snapshot the rows and the document make: the shape <c>Plant.Snapshot()</c> writes and <c>Plant.Restore</c> reads.</summary>
    public static JsonObject Join(IEnumerable<PlantAssignmentRow> assignments, IEnumerable<PlantHolderRow> holders, IEnumerable<PlantDrainRow> drains, JsonObject document)
    {
        static string Iso(DateTimeOffset? d) => d is { } x ? x.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) : "";
        var snap = (JsonObject)document.DeepClone();
        snap["assignments"] = new JsonArray(assignments.OrderBy(a => a.Item).ThenBy(a => a.Id, StringComparer.Ordinal).Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id, ["item"] = a.Item, ["kind"] = a.Kind, ["role"] = a.Role, ["column"] = a.BoardColumn, ["mode"] = a.Mode, ["reason"] = a.Reason, ["worker"] = a.Worker, ["pr"] = a.PullRequest, ["pin"] = a.Pin,
            ["accepted"] = a.Accepted, ["acceptedAt"] = Iso(a.AcceptedAt), ["createdAt"] = Iso(a.CreatedAt), ["slot"] = a.SlotId, ["runtime"] = a.SlotRuntime, ["arm"] = a.ArmId
        }).ToArray());
        JsonObject Held(string kind) => new(holders.Where(h => h.Kind == kind).OrderBy(h => h.Item).Select(h => KeyValuePair.Create(h.Item.ToString(CultureInfo.InvariantCulture), (JsonNode?)h.Holder)));
        snap["owners"] = Held(Owner); snap["bots"] = Held(Bot);
        JsonArray Names(string kind) => new(drains.Where(d => d.Kind == kind).OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => (JsonNode)d.Name).ToArray());
        snap["drained"] = Names(Operator); snap["managerDrained"] = Names(Manager);
        return snap;
    }
}

public sealed class SqlPlantStateStore(Func<FactoryDbContext> open, Func<DateTimeOffset>? clock = null) : IPlantStateStore
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<bool> Save(JsonObject snapshot, long epoch, string holder, CancellationToken ct = default)
    {
        var rows = PlantStateSplit.Split(snapshot);
        await using var db = open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var header = await db.PlantState.FirstOrDefaultAsync(x => x.Name == PlantStateSplit.Row, ct);
        // a term of a later epoch has written: this writer lost the lock and must not put its plant over the successor's (epoch 0 is a process with no lease and no lock: nothing to compare)
        if (header is not null && epoch > 0 && header.Epoch > epoch) return false;
        await db.PlantAssignments.ExecuteDeleteAsync(ct);
        await db.PlantHolders.ExecuteDeleteAsync(ct);
        await db.PlantDrains.ExecuteDeleteAsync(ct);
        db.PlantAssignments.AddRange(rows.Assignments);
        db.PlantHolders.AddRange(rows.Holders);
        db.PlantDrains.AddRange(rows.Drains);
        var text = rows.Document.ToJsonString();
        if (header is null) db.PlantState.Add(new PlantStateRow { Name = PlantStateSplit.Row, Epoch = epoch, Holder = Clip(holder), SavedAt = now(), Document = text });
        else { header.Epoch = epoch; header.Holder = Clip(holder); header.SavedAt = now(); header.Document = text; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<PlantStateRead?> Load(CancellationToken ct = default)
    {
        await using var db = open();
        // (repeatable read: the four tables are read as one state; the database has no snapshot isolation switched on)
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var header = await db.PlantState.AsNoTracking().FirstOrDefaultAsync(x => x.Name == PlantStateSplit.Row, ct);
        if (header is null) return null;
        var assignments = await db.PlantAssignments.AsNoTracking().ToListAsync(ct);
        var holders = await db.PlantHolders.AsNoTracking().ToListAsync(ct);
        var drains = await db.PlantDrains.AsNoTracking().ToListAsync(ct);
        var document = JsonNode.Parse(header.Document) as JsonObject ?? [];
        return new PlantStateRead(PlantStateSplit.Join(assignments, holders, drains, document), header.Epoch, header.Holder, header.SavedAt);
    }

    static string Clip(string s) => s.Length > 100 ? s[..100] : s;
}
