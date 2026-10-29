namespace Fworker.Workflows.Merge;

/// <summary>
/// How a merge train lets the pull requests of a repository through (docs/design/delivery-speed.md 9; <c>mergeStrategy</c> of config/instance.json).
/// The merge queue of GitHub (<c>queue</c>) is deferred by the owner's decision of 2026-10-28 (W7 scope cut): it needs the ruleset change of W7.10, which waits for the owner. A configuration that names it, or anything else,
/// runs <see cref="Direct"/> (<see cref="MergeTrainSettings.ParseStrategy"/> says so).
/// </summary>
public enum MergeStrategy
{
    /// <summary>The gateway's squash merge with the update of a branch that is behind, as today: every request is let through at once, no order.</summary>
    Direct,
    /// <summary>Direct merges, serialized: the pull request first in line is let through, the others wait ("merge-train"), so a branch is updated at most once per merge ahead of it. Needs no ruleset change.</summary>
    Train,
}

/// <summary>
/// The settings of the merge trains. <paramref name="EntryExpiry"/> is how long an item may go without asking (the saga asks again after a minute while it waits) before it loses its place: an item that was parked, merged
/// elsewhere or lost does not hold the others back. <paramref name="HeadPatience"/> is how long the first place may stay with one item while others wait: past it the item goes to the back (a head that never gets
/// its checks green must not hold the line for good). <paramref name="WaitRetry"/> is how long after the wait of the line ("merge-train") the saga asks again; unset, it is the saga's own pause (a minute). Every ask is
/// one of the saga's merge calls (<c>DocsPlanRules.MaxMergeAttempts</c>), so a shorter pause hands the turn on sooner and uses the budget of an item that waits longer up sooner.
/// </summary>
public sealed record MergeTrainSettings(MergeStrategy Strategy = MergeStrategy.Direct, TimeSpan? EntryExpiry = null, TimeSpan? HeadPatience = null, TimeSpan? WaitRetry = null)
{
    public static readonly MergeTrainSettings Default = new();

    public TimeSpan Expiry => EntryExpiry is { } e && e > TimeSpan.Zero ? e : TimeSpan.FromMinutes(5);
    public TimeSpan Patience => HeadPatience is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromMinutes(15);

    /// <summary>
    /// The strategy the configuration names. "direct" and "train" (any case) are the two that exist; an empty value is direct; "queue" is deferred and anything else unknown: both run direct and the warning says why
    /// (the caller logs it once). A configuration error must not stop the gateway or turn merging off.
    /// </summary>
    public static (MergeStrategy Strategy, string? Warning) ParseStrategy(string? text)
    {
        var name = (text ?? "").Trim();
        if (name.Length == 0 || name.Equals("direct", StringComparison.OrdinalIgnoreCase)) return (MergeStrategy.Direct, null);
        if (name.Equals("train", StringComparison.OrdinalIgnoreCase)) return (MergeStrategy.Train, null);
        if (name.Equals("queue", StringComparison.OrdinalIgnoreCase)) return (MergeStrategy.Direct, "mergeStrategy 'queue' is deferred (the merge queue needs the ruleset change of W7.10); merging directly");
        return (MergeStrategy.Direct, $"mergeStrategy '{name}' is not one of direct, train; merging directly");
    }
}
