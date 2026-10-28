using Fworker.Contracts.Seams;
using Wolverine;

namespace Fworker.Workflows.Delivery;

/// <summary>
/// Where the delivery train's messages travel in a host. Two ordered paths, both one at a time:
/// the watcher's Octopus events (<see cref="OctopusQueue"/>) and the trains' inputs (<see cref="TrainQueue"/>). Without the first, Wolverine gives every message type its own parallel local queue,
/// so an event published later can be handled earlier: the end of the replay (a different type) overtook the last deployment's Succeeded on tdd, the train judged its state once with that
/// deployment still Started, and the decision stood.
/// </summary>
public static class DeliveryTopology
{
    public const string TrainQueue = "delivery-train";
    public const string OctopusQueue = "octopus-events";

    /// <summary>The events the Octopus watcher publishes: the router handles them in the order the watcher published them.</summary>
    public static readonly Type[] OctopusEvents = [typeof(OctopusReleaseCreated), typeof(ReleaseCommitResolved), typeof(OctopusDeploymentChanged), typeof(SignOffRequested), typeof(OctopusReplayBegan), typeof(OctopusReplayComplete)];

    public static IEnumerable<Type> TrainInputs => typeof(TrainInput).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(TrainInput).IsAssignableFrom(t));

    public static void Route(WolverineOptions w)
    {
        RouteTrainInputs(w);
        RouteOctopusEvents(w);
    }

    public static void RouteTrainInputs(WolverineOptions w)
    {
        // the inputs of the trains run one at a time: a burst of the watcher's events (one poll publishes a dozen) would otherwise move the same train from several threads and exhaust its retries
        // (TrainConflictException). One queue for all trains is the shadow's stand-in for a queue per key; it is durable, so a timer or input survives a restart.
        foreach (var t in TrainInputs) w.PublishMessage(t).ToLocalQueue(TrainQueue);
        w.LocalQueue(TrainQueue).UseDurableInbox().Sequential();
    }

    public static void RouteOctopusEvents(WolverineOptions w)
    {
        // not durable: the watcher replays what a restart lost, and the ingress and the train deduplicate by key
        foreach (var t in OctopusEvents) w.PublishMessage(t).ToLocalQueue(OctopusQueue);
        w.LocalQueue(OctopusQueue).Sequential();
    }
}
