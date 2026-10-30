namespace Fworker.Workflows.WorkItem;

// W6.1: the plans of the WorkItemSaga, as data (docs/design/wolverine-factory.md 3.1). One saga type for every kind of item: the plan says which steps the item goes through, it is chosen when the item
// starts and may switch later (feature to docs when the design carries the docs-only marker, back when the pull request touches code, to docs after the merge when no release was built). A new kind of item
// (a dependency update, the UX columns) is a new row here and the steps it needs, never a new saga type.

/// <summary>The kinds of step a plan is made of. Each is a class in <c>WorkSteps.cs</c> with its own lease; W6.6 gives the docs plan's steps their rules.</summary>
public enum StepKind { Design, Develop, Verify, Merge, Deliver }

/// <summary>A plan: its name, the steps in order, and whether the items on it rank at the top of the queue and pass every gate (the hotfix).</summary>
public sealed record WorkPlan(string Name, IReadOnlyList<StepKind> Steps, bool TopRank = false)
{
    public StepKind First => Steps[0];

    public bool Has(StepKind kind) => IndexOf(kind) >= 0;

    /// <summary>The step after <paramref name="kind"/>, or null when it is the plan's last (or the plan has no such step).</summary>
    public StepKind? After(StepKind kind)
    {
        var i = IndexOf(kind);
        return i >= 0 && i + 1 < Steps.Count ? Steps[i + 1] : null;
    }

    public int IndexOf(StepKind kind)
    {
        for (var i = 0; i < Steps.Count; i++) if (Steps[i] == kind) return i;
        return -1;
    }
}

public static class WorkPlans
{
    public const string Feature = "feature";
    public const string Docs = "docs";
    public const string Hotfix = "hotfix";
    public const string Redeliver = "redeliver";

    /// <summary>Design, Develop, Verify, Merge, Deliver.</summary>
    public static readonly WorkPlan FeaturePlan = new(Feature, [StepKind.Design, StepKind.Develop, StepKind.Verify, StepKind.Merge, StepKind.Deliver]);
    /// <summary>Design, Develop, Verify, Merge: a documentation change has nothing to deliver.</summary>
    public static readonly WorkPlan DocsPlan = new(Docs, [StepKind.Design, StepKind.Develop, StepKind.Verify, StepKind.Merge]);
    /// <summary>Develop, Verify, Merge, Deliver; ranks top and passes every gate.</summary>
    public static readonly WorkPlan HotfixPlan = new(Hotfix, [StepKind.Develop, StepKind.Verify, StepKind.Merge, StepKind.Deliver], TopRank: true);

    /// <summary>Deliver only: a closed item whose delivery was undone after its settle window delivers again (wolverine-factory.md 3.1); it starts nowhere else.</summary>
    public static readonly WorkPlan RedeliverPlan = new(Redeliver, [StepKind.Deliver]);

    public static IReadOnlyList<WorkPlan> All { get; } = [FeaturePlan, DocsPlan, HotfixPlan, RedeliverPlan];

    /// <summary>
    /// Whether the plan changes code and so builds a release: the feature plan and the hotfix plan (W8.1) share the steps that watch CI, ask for a slot and go through the merge line; the docs plan does not, and the
    /// redeliver plan has no session step at all.
    /// </summary>
    public static bool IsCode(string? plan) => string.Equals(plan, Feature, StringComparison.OrdinalIgnoreCase) || string.Equals(plan, Hotfix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The plan of that name (any case), or null.</summary>
    public static WorkPlan? Find(string? name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static WorkPlan Get(string name) => Find(name) ?? throw new ArgumentException($"there is no plan '{name}'", nameof(name));

    /// <summary>The plan an item starts on: the named one, else the hotfix plan for a hotfix, else the feature plan (the docs plan is chosen after design, or named by the one that starts the item).</summary>
    public static WorkPlan? Choose(string? named, bool hotfix) => string.IsNullOrWhiteSpace(named) ? (hotfix ? HotfixPlan : FeaturePlan) : Find(named);
}
