using Fworker.Contracts.Ids;
using Fworker.Domain;

namespace Fworker.Workflows.Capacity;

// W7.1: the CapacityScheduler, one per factory (docs/design/wolverine-factory.md 3.2, delivery-speed.md 3 and 4). This is its state machine as a pure function, like WorkItemSaga: Apply takes the ledger (or none), one
// input and the time, and answers the new ledger, the events to publish and the timer to ask for. Nothing here reads or writes anything; CapacityService loads and saves the ledger with a revision and the
// Wolverine handlers publish the result, so the whole machine is tested without a database.
//
//  - Every input is followed by a plan pass (the "slot freed" and "item became ready" triggers of delivery-speed.md 3.3): the waiting requests are ranked (CapacityRanking), then each one goes through the admission
//    chain (D1's AdmissionPass: file-overlap hold, runtime off, toolchain, runtime capacity and day budget, generated last) and, for a runtime with hosts, the host choice (D7's HostChoiceRule: guaranteed slots before
//    burst slots, most headroom first, a host that answered host-full blocked). An item that passes holds a slot; one that does not waits, and its waiting code is published when it changes (Held).
//  - The ledger is the v2 engine's: a slot is held from the grant to SessionEnded (the session's own end signal; a verdict does not free it), to a start refused, or to its time limit (a lost end signal must not hold
//    capacity for ever). v1's plant is still the ledger of the sessions it offers (the hybrid composition): what runs outside the ledger is passed in (RuntimeOutside, and the host reports' Outside), so the limits
//    of a runtime are shared and neither engine oversubscribes it.
//  - host-full is a wait, not a failure (delivery-speed.md 4.2): the slot is freed, the day's execution is not used, only that host is blocked for hostFullSeconds and the request waits again.
//  - The file-overlap rule (D6) compares an item that is entering Development with the items in flight (granted a slot for a phase past Technical Design, not yet cleared). A holder is never held: that is the whole
//    no-deadlock argument. The hold keeps its Since and Until while the item waits.
//  - Business keys and redelivery work as in the WorkItemSaga: a key applied before does nothing; an input whose key is that of the last applied one publishes what that transition published again.

/// <summary>
/// The v1 plant's executions of a runtime that the ledger does not hold: running now and started today. The gateway supplies them (RuntimeManager); a test passes its own. <paramref name="Blocked"/> is a reason the
/// runtime cannot start anything now that is not a shortage of slots (its executions are not counted yet after a restart, it is unhealthy, its quota is spent): the waiting code of the request.
/// </summary>
public sealed record RuntimeOutside(int Running, int ExecutionsToday, string? Blocked = null);

public enum CapacityOutcome
{
    /// <summary>The input changed the ledger, or was a legal no-op of it.</summary>
    Applied,
    /// <summary>The input names nothing the ledger holds (an item with no request, no slot, not in flight): ends quietly.</summary>
    NotFound,
    /// <summary>The input names a slot that is not the item's current one, or a report older than the last: ignored.</summary>
    Stale,
    /// <summary>The business key was applied before.</summary>
    Duplicate,
    /// <summary>The input is valid but changes nothing (a request of an item that already holds a slot: it is answered with that slot again).</summary>
    Refused,
}

/// <summary>What one input made the scheduler do: the answer, the events to publish and the time its next timer is due (null: none new).</summary>
public sealed record CapacityResult(CapacityOutcome Outcome, IReadOnlyList<object> Messages, DateTimeOffset? WakeAt, string Reason = "")
{
    public static readonly CapacityResult None = new(CapacityOutcome.Applied, [], null);
}

/// <summary>What Apply made of an input: the state to save (null when nothing changed), whether it changed, and the result.</summary>
public sealed record CapacityTransition(CapacityState? State, bool Changed, CapacityResult Result);

/// <summary>
/// The ranking of waiting requests (delivery-speed.md 3.1): pinned, then a resume of an owned item, then v1's own order (<see cref="PlanRankComparer"/>: priority label, then past intake before intake), then the
/// deeper column, then the older request, then the item (ordinal), so the order is the same whatever the order the ledger lists them in. Generated items are not ranked here: the plan takes them after every real item.
/// </summary>
public sealed class CapacityRanking : IComparer<WaitingRequest>
{
    public static readonly CapacityRanking Instance = new();

