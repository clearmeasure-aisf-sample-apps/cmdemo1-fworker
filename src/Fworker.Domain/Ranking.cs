namespace Fworker.Domain;

// The order in which the plan offers the items of a sweep (delivery-speed.md 3.1), ported from gateway/PlanRanking.cs (W0.9) as pure values:
// no JSON, no clock, no I/O. The gateway still calls its own copy; W1.3 only ports the rule and pins it against that copy (the differential
// in tests/Fworker.Domain.Tests). Pinned items first (issue 46), then the priority label (top, high, none), then work already started before new
// work (issue 97). The sort that uses it must be stable, so items of one rank keep the sweep's order.
public readonly record struct PlanRank(bool Pinned, string? Priority, bool InIntake)
{
    /// <summary>top 0, high 1, anything else (none, unknown) 2; the label is compared as written (ordinal).</summary>
    public static int PriorityRank(string? priority) => priority == "top" ? 0 : priority == "high" ? 1 : 2;

    /// <summary>
    /// The rank of one offer. <paramref name="pin"/> is the worker the item is pinned to (empty or null: not pinned), <paramref name="priority"/>
    /// the priority label, <paramref name="column"/> the item's board column (null: past intake), compared with the intake column without case.
    /// </summary>
    public static PlanRank Of(string? pin, string? priority, string? column, string intakeColumn)
        => new(!string.IsNullOrEmpty(pin), priority, string.Equals(column ?? "", intakeColumn, StringComparison.OrdinalIgnoreCase));
}

public sealed class PlanRankComparer : IComparer<PlanRank>
{
    public static readonly PlanRankComparer Instance = new();

    public int Compare(PlanRank a, PlanRank b)
    {
        if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
        var pc = PlanRank.PriorityRank(a.Priority).CompareTo(PlanRank.PriorityRank(b.Priority));
        if (pc != 0) return pc;
        if (a.InIntake != b.InIntake) return a.InIntake ? 1 : -1;
        return 0;
    }

    /// <summary>The offers in the order the plan takes them (Enumerable.OrderBy is stable).</summary>
    public static IEnumerable<T> Order<T>(IEnumerable<T> offers, Func<T, PlanRank> rank)
        => offers.OrderBy(rank, Instance);
}
