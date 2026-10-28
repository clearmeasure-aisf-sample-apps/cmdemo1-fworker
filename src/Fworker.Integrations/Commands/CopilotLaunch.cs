using System.Collections.Concurrent;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;

namespace Fworker.Integrations.Commands;

// W3.3: the Copilot launch with a reservation. A Copilot launch is a POST that creates an agent task: when the call times out or the gateway stops before the
// answer is read, nobody knows whether the task exists, and running the command again could start a second task for the same item and phase (one task owns an
// item). So the launch reserves (item, phase) first, then calls GitHub, then marks the reservation Launched or Released. A reservation whose outcome is
// unknown stays Reserved: it blocks another launch of that item and phase, and the reconciler settles it against GitHub (a task created since the reservation
// -> Launched, none -> Released, so the launch can be tried again). Only the Copilot runtime goes through this (CopilotLaunchPort); every other command passes.

public enum ReservationState { Reserved, Launched, Released }

/// <summary>One reservation per (item, phase): the worker whose launch it is, when it was made and, once launched, the task and its page.</summary>
public sealed record CopilotReservation(int Item, string Phase, string Worker, ReservationState State, DateTime At, string TaskId = "", string Url = "");

public interface ICopilotReservations
{
    CopilotReservation? Get(int item, string phase);
    void Put(CopilotReservation reservation);
    /// <summary>Atomically takes (item, phase) unless a Reserved one is there; false then.</summary>
    bool TryReserve(CopilotReservation reservation);
    IReadOnlyList<CopilotReservation> Reserved();
}

public sealed class InMemoryCopilotReservations : ICopilotReservations
{
    readonly ConcurrentDictionary<(int, string), CopilotReservation> _all = new();
    public CopilotReservation? Get(int item, string phase) => _all.TryGetValue((item, phase), out var r) ? r : null;
    public void Put(CopilotReservation r) { lock (_all) _all[(r.Item, r.Phase)] = r; }
    public bool TryReserve(CopilotReservation r)
    {
        lock (_all)
        {
            if (_all.TryGetValue((r.Item, r.Phase), out var cur) && cur.State == ReservationState.Reserved) return false;
            _all[(r.Item, r.Phase)] = r; return true;
        }
    }
    public IReadOnlyList<CopilotReservation> Reserved() => _all.Values.Where(r => r.State == ReservationState.Reserved).ToList();
}

/// <summary>Asks GitHub whether an agent task was created for the item and phase at or after <paramref name="since"/>. Throws when GitHub cannot be asked (the reservation then stays).</summary>
public interface ICopilotTaskLookup
{
    Task<(string TaskId, string Url)?> FindAsync(int item, string phase, DateTime since, CancellationToken ct);
}

public enum ReconcileOutcome { Launched, Released, Unknown }

public sealed record ReconcileResult(int Item, string Phase, ReconcileOutcome Outcome);

public sealed class CopilotReconciler
{
    readonly ICopilotReservations store; readonly ICopilotTaskLookup lookup; readonly Func<DateTime> utcNow;
    public CopilotReconciler(ICopilotReservations store, ICopilotTaskLookup lookup, Func<DateTime>? utcNow = null)
    { this.store = store; this.lookup = lookup; this.utcNow = utcNow ?? (() => DateTime.UtcNow); }

    /// <summary>Settles one reservation against GitHub. The created_at of a task is GitHub's clock, so the search starts a minute before the reservation.</summary>
    public async Task<ReconcileOutcome> Settle(CopilotReservation r, CancellationToken ct = default)
    {
        (string TaskId, string Url)? found;
        try { found = await lookup.FindAsync(r.Item, r.Phase, r.At - TimeSpan.FromMinutes(1), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return ReconcileOutcome.Unknown; }
        // the reservation may have been settled by the launch itself while GitHub was asked: do not overwrite a newer record
        if (store.Get(r.Item, r.Phase) != r) return ReconcileOutcome.Unknown;
        if (found is { } t) { store.Put(r with { State = ReservationState.Launched, TaskId = t.TaskId, Url = t.Url }); return ReconcileOutcome.Launched; }
        store.Put(r with { State = ReservationState.Released });
        return ReconcileOutcome.Released;
    }

    /// <summary>One pass: every reservation still Reserved after <paramref name="minAge"/> (a launch in flight is not touched).</summary>
    public async Task<IReadOnlyList<ReconcileResult>> Run(TimeSpan minAge, CancellationToken ct = default)
    {
        var done = new List<ReconcileResult>();
        foreach (var r in store.Reserved().Where(r => utcNow() - r.At >= minAge))
            done.Add(new(r.Item, r.Phase, await Settle(r, ct)));
        return done;
    }
}

/// <summary>The command port of the Copilot runtime: StartRuntimeSession for "copilot-cloud" runs under a reservation, everything else goes to the inner port.</summary>
public sealed class CopilotLaunchPort : ICommandPort
{
    public const string Runtime = "copilot-cloud";
    readonly ICommandPort inner; readonly ICopilotReservations store; readonly CopilotReconciler reconciler; readonly Func<DateTime> utcNow; readonly TimeSpan minAge;

    /// <param name="minAge">A reservation younger than this is a launch in flight: it is not settled inline (the reconciler does that once it is older).</param>
    public CopilotLaunchPort(ICommandPort inner, ICopilotReservations store, CopilotReconciler reconciler, Func<DateTime>? utcNow = null, TimeSpan? minAge = null)
    { this.inner = inner; this.store = store; this.reconciler = reconciler; this.utcNow = utcNow ?? (() => DateTime.UtcNow); this.minAge = minAge ?? TimeSpan.FromMinutes(2); }

    public async Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default)
    {
        if (command is not StartRuntimeSession s || s.Runtime != Runtime) return await inner.SendAsync(command, ct);
        var cur = store.Get(s.Item, s.Phase);
        if (cur is { State: ReservationState.Reserved } && utcNow() - cur.At >= minAge) { await reconciler.Settle(cur, ct); cur = store.Get(s.Item, s.Phase); }
        // a replay of the launch that already happened answers what it answered
        if (cur is { State: ReservationState.Launched } && cur.Worker == s.WorkerName) return CommandResult.Success(cur.TaskId, cur.Url);
        // another launch of this item and phase has an outcome nobody knows yet: a second task could be the result, so wait for the reconciler
        if (cur is { State: ReservationState.Reserved }) return CommandResult.Failed(FailureKind.Transient, $"copilot launch of #{s.Item} {s.Phase} is reserved by {cur.Worker}; its outcome is not known yet");
        var mine = new CopilotReservation(s.Item, s.Phase, s.WorkerName, ReservationState.Reserved, utcNow());
        if (!store.TryReserve(mine)) return CommandResult.Failed(FailureKind.Transient, $"copilot launch of #{s.Item} {s.Phase} is reserved by another command");
        var r = await inner.SendAsync(command, ct);   // a throw leaves the reservation: the outcome is unknown
        if (r.Ok) { store.Put(mine with { State = ReservationState.Launched, TaskId = r.Execution, Url = r.Url }); return r; }
        if (!Unknown(r)) store.Put(mine with { State = ReservationState.Released });
        return r;
    }

    // Provision answers "launch-<status>" for a refusal (no task) and "launch-<ExceptionName>" or "launch-no-id" for a call that may have created the task
    static bool Unknown(CommandResult r) => r.Error == "launch-no-id" || (r.Error.StartsWith("launch-", StringComparison.Ordinal) && !int.TryParse(r.Error["launch-".Length..], out _));
}
