using System.Text.Json.Nodes;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

// W9.10m: the rows beside the saga's state that the adoption writes, as pure edits of the state they belong to. The scheduler's ledger takes the slot a live owner's session holds (and the place of an item in flight); the delivery
// train's state takes the Sign-offs its deployments wait at, with the time they have waited and the items they carry. Each edit is idempotent and never takes anything away: what the ledger or the train holds already is the
// newer truth (it is v2's own, saved by the scheduler or the train), and adoption only adds what v1 knew and they did not.

public static class AdoptionLedger
{
    /// <summary>
    /// W9.10n: the ledger without what it holds for the item that belongs to another instance than <paramref name="fence"/> keeps (null: nothing of the item stays). A slot, a waiting request, a place among the items in flight and a
    /// file-overlap hold of an instance that ended or was replaced by a re-adoption (v1 acted on the item in between, so its session, its request and its footprint are no longer the item's) are removed, so that the item asks again
    /// as the new instance and holds nobody back. An entry that names no generation (a state saved before the field, a request made without one) goes too when the fence opens a new instance (<c>KeepGeneration</c> set), because
    /// anything the ledger held before the instance was opened is older; the entries of the kept generation stay, which is what makes it safe to run again (the adopted slot is applied after it). Run it before <see cref="Apply"/>.
    /// A copy; the state given is not changed.
    /// </summary>
    public static CapacityState Fence(CapacityState ledger, LedgerFence fence)
    {
        var next = ledger.Copy();
        var item = fence.Item.ToString();
        bool Old(int generation) => fence.KeepGeneration is not { } keep || generation != keep;
        if (next.Slots.TryGetValue(item, out var slot) && Old(slot.Request.Generation)) next.Slots.Remove(item);
        if (next.Queue.TryGetValue(item, out var queued) && Old(queued.Generation)) next.Queue.Remove(item);
        if (next.InFlight.TryGetValue(item, out var flight) && Old(flight.Generation)) next.InFlight.Remove(item);
        // (a hold belongs to the waiting request: it goes with it, or with the item that ended)
        if (!next.Queue.ContainsKey(item)) next.Holds.Remove(item);
        return next;
    }

    /// <summary>
    /// The ledger with the adopted slot and the item among those in flight. The slot is what a live owner's session already holds: it is not a grant, so the executions of the day do not count it (the session started before, and the
    /// runtime's reading counts it as running). An item that holds a slot in the ledger already keeps it. A copy; the state given is not changed.
    /// </summary>
    public static CapacityState Apply(CapacityState ledger, AdoptionSlot? slot, AdoptionFlight? flight)
    {
        var next = ledger.Copy();
        if (slot is not null)
        {
            var item = slot.Item.ToString();
            if (!next.Slots.ContainsKey(item))
            {
                var request = new WaitingRequest { Item = item, Generation = slot.Generation, Runtime = slot.Runtime, Phase = slot.Phase, Resume = true, ColumnDepth = slot.Phase switch { "design" => 1, "develop" => 2, _ => 3 },
                    RequestedAt = slot.GrantedAt, Footprint = Footprint(slot.Footprint) };
                next.Slots[item] = new LedgerSlot { SlotId = slot.SlotId, Item = item, Runtime = slot.Runtime, Host = slot.Host, Kind = SlotKind.Guaranteed, GrantedAt = slot.GrantedAt, ExpiresAt = slot.ExpiresAt, Request = request };
                next.Queue.Remove(item);
                // past Technical Design the item is a holder of the file-overlap rule until it is cleared
                if (request.PastDesign && !next.InFlight.ContainsKey(item)) { next.InFlight[item] = new InFlightItem { Item = item, Footprint = request.Footprint, Since = slot.GrantedAt, Generation = slot.Generation }; next.Slots[item].EnteredFlight = true; }
            }
        }
        if (flight is not null && !next.InFlight.ContainsKey(flight.Item.ToString()))
            next.InFlight[flight.Item.ToString()] = new InFlightItem { Item = flight.Item.ToString(), Footprint = Footprint(flight.Footprint), Since = flight.Since, Generation = flight.Generation };
        return next;
    }

