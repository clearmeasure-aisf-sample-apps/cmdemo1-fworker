// The gap that the first sweep after a start renews claims for (Program.cs, Leader.cs): set once by the restore, taken by one sweep.
sealed class SweepGap
{
    DateTime? since, renewSince, renewDone;
    // V1.4: the gap renewal of the saga's items (job gap-renew) reads the same gap on its own, whenever the sweep takes it: a second latch, armed by the same Set, closed once per gap (a Set of a gap already renewed, the sweep putting its
    // share back after a failed pass, does not open it again)
    public void Set(DateTime s) { lock (this) { since = s; if (renewDone != s) renewSince = s; } }
    public DateTime? Take() { lock (this) { var s = since; since = null; return s; } }
    /// <summary>The gap the saga's items are still to be renewed for, or null (the job gap-renew; <see cref="RenewDone"/> closes it).</summary>
    public DateTime? PeekRenew() { lock (this) return renewSince; }
    public void RenewDone(DateTime s) { lock (this) { renewDone = s; if (renewSince == s) renewSince = null; } }
    /// <summary>The gap without taking it (the adoption of the term reads it before the first full sweep takes it).</summary>
    public DateTime? Peek() { lock (this) return since; }
}
