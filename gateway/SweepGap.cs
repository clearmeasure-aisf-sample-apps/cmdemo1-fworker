// The gap while no gateway was up (Program.cs, Leader.cs): set by the restore of the plant. The adoption of the term reads it (Peek), and the gap renewal of the saga's owners' claims (job gap-renew) renews for it once.
sealed class SweepGap
{
    DateTime? since, renewSince, renewDone;
    // V1.4: the gap renewal's latch, armed by Set and closed once per gap (a Set of a gap already renewed does not open it again)
    public void Set(DateTime s) { lock (this) { since = s; if (renewDone != s) renewSince = s; } }
    /// <summary>The gap the saga's items are still to be renewed for, or null (the job gap-renew; <see cref="RenewDone"/> closes it).</summary>
    public DateTime? PeekRenew() { lock (this) return renewSince; }
    public void RenewDone(DateTime s) { lock (this) { renewDone = s; if (renewSince == s) renewSince = null; } }
    /// <summary>The gap of the last restore (the adoption of the term reads it).</summary>
    public DateTime? Peek() { lock (this) return since; }
}
