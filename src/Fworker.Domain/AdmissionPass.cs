using Fworker.Contracts.Ids;

namespace Fworker.Domain;

/// <summary>One ranked item asking for a session on a runtime (the routing already picked the runtime; delivery-speed.md 3.2).</summary>
/// <param name="Runtime">The runtime kind the item goes to (octopus, cae-job, ...), the key of <see cref="AdmissionFacts.Runtimes"/>.</param>
/// <param name="Generated">The item carries the keeper-replenish marker.</param>
/// <param name="RequiredToolchains">What its repository needs (system.json); empty: nothing special.</param>
/// <param name="Host">The host the plan chose among the hosts below their limit, or null when the runtime has no hosts or none is chosen yet.</param>
/// <param name="Hold">The file-overlap hold on the item, if D6 found one.</param>
public sealed record AdmissionRequest(ItemId Item, string Runtime, bool Generated = false, IReadOnlyCollection<string>? RequiredToolchains = null,
    string? Host = null, FileOverlapHold? Hold = null);

/// <summary>A runtime's side of the facts: its load and the toolchains it (or its hosts) offers.</summary>
public sealed record AdmissionTarget(RuntimeLoad Load, IReadOnlyCollection<string> Toolchains);

/// <summary>The plant facts of one plan pass. <paramref name="HostFullAt"/> is keyed by host name: when it last answered host-full.</summary>
public sealed record AdmissionFacts(DateTimeOffset Now, IReadOnlyDictionary<string, AdmissionTarget> Runtimes, IReadOnlyDictionary<string, DateTimeOffset>? HostFullAt = null);

/// <summary>An item's decision in a pass.</summary>
public sealed record AdmissionVerdict(ItemId Item, AdmissionDecision Decision);

public static class AdmissionPass
{
    /// <summary>
    /// The admission chain for one item, rules evaluated in order and the first block wins (delivery-speed.md 3.2): file-overlap hold (rule 2), toolchain
    /// (R7; placed before the load rules so a permanent mismatch is not reported as a passing shortage), runtime capacity and budget (rule 3), generated
    /// last, then the host (rule 4's host-full part; the headroom test is D7's). A runtime that is not in the facts is `runtime-off`.
    /// </summary>
    public static AdmissionDecision Check(AdmissionRequest request, AdmissionFacts facts, AdmissionSettings settings, int realItemsWaitingForSlot = 0)
    {
        var hold = AdmissionRules.FileOverlap(request.Hold, facts.Now, settings.MaxHoldMinutes);
        if (!hold.Admitted) return hold;
        if (!facts.Runtimes.TryGetValue(request.Runtime, out var target)) return AdmissionDecision.Block(AdmissionCodes.RuntimeOff);
        var tool = AdmissionRules.Toolchain(request.RequiredToolchains, target.Toolchains);
        if (!tool.Admitted) return tool;
        var cap = AdmissionRules.RuntimeCapacity(target.Load);
        if (!cap.Admitted) return cap;
        var gen = AdmissionRules.GeneratedLast(request.Generated, realItemsWaitingForSlot);
        if (!gen.Admitted) return gen;
        if (request.Host is { } host)
        {
            DateTimeOffset? at = facts.HostFullAt is { } m && m.TryGetValue(host, out var t) ? t : null;
            var hf = AdmissionRules.HostFull(at, facts.Now, settings.HostFullSeconds);
            if (!hf.Admitted) return hf;
        }
        return AdmissionDecision.Admit;
    }

    /// <summary>
    /// One plan pass over items already ranked (<see cref="PlanRankComparer"/>): real items first in their given order, generated items after all of them
    /// (stable), each admitted item taking a slot and a day execution of its runtime for the items after it. A real item that waits for lack of a slot
    /// (runtime at capacity or budget, host-full) makes the generated items wait (generated-last); a real item that waits for another reason (hold,
    /// toolchain) does not, because a free slot is of no use to it. The verdicts come in the order evaluated.
    /// </summary>
    public static IReadOnlyList<AdmissionVerdict> Run(IReadOnlyList<AdmissionRequest> ranked, AdmissionFacts facts, AdmissionSettings settings)
    {
        var loads = facts.Runtimes.ToDictionary(kv => kv.Key, kv => kv.Value.Load);
        var verdicts = new List<AdmissionVerdict>(ranked.Count);
        var waitingForSlot = 0;
        foreach (var r in ranked.Where(r => !r.Generated).Concat(ranked.Where(r => r.Generated)))
        {
            var view = facts with { Runtimes = facts.Runtimes.ToDictionary(kv => kv.Key, kv => kv.Value with { Load = loads[kv.Key] }) };
            var d = Check(r, view, settings, waitingForSlot);
            verdicts.Add(new AdmissionVerdict(r.Item, d));
            if (d.Admitted) { var l = loads[r.Runtime]; loads[r.Runtime] = l with { Running = l.Running + 1, ExecutionsToday = l.ExecutionsToday + 1 }; }
            else if (!r.Generated && d.Code is AdmissionCodes.RuntimeAtCapacity or AdmissionCodes.RuntimeBudget or AdmissionCodes.HostFull) waitingForSlot++;
        }
        return verdicts;
    }
}
