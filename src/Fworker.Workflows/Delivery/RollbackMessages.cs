using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.Delivery;

// W8.3: rollback through the delivery train (docs/design/wolverine-factory.md 3.3, docs/design/fworker-wolverine-implementation.md W8). The IncidentSaga (W8.1) never calls Octopus: it sends
// Rollback(environment, to release) to the train that owns the deployments, and the train answers with RollbackFinished. Owner decision of 2026-10-29: the tdd train may create exactly one
// kind of deployment itself, the incident rollback of the app in tdd, automatic and tdd only (uat and prod are not configured); every other train answers a Rollback by escalating.

/// <summary>
/// The incident asks the train of (<paramref name="Deployable"/>, <paramref name="Environment"/>) to put the last good release back. <paramref name="Incident"/> names the incident (and its generation): it is the
/// request's identity, so a message delivered twice is one rollback. <paramref name="ToRelease"/> is the version to go back to; null lets the train choose the release that ran in the environment before the one it
/// runs now.
/// </summary>
[MessageIdentity("delivery.rollback")]
public sealed record Rollback(string Deployable, string Environment, string Incident, string? ToRelease, string Reason) : ICommand;

/// <summary>
/// How a rollback ended, for the incident that asked. <see cref="Outcome"/> is <see cref="RollbackOutcome.RolledBack"/> (the environment runs <paramref name="To"/> again and <paramref name="Items"/> were
/// undelivered) or <see cref="RollbackOutcome.Escalated"/> (nothing was rolled back, or the rollback deployment failed: a person decides, with <paramref name="Reasons"/> to read). The incident fixes forward or escalates on it.
/// </summary>
[MessageIdentity("delivery.rollback-finished")]
public sealed record RollbackFinished(string Deployable, string Environment, string Incident, string Outcome, string From, string To, IReadOnlyList<string> Items, IReadOnlyList<string> Reasons, DateTimeOffset At) : IEvent;

public static class RollbackOutcome
{
    public const string RolledBack = "rolled-back";
    public const string Escalated = "escalated";
}

/// <summary>The incident's request, addressed to the train it concerns (the router turns a <see cref="Rollback"/> into this).</summary>
[MessageIdentity("delivery.train.rollback")]
public sealed record TrainRollbackRequested(string Key, string Incident, string? ToRelease, string Reason) : TrainInput(Key);

/// <summary>The executor was refused for good (Octopus refused the deployment, the project or the release is not there, the key may not deploy): the train cannot roll back and escalates.</summary>
[MessageIdentity("delivery.train.rollback-failed")]
public sealed record TrainRollbackFailed(string Key, string Incident, string Error) : TrainInput(Key);
