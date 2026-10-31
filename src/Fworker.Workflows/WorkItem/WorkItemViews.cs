using System.Text.Json;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.WorkItem;

// W6.7: what the dashboard, /api/v1/status and the supervisor's digest show of the items the v2 engine owns. Read only: the rows of the WorkItems table (the saga's state) with the newest decisions of each
// item from the item history (4.7), grouped by repository. Nothing here writes, and nothing here exists for an item without a saga (v1's items are not in these tables).

/// <summary>One decision of an item's saga, as the history recorded it (<see cref="WorkItemHistory"/>).</summary>
public sealed record WorkItemHistoryLine(DateTimeOffset At, long Sequence, string Action, string Plan, string? Step, IReadOnlyList<string> Reasons);

/// <summary>One item of the v2 engine: the row of its saga with the newest decisions of its current generation (newest first).</summary>
public sealed record WorkItemView(string Item, int Number, string Repository, int Generation, string Status, string Plan, string Step, bool Parked, string ParkReason, int PullRequest,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LeaseDue, IReadOnlyDictionary<string, string> Delivered, IReadOnlyList<WorkItemHistoryLine> History,
    DateTimeOffset? SettleUntil = null, bool SettleHeld = false)
{
    public bool IsOpen => Status != WorkItemStatus.Completed;
}

/// <summary>What a read asks for: every item that has not completed, the completed ones since <paramref name="CompletedSince"/>, at most <paramref name="MaxItems"/> items, the newest decisions per item.</summary>
public sealed record WorkItemViewQuery(DateTimeOffset CompletedSince, int MaxItems = 500, int HistoryPerItem = 5);

/// <summary>The items of a read; <paramref name="Truncated"/> when more than the query's maximum matched (the open ones come first, so what is cut is the oldest completed).</summary>
public sealed record WorkItemViewSet(IReadOnlyList<WorkItemView> Items, bool Truncated);

public interface IWorkItemViewSource
{
    Task<WorkItemViewSet> Read(WorkItemViewQuery query, CancellationToken ct);
}

public sealed class SqlWorkItemViewSource(Func<FactoryDbContext> open) : IWorkItemViewSource
{
    /// <summary>The cap of history rows one read takes (all items together): a bound for the read, not a count per item.</summary>
    public const int HistoryRowCap = 4000;

    public async Task<WorkItemViewSet> Read(WorkItemViewQuery query, CancellationToken ct)
    {
        await using var db = open();
        // open items first (a point query on the status), then the newest completed; one more than the maximum says whether anything was cut
        var rows = await db.WorkItems.AsNoTracking()
            .Where(x => x.Status != WorkItemStatus.Completed || x.UpdatedAt >= query.CompletedSince)
            .OrderBy(x => x.Status == WorkItemStatus.Completed ? 1 : 0).ThenByDescending(x => x.UpdatedAt)
            .Take(query.MaxItems + 1)
            .Select(x => new { x.ItemKey, x.Repository, x.Generation, x.Status, x.Plan, x.Step, x.StateJson, x.UpdatedAt })
            .ToListAsync(ct);
        var truncated = rows.Count > query.MaxItems;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var keys = rows.Select(r => r.ItemKey).ToList();
        var history = keys.Count == 0 ? [] : await db.ItemHistory.AsNoTracking()
            .Where(h => h.Type == WorkItemHistory.HistoryType && keys.Contains(h.ItemKey))
            .OrderByDescending(h => h.Id).Take(HistoryRowCap)
            .Select(h => new { h.ItemKey, h.Generation, h.At, h.Detail }).ToListAsync(ct);
        var byItem = history.GroupBy(h => h.ItemKey).ToDictionary(g => g.Key, g => g.ToList());

        var items = new List<WorkItemView>(rows.Count);
        foreach (var r in rows)
        {
            WorkItemState state;
            try { state = WorkItemState.FromJson(r.StateJson); }
            catch (JsonException) { state = new WorkItemState { Item = r.ItemKey, Generation = r.Generation, Status = r.Status, Plan = r.Plan }; }   // one damaged document does not take the others down
            var lines = (byItem.GetValueOrDefault(r.ItemKey) ?? []).Where(h => h.Generation == r.Generation).Take(query.HistoryPerItem)
                .Select(h => Line(h.At, h.Detail)).ToList();
            var number = Contracts.Ids.ItemId.TryParse(r.ItemKey, out var id) ? id.Number : 0;
            items.Add(new WorkItemView(r.ItemKey, number, r.Repository, r.Generation, r.Status, r.Plan, r.Step, state.Parked, state.ParkReason ?? "", state.PullRequest,
                state.StartedAt, state.CompletedAt, r.UpdatedAt, state.LeaseDue, state.Delivered ?? [], lines, state.SettleUntil, state.SettleHeld));
        }
        return new WorkItemViewSet(items, truncated);
    }

    /// <summary>A history row's detail ({Sequence, Action, Plan, Step, ArmId, Reasons}) as a line; a detail that cannot be read still shows the time.</summary>
    public static WorkItemHistoryLine Line(DateTimeOffset at, string? detail)
    {
        try
        {
            using var doc = JsonDocument.Parse(detail ?? "{}");
            var o = doc.RootElement;
            string Text(string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var sequence = o.ValueKind == JsonValueKind.Object && o.TryGetProperty("Sequence", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
            var reasons = new List<string>();
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("Reasons", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var x in rs.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } t) reasons.Add(t);
            var step = Text("Step");
            return new WorkItemHistoryLine(at, sequence, Text("Action"), Text("Plan"), step.Length > 0 ? step : null, reasons);
        }
        catch (JsonException) { return new WorkItemHistoryLine(at, 0, "", "", null, []); }
    }
}
