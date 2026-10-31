using Fworker.Domain;

namespace Fworker.Workflows.WorkItem;

/// <summary>
/// What the saga is tuned with: how long a step may go without a sign of life (its lease; a step with no lease waits for events only), the budgets and the settle window. The failures of a phase and the
/// idle follow-ups of an item are counted against the failure budget of the recovery table (D5, <see cref="Recovery"/>: v1's <c>maxFailuresPerPhase</c>, 2); the launches in a row have their own limit
/// (<c>maxLaunchesPerPhase</c> of the runtimes, 3). The defaults are v1's (<c>verdictTimeoutMinutes</c> 180, <c>leaseMinutes</c> 30) and the design's 24 hour settle window; the docs plan's rules (W6.6) are <see cref="DocsPlanRules"/>.
/// </summary>
public sealed record WorkItemPolicy(
    IReadOnlyDictionary<StepKind, TimeSpan?> Leases,
    RecoverySettings? Budgets = null,
    int MaxLaunchesPerPhase = 3,
    TimeSpan? SettleWindow = null,
    string FinalEnvironment = "prod",
    int KeepKeys = 100,
    DocsPlanRules? Docs = null,
    IReadOnlyList<string>? CarriedPlans = null,
    FeaturePlanRules? FeatureRules = null,
    SweepRules? Sweep = null,
    TimeSpan? SettleCapWindow = null,
    bool V1PromotesProd = false)
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

    /// <summary>The rules of the docs plan's steps (W6.6).</summary>
    public DocsPlanRules DocsRules => Docs ?? DocsPlanRules.Default;

    /// <summary>
    /// Whether this engine carries items of the plan. In the hybrid composition v2 carries only the plans whose steps it has (intake.v2Plans: the docs plan): an item that switches to another plan is handed back to v1, whose
    /// sweep takes it up again from the state comment and the board. Unset: every plan (the saga on its own, as the tests run it).
    /// </summary>
    public bool Carries(string plan) => CarriedPlans is null || CarriedPlans.Contains(plan, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the feature plan has the steps of W7.3 (Plans/FeatureSteps.cs): reviews, the pull request, CI alongside Functional Testing, fix-forward rounds, the scheduler's slots. Unset, the plan's steps are the core's.</summary>
    public bool HasFeatureSteps => FeatureRules is not null;

    /// <summary>Whether the steps of a plan ask the scheduler for a slot when they need a session: the code plans (feature, and the hotfix plan of an incident, which asks at the top rank), where the engine carries the feature plan.</summary>
    public bool Schedules(string plan) => FeatureRules is { UseScheduler: true } && WorkPlans.IsCode(plan);

    /// <summary>Whether the saga takes the sweep's steps for its items (W9.10f): parks that expire, the watch of master's CI after a merge, the owner that waits out a hold. Unset, none of them exist.</summary>
    public bool TakesSweepSteps => Sweep is not null;

    /// <summary>Whether the saga watches master's CI for the merge commit of an item on that plan (a code item; a documentation change ends at the merge).</summary>
    public bool WatchesMaster(string plan) => Sweep is { MasterWatch: true } && WorkPlans.IsCode(plan);

    public TimeSpan Settle => SettleWindow ?? TimeSpan.FromHours(24);

    /// <summary>
    /// <see cref="V1PromotesProd" /> says that v1's AutoPromoter promotes prod from this gateway (delivery.autoPromote enabled, prod among its environments, gatewayEnvironment this one); a gateway where nothing does never holds.
    /// Where the final environment is before prod (tdd: v2 delivers there), v1's AutoPromoter still promotes the release on to uat and prod, and it takes its items from the Release Queue (the state's merge.awaiting
    /// release-queue, which the projection writes only while the item is Settling). So the settle window does not complete an item whose release has not reached prod: it holds, until the DeliveredTo(prod) of the
    /// prod train or until <see cref="SettleCap"/> after the delivery.
    /// </summary>
    public bool HoldsForProd => V1PromotesProd && !string.Equals(FinalEnvironment, "prod", StringComparison.OrdinalIgnoreCase);

    /// <summary>The longest an item is held in Settling for prod, counted from the delivery to the final environment (default 14 days; a safety cap, so that an item never waits for a prod that does not come).</summary>
    public TimeSpan SettleCap => SettleCapWindow ?? TimeSpan.FromDays(14);

    public TimeSpan? LeaseOf(StepKind step) => Leases.TryGetValue(step, out var l) ? l : null;
}
