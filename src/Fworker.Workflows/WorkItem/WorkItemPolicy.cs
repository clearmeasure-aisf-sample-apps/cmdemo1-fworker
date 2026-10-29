using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.WorkItem;

/// <summary>
/// What the saga is tuned with: how long a step may go without a sign of life (its lease; a step with no lease waits for events only), the budgets and the settle window. The defaults are v1's
/// (config/instance.json: <c>maxFailuresPerPhase</c> 2 and <c>maxLaunchesPerPhase</c> 3 of the runtimes, <c>verdictTimeoutMinutes</c> 180, <c>leaseMinutes</c> 30) and the design's 24 hour settle window; W6.6
/// refines them with the docs plan's steps. The recovery policy table of D5 replaces the budget fields when it is on master.
/// </summary>
public sealed record WorkItemPolicy(
    IReadOnlyDictionary<StepKind, TimeSpan?> Leases,
    int MaxFailuresPerPhase = 2,
    int MaxLaunchesPerPhase = 3,
    int MaxIdleFollowUps = 2,
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

    public TimeSpan Settle => SettleWindow ?? TimeSpan.FromHours(24);

    public TimeSpan? LeaseOf(StepKind step) => Leases.TryGetValue(step, out var l) ? l : null;
}
