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
    public static Task<OutgoingMessages> Handle(TrainReplay m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TrainWake m, DeliveryTrainService svc, CancellationToken ct) => Run(m, svc, ct);

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
        if (result.WakeAt is { } due) o.Add(new TrainWake(key, due).ScheduledAt(due));
        return o;
    }
}

/// <summary>
/// The router: which trains a fact concerns. A merged item is a candidate of every train (each environment) of the deployables built from its repository; an Octopus event reaches every train of
/// the deployables that have an Octopus project (the watcher watches one project and the events name none). The time of an event is the time the router receives it.
/// </summary>
public sealed class DeliveryTrainRouter(IDeliveryPolicies policies, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<TrainInput> Route(ItemMerged m)
        => policies.ForRepository(m.Item.Repository).Select(p => (TrainInput)new TrainCandidateAdded(TrainKey.Of(p.Deployable, p.Environment), m)).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusReleaseCreated e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReleaseSeen(TrainKey.Of(p.Deployable, p.Environment), e.ReleaseId, e.Version, e.Commit, clock())).ToList();

    public IReadOnlyList<TrainInput> Route(ReleaseCommitResolved e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReleaseSeen(TrainKey.Of(p.Deployable, p.Environment), e.ReleaseId, e.Version, e.Commit, clock())).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusDeploymentChanged e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainDeploymentSeen(TrainKey.Of(p.Deployable, p.Environment), e.DeploymentId, e.TaskId, e.ReleaseId, e.Version, e.Environment, e.Phase.ToString(), e.Commit, clock())).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusReplayBegan e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReplay(TrainKey.Of(p.Deployable, p.Environment), true)).ToList();

    public IReadOnlyList<TrainInput> Route(OctopusReplayComplete e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainReplay(TrainKey.Of(p.Deployable, p.Environment), false)).ToList();

    public IReadOnlyList<TrainInput> Route(SignOffRequested e)
        => policies.ForOctopusEvents().Select(p => (TrainInput)new TrainSignOffAsked(TrainKey.Of(p.Deployable, p.Environment), e.InterruptionId, e.DeploymentId, e.TaskId, e.ReleaseId, e.Version, e.Environment, e.Commit, clock())).ToList();
}

/// <summary>The router's handlers: each fact becomes the inputs of its trains, published through the outbox. Public because Wolverine's generated code refers to them.</summary>
public static class DeliveryTrainRouting
{
    public static OutgoingMessages Handle(ItemMerged m, DeliveryTrainRouter r) => All(r.Route(m));
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
