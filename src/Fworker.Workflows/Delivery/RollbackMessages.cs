using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.Delivery;

// W8.3: rollback through the delivery train (docs/design/wolverine-factory.md 3.3, docs/design/fworker-wolverine-implementation.md W8). One contract with the IncidentSaga (W8.1): the incident sends TrainRollback
// (DeliveryTrainMessages.cs) to the train that owns the deployments and reads the train's RollbackAnswered ("started", then "done", or "refused" / "failed" with the code that says why); it never calls Octopus.
// Owner decision of 2026-10-29: the tdd train may create exactly one kind of deployment itself, the incident rollback of the app in tdd, automatic and tdd only (uat and prod are not configured); every other
// train answers a TrainRollback with a refusal ("not-permitted") and the incident goes to its owner.

/// <summary>The codes of <see cref="RollbackAnswered.Code"/> the train uses.</summary>
public static class RollbackCodes
{
    /// <summary>The environment is not one where the train rolls back by itself, or the deployable has no Octopus project.</summary>
    public const string NotPermitted = "not-permitted";
    /// <summary>A database migration was added since the release to go back to: migrations are forward-only, so this is no rollback (the incident fixes forward).</summary>
    public const string Migration = "migration";
    public const string NoPreviousRelease = "no-previous-release";
    /// <summary>The train cannot decide (no release known in the environment, a deployment that does not end, a probe it does not have) or the rollback could not be shown to be safe.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>The rollback deployment was asked for and failed, or was not seen in Octopus.</summary>
    public const string Failed = "deployment-failed";
}

/// <summary>The executor was refused for good (Octopus refused the deployment, the project or the release is not there, the key may not deploy): the train cannot roll back and answers that it failed.</summary>
[MessageIdentity("delivery.train.rollback-failed")]
public sealed record TrainRollbackFailed(string Key, string IncidentId, string Error) : TrainInput(Key);
