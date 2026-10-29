using Fworker.Contracts.Ids;

namespace Fworker.Domain;

/// <summary>The defaults of the admission rules, as constants of a settings record. No config keys yet: W7.0 moves them into config/instance.json.</summary>
/// <param name="MaxHoldMinutes">schedule.fileOverlap.maxHoldMinutes (delivery-speed.md 14: 120): a file-overlap hold ends after this many minutes.</param>
/// <param name="HostFullSeconds">runtimes.octopus.hostFullSeconds (delivery-speed.md 14: 30): how long a host that answered host-full stays blocked.</param>
public sealed record AdmissionSettings(int MaxHoldMinutes = AdmissionSettings.DefaultMaxHoldMinutes, int HostFullSeconds = AdmissionSettings.DefaultHostFullSeconds)
{
    public const int DefaultMaxHoldMinutes = 120;
    public const int DefaultHostFullSeconds = 30;
}

/// <summary>A runtime's load: what it runs now, its limits and the executions started today (RuntimeManager: execs not ended, MaxConcurrent, today, MaxExecutionsPerDay).</summary>
public sealed record RuntimeLoad(int Running, int MaxConcurrent, int ExecutionsToday, int MaxExecutionsPerDay);

/// <summary>A file-overlap hold on an item: the item that holds it (the one with the intersecting footprint) and when the hold began (D6 decides both).</summary>
public sealed record FileOverlapHold(ItemId HeldBy, DateTimeOffset Since);

/// <summary>The rules of the admission chain, one function each. Each returns <see cref="AdmissionDecision.Admit"/> or a block with its waiting code.</summary>
public static class AdmissionRules
{
    /// <summary>
    /// Runtime at maxConcurrent or at its day budget (delivery-speed.md 3.2 rule 3; RuntimeManager.CanStart): the capacity is tested first, then the budget.
    /// A limit of 0 admits nothing, as in v1 (`Running >= MaxConcurrent`, `today >= MaxExecutionsPerDay`); a runtime that is off has MaxConcurrent 0.
    /// </summary>
    public static AdmissionDecision RuntimeCapacity(RuntimeLoad load)
    {
        if (load.Running >= load.MaxConcurrent) return AdmissionDecision.Block(AdmissionCodes.RuntimeAtCapacity);
        if (load.ExecutionsToday >= load.MaxExecutionsPerDay) return AdmissionDecision.Block(AdmissionCodes.RuntimeBudget);
        return AdmissionDecision.Admit;
    }

    /// <summary>
    /// Toolchain and capability admission (R7; system-gateway.md H4 and H8): every toolchain the item's repository needs must be offered by the runtime or host.
    /// Labels compare without case. No requirement admits; the detail of a block lists what is missing, lower case and sorted.
    /// </summary>
    public static AdmissionDecision Toolchain(IReadOnlyCollection<string>? required, IReadOnlyCollection<string>? offered)
    {
        if (required is null || required.Count == 0) return AdmissionDecision.Admit;
        var have = new HashSet<string>((offered ?? Array.Empty<string>()).Select(o => o.Trim()), StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().ToLowerInvariant()).Distinct().Where(r => !have.Contains(r)).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0 ? AdmissionDecision.Admit : AdmissionDecision.Block(AdmissionCodes.Toolchain, string.Join(",", missing));
    }

    /// <summary>
    /// Generated items go last, and only into a free slot (delivery-speed.md 3.1 rank 6 and 6): a generated item is admitted only when no real item still
    /// waits for a slot (<paramref name="realItemsWaitingForSlot"/>, the count the plan already passes to the keeper); the free slot itself is the capacity rule's.
    /// A real item is never held by this rule.
    /// </summary>
    public static AdmissionDecision GeneratedLast(bool generated, int realItemsWaitingForSlot)
        => generated && realItemsWaitingForSlot > 0 ? AdmissionDecision.Block(AdmissionCodes.GeneratedLast) : AdmissionDecision.Admit;

    /// <summary>
    /// The file-overlap hold with its expiry (delivery-speed.md 3.2 rule 2 and 7): held while a hold exists and has lasted less than maxHoldMinutes; at
    /// exactly maxHoldMinutes it has ended (the hold lasts at most that long). A hold that began in the future (clock skew) still holds. A maxHoldMinutes
    /// of 0 or less ends every hold at once. The block's detail is the holding item, `file-overlap owner/repo#N`.
    /// </summary>
    public static AdmissionDecision FileOverlap(FileOverlapHold? hold, DateTimeOffset now, int maxHoldMinutes)
    {
        if (hold is null) return AdmissionDecision.Admit;
        return HoldExpired(hold, now, maxHoldMinutes) ? AdmissionDecision.Admit : AdmissionDecision.Block(AdmissionCodes.FileOverlap, hold.HeldBy.ToString());
    }

    /// <summary>True when the hold has lasted maxHoldMinutes or more (see <see cref="FileOverlap"/>).</summary>
    public static bool HoldExpired(FileOverlapHold hold, DateTimeOffset now, int maxHoldMinutes)
        => maxHoldMinutes <= 0 || now - hold.Since >= TimeSpan.FromMinutes(maxHoldMinutes);

    /// <summary>
    /// Host-full as a non-failure block (delivery-speed.md 4.2): a host that answered host-full at <paramref name="reportedAt"/> is blocked until
    /// hostFullSeconds have passed (at exactly hostFullSeconds it is free again); only that host is blocked. Null: it never answered host-full.
    /// </summary>
    public static AdmissionDecision HostFull(DateTimeOffset? reportedAt, DateTimeOffset now, int hostFullSeconds)
    {
        if (reportedAt is not { } at) return AdmissionDecision.Admit;
        return now - at < TimeSpan.FromSeconds(Math.Max(0, hostFullSeconds)) ? AdmissionDecision.Block(AdmissionCodes.HostFull) : AdmissionDecision.Admit;
    }

    /// <summary>
    /// Whether a start outcome counts toward maxFailuresPerPhase and unhealthyAfterFailures: host-full does not (a "no capacity" start, delivery-speed.md 4.2
    /// and 17), every other failed outcome does. The caller passes only the outcomes of a start that did not succeed.
    /// </summary>
    public static bool CountsAsFailure(string? failedStartOutcome) => failedStartOutcome != AdmissionCodes.HostFull;
}
