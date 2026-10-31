using System.Text.Json.Nodes;

namespace Fworker.Workflows.Adoption;

// W9.9b (review): the hold on the saga's generation-bearing inputs between the start of the Wolverine host and the end of the adoption of the term, for a start that follows a break (AdoptionBreak: v1 may have acted on the
// items since the engine last did). W9.10n's fence only works once the adoption has moved a changed item to its next generation; until then an item's row is still the old instance, and the durable timers (PhaseLeaseExpired,
// MergeRetryDue, SettleWindowElapsed, ...) and commands of the old instance, recovered when the host starts, would act on it: a session launched on an item v1 moved to Verify, a merge asked for again for an item v1 merged.
// So the host decides the break before its listeners and its scheduled-job polling start (Begin, called by WolverineHostService), and while the gate holds, WorkItemService answers a generation-bearing input of an open row
// that the adoption has not examined yet with NotFound ("awaiting re-adoption") and does nothing. An item the adoption examined (Adopted: its row is the new generation, or was found to stand) is open again at once, so the
// adoption's own messages pass. What the hold drops is the timers (the re-arm job sends an overdue one again once the item is released, WorkItemRearm) and the sessions' reports, which the adoption reads from the board and the
// state comment again. The gate is released by the adoption job (AdoptionJob) when the term is not after a break, or when its run is done; the evidence of a break stays (the "acted" heartbeat is not written before) so that
// a process that stops in between holds again at its start. Without a break nothing is held.

public sealed class ReadoptionGate
{
    readonly object gate = new();
    readonly HashSet<string> adopted = new(StringComparer.Ordinal);
    bool held;
    string why = "";
    long refused;

    public bool Held { get { lock (gate) return held; } }

    /// <summary>Whether a generation-bearing input for this item is to be held back: the gate holds and the adoption has not examined the item since.</summary>
    public bool Refuses(string itemKey) { lock (gate) return held && !adopted.Contains(itemKey); }

    /// <summary>Counts an input that was held back (for the status).</summary>
    public void Refused() => Interlocked.Increment(ref refused);

    /// <summary>Starts or keeps the hold. <paramref name="newTerm"/> forgets which items were examined: a start of the host is a new term, and the adoption of the earlier one says nothing about the break of this one.</summary>
    public void Hold(string reason, bool newTerm = false)
    {
        lock (gate)
        {
            if (!held || newTerm) adopted.Clear();
            held = true; why = reason;
        }
    }

    /// <summary>The adoption examined the item (its row is in its final generation for this term): its inputs pass again.</summary>
    public void Adopted(string itemKey) { lock (gate) if (held) adopted.Add(itemKey); }

    public void Release() { lock (gate) { held = false; adopted.Clear(); why = ""; } }

    /// <summary>
    /// What the host does before its listeners start: reads the engine's markers and holds when the start is after a break. Markers that cannot be read hold too (the wrong answer is the unfenced one); the adoption job
    /// releases the gate when it can tell that the term is not after a break. A start with no break releases it (the gateway holds it from the moment it builds the host, before the first start).
    /// </summary>
    public async Task Begin(IEngineMarkers markers, DateTimeOffset now, CancellationToken ct, TimeSpan? gap = null)
    {
        try
        {
            var decided = AdoptionBreak.Detect(await markers.Read(ct), now, gap);
            if (decided.Break) Hold(decided.Why, newTerm: true); else Release();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Hold("the engine markers could not be read", newTerm: true);
        }
    }

    /// <summary>The "readoptionHold" block of the v2 status.</summary>
    public JsonObject Block()
    {
        lock (gate) return new JsonObject { ["held"] = held, ["why"] = why.Length == 0 ? null : why, ["adoptedItems"] = adopted.Count, ["refused"] = Interlocked.Read(ref refused) };
    }
}