    public int Compare(WaitingRequest? a, WaitingRequest? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        if (a.Rank.Pinned != b.Rank.Pinned) return a.Rank.Pinned ? -1 : 1;
        if (a.Resume != b.Resume) return a.Resume ? -1 : 1;
        // pinned is decided above; the comparer then orders priority and intake
        var rank = PlanRankComparer.Instance.Compare(a.Rank with { Pinned = false }, b.Rank with { Pinned = false });
        if (rank != 0) return rank;
        if (a.ColumnDepth != b.ColumnDepth) return b.ColumnDepth.CompareTo(a.ColumnDepth);
        var age = a.RequestedAt.CompareTo(b.RequestedAt);
        return age != 0 ? age : string.CompareOrdinal(a.Item, b.Item);
    }

    public static IReadOnlyList<WaitingRequest> Order(IEnumerable<WaitingRequest> requests) => requests.OrderBy(r => r, Instance).ToList();
}

public sealed class CapacityScheduler(CapacitySettings settings, Func<string, RuntimeOutside>? outside = null)
{
    readonly Func<string, RuntimeOutside> outside = outside ?? (_ => new RuntimeOutside(0, 0));

    public CapacitySettings Settings => settings;

    static string ReplayKeyOf(CapacityInput input) => input switch
    {
        CapacityWake w => $"wake|{w.Due.UtcTicks}",
        _ => input.Key,
    };

    public CapacityTransition Apply(CapacityState? existing, CapacityInput input, DateTimeOffset now)
    {
        var replay = ReplayKeyOf(input);
        if (existing is not null && replay.Length > 0 && replay == existing.LastKey) return Reemit(existing);
        if (existing is not null && input.Key.Length > 0 && existing.Keys.Contains(input.Key)) return Nothing(CapacityOutcome.Duplicate, "the key was applied");

        // a copy: whatever a refused or stale input touched is dropped with it
        var state = (existing ?? new CapacityState()).Copy();
        var before = (existing ?? new CapacityState()).ToJson();
        var notes = new List<EmittedNote>();
        RollDay(state, now);
        Expire(state, now, notes);

        var answer = Route(state, input, now, notes);
        // an input that was refused or found nothing changes nothing; but a day that rolled or a slot that ran out while it was looked at is kept
        if (answer.Outcome is not CapacityOutcome.Applied && state.ToJson() == before)
            return new CapacityTransition(null, false, new CapacityResult(answer.Outcome, notes.Select(n => n.ToMessage()).ToList(), null, answer.Reason));
        Plan(state, now, notes);
        var wake = Reschedule(state, now);
        var changed = state.ToJson() != before || notes.Count > 0 || wake is not null;
        if (!changed) return new CapacityTransition(null, false, new CapacityResult(answer.Outcome, [], null, answer.Reason));

        if (input.Key.Length > 0) { state.Keys.Add(input.Key); if (state.Keys.Count > settings.KeepKeys) state.Keys.RemoveRange(0, state.Keys.Count - settings.KeepKeys); }
        state.LastKey = replay; state.Emitted = notes;
        return new CapacityTransition(state, true, new CapacityResult(answer.Outcome, notes.Select(n => n.ToMessage()).ToList(), wake, answer.Reason));
    }

    static CapacityTransition Nothing(CapacityOutcome outcome, string reason) => new(null, false, new CapacityResult(outcome, [], null, reason));

    /// <summary>A redelivery of the last applied input (the handler died between the save and the publish): what that transition published, and its timer, again.</summary>
    static CapacityTransition Reemit(CapacityState state)
        => new(null, false, new CapacityResult(CapacityOutcome.Applied, state.Emitted.Select(n => n.ToMessage()).ToList(), state.WakeDue));

    // ---- the inputs -------------------------------------------------------------------------------------------------------------------------

    sealed record Answer(CapacityOutcome Outcome, string Reason = "")
    {
        public static readonly Answer Applied = new(CapacityOutcome.Applied);
    }

    Answer Route(CapacityState state, CapacityInput input, DateTimeOffset now, List<EmittedNote> notes)
    {
        switch (input)
        {
            case RequestSlot r: return Request(state, r, now, notes);
            case ClearItem c: return Clear(state, c);
            case SessionEnded e: return Ended(state, e);
            case StartRefused s: return Refused(state, s, now);
            case HostReported h: return Report(state, h, now);
            case FootprintChanged f: return Footprints(state, f);
            case ReapStale r: return Reap(state, r);
            case CapacityWake w:
                if (w.Due == state.WakeDue) state.WakeDue = null;   // (a timer that is not the pending one only plans again)
                return Answer.Applied;
            default: return new Answer(CapacityOutcome.Refused, "no handler takes the input");
        }
    }

