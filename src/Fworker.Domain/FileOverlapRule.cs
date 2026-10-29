namespace Fworker.Domain;

// File-overlap hold (D6, docs/design/delivery-speed.md section 7), part 3: the decision. At Technical Design's `complete` an item whose footprint
// intersects the footprint of another item between Development and merged is held (`held: file-overlap #N`): its session ends and it waits. The hold ends when
// the holder merges, parks, is closed or leaves that range, or after maxHoldMinutes, and items held on the same files are released in rank order.
// A holder is past Technical Design, so it is never held itself: only the items passed in as `inFlight` can block, and they are never asked. That is the
// whole no-deadlock argument, and the pass below keeps it: an item it admits becomes a holder for the items after it, never the other way round.
// The caller decides who is in flight (board column Development up to, not including, merged; not parked, not closed) and keeps the hold record
// (`by #N, since, until`, durable per item) for as long as the item waits at Technical Design: an expired record keeps admitting, so an item is not held twice.

/// <summary>`schedule.fileOverlap`: the switch (false by default), the longest hold in minutes (120) and `schedule.overlapIgnore` (none).</summary>
public sealed record FileOverlapSettings(bool Enabled, int MaxHoldMinutes, IReadOnlyList<string> Ignore)
{
    public static readonly FileOverlapSettings Default = new(false, 120, Array.Empty<string>());
}

/// <summary>An item between Development and merged: its number and its footprint in force (<see cref="Footprint.Resolve"/>).</summary>
public sealed record OverlapPeer(int Number, Footprint Footprint);

/// <summary>The durable hold of an item: held by #<see cref="By"/> since <see cref="Since"/>, ends at the latest at <see cref="Until"/>.</summary>
public sealed record OverlapHold(int By, DateTimeOffset Since, DateTimeOffset Until);

public enum OverlapReason
{
    /// <summary>The rule is off.</summary>
    Disabled,
    /// <summary>The item names no files (after the ignore patterns): there is nothing to compare, it is never held.</summary>
    NoFootprint,
    /// <summary>No item between Development and merged shares a file with it.</summary>
    NoOverlap,
    /// <summary>The hold ran out (maxHoldMinutes), or maxHoldMinutes is not positive: the item goes on though files are still shared.</summary>
    Expired,
    /// <summary>Held by <see cref="OverlapHold.By"/>.</summary>
    Overlap,
}

public sealed record OverlapDecision(OverlapReason Reason, OverlapHold? Hold)
{
    public bool Held => Reason == OverlapReason.Overlap;

    /// <summary>The waiting code of the admission chain (`file-overlap #N`), null when the item is admitted.</summary>
    public string? WaitingCode => Held ? $"file-overlap #{Hold!.By}" : null;
}

/// <summary>An item waiting at Technical Design's end: its footprint, its rank, its age (older first) and the hold it has, if any.</summary>
public sealed record OverlapWaiter(int Number, Footprint Footprint, PlanRank Rank, DateTimeOffset Age, OverlapHold? Hold);

public sealed record OverlapRelease(int Number, OverlapDecision Decision);

public static class FileOverlapRule
{
    /// <summary>
    /// The decision for one item at Technical Design's `complete`. <paramref name="existing"/> is the hold it already has (null: none yet).
    /// A hold keeps its Since and Until while the item waits, even when the holder changes (a second item takes the first one's place): the limit is on the
    /// whole wait. The item itself is skipped among <paramref name="inFlight"/>. Of several holders the one kept is the current holder when it still shares
    /// a file, else the lowest number.
    /// </summary>
    public static OverlapDecision Check(int item, Footprint footprint, IEnumerable<OverlapPeer> inFlight, FileOverlapSettings settings, DateTimeOffset now, OverlapHold? existing = null)
    {
        if (!settings.Enabled) return new(OverlapReason.Disabled, null);
        if (settings.MaxHoldMinutes <= 0 || (existing is not null && now >= existing.Until)) return new(OverlapReason.Expired, null);
        var mine = footprint.WithoutIgnored(settings.Ignore);
        if (mine.IsEmpty) return new(OverlapReason.NoFootprint, null);

        var holders = inFlight.Where(p => p.Number != item && mine.Overlaps(p.Footprint.WithoutIgnored(settings.Ignore))).Select(p => p.Number).Distinct().OrderBy(n => n).ToList();
        if (holders.Count == 0) return new(OverlapReason.NoOverlap, null);

        var by = existing is not null && holders.Contains(existing.By) ? existing.By : holders[0];
        var hold = existing is null ? new OverlapHold(by, now, now.AddMinutes(settings.MaxHoldMinutes)) : existing with { By = by };
        return new(OverlapReason.Overlap, hold);
    }

    /// <summary>The order in which waiting items are released: rank (<see cref="PlanRankComparer"/>), then the older first, then the lower number.</summary>
    public static IReadOnlyList<OverlapWaiter> ReleaseOrder(IEnumerable<OverlapWaiter> waiters)
        => waiters.OrderBy(w => w.Rank, PlanRankComparer.Instance).ThenBy(w => w.Age).ThenBy(w => w.Number).ToList();

    /// <summary>
    /// One pass over the waiting items in release order (ranked release). Each admitted item joins the holders for the items after it, so of several items on
    /// the same files only the first by rank is released and the others are held by it; when it merges, the next one is. The result lists every waiter in
    /// release order with its decision.
    /// </summary>
    public static IReadOnlyList<OverlapRelease> Release(IEnumerable<OverlapWaiter> waiters, IEnumerable<OverlapPeer> inFlight, FileOverlapSettings settings, DateTimeOffset now)
    {
        var peers = inFlight.ToList();
        var result = new List<OverlapRelease>();
        foreach (var w in ReleaseOrder(waiters))
        {
            var d = Check(w.Number, w.Footprint, peers, settings, now, w.Hold);
            result.Add(new(w.Number, d));
            if (!d.Held) peers.Add(new(w.Number, w.Footprint));
        }
        return result;
    }
}
