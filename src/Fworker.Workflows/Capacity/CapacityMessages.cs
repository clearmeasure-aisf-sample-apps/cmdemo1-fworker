using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;

namespace Fworker.Workflows.Capacity;

// W7.1: the messages of the CapacityScheduler (docs/design/wolverine-factory.md 3.2). The scheduler is one per factory: everything that reaches it is a CapacityInput, handled one at a time in the order the queue
// delivers (CapacityTopology), and what it says leaves as events: SlotGranted, Held (the waiting code of an item, published when it changes) and SlotExpired. The item's saga sends RequestSlot with its rank facts,
// its footprint and what its repository needs, and ClearItem (the design's "Clear") when it no longer wants a slot or is no longer in flight (merged, parked, closed). The session's end (SessionEnded, confirmed by
// the host report) frees the slot; a verdict does not. The inputs that change the ledger carry a business key and delivery is at least once, so a repeated key does nothing. These messages are not item messages
// (IItemMessage): the scheduler's queue is one sequential queue, not partitioned by item.

/// <summary>The id of the scheduler's ledger: one per factory.</summary>
public static class CapacityKeys
{
    public const string Factory = "factory";
}

/// <summary>The phases a slot is asked for. Only the phase decides whether the item is past Technical Design (a holder of the file-overlap rule): a request for any other phase makes it one once granted.</summary>
public static class CapacityPhases
{
    public const string Design = "design";
    public const string Develop = "develop";
    public const string Verify = "verify";
}

/// <summary>The waiting codes the scheduler adds to D1's (<see cref="AdmissionCodes"/>).</summary>
public static class CapacityCodes
{
    /// <summary>No host of the runtime has a free slot with headroom (delivery-speed.md 3.2 rule 4).</summary>
    public const string HostHeadroom = "host-headroom";
}

/// <summary>Any input of the scheduler. <paramref name="Key"/> is the business key ("" for an input that has none).</summary>
public abstract record CapacityInput(string Key) : IMessage;

/// <summary>
/// The item asks for a slot of <paramref name="Runtime"/> for a session of <paramref name="Phase"/>. A request replaces the item's earlier one that still waits (the rank facts and the footprint are fresh); a request of
/// an item that already holds a slot is answered with that slot again. <paramref name="Rank"/> is the plan's rank (pinned, priority, intake), <paramref name="Resume"/> an owned item whose session was lost or whose
/// hold ended (rank 2 of delivery-speed.md 3.1), <paramref name="ColumnDepth"/> how far the item is (Functional Testing 3, Development 2, Technical Design 1: deeper first) and <paramref name="RequestedAt"/> its age
/// (older first). <paramref name="Footprint"/> is the item's footprint in force (Technical Design's Files line, or the pull request's changed files).
/// </summary>
[MessageIdentity("capacity.request-slot")]
public sealed record RequestSlot(ItemId Item, string Key, int Generation, string Runtime, string Phase, PlanRank Rank, bool Generated, bool Resume, int ColumnDepth, DateTimeOffset RequestedAt,
    IReadOnlyList<string> RequiredToolchains, Footprint Footprint) : CapacityInput(Key);

/// <summary>The item no longer wants a slot or is no longer in flight (merged, parked, closed, left the range): its request, its footprint and its file-overlap hold go. A slot it still holds stays until the session ends.</summary>
[MessageIdentity("capacity.clear-item")]
public sealed record ClearItem(ItemId Item, string Key, string Reason) : CapacityInput(Key);

/// <summary>
/// The session of the item's slot ended (the session's own end signal, confirmed by the host report): the slot is free at once. <paramref name="SlotId"/> names the slot it ended (empty: the item's current one).
/// <paramref name="MemPeakMb"/> is the peak memory of the session's container, a sample of the host's footprint (D7), when the host reported it.
/// </summary>
[MessageIdentity("capacity.session-ended")]
public sealed record SessionEnded(ItemId Item, string Key, string SlotId, int? MemPeakMb = null) : CapacityInput(Key);

/// <summary>
/// The start of the slot's session was refused: <paramref name="Outcome"/> is the host's answer. <c>host-full</c> (<see cref="AdmissionCodes.HostFull"/>) is a wait, not a failure: the slot is freed, the day's execution
/// is not used, that host is blocked for hostFullSeconds and the request waits again. Any other outcome frees the slot and drops the request: the item's saga decides what to do (it counts the failure).
/// </summary>
[MessageIdentity("capacity.start-refused")]
public sealed record StartRefused(ItemId Item, string Key, string SlotId, string Outcome) : CapacityInput(Key);

/// <summary>What a host report says about a host (reconcile-hosts.sh): the containers running on it (every <c>fw-*</c>, an exiting one included) and its metrics.</summary>
[MessageIdentity("capacity.host-reported")]
public sealed record HostReported(string Key, string Host, string Runtime, int Running, HostMetrics Metrics, DateTimeOffset At) : CapacityInput(Key);

/// <summary>The pull request's changed files replace the footprint of an item in flight (D6: what was pushed is what the item touches).</summary>
[MessageIdentity("capacity.footprint-changed")]
public sealed record FootprintChanged(ItemId Item, string Key, IReadOnlyList<string> Files) : CapacityInput(Key);

/// <summary>The scheduler's own timer: <paramref name="Due"/> is the time it was scheduled for. It frees the slots whose time ran out, ends the holds and the host blocks that ran out, and plans again.</summary>
[MessageIdentity("capacity.wake")]
public sealed record CapacityWake(DateTimeOffset Due) : CapacityInput("");

/// <summary>The slot kinds a grant can give, as text for the wire (<see cref="SlotKind"/>).</summary>
public static class SlotKinds
{
    public static string Name(SlotKind k) => k switch { SlotKind.Guaranteed => "guaranteed", SlotKind.Burst => "burst", _ => "none" };
}

/// <summary>
/// The item has a slot: the item's saga starts its session on <paramref name="Runtime"/> (and <paramref name="Host"/> when the runtime has hosts) and owns the session's lease and lineage. The slot is freed by
/// <see cref="SessionEnded"/>, by a refused start, or at <paramref name="ExpiresAt"/>.
/// </summary>
[MessageIdentity("capacity.slot-granted")]
public sealed record SlotGranted(ItemId Item, int Generation, string SlotId, string Runtime, string? Host, string Kind, string Phase, DateTimeOffset At, DateTimeOffset ExpiresAt) : ItemEvent(Item);

/// <summary>
/// The item waits, and why: <paramref name="Code"/> is an admission code (<see cref="AdmissionCodes"/>, <see cref="CapacityCodes"/>), <paramref name="Detail"/> its detail (the holding item of a file overlap) and
/// <paramref name="By"/> the item that holds it, when one does. Published when the item's waiting text changes, never repeated.
/// </summary>
[MessageIdentity("capacity.held")]
public sealed record Held(ItemId Item, int Generation, string Code, string Detail, ItemId? By, DateTimeOffset At) : ItemEvent(Item)
{
    public string Waiting => Detail.Length == 0 ? Code : $"{Code} {Detail}";
}

/// <summary>A slot was freed because its time ran out with no end signal (a lost session end must not hold capacity for ever). The item's saga treats the session as lost.</summary>
[MessageIdentity("capacity.slot-expired")]
public sealed record SlotExpired(ItemId Item, int Generation, string SlotId, string Runtime, string? Host, DateTimeOffset At) : ItemEvent(Item);
