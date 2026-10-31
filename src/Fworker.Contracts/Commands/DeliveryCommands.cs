namespace Fworker.Contracts.Commands;

// W5.1: the two Octopus side effects the delivery train decides on. They are contracts only: nothing executes them yet (the executor joins with the cutover, W9.9), and a
// host registers no handler for them unless the engine acts (Engine.V2Acts). In tdd (shadow) v1 keeps promoting with Verbs.Promote.ps1; the train's decisions are compared with
// v1's by the shadow run (W5.2), and these commands are only recorded.

/// <summary>
/// Creates the deployment of <paramref name="Version"/> to <paramref name="Environment"/> (the key: uat or prod) unless Octopus already lists one (Verbs.Promote.ps1: deployments/create/untenanted/v1).
/// <paramref name="Items"/> (W9.10i) are the work items the release carries, "owner/repo#N": the executor records the deployment on them (<c>merge.promoted.&lt;environment&gt;</c> of the state comment, which v1's
/// promotion reads to know a deployment is the gateway's own). Empty for a command of an older shape, which records nothing.
/// </summary>
public sealed record PromoteRelease(string IdempotencyKey, string Deployable, string Project, string Version, string Environment, IReadOnlyList<string>? Items = null) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "PromoteRelease";
    public override CommandTarget Target => CommandTarget.Octopus;
}

/// <summary>Answers the Sign-off step (a manual intervention) of a deployment the gateway created: Proceed when every gate still holds, Abort when not (Resolve-SignOff).</summary>
public sealed record AnswerSignOff(string IdempotencyKey, string Deployable, string InterruptionId, string TaskId, bool Proceed, string Notes) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "AnswerSignOff";
    public override CommandTarget Target => CommandTarget.Octopus;
}