    static Footprint Footprint(IReadOnlyList<string> files) => files.Count == 0 ? Domain.Footprint.Empty : new Footprint(files, FootprintSource.PullRequest);
}

/// <summary>A Sign-off (manual intervention) Octopus shows a deployment waiting at: the facts of the live read.</summary>
public sealed record PendingSignOff(string InterruptionId, string DeploymentId, string TaskId, string Version, DateTimeOffset Since);

/// <summary>What v1 recorded when the gateway promoted a release for an item: <c>merge.promoted.&lt;environment&gt;</c> (task, time, release) and the time its Sign-off began to wait (<c>signoff_wait_since</c>, kept on the release's first item).</summary>
public sealed record ItemPromotion(string Item, string Environment, string TaskId, string Release, DateTimeOffset At, DateTimeOffset? SignOffWaitSince);

public static class AdoptionTrain
{
    /// <summary>The promotions v1 recorded on an item's state comment (<c>merge.promoted</c>), in the order of the environments' names.</summary>
    public static IReadOnlyList<ItemPromotion> Promotions(string item, JsonObject state)
    {
        var list = new List<ItemPromotion>();
        if (V1State.Obj(V1State.Obj(state, "merge"), "promoted") is not { } promoted) return list;
        foreach (var kv in promoted.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            if (kv.Value is JsonObject p && V1State.Str(p, "task") is { Length: > 0 } task)
                list.Add(new ItemPromotion(item, kv.Key.ToLowerInvariant(), task, V1State.Str(p, "release"), V1State.At(p, "at") ?? DateTimeOffset.MinValue, V1State.At(p, "signoff_wait_since")));
        return list;
    }

    /// <summary>
    /// The train with the Sign-offs its deployments are waiting at now. A Sign-off is the train's to answer when the deployment it waits at was the gateway's own, which v1 recorded as <c>merge.promoted.&lt;environment&gt;.task</c> on
    /// the items of the release; the time it has waited is the recorded <c>signoff_wait_since</c> (so that the two-hour limit of signoffWaitHours survives the switch) else the time Octopus shows. The release counts as
    /// requested by the train, with the items it carries. A Sign-off the train knows already is left as it is (it may have been answered); a deployment that is no longer waiting is not added. A copy.
    /// </summary>
    public static TrainState SignOffs(TrainState train, IReadOnlyList<PendingSignOff> pending, IReadOnlyList<ItemPromotion> promotions)
    {
        var next = TrainState.FromJson(train.ToJson());
        var environment = train.Environment.ToLowerInvariant();
        foreach (var wait in pending)
        {
            if (next.SignOffs.Any(s => s.InterruptionId == wait.InterruptionId)) continue;
            var own = promotions.Where(p => p.Environment == environment && p.TaskId == wait.TaskId).ToList();
            if (own.Count == 0) continue;   // (a deployment somebody else made: not the train's to answer)
            var version = wait.Version.Length > 0 ? wait.Version : own.Select(p => p.Release).FirstOrDefault(r => r.Length > 0) ?? "";
            var since = own.Where(p => p.SignOffWaitSince is not null).Select(p => p.SignOffWaitSince!.Value).DefaultIfEmpty(wait.Since).Min();
            next.SignOffs.Add(new TrainSignOff { InterruptionId = wait.InterruptionId, DeploymentId = wait.DeploymentId, TaskId = wait.TaskId, Version = version, Since = since });
            if (version.Length == 0) continue;
            var at = own.Select(p => p.At).Min();
            next.Requested.TryAdd(version, at); next.FirstRequested.TryAdd(version, at); next.RequestNumbers.TryAdd(version, 1);
            var items = next.RequestItems.TryGetValue(version, out var known) ? known : [];
            next.RequestItems[version] = [.. items.Union(own.Select(p => p.Item), StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.Ordinal)];
        }
        return next;
    }
}