    Answer Request(CapacityState state, RequestSlot r, DateTimeOffset now, List<EmittedNote> notes)
    {
        var item = r.Item.ToString();
        if (r.Runtime.Trim().Length == 0) return new Answer(CapacityOutcome.Refused, "the request names no runtime");
        if (state.Slots.TryGetValue(item, out var held))
        {
            // a reopened item (another generation) asks again: the slot of the earlier instance is none of the new one's, and its session is the host report's to count until it ends
            if (r.Generation != held.Request.Generation && r.Generation != 0 && held.Request.Generation != 0) state.Slots.Remove(item);
            else
            {
                // W7.3: the holder of a Technical Design slot that asks for a later phase is entering Development in the same session; the file-overlap rule decides it here, in the invoke (delivery-speed.md 7)
                if (CarryOn(state, held, r, now)) return Answer.Applied;
                notes.Add(Granted(held, now));
                return new Answer(CapacityOutcome.Refused, $"the item holds slot {held.SlotId}");
            }
        }
        var request = WaitingRequest.Of(r);
        if (state.Queue.TryGetValue(item, out var earlier))
        {
            request.Announced = earlier.Announced;
            if (earlier.RequestedAt < request.RequestedAt) request.RequestedAt = earlier.RequestedAt;   // the wait began at the first request
        }
        state.Queue[item] = request;
        return Answer.Applied;
    }

    /// <summary>
    /// The item holds the slot of a Technical Design session and asks for a later phase (an owner session goes from Technical Design to Development without a plan pass, so nothing else would compare it with the items in
    /// flight). If the file-overlap rule holds it, the slot is given up (the session ends; its execution of the day is given back, the next grant counts again) and the request waits as any request does, its hold published
    /// with the next plan pass; true. Otherwise the same session goes on and the item becomes a holder of the rule from now on (it is in flight); false. An item that holds a later phase's slot already is never asked again: a holder is never held.
    /// </summary>
    bool CarryOn(CapacityState state, LedgerSlot held, RequestSlot r, DateTimeOffset now)
    {
        var item = r.Item.ToString();
        if (string.Equals(r.Phase, CapacityPhases.Design, StringComparison.OrdinalIgnoreCase) || held.Request.PastDesign || state.InFlight.ContainsKey(item)) return false;
        var peers = state.InFlight.Values.Select(f => (Id: ItemId.Parse(f.Item), f.Footprint)).Where(f => f.Id.Repository == r.Item.Repository).Select(f => new OverlapPeer(f.Id.Number, f.Footprint)).ToList();
        var existing = state.Holds.TryGetValue(item, out var h) ? new OverlapHold(h.By, h.Since, h.Until) : null;
        var decision = FileOverlapRule.Check(r.Item.Number, r.Footprint, peers, settings.Overlap, now, existing);
        if (decision.Held)
        {
            state.Slots.Remove(item);
            if (state.ExecutionsToday.TryGetValue(held.Runtime, out var n)) state.ExecutionsToday[held.Runtime] = Math.Max(0, n - 1);
            state.Queue[item] = WaitingRequest.Of(r);
            state.Holds[item] = new HoldRecord { By = decision.Hold!.By, Since = decision.Hold.Since, Until = decision.Hold.Until };
            return true;
        }
        held.Request.Phase = r.Phase;
        if (!r.Footprint.IsEmpty) held.Request.Footprint = r.Footprint;
        state.InFlight[item] = new InFlightItem { Item = item, Footprint = held.Request.Footprint, Since = now, Generation = held.Request.Generation };
        held.EnteredFlight = true;
        return false;
    }

    /// <summary>
    /// W9.10n: the instance (generation) the ledger holds entries of the item for: the waiting request's, else the slot's, else the one in flight; 0 when it holds none or they name none. A command of another instance
    /// (an item that was re-adopted after the kill switch, or reopened, asked again as a new generation) is no business of these entries.
    /// </summary>
    public static int GenerationOf(CapacityState state, string item)
    {
        if (state.Queue.TryGetValue(item, out var q) && q.Generation != 0) return q.Generation;
        if (state.Slots.TryGetValue(item, out var s) && s.Request.Generation != 0) return s.Request.Generation;
        if (state.InFlight.TryGetValue(item, out var f) && f.Generation != 0) return f.Generation;
        return 0;
    }

