using System.Collections.Concurrent;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;

namespace Fworker.Integrations.Commands;

// W3.3: the Copilot launch with a reservation. A Copilot launch is a POST that creates an agent task: when the call times out or the gateway stops before the
// answer is read, nobody knows whether the task exists, and running the command again could start a second task for the same item and phase (one task owns an
// item). So the launch reserves (item, phase) first, then calls GitHub, then marks the reservation Launched or Released. A reservation whose outcome is
// unknown stays Reserved: it blocks another launch of that item and phase, and the reconciler settles it against GitHub (a task created since the reservation
// -> Launched, none -> Released, so the launch can be tried again). Only the Copilot runtime goes through this (CopilotLaunchPort); every other command passes.
//
// W9.10c: a resume (the @copilot comment that starts a follow-up session of the owner task) is as non-idempotent as a launch: when its call times out the comment may be on the pull
// request, and posting it again would start a second session. So a resume that fails by an exception is unknown too, not failed: the reservation stays, the start is answered "reserved"
// (the manager queues the item and counts no failure), and the reconciler reads the pull request's comments and the task's sessions for it. The reconciler also writes the owner record
// of a task it finds (the item state's copilot_task), so the next worker name resumes that task instead of posting a second one, and its settling is a conditional write, so it never
// overwrites a record the launch itself made meanwhile.

public enum ReservationState { Reserved, Launched, Released }

/// <summary>One reservation per (item, phase): the worker whose launch it is, when it was made and, once launched, the task and its page.</summary>
/// <param name="Mode">What the unknown call was: "launch" (a new task), "resume" (a follow-up comment) or "" (not known yet, or a reservation of before W9.10c).</param>
public sealed record CopilotReservation(int Item, string Phase, string Worker, ReservationState State, DateTime At, string TaskId = "", string Url = "", string Mode = "")
{
    public const string LaunchMode = "launch", ResumeMode = "resume";
}

public interface ICopilotReservations
{
    CopilotReservation? Get(int item, string phase);
    void Put(CopilotReservation reservation);
    /// <summary>W9.10c: atomically replaces <paramref name="expected"/> with <paramref name="next"/> only while the stored reservation still is exactly <paramref name="expected"/>; false when it changed.</summary>
    bool Transition(CopilotReservation expected, CopilotReservation next);
    /// <summary>Atomically takes (item, phase) unless a Reserved one is there; false then.</summary>
    bool TryReserve(CopilotReservation reservation);
    IReadOnlyList<CopilotReservation> Reserved();
}

public sealed class InMemoryCopilotReservations : ICopilotReservations
{
    readonly ConcurrentDictionary<(int, string), CopilotReservation> _all = new();
    public CopilotReservation? Get(int item, string phase) => _all.TryGetValue((item, phase), out var r) ? r : null;
    public void Put(CopilotReservation r) { lock (_all) _all[(r.Item, r.Phase)] = r; }
    public bool Transition(CopilotReservation expected, CopilotReservation next)
    {
        lock (_all)
        {
            if (!_all.TryGetValue((expected.Item, expected.Phase), out var cur) || cur != expected) return false;
            _all[(next.Item, next.Phase)] = next; return true;
        }
    }
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
    /// <summary>W9.10c: asks whether a resume was posted for the item at or after <paramref name="since"/> (a @copilot comment on its pull request, and the session it started); the task and its page when it was. Throws when GitHub cannot be asked.</summary>
    Task<(string TaskId, string Url)?> FindResumeAsync(int item, string phase, DateTime since, CancellationToken ct);
}

/// <summary>W9.10c: writes the owner record (the item state's copilot_task) of a task the reconciler found, before the reservation is settled as Launched. Throws when it cannot (the reservation then stays Reserved and the next pass tries again).</summary>
public interface ICopilotOwnerRecorder
{
    Task RecordAsync(CopilotReservation settled, CancellationToken ct);
}

/// <summary>
/// W9.10c: what <see cref="ICopilotTaskLookup.FindResumeAsync"/> throws when there is nothing to look at (the item has no owner task, or its task has no pull request), as opposed to GitHub failing. A resume
/// can only have been posted on an owner's pull request, so for a reservation of unknown mode this is an answer ("no resume"), not a failure: the first launch of an item has no owner at all.
/// </summary>
public sealed class CopilotNothingToLookAtException(string message) : InvalidOperationException(message);

public enum ReconcileOutcome { Launched, Released, Unknown }

public sealed record ReconcileResult(int Item, string Phase, ReconcileOutcome Outcome);

public sealed class CopilotReconciler
{
    readonly ICopilotReservations store; readonly ICopilotTaskLookup lookup; readonly Func<DateTime> utcNow; readonly ICopilotOwnerRecorder? owners;
    public CopilotReconciler(ICopilotReservations store, ICopilotTaskLookup lookup, Func<DateTime>? utcNow = null, ICopilotOwnerRecorder? owners = null)
    { this.store = store; this.lookup = lookup; this.utcNow = utcNow ?? (() => DateTime.UtcNow); this.owners = owners; }

