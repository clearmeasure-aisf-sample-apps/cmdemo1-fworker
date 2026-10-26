using System.Text.Json.Nodes;
using System.Threading.Channels;

sealed class EventHub
{
    private readonly object gate = new();
    private readonly List<(long Id, string Json)> buffer = new();
    private readonly List<Channel<(long, string)>> subs = new();
    private List<string> pending = new();
    private long next = 1;
    private readonly SemaphoreSlim sweepSignal = new(0, 1);
    public DateTime? LastSweep { get; set; }
    public int SubscriberCount { get { lock (gate) return subs.Count; } }
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public long LastId { get { lock (gate) return next - 1; } }

    public void Publish(string json)
    {
        lock (gate)
        {
            var id = next++; buffer.Add((id, json)); if (buffer.Count > 500) buffer.RemoveAt(0);
            foreach (var s in subs) s.Writer.TryWrite((id, json));
        }
    }
    public (Channel<(long, string)>, List<(long, string)>, List<string>) Subscribe(long after)
    {
        var ch = Channel.CreateUnbounded<(long, string)>();
        lock (gate) { subs.Add(ch); return (ch, buffer.Where(b => b.Id > after && after >= 0).ToList(), pending.ToList()); }
    }
    public void Unsubscribe(Channel<(long, string)> ch) { lock (gate) subs.Remove(ch); }

    // Legacy broadcast mode: offers are the full list of work currently wanted. A new offer is pushed at once; one still wanted is pushed again after
    // reofferSeconds, because the worker that took it may have died.
    private readonly Dictionary<string, DateTime> offeredAt = new();
    private readonly double reofferSeconds = double.TryParse(Environment.GetEnvironmentVariable("FACTORY_REOFFER_SECONDS"), out var rs) ? rs : 120;
    public void SetOffers(List<string> offers)
    {
        List<string> fresh = new();
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var o in offers) if (!offeredAt.TryGetValue(o, out var at) || (now - at).TotalSeconds >= reofferSeconds) { fresh.Add(o); offeredAt[o] = now; }
            foreach (var k in offeredAt.Keys.Where(k => !offers.Contains(k)).ToList()) offeredAt.Remove(k);
            pending = offers;
        }
        foreach (var o in fresh) Publish(o);
    }
    // Sweep requests (issue 92): a trigger of one item queues that item for an incremental pass; a trigger of no item asks for a full sweep. The
    // sweeper takes them; a request that finds its item (or a full sweep) already asked for is coalesced into it.
    readonly HashSet<int> sweepItems = new();
    bool sweepFull;
    long sweepRequests, sweepCoalesced, fullSweeps, incrementalPasses, itemsPassed, lowSweeps;
    DateTime? lastIncremental;
    public void RequestSweep() => RequestSweep(0);
    public void RequestSweep(int item)
    {
        lock (gate) { sweepRequests++; if (item > 0 ? !sweepItems.Add(item) : sweepFull) sweepCoalesced++; if (item <= 0) sweepFull = true; }
        try { sweepSignal.Release(); } catch (SemaphoreFullException) { }
    }
    public (bool Full, int Items) PendingSweep() { lock (gate) return (sweepFull, sweepItems.Count); }
    public List<int> TakeSweepItems() { lock (gate) { var l = sweepItems.OrderBy(i => i).ToList(); sweepItems.Clear(); return l; } }
    // a full sweep covers every queued item and the full request
    public void FullSweepTaken() { lock (gate) { sweepItems.Clear(); sweepFull = false; } }
    public void NoteSweep(bool full, int items, bool low) { lock (gate) { if (full) fullSweeps++; else { incrementalPasses++; itemsPassed += items; lastIncremental = DateTime.UtcNow; } if (low) lowSweeps++; } }
    public Task WaitForSweepSignal(TimeSpan max, CancellationToken ct) => sweepSignal.WaitAsync(max, ct);
    public JsonObject SweepStatus(int minSeconds)
    {
        lock (gate)
            return new JsonObject
            {
                ["minSeconds"] = minSeconds, ["full"] = fullSweeps, ["incremental"] = incrementalPasses, ["itemsInIncremental"] = itemsPassed, ["lowBudgetSweeps"] = lowSweeps,
                ["requests"] = sweepRequests, ["coalesced"] = sweepCoalesced, ["queued"] = sweepItems.Count, ["fullRequested"] = sweepFull,
                ["lastFull"] = LastSweep?.ToString("o"), ["lastIncremental"] = lastIncremental?.ToString("o")
            };
    }
}