    static bool OtherInstance(CapacityState state, string item, int generation) => generation != 0 && GenerationOf(state, item) is var held && held != 0 && held != generation;

    static Answer Clear(CapacityState state, ClearItem c)
    {
        var item = c.Item.ToString();
        if (OtherInstance(state, item, c.Generation)) return new Answer(CapacityOutcome.Stale, $"the ledger holds the item's generation {GenerationOf(state, item)}, not {c.Generation}");
        var any = state.Queue.Remove(item);
        any |= state.InFlight.Remove(item);
        any |= state.Holds.Remove(item);
        return any ? Answer.Applied : new Answer(CapacityOutcome.NotFound, "the scheduler holds nothing of the item");
    }

    Answer Ended(CapacityState state, SessionEnded e)
    {
        var item = e.Item.ToString();
        if (!state.Slots.TryGetValue(item, out var slot)) return new Answer(CapacityOutcome.NotFound, "the item holds no slot");
        if (e.SlotId.Length > 0 && e.SlotId != slot.SlotId) return new Answer(CapacityOutcome.Stale, $"the slot is {slot.SlotId}");
        state.Slots.Remove(item);
        if (e.MemPeakMb is > 0 and var peak && slot.Host is { } host && state.Hosts.TryGetValue(host, out var h))
        {
            h.Samples.Add(peak);
            if (h.Samples.Count > settings.MaxFootprintSamples) h.Samples.RemoveRange(0, h.Samples.Count - settings.MaxFootprintSamples);
        }
        return Answer.Applied;
    }

    Answer Refused(CapacityState state, StartRefused s, DateTimeOffset now)
    {
        var item = s.Item.ToString();
        if (!state.Slots.TryGetValue(item, out var slot)) return new Answer(CapacityOutcome.NotFound, "the item holds no slot");
        if (s.SlotId.Length > 0 && s.SlotId != slot.SlotId) return new Answer(CapacityOutcome.Stale, $"the slot is {slot.SlotId}");
        state.Slots.Remove(item);
        if (s.Outcome != AdmissionCodes.HostFull) return Answer.Applied;   // a real refusal: the slot is free, the request is dropped, the item's saga counts the failure and decides

        // host-full: a wait. The execution was never started, only that host is blocked, and the item asks again as it was (unless it has asked again in the meantime)
        if (state.ExecutionsToday.TryGetValue(slot.Runtime, out var n)) state.ExecutionsToday[slot.Runtime] = Math.Max(0, n - 1);
        if (slot.Host is { } host && state.Hosts.TryGetValue(host, out var h)) h.HostFullAt = now;
        if (slot.EnteredFlight) state.InFlight.Remove(item);
        if (!state.Queue.ContainsKey(item)) { slot.Request.Announced = ""; state.Queue[item] = slot.Request; }
        return Answer.Applied;
    }

    static Answer Report(CapacityState state, HostReported r, DateTimeOffset now)
    {
        if (r.Host.Trim().Length == 0) return new Answer(CapacityOutcome.Refused, "the report names no host");
        var at = r.At > now ? now : r.At;   // (never ahead of the scheduler's clock)
        if (!state.Hosts.TryGetValue(r.Host, out var h)) state.Hosts[r.Host] = h = new LedgerHost { Name = r.Host };
        else if (at < h.ReportedAt) return new Answer(CapacityOutcome.Stale, "an older report than the last");
        h.Runtime = r.Runtime; h.Metrics = r.Metrics; h.ReportedAt = at;
        h.Outside = Math.Max(0, r.Running - state.SlotsOn(r.Host));
        return Answer.Applied;
    }

