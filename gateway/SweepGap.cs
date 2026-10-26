// The gap that the first sweep after a start renews claims for (Program.cs, Leader.cs): set once by the restore, taken by one sweep.
sealed class SweepGap
{
    DateTime? since;
    public void Set(DateTime s) { lock (this) since = s; }
    public DateTime? Take() { lock (this) { var s = since; since = null; return s; } }
}
