using System.Text.Json.Nodes;

namespace Fworker.Workflows.Adoption;

// W9.9b: when the adoption runs. "At every start", in the process that acts: a recurring job (every minute, on the leader) asks this class, which runs the adoption once per term of the lease (a term is the epoch of the claim the process holds;
// a process that loses the lease and takes it again begins a new term) and after the plant is restored, so that the owners' claims the plant renews are in place before the saga's rows are compared with them. A run that is partial (an
// item or a part failed) is tried again at the next minute, up to MaxAttempts; a run that ends "aborted" (the lease was lost during it) is left to the next holder. The heartbeat of the engine ("acted" marker) is written once the
// term's adoption is over and then every minute, never before: a start whose run broke off finds the same evidence of a break (W9.10n) again, and decides the same way (the decision is also kept in memory for the term).
// After a break the term is not over before a run is "done": items whose reads failed were not fenced, and an "acted" heartbeat written then would hide the break from the next start. So a partial run of such a term is tried again
// past MaxAttempts, with growing pauses (up to RetryCap), and the status says so (breakPending). Until then the ReadoptionGate holds the saga's old-generation inputs (it is also held by the host's start, WolverineHostService).

public sealed class AdoptionJob(AdoptionRunner runner, IEngineMarkers markers, Func<bool> mayAct, Func<long> epoch, Func<bool> ready, string holder, Func<DateTimeOffset>? clock = null, int maxAttempts = 5, TimeSpan? gap = null, ReadoptionGate? readoption = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly object gate = new();
    long? termEpoch, doneEpoch, clearedEpoch;
    (bool Break, string Why)? termBreak;
    int attempts;
    DateTimeOffset? retryAt;
    /// <summary>The longest pause between two tries of a term after a break whose adoption is not done.</summary>
    public static readonly TimeSpan RetryCap = TimeSpan.FromMinutes(5);
    string state = "idle", waitingFor = "";
    DateTimeOffset? doneAt;

    /// <summary>One look: waits, runs the adoption of the term, or writes the heartbeat. Returns what it did ("not-leader", "waiting: ...", "heartbeat", or the run's outcome).</summary>
    public async Task<string> Tick(CancellationToken ct)
    {
        await oneTick.WaitAsync(ct);
        try { return await TickCore(ct); } finally { oneTick.Release(); }
    }

    readonly SemaphoreSlim oneTick = new(1, 1);

    /// <summary>
    /// Cutover review: the sweep waits for the adoption of the term, and the recurring job's first look can be late (the scheduler starts with the host). So the sweep asks for the term's FIRST look itself: it runs only when no look has begun
    /// in this term and none is running; later looks (the retries, with their pauses and attempt counts) stay with the recurring job. Returns null when it did nothing.
    /// </summary>
    public async Task<string?> TickIfFirst(CancellationToken ct)
    {
        bool Begun() { lock (gate) return termEpoch == epoch() && attempts > 0; }
        if (Begun() || !await oneTick.WaitAsync(0, ct)) return null;
        try { return Begun() ? null : await TickCore(ct); } finally { oneTick.Release(); }
    }

    async Task<string> TickCore(CancellationToken ct)
    {
        if (!mayAct()) { lock (gate) { state = "not-leader"; } return "not-leader"; }
        var e = epoch();
        bool finished; lock (gate) finished = doneEpoch == e;
        if (finished)
        {
            readoption?.Release();   // (a term that is over holds nothing: it was no break, or its run was done)
            await Heartbeat(e, ct);
            return "heartbeat";
        }
        if (!ready()) { lock (gate) { state = "waiting"; waitingFor = "plant-restore"; } return "waiting: plant-restore"; }
        (bool Break, string Why) decided;
        lock (gate)
        {
            if (termEpoch != e) { termEpoch = e; termBreak = null; attempts = 0; retryAt = null; }
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
        if (decided.Break) readoption?.Hold(decided.Why); else readoption?.Release();
        lock (gate)
        {
            // after a break the tries go on past maxAttempts, with growing pauses
            if (decided.Break && retryAt is { } at && now() < at) { state = "partial"; waitingFor = "retry-backoff"; return "waiting: retry-backoff"; }
            attempts++; state = "running"; waitingFor = "";
        }
        var report = await runner.Run(decided.Break, decided.Why, () => mayAct() && epoch() == e, ct);
        lock (gate)
        {
            state = report.Outcome;
            if (report.Outcome == "done" || (report.Outcome == "partial" && !decided.Break && report.ItemsCompleted)) clearedEpoch = e;   // (the sweep may begin: a finished run of the term, no break whose fencing is outstanding)
            if (report.Outcome == "done" || (report.Outcome == "partial" && attempts >= maxAttempts && !decided.Break)) { doneEpoch = e; doneAt = now(); }
            else if (report.Outcome == "partial" && decided.Break && attempts >= maxAttempts) retryAt = now() + TimeSpan.FromTicks(Math.Min(RetryCap.Ticks, TimeSpan.FromMinutes(1).Ticks << Math.Min(attempts - maxAttempts, 10)));
        }
        bool over; lock (gate) over = doneEpoch == e;
        if (over) { readoption?.Release(); await Heartbeat(e, ct); }
        return report.Outcome;
    }

    async Task Heartbeat(long e, CancellationToken ct)
    {
        try { await markers.Mark(EngineMarkerNames.Acted, now(), holder, $"epoch {e}", ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"adoption: the engine marker could not be written ({ex.GetType().Name})"); }
    }

    /// <summary>Whether the sweep may begin in the term with this epoch: a run of the term finished "done", or finished "partial" where no break is outstanding (items it could not read stay v1's, as before; the job retries them), or a partial one was given up after MaxAttempts. After a break only a "done" run (the fencing) counts. The gateway's sweep waits for it (AdoptionGate): the first pass of a term does not begin beside the adoption.</summary>
    public bool IsDone(long termEpochToAsk) { lock (gate) return doneEpoch == termEpochToAsk || clearedEpoch == termEpochToAsk; }

    /// <summary>The "adoption" block of the v2 status: where the term stands and the report of the latest run.</summary>
    public JsonObject Block()
    {
        lock (gate)
        {
            var o = new JsonObject { ["state"] = state, ["waitingFor"] = waitingFor.Length == 0 ? null : waitingFor, ["attempts"] = attempts, ["done"] = doneEpoch is { } d && d == termEpoch, ["doneAt"] = doneAt?.ToString("o"),
                ["breakPending"] = termBreak is { Break: true } && doneEpoch != termEpoch, ["retryAt"] = retryAt is { } r && doneEpoch != termEpoch ? r.ToString("o") : null };
            if (runner.Last is { } last) o["last"] = last.ToJson();
            if (readoption is not null) o["hold"] = readoption.Block();
            return o;
        }
    }
}
