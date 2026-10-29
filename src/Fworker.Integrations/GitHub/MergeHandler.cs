using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// The handler of <see cref="MergePullRequest"/> on the W3.0 base: the idempotency ledger answers a replay of a settled key without a second merge call, and an exception of the
/// transport is classified (a 5xx or a timeout is transient). A merge, a head that moved and a refusal are settled and recorded; a blocked one is not, so the same key asks again.
/// </summary>
public sealed class MergePullRequestHandler : IntegrationCommandHandler<MergePullRequest>
{
    readonly MergeDirect _merge;
    public MergePullRequestHandler(IIdempotencyLedger ledger, MergeDirect merge) : base(ledger) { _merge = merge; }

    protected override async Task<CommandResult> Execute(MergePullRequest command, CancellationToken ct) => (await _merge.Run(command, ct)).ToResult();

    /// <summary>Runs the command and says how it ended. A transport failure that the base classified comes back as Failed ("command-failed") with its kind.</summary>
    public async Task<MergeOutcome> Process(MergePullRequest command, CancellationToken ct = default) => MergeOutcome.From(await Handle(command, ct));
}