    /// <summary>
    /// W9.10j: the entries of the ledger that no saga has in flight any more (see <see cref="ReapStale"/>). A slot is never reaped here: it ends with the session (SessionEnded), a refused start, or its time limit (Expire), and the
    /// item's entries go with the pass after that. Plan runs after every input, so a waiting item the reaped holder was holding back is admitted in the same transition.
    /// </summary>
    static Answer Reap(CapacityState state, ReapStale r)
    {
        var live = r.Live.ToHashSet(StringComparer.Ordinal);
        var cutoff = r.ReadAt - r.Grace;
        var reaped = 0;
        foreach (var key in state.InFlight.Where(kv => !live.Contains(kv.Key) && !state.Slots.ContainsKey(kv.Key) && kv.Value.Since <= cutoff).Select(kv => kv.Key).ToList()) { state.InFlight.Remove(key); state.Holds.Remove(key); reaped++; }
        foreach (var key in state.Queue.Where(kv => !live.Contains(kv.Key) && !state.Slots.ContainsKey(kv.Key) && kv.Value.RequestedAt <= cutoff).Select(kv => kv.Key).ToList()) { state.Queue.Remove(key); state.Holds.Remove(key); reaped++; }
        return reaped > 0 ? Answer.Applied : new Answer(CapacityOutcome.NotFound, "the ledger holds nothing that no saga has in flight");
    }

    static Answer Footprints(CapacityState state, FootprintChanged f)
    {
        if (!state.InFlight.TryGetValue(f.Item.ToString(), out var flight)) return new Answer(CapacityOutcome.NotFound, "the item is not in flight");
        if (OtherInstance(state, f.Item.ToString(), f.Generation)) return new Answer(CapacityOutcome.Stale, $"the ledger holds the item's generation {GenerationOf(state, f.Item.ToString())}, not {f.Generation}");
        flight.Footprint = Footprint.Resolve(flight.Footprint, f.Files);
        return Answer.Applied;
    }

    // ---- time -------------------------------------------------------------------------------------------------------------------------------

    static void RollDay(CapacityState state, DateTimeOffset now)
    {
        var day = now.UtcDateTime.ToString("yyyy-MM-dd");
        if (state.Day == day) return;
        state.Day = day; state.ExecutionsToday.Clear();
    }

    /// <summary>Frees the slots whose time ran out (the end signal was lost): the item's saga is told, and treats the session as lost. The day's execution stays used.</summary>
    static void Expire(CapacityState state, DateTimeOffset now, List<EmittedNote> notes)
    {
        foreach (var slot in state.Slots.Values.Where(s => s.ExpiresAt <= now).OrderBy(s => s.ExpiresAt).ThenBy(s => s.Item, StringComparer.Ordinal).ToList())
        {
            state.Slots.Remove(slot.Item);
            notes.Add(new EmittedNote { Kind = "expired", Item = slot.Item, Generation = slot.Request.Generation, SlotId = slot.SlotId, Runtime = slot.Runtime, Host = slot.Host, At = now });
        }
    }

    /// <summary>
    /// The next time the ledger needs looking at without an input: a slot's time limit, a hold's end, the end of a host's block, and a safety look while anything waits. Returns the new timer to schedule, or null when the
    /// pending one (or none) is enough: a timer is asked for only when it is earlier than the pending one.
    /// </summary>
    DateTimeOffset? Reschedule(CapacityState state, DateTimeOffset now)
    {
        var next = NextWake(state, now);
        if (next is null) { state.WakeDue = null; return null; }
        if (state.WakeDue is { } pending && pending > now && pending <= next) return null;
        state.WakeDue = next;
        return next;
    }