    /// <summary>
    /// Settles one reservation against GitHub. The created_at of a task is GitHub's clock, so the search starts a minute before the reservation. A reservation of a resume looks for the comment, one of a launch
    /// for the task, one of unknown mode for either. A task or a session found is recorded as the item's owner first (so the next start resumes it) and only then is the reservation made Launched; both
    /// settlings are conditional on the reservation still being the one that was read.
    /// </summary>
    public async Task<ReconcileOutcome> Settle(CopilotReservation r, CancellationToken ct = default)
    {
        (string TaskId, string Url)? found; var mode = r.Mode;
        try
        {
            var since = r.At - TimeSpan.FromMinutes(1);
            if (mode == CopilotReservation.ResumeMode) found = await lookup.FindResumeAsync(r.Item, r.Phase, since, ct);
            else
            {
                found = await lookup.FindAsync(r.Item, r.Phase, since, ct);
                if (found != null) mode = CopilotReservation.LaunchMode;
                else if (mode == "" && await ResumeOrNone(r, since, ct) is { } resumed) { found = resumed; mode = CopilotReservation.ResumeMode; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return ReconcileOutcome.Unknown; }
        // the reservation may have been settled by the launch itself while GitHub was asked: do not overwrite a newer record
        if (store.Get(r.Item, r.Phase) != r) return ReconcileOutcome.Unknown;
        if (found is { } t)
        {
            var launched = r with { State = ReservationState.Launched, TaskId = t.TaskId, Url = t.Url, Mode = mode };
            if (owners != null)
            {
                try { await owners.RecordAsync(launched, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { return ReconcileOutcome.Unknown; }
            }
            return store.Transition(r, launched) ? ReconcileOutcome.Launched : ReconcileOutcome.Unknown;
        }
        return store.Transition(r, r with { State = ReservationState.Released }) ? ReconcileOutcome.Released : ReconcileOutcome.Unknown;
    }

    // the resume probe of a reservation of unknown mode: an item with no owner task or no pull request has no resume to find (a first launch), which is a miss and not a failure of GitHub
    async Task<(string TaskId, string Url)?> ResumeOrNone(CopilotReservation r, DateTime since, CancellationToken ct)
    {
        try { return await lookup.FindResumeAsync(r.Item, r.Phase, since, ct); }
        catch (CopilotNothingToLookAtException) { return null; }
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
    /// <summary>The error of a start that waits for the reconciler: RateLimited, so it is no failure of the item (RuntimeManager gives the launch back and the item is offered again).</summary>
    public const string ReservedError = "reserved";
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
        // another launch of this item and phase has an outcome nobody knows yet: a second task could be the result, so wait for the reconciler (the manager queues the item; it is no failure)
        if (cur is { State: ReservationState.Reserved }) return CommandResult.Failed(FailureKind.RateLimited, ReservedError);
        var mine = new CopilotReservation(s.Item, s.Phase, s.WorkerName, ReservationState.Reserved, utcNow());
        if (!store.TryReserve(mine)) return CommandResult.Failed(FailureKind.RateLimited, ReservedError);
        var r = await inner.SendAsync(command, ct);   // a throw leaves the reservation: the outcome is unknown
        // the execution of a resume is "<task>#<session>", the one of a launch is the task
        if (r.Ok) { store.Put(mine with { State = ReservationState.Launched, TaskId = r.Execution, Url = r.Url, Mode = r.Execution.Contains('#') ? CopilotReservation.ResumeMode : CopilotReservation.LaunchMode }); return r; }
        if (!Unknown(r)) { store.Put(mine with { State = ReservationState.Released }); return r; }
        // W9.10c: a call that may have created the task or posted the comment is unknown, not failed: the reservation stays (and says what to look for), the start answers "reserved" and the reconciler decides
        store.Put(mine with { Mode = r.Error.StartsWith("resume-", StringComparison.Ordinal) ? CopilotReservation.ResumeMode : CopilotReservation.LaunchMode });
        return CommandResult.Failed(FailureKind.RateLimited, ReservedError);
    }

    // Provision answers "launch-<status>" for a refusal (no task) and "launch-<ExceptionName>" or "launch-no-id" for a call that may have created the task; a resume answers "resume-<status>" for a
    // refusal and "resume-<ExceptionName>" for a call that may have posted the comment
    static bool Unknown(CommandResult r) => r.Error == "launch-no-id"
        || (r.Error.StartsWith("launch-", StringComparison.Ordinal) && !int.TryParse(r.Error["launch-".Length..], out _))
        || (r.Error.StartsWith("resume-", StringComparison.Ordinal) && !int.TryParse(r.Error["resume-".Length..], out _));
}
