using Fworker.Domain;

namespace Fworker.Workflows.WorkItem;

/// <summary>
/// What the saga is tuned with: how long a step may go without a sign of life (its lease; a step with no lease waits for events only), the budgets and the settle window. The failures of a phase and the
/// idle follow-ups of an item are counted against the failure budget of the recovery table (D5, <see cref="Recovery"/>: v1's <c>maxFailuresPerPhase</c>, 2); the launches in a row have their own limit
/// (<c>maxLaunchesPerPhase</c> of the runtimes, 3). The defaults are v1's (<c>verdictTimeoutMinutes</c> 180, <c>leaseMinutes</c> 30) and the design's 24 hour settle window; W6.6 refines the leases with the
/// docs plan's steps.
/// </summary>
public sealed record WorkItemPolicy(
    IReadOnlyDictionary<StepKind, TimeSpan?> Leases,
    RecoverySettings? Budgets = null,
    int MaxLaunchesPerPhase = 3,
    TimeSpan? SettleWindow = null,
    string FinalEnvironment = "prod",
    int KeepKeys = 100)
{
    public static readonly WorkItemPolicy Default = new(new Dictionary<StepKind, TimeSpan?>
    {
        [StepKind.Design] = TimeSpan.FromMinutes(60),
        [StepKind.Develop] = TimeSpan.FromMinutes(120),
        [StepKind.Verify] = TimeSpan.FromMinutes(180),
        [StepKind.Merge] = TimeSpan.FromMinutes(30),
        [StepKind.Deliver] = null,
    });

    public RecoverySettings Recovery => Budgets ?? RecoverySettings.Default;

    public TimeSpan Settle => SettleWindow ?? TimeSpan.FromHours(24);

    public TimeSpan? LeaseOf(StepKind step) => Leases.TryGetValue(step, out var l) ? l : null;
}
