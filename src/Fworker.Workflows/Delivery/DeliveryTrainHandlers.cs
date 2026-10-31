using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Wolverine;

namespace Fworker.Workflows.Delivery;

// W5.1: the Wolverine handlers of the delivery train. The train's own handler takes any TrainInput to DeliveryTrainService and returns what it decided: the messages (cascaded through the
// outbox; the state is saved first by the train's own store, so a crash between the two can lose a command or a timer: the train sends an unconfirmed request again after five minutes and
// every input sends its due time again, which heals it) and the timer as a scheduled TrainWake. The router turns an ItemMerged and the events of the Octopus watcher (W4.2) into
// one input per train they concern. Public because Wolverine's generated code refers to them; the gateway attaches them in the shadow run (W5.2, DeliveryShadowWiring) and, at the cutover, where the engine acts; in the shadow run the commands they cascade are only recorded
// (DeliveryShadowHandlers) and nothing executes them.

/// <summary>The handlers of the train's inputs, one per message type (Wolverine routes by the concrete type).</summary>
public static class DeliveryTrainHandlers
{
    public static Task<OutgoingMessages> Handle(TrainCandidateAdded m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainGatesChanged m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainReleaseSeen m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainDeploymentSeen m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainSignOffAsked m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainHold m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainRollback m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainReplay m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainDependencyDelivered m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainWake m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainRollbackFailed m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);

    public static async Task<OutgoingMessages> Run(TrainInput input, DeliveryTrainService svc, CancellationToken ct)
    {
        var result = await svc.Handle(input, ct);
        return Outgoing(result, input.Key);
    }

    /// <summary>The messages of a result, with the timer scheduled for its due time.</summary>
    public static OutgoingMessages Outgoing(TrainResult result, string key)
    {
        var o = new OutgoingMessages();
        foreach (var m in result.Messages) o.Add(m);
        if (WakeFor(result, key) is { } wake) o.Add(wake.ScheduledAt(result.WakeAt!.Value));
        return o;
    }

    /// <summary>
    /// The timer of a result as the message that fires it. The train's own wake carries its due time (a timer that is not the pending one is stale and is dropped while one is pending). The transient
    /// retry carries none: it is not the pending wake, which stays in the state, and with a due time it would be dropped as stale and swallowed.
    /// </summary>
    public static TrainWake? WakeFor(TrainResult result, string key) => result.WakeAt is { } due ? new TrainWake(key, result.Retry ? null : due) : null;
}

/// <summary>
/// The router: which trains a fact concerns. A merged item is a candidate of every train (each environment) of the deployables built from its repository; an Octopus event reaches every train of
/// the deployables that have an Octopus project (the watcher watches one project and the events name none). The time of an event is the time Octopus gave it (At), never later than the router's clock;
/// an event without one (a reader that has no time, a message from before At existed) is stamped with the time the router receives it.
/// </summary>
public sealed class DeliveryTrainRouter(IDeliveryPolicies policies, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The time of an event: its own, clamped so that it is never in the future (clock skew between Octopus and the gateway must not put a fact ahead of the train's clock); the router's clock when it has none.</summary>
    DateTimeOffset TimeOf(DateTimeOffset? at) { var now = clock(); return at is { } t && t < now ? t : now; }

    public IReadOnlyList<TrainInput> Route(ItemMerged m)
        => policies.ForRepository(m.Item.Repository).Select(p => (TrainInput)new TrainCandidateAdded(TrainKey.Of(p.Deployable, p.Environment), m)).ToList();

    /// <summary>
    /// W7.7: an item reached an environment: the trains of that environment whose rule dependencies-delivered is on look again (each one only if a candidate of its own waits for the item). Nothing
    /// where the rule is off, which is every train in the shadow run: a delivery there wakes no train.
    /// </summary>
    public IReadOnlyList<TrainInput> Route(DeliveredTo e)
        => policies.All().Where(p => p.DependenciesDelivered && p.Acts && string.Equals(p.Environment, e.Environment, StringComparison.OrdinalIgnoreCase))
            .Select(p => (TrainInput)new TrainDependencyDelivered(TrainKey.Of(p.Deployable, p.Environment), e.Item.ToString(), p.Environment)).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusReleaseCreated e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReleaseSeen(TrainKey.Of(p.Deployable, p.Environment), e.ReleaseId, e.Version, e.Commit, TimeOf(e.At))).ToList();

    public IReadOnlyList<TrainInput> Route(ReleaseCommitResolved e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReleaseSeen(TrainKey.Of(p.Deployable, p.Environment), e.ReleaseId, e.Version, e.Commit, clock())).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusDeploymentChanged e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainDeploymentSeen(TrainKey.Of(p.Deployable, p.Environment), e.DeploymentId, e.TaskId, e.ReleaseId, e.Version, e.Environment, e.Phase.ToString(), e.Commit, TimeOf(e.At))).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusReplayBegan e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReplay(TrainKey.Of(p.Deployable, p.Environment), true)).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusReplayComplete e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReplay(TrainKey.Of(p.Deployable, p.Environment), false)).ToList();

    public IReadOnlyList<TrainInput> Route(SignOffRequested e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainSignOffAsked(TrainKey.Of(p.Deployable, p.Environment), e.InterruptionId, e.DeploymentId, e.TaskId, e.ReleaseId, e.Version, e.Environment, e.Commit, TimeOf(e.At))).ToList();
}

/// <summary>The router's handlers: each fact becomes the inputs of its trains, published through the outbox. Public because Wolverine's generated code refers to them.</summary>
public static class DeliveryTrainRouting
{
    public static OutgoingMessages Handle(ItemMerged m, DeliveryTrainRouter r) => All(r.Route(m));
    /// <summary>
    /// W7.7: the delivery of an item wakes the trains that may wait for it. The delivered-to row is recorded here before the wake goes out (the gateway's own handler of the message records it too, and the
    /// two run in either order): a train that looked again before the row existed would find the dependency still missing and wait for its safety timer. Where no train is woken nothing is read or written.
    /// </summary>
    public static async Task<OutgoingMessages> Handle(DeliveredTo m, DeliveryTrainRouter r, DeliveredToTable table, CancellationToken ct)
    {
        var inputs = r.Route(m);
        if (inputs.Count > 0) await table.Record(m, DateTimeOffset.UtcNow, ct);
        return All(inputs);
    }

    public static OutgoingMessages Handle(OctopusReleaseCreated m, DeliveryTrainRouter r) => All(r.Route(m));
    public static OutgoingMessages Handle(ReleaseCommitResolved m, DeliveryTrainRouter r) => All(r.Route(m));
    public static OutgoingMessages Handle(OctopusDeploymentChanged m, DeliveryTrainRouter r) => All(r.Route(m));
    public static OutgoingMessages Handle(SignOffRequested m, DeliveryTrainRouter r) => All(r.Route(m));
    public static OutgoingMessages Handle(OctopusReplayBegan m, DeliveryTrainRouter r) => All(r.Route(m));
    public static OutgoingMessages Handle(OctopusReplayComplete m, DeliveryTrainRouter r) => All(r.Route(m));

    static OutgoingMessages All(IReadOnlyList<TrainInput> inputs)
    {
        var o = new OutgoingMessages();
        foreach (var i in inputs) o.Add(i);
        return o;
    }
}
