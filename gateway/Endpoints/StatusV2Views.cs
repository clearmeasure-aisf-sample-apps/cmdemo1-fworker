using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Workflows.WorkItem;

// W6.7 (docs/design/fworker-wolverine-implementation.md W6): the items of the v2 engine in the views, grouped by repository. /api/v1/status carries the counts, the dashboard the items with their newest
// decisions from the item history, and /api/v1/digest the items that need attention. Read only: it reads the saga's table and the history and changes nothing. The gateway registers it only where the engine
// acts and has a database (Engine.V2Acts, WorkItemWiring), so every other environment answers exactly as before and the items of v1 are not touched: v1's plant status lists them as it always did.
//
// A read that fails is tried again (a transient fault of the database must not blank the page), the last good answer stands in for a read that still fails and says so (stale, error), and a failure is
// never kept: the next request reads again. Concurrent requests share one read.
sealed partial class V2ItemViews
{
    public const int Attempts = 3;
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5), DefaultAttemptLimit = TimeSpan.FromSeconds(8), CompletedWindow = TimeSpan.FromDays(7);

    readonly IWorkItemViewSource source;
    readonly Func<DateTimeOffset> now;
    readonly Func<TimeSpan, CancellationToken, Task> pause;
    readonly TimeSpan attemptLimit;
    readonly SemaphoreSlim reading = new(1, 1);
    volatile Snapshot? last;

    sealed record Snapshot(WorkItemViewSet Set, DateTimeOffset ReadAt);
    /// <summary>One answer of <see cref="Read"/>: the items (null when nothing was ever read), when they were read, and whether a failed read left an older answer standing.</summary>
    public sealed record Answer(WorkItemViewSet? Set, DateTimeOffset? ReadAt, bool Stale, string? Error);

    public V2ItemViews(IWorkItemViewSource source, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? pause = null, TimeSpan? attemptLimit = null)
    {
        this.attemptLimit = attemptLimit ?? DefaultAttemptLimit; this.source = source; this.now = now ?? (() => DateTimeOffset.UtcNow); this.pause = pause ?? ((t, ct) => Task.Delay(t, ct));
    }

    /// <summary>The items now: the answer of the last 5 seconds when there is one, else a read (3 attempts, 8 seconds each), else the last good answer marked stale.</summary>
    public async Task<Answer> Read(CancellationToken ct)
    {
        if (Current() is { } fresh) return new Answer(fresh.Set, fresh.ReadAt, false, null);
        // a read in flight and an older answer: serve the older one rather than queue behind a read that may take 24 seconds
        if (last is { } old && reading.CurrentCount == 0) return new Answer(old.Set, old.ReadAt, true, null);
        await reading.WaitAsync(ct);
        try
        {
            if (Current() is { } shared) return new Answer(shared.Set, shared.ReadAt, false, null);
            string error = "";
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(attemptLimit);
                try
                {
                    var at = now();
                    var set = await source.Read(new WorkItemViewQuery(at - CompletedWindow), limit.Token);
                    last = new Snapshot(set, at);
                    return new Answer(set, at, false, null);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    error = e is OperationCanceledException ? "timeout" : e.GetType().Name;
                    Console.Error.WriteLine($"v2 views: read attempt {attempt} of {Attempts} failed: {error}");
                    if (attempt < Attempts) await pause(TimeSpan.FromMilliseconds(250 * attempt), ct);
                }
            }
            return last is { } l ? new Answer(l.Set, l.ReadAt, true, error) : new Answer(null, null, false, error);
        }
        finally { reading.Release(); }
    }

    /// <summary>W9.10k: the last good answer without a read (null before the first), for the patrol digest, which runs under the plant's lock and cannot wait on the database. Whoever refreshes the views (the sweep) keeps it fresh.</summary>
    public Answer? Latest => last is { } l ? new Answer(l.Set, l.ReadAt, true, null) : null;

    Snapshot? Current() =>last is { } l && now() - l.ReadAt < Fresh ? l : null;

    // ---- the three views ------------------------------------------------------------------------------------------------------------------------------------------------------------

    static readonly string[] Order = [WorkItemStatus.Open, WorkItemStatus.Settling, WorkItemStatus.Completed];

    static int Rank(string status) { var i = Array.IndexOf(Order, status); return i < 0 ? Order.Length : i; }

    static IEnumerable<IGrouping<string, WorkItemView>> ByRepository(WorkItemViewSet set) => set.Items.GroupBy(i => i.Repository).OrderBy(g => g.Key, StringComparer.Ordinal);

    static JsonObject Counts(IEnumerable<WorkItemView> items, DateTimeOffset at)
    {
        var l = items.ToList();
        return new JsonObject { ["open"] = l.Count(i => i.Status == WorkItemStatus.Open), ["settling"] = l.Count(i => i.Status == WorkItemStatus.Settling), ["settleHeldForProd"] = l.Count(i => i.Status == WorkItemStatus.Settling && i.SettleHeld), ["completed"] = l.Count(i => i.Status == WorkItemStatus.Completed),
            ["parked"] = l.Count(i => i.IsOpen && i.Parked), ["leaseOverdue"] = l.Count(LeaseOverdueAt(at)) };
    }

    static Func<WorkItemView, bool> LeaseOverdueAt(DateTimeOffset at) => i => i.IsOpen && !i.Parked && i.LeaseDue is { } due && due <= at;

    static JsonObject Frame(Answer a)
    {
        var o = new JsonObject { ["available"] = a.Set != null, ["stale"] = a.Stale };
        if (a.ReadAt is { } at) o["readAt"] = at.ToString("o");
        if (a.Error != null) o["error"] = a.Error;
        if (a.Set is { Truncated: true }) o["truncated"] = true;
        return o;
    }

    /// <summary>/api/v1/status: counts and states only, per repository.</summary>
    public static JsonObject StatusBlock(Answer a)
    {
        var o = Frame(a);
        var repos = new JsonObject();
        if (a.Set != null) foreach (var g in ByRepository(a.Set)) repos[g.Key] = Counts(g, a.ReadAt ?? DateTimeOffset.UtcNow);
        o["repositories"] = repos;
        return o;
    }

    /// <summary>The dashboard: per repository the counts and the items, open ones first, each with its newest decisions from the history. The text a person wrote (a park reason) and the reasons of a decision are cut to 200 characters; the page shows them as text.</summary>
    public static JsonObject DashboardBlock(Answer a)
    {
        var o = Frame(a);
        var at = a.ReadAt ?? DateTimeOffset.UtcNow;
        var repos = new JsonArray();
        if (a.Set != null)
            foreach (var g in ByRepository(a.Set))
            {
                var items = new JsonArray();
                foreach (var i in g.OrderBy(i => Rank(i.Status)).ThenByDescending(i => i.UpdatedAt).ThenBy(i => i.Number))
                {
                    var history = new JsonArray();
                    foreach (var h in i.History)
                        history.Add(new JsonObject { ["at"] = h.At.ToString("o"), ["sequence"] = h.Sequence, ["action"] = Clip(h.Action), ["plan"] = Clip(h.Plan), ["step"] = h.Step == null ? null : Clip(h.Step), ["reasons"] = new JsonArray(h.Reasons.Take(5).Select(r => (JsonNode)Clip(r)).ToArray()) });
                    var delivered = new JsonObject();
                    foreach (var (env, release) in i.Delivered.OrderBy(kv => kv.Key, StringComparer.Ordinal)) delivered[env] = Clip(release);
                    items.Add(new JsonObject { ["item"] = i.Item, ["number"] = i.Number, ["generation"] = i.Generation, ["status"] = i.Status, ["plan"] = i.Plan, ["step"] = i.Step, ["parked"] = i.Parked,
                        ["parkReason"] = i.Parked ? Clip(i.ParkReason) : "", ["leaseOverdue"] = LeaseOverdueAt(at)(i), ["leaseDue"] = i.LeaseDue?.ToString("o"), ["settleUntil"] = i.SettleUntil?.ToString("o"), ["settleHeld"] = i.SettleHeld, ["pullRequest"] = i.PullRequest, ["delivered"] = delivered,
                        ["startedAt"] = i.StartedAt.ToString("o"), ["completedAt"] = i.CompletedAt?.ToString("o"), ["updatedAt"] = i.UpdatedAt.ToString("o"), ["history"] = history });
                }
                var r = Counts(g, at); r["repository"] = g.Key; r["items"] = items;
                repos.Add(r);
            }
        o["repositories"] = repos;
        return o;
    }

    /// <summary>/api/v1/digest: per repository the items that need someone, parked or with a lease that ran out. The digest's rule holds (3.9): numbers, names of plans and steps, gated codes; no text a person wrote.</summary>
    public static JsonObject DigestBlock(Answer a)
    {
        var o = Frame(a);
        var at = a.ReadAt ?? DateTimeOffset.UtcNow;
        var repos = new JsonArray();
        if (a.Set != null)
            foreach (var g in ByRepository(a.Set))
            {
                var overdue = LeaseOverdueAt(at);
                var attention = new JsonArray();
                foreach (var i in g.Where(i => i.IsOpen && (i.Parked || overdue(i))).OrderBy(i => i.Number))
                {
                    var last = i.History.FirstOrDefault();
                    attention.Add(new JsonObject { ["item"] = i.Number, ["kind"] = i.Parked ? "parked" : "lease-overdue", ["plan"] = Code(i.Plan), ["step"] = Code(i.Step), ["generation"] = i.Generation,
                        ["lastAction"] = last == null ? null : Code(last.Action), ["lastAt"] = last?.At.ToString("o"), ["sinceMinutes"] = (int)Math.Max(0, (at - i.UpdatedAt).TotalMinutes) });
                }
                var r = new JsonObject { ["repository"] = g.Key, ["open"] = g.Count(i => i.Status == WorkItemStatus.Open), ["settling"] = g.Count(i => i.Status == WorkItemStatus.Settling), ["attention"] = attention };
                repos.Add(r);
            }
        o["repositories"] = repos;
        return o;
    }

    static string Clip(string s) => s.Length <= 200 ? s : s[..200];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,60}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRx();

    // the digest's gate: a plan, a step or an action is written only when it has the shape of a code
    static string Code(string s) => CodeRx().IsMatch(s) ? s : "other";
}