    DateTimeOffset? NextWake(CapacityState state, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset t) { if (t > now && (next is null || t < next)) next = t; }
        foreach (var s in state.Slots.Values) Consider(s.ExpiresAt);
        if (state.Queue.Count > 0)
        {
            foreach (var key in state.Queue.Keys) if (state.Holds.TryGetValue(key, out var hold)) Consider(hold.Until);
            foreach (var h in state.Hosts.Values) if (h.HostFullAt is { } at) Consider(at.AddSeconds(settings.Headroom.HostFullSeconds));
            Consider(now + settings.SafetyWake);
        }
        return next;
    }

    // ---- the plan pass ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The ledger's view of how many sessions run on a host: the slots it granted there plus the containers of the last report that were none of them.</summary>
    static int Running(CapacityState state, LedgerHost host) => state.SlotsOn(host.Name) + host.Outside;

    RuntimeLoad LoadOf(CapacityState state, SchedulerRuntime limits, RuntimeOutside o)
    {
        var running = limits.HasHosts
            ? state.Hosts.Values.Where(h => h.Runtime == limits.Kind).Sum(h => Running(state, h)) + state.Slots.Values.Count(s => s.Runtime == limits.Kind && s.Host is null)
            : state.SlotsOf(limits.Kind) + o.Running;
        return new RuntimeLoad(running, limits.MaxConcurrent, state.ExecutionsToday.GetValueOrDefault(limits.Kind) + o.ExecutionsToday, limits.MaxExecutionsPerDay);
    }

    /// <summary>The file-overlap decision of every item entering Development (a request for a phase past Technical Design that is not in flight yet), by item, in release order per repository (D6's ranked release).</summary>
    Dictionary<string, OverlapDecision> Overlaps(CapacityState state, DateTimeOffset now)
    {
        var decisions = new Dictionary<string, OverlapDecision>(StringComparer.Ordinal);
        if (!settings.Overlap.Enabled) { state.Holds.Clear(); return decisions; }
        var entering = state.Queue.Values.Where(r => r.PastDesign && !state.InFlight.ContainsKey(r.Item)).GroupBy(r => r.ItemId.Repository);
        foreach (var repo in entering)
        {
            var peers = state.InFlight.Values.Select(f => (Id: ItemId.Parse(f.Item), f.Footprint)).Where(f => f.Id.Repository == repo.Key).Select(f => new OverlapPeer(f.Id.Number, f.Footprint)).ToList();
            var waiters = repo.Select(r => new OverlapWaiter(r.ItemId.Number, r.Footprint, r.Rank, r.RequestedAt, state.Holds.TryGetValue(r.Item, out var h) ? new OverlapHold(h.By, h.Since, h.Until) : null)).ToList();
            foreach (var release in FileOverlapRule.Release(waiters, peers, settings.Overlap, now))
            {
                var key = new ItemId(repo.Key, release.Number).ToString();
                decisions[key] = release.Decision;
                if (release.Decision.Hold is { } hold) state.Holds[key] = new HoldRecord { By = hold.By, Since = hold.Since, Until = hold.Until };
            }
        }
        // a hold of an item that no longer waits is gone with its request (ClearItem, a grant); one whose rule turned out not to apply keeps its record until then: the limit is on the whole wait
        foreach (var key in state.Holds.Keys.Where(k => !state.Queue.ContainsKey(k)).ToList()) state.Holds.Remove(key);
        return decisions;
    }

    void Plan(CapacityState state, DateTimeOffset now, List<EmittedNote> notes)
    {
        var overlaps = Overlaps(state, now);
        // what the plant runs outside the ledger is read once per pass: a read that fails is no answer (the exception ends the input, and Wolverine runs it again)
        var outsideNow = settings.Runtimes.Keys.ToDictionary(k => k, k => outside(k), StringComparer.Ordinal);
        var waitingForSlot = 0;
        var ordered = CapacityRanking.Order(state.Queue.Values.Where(r => !r.Generated)).Concat(CapacityRanking.Order(state.Queue.Values.Where(r => r.Generated)));
        foreach (var request in ordered.ToList())
        {
            var (decision, host) = Decide(state, request, overlaps, outsideNow, waitingForSlot, now);
            if (decision.Admitted) { Grant(state, request, host, now, notes); continue; }
            if (!request.Generated && decision.Code is AdmissionCodes.RuntimeAtCapacity or AdmissionCodes.RuntimeBudget or AdmissionCodes.HostFull or CapacityCodes.HostHeadroom) waitingForSlot++;
            Wait(request, decision, overlaps, now, notes);
        }
    }

    (AdmissionDecision Decision, HostChoice? Host) Decide(CapacityState state, WaitingRequest request, Dictionary<string, OverlapDecision> overlaps, Dictionary<string, RuntimeOutside> outsideNow, int waitingForSlot, DateTimeOffset now)
    {
        FileOverlapHold? hold = overlaps.TryGetValue(request.Item, out var d) && d.Held ? new FileOverlapHold(new ItemId(request.ItemId.Repository, d.Hold!.By), d.Hold.Since) : null;
        var runtimes = new Dictionary<string, AdmissionTarget>(StringComparer.Ordinal);
        foreach (var limits in settings.Runtimes.Values)
            runtimes[limits.Kind] = new AdmissionTarget(LoadOf(state, limits, outsideNow[limits.Kind]), limits.Toolchains ?? request.RequiredToolchains);   // (no toolchains configured: the runtime offers what the repository needs)
        var facts = new AdmissionFacts(now, runtimes);
        var check = AdmissionPass.Check(new AdmissionRequest(request.ItemId, request.Runtime, request.Generated, request.RequiredToolchains, null, hold), facts, settings.Admission, waitingForSlot);
        if (!check.Admitted) return (check, null);
        // the plant's manager can hold a runtime back for a reason that is no shortage of slots (not counted yet after a restart, unhealthy, quota spent)
        if (outsideNow[request.Runtime].Blocked is { Length: > 0 } blocked) return (AdmissionDecision.Block(blocked), null);

        var limit = settings.Runtimes[request.Runtime];
        if (!limit.HasHosts) return (check, null);
        // a host that has not reported for three times the age of a usable report is taken for gone: it is offered nothing (the ledger still counts the slots it holds)
        var hosts = state.Hosts.Values.Where(h => h.Runtime == limit.Kind && now - h.ReportedAt <= settings.HostGone).OrderBy(h => h.Name, StringComparer.Ordinal)
            .Select(h => new HostState(h.Name, limit.PerHostCapacity,
                // a report that is too old says nothing of the headroom: the host gives its guaranteed slots only
                now - h.ReportedAt > settings.HostReportMax ? limit.PerHostCapacity : limit.PerHostBurst,
                Running(state, h), h.Metrics, h.Samples, h.HostFullAt)).ToList();
        var choice = HostChoiceRule.Pick(hosts, settings.ContainerCpus, now, settings.Headroom);
        if (choice is not null) return (check, choice);
        if (hosts.Count == 0) return (AdmissionDecision.Block(CapacityCodes.HostHeadroom, "no host reported"), null);
        return (hosts.All(h => HostChoiceRule.IsBlocked(h, now, settings.Headroom)) ? AdmissionDecision.Block(AdmissionCodes.HostFull) : AdmissionDecision.Block(CapacityCodes.HostHeadroom), null);
    }

    void Grant(CapacityState state, WaitingRequest request, HostChoice? host, DateTimeOffset now, List<EmittedNote> notes)
    {
        var slot = new LedgerSlot
        {
            SlotId = $"s{++state.SlotSeq}", Item = request.Item, Runtime = request.Runtime, Host = host?.Name, Kind = host?.Slot ?? SlotKind.Guaranteed,
            GrantedAt = now, ExpiresAt = now + settings.SlotMax, Request = request,
        };
        state.Slots[request.Item] = slot;
        state.Queue.Remove(request.Item);
        state.Holds.Remove(request.Item);
        state.ExecutionsToday[request.Runtime] = state.ExecutionsToday.GetValueOrDefault(request.Runtime) + 1;
        // past Technical Design the item is a holder of the file-overlap rule until it is cleared (merged, parked, closed)
        if (request.PastDesign)
        {
            if (state.InFlight.TryGetValue(request.Item, out var flight)) { if (!request.Footprint.IsEmpty) flight.Footprint = request.Footprint; }
            else { state.InFlight[request.Item] = new InFlightItem { Item = request.Item, Footprint = request.Footprint, Since = now, Generation = request.Generation }; slot.EnteredFlight = true; }
        }
        notes.Add(Granted(slot, now));
    }

    static EmittedNote Granted(LedgerSlot slot, DateTimeOffset now)
        => new() { Kind = "granted", Item = slot.Item, Generation = slot.Request.Generation, SlotId = slot.SlotId, Runtime = slot.Runtime, Host = slot.Host, SlotKind = SlotKinds.Name(slot.Kind), Phase = slot.Request.Phase, At = now, ExpiresAt = slot.ExpiresAt };

    static void Wait(WaitingRequest request, AdmissionDecision decision, Dictionary<string, OverlapDecision> overlaps, DateTimeOffset now, List<EmittedNote> notes)
    {
        var text = decision.Waiting;
        if (text == request.Announced) return;
        request.Announced = text;
        string? by = null;
        if (decision.Code == AdmissionCodes.FileOverlap && overlaps.TryGetValue(request.Item, out var d) && d.Hold is { } hold) by = new ItemId(request.ItemId.Repository, hold.By).ToString();
        notes.Add(new EmittedNote { Kind = "held", Item = request.Item, Generation = request.Generation, Code = decision.Code, Detail = decision.Detail, By = by, At = now });
    }
}
