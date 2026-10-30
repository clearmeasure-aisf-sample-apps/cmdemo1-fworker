namespace Fworker.Contracts.Commands;

// W8.3: the one deployment the delivery train creates in tdd. tdd is deployed by the release's own lifecycle and the train only observes (DeliveryPolicy.Acts), with one exception the
// owner decided on 2026-10-29: the incident rollback of the app in tdd, automatic, tdd only (uat and prod are not configured). The command is a contract only; the executor
// (Fworker.Integrations.Octopus.RollbackExecutor) refuses every environment but tdd, and a host without Engine.V2Acts registers no handler for it.

/// <summary>
/// Deploys <paramref name="Version"/>, the last good release, to <paramref name="Environment"/> again (the key: tdd only) as the rollback of the incident <paramref name="Incident"/>. Unlike PromoteRelease it
/// creates a deployment although Octopus lists an earlier one of that release to the environment (that is what a rollback is), and it creates none when the environment's newest deployment already is of
/// that release (a replay of the command, a restart or a second gateway never deploys twice).
/// </summary>
public sealed record RollbackRelease(string IdempotencyKey, string Deployable, string Project, string Version, string Environment, string Incident) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "RollbackRelease";
    public override CommandTarget Target => CommandTarget.Octopus;
}
