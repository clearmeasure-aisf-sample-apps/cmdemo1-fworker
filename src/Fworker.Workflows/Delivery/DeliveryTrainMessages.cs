using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.Delivery;

// W5.1: the messages of one delivery train. Everything that reaches a train is a TrainInput addressed by the train's key (see TrainKey): the router turns an ItemMerged and the
// Octopus watcher's events into one input per train they concern, so the train's handler sees one train's facts in the order the queue delivers them. What a train publishes:
// DeliveredTo and Undelivered (the delivered-to table), PromoteRelease and AnswerSignOff (integration commands, Fworker.Contracts), TrainDecided (the decision, for the history
// and the shadow run's comparison with v1) and its own TrainWake (a Wolverine scheduled message).

/// <summary>Any input of a train.</summary>
public abstract record TrainInput(string Key) : IMessage;

/// <summary>A merged item is a candidate of the train: the item, its merge commit and the gates read when it merged.</summary>
[MessageIdentity("delivery.train.candidate")]
public sealed record TrainCandidateAdded(string Key, ItemMerged Merged) : TrainInput(Key);

/// <summary>The gates of a merged item changed (a Hold label added, a demo video published): the train judges its releases again. v1 re-reads the gates at every pass.</summary>
[MessageIdentity("delivery.train.gates-changed")]
public sealed record TrainGatesChanged(string Key, string Item, ItemMergedGates Gates) : TrainInput(Key);

/// <summary>A release exists (OctopusReleaseCreated, or ReleaseCommitResolved with the commit). <paramref name="At"/> is when the router received it: Octopus events carry no time.</summary>
[MessageIdentity("delivery.train.release")]
public sealed record TrainReleaseSeen(string Key, string ReleaseId, string Version, string? Commit, DateTimeOffset At) : TrainInput(Key);

/// <summary>A deployment started, succeeded or failed in <paramref name="Environment"/> (the Octopus name).</summary>
[MessageIdentity("delivery.train.deployment")]
public sealed record TrainDeploymentSeen(string Key, string DeploymentId, string TaskId, string ReleaseId, string Version, string Environment, string Phase, string? Commit, DateTimeOffset At) : TrainInput(Key);

/// <summary>A deployment waits at the Sign-off step.</summary>
[MessageIdentity("delivery.train.signoff")]
public sealed record TrainSignOffAsked(string Key, string InterruptionId, string DeploymentId, string TaskId, string ReleaseId, string Version, string Environment, string? Commit, DateTimeOffset At) : TrainInput(Key);

/// <summary>An incident holds the train (true) or releases it (false): nothing is promoted while it is held. The incident's own hotfix and rollback (W8) join with their saga.</summary>
[MessageIdentity("delivery.train.hold")]
public sealed record TrainHold(string Key, bool Held, string Reason) : TrainInput(Key);

/// <summary>The train looks again: a timer fired (<paramref name="Due"/> is the time it was scheduled for), or a fact the train does not keep changed (an item was delivered to another environment).</summary>
[MessageIdentity("delivery.train.wake")]
public sealed record TrainWake(string Key, DateTimeOffset? Due = null) : TrainInput(Key);

/// <summary>
/// W7.7: an item the train's candidates may wait for was delivered to the train's environment (a <see cref="DeliveredTo"/> of another item, in this train or in another deployable's): the train looks
/// again, if one of its candidates "Depends on" that item. It carries no fact the train keeps: the delivered-to table is the evidence, the message only triggers the re-check.
/// </summary>
[MessageIdentity("delivery.train.dependency-delivered")]
public sealed record TrainDependencyDelivered(string Key, string Item, string Environment) : TrainInput(Key);

/// <summary>
/// What the train decided, once per change: <paramref name="Action"/> is one of idle, waiting, promote, following, signoff-waiting, signoff-proceed, signoff-abort, delivered, held,
/// observing (the train does not act in this environment). One decision is per release: "idle" with a release says the release is no longer judged (a decision before it is retracted), "delivered"
/// retracts the release and the <paramref name="Superseded"/> ones it carried. The shadow run (W5.2) compares it with v1's promotions and Sign-off answers.
/// </summary>
[MessageIdentity("delivery.train.decided")]
public sealed record TrainDecided(string Key, string Action, string Release, IReadOnlyList<string> Items, IReadOnlyList<string> Reasons, DateTimeOffset At,
    IReadOnlyList<string>? Superseded = null, bool Replay = false) : IEvent;

/// <summary>
/// The watcher's replay of Octopus's history begins (<paramref name="Active"/> true) or is applied (false). While it runs the train takes its inputs in and decides nothing; the end makes it judge
/// its state once, and what it decides then is flagged <see cref="TrainDecided.Replay"/> (a judgment of history, not of a live event).
/// </summary>
[MessageIdentity("delivery.train.replay")]
public sealed record TrainReplay(string Key, bool Active) : TrainInput(Key);
