using System.Text.Json.Nodes;

namespace Fworker.Workflows.Adoption;

// W9.9b: when the adoption runs. "At every start", in the process that acts: a recurring job (every minute, on the leader) asks this class, which runs the adoption once per term of the lease (a term is the epoch of the claim the process holds;
// a process that loses the lease and takes it again begins a new term) and after the plant is restored, so that the owners' claims the plant renews are in place before the saga's rows are compared with them. A run that is partial (an
// item or a part failed) is tried again at the next minute, up to MaxAttempts; a run that ends "aborted" (the lease was lost during it) is left to the next holder. The heartbeat of the engine ("acted" marker) is written once the
// term's adoption is over and then every minute, never before: a start whose run broke off finds the same evidence of a break (W9.10n) again, and decides the same way (the decision is also kept in memory for the term).

public sealed class AdoptionJob(AdoptionRunner runner, IEngineMarkers markers, Func<bool> mayAct, Func<long> epoch, Func<bool> ready, string holder, Func<DateTimeOffset>? clock = null, int maxAttempts = 5, TimeSpan? gap = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly object gate = new();
    long? termEpoch, doneEpoch;
    (bool Break, string Why)? termBreak;
    int attempts;
    string state = "idle", waitingFor = "";
    DateTimeOffset? doneAt;

    /// <summary>One look: waits, runs the adoption of the term, or writes the heartbeat. Returns what it did ("not-leader", "waiting: ...", "heartbeat", or the run's outcome).</summary>
    public async Task<string> Tick(CancellationToken ct)
    {
        if (!mayAct()) { lock (gate) { state = "not-leader"; } return "not-leader"; }
        var e = epoch();
        bool finished; lock (gate) finished = doneEpoch == e;
        if (finished)
        {
            await Heartbeat(e, ct);
            return "heartbeat";
        }
        if (!ready()) { lock (gate) { state = "waiting"; waitingFor = "plant-restore"; } return "waiting: plant-restore"; }
        (bool Break, string Why) decided;
        lock (gate)
        {
            if (termEpoch != e) { termEpoch = e; termBreak = null; attempts = 0; }
            attempts++; state = "running"; waitingFor = "";
        }
        try
        {
            decided = termBreak ??= AdoptionBreak.Detect(await markers.Read(ct), now(), gap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // without the markers the break cannot be told, and the wrong answer is the unfenced one: wait for the next minute
            lock (gate) { state = "waiting"; waitingFor = "engine-markers"; }
            return "waiting: engine-markers";
        }
        var report = await runner.Run(decided.Break, decided.Why, () => mayAct() && epoch() == e, ct);
        lock (gate)
        {
            state = report.Outcome;
            if (report.Outcome == "done" || (report.Outcome == "partial" && attempts >= maxAttempts)) { doneEpoch = e; doneAt = now(); }
        }
        bool over; lock (gate) over = doneEpoch == e;
        if (over) await Heartbeat(e, ct);
        return report.Outcome;
    }

    async Task Heartbeat(long e, CancellationToken ct)
    {
        try { await markers.Mark(EngineMarkerNames.Acted, now(), holder, $"epoch {e}", ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"adoption: the engine marker could not be written ({ex.GetType().Name})"); }
    }

    /// <summary>The "adoption" block of the v2 status: where the term stands and the report of the latest run.</summary>
    public JsonObject Block()
    {
        lock (gate)
        {
            var o = new JsonObject { ["state"] = state, ["waitingFor"] = waitingFor.Length == 0 ? null : waitingFor, ["attempts"] = attempts, ["done"] = doneEpoch is { } d && d == termEpoch, ["doneAt"] = doneAt?.ToString("o") };
            if (runner.Last is { } last) o["last"] = last.ToJson();
            return o;
        }
    }
}
