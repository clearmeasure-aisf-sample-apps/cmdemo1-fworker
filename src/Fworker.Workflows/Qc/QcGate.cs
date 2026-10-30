using System.Text.Json.Nodes;

namespace Fworker.Workflows.Qc;

/// <summary>Why the merge gate refuses: the code and reason of the answer, and the event the owner waits for (<c>wait_for</c>) with the head it is for.</summary>
public sealed record QcGateRefusal(string Code, string Reason, string WaitFor, string Sha);

/// <summary>
/// The merge gate of the QC environments (design pr-qc-environment.md 3.6), as Verbs.Merge.ps1 applies it: an item without the Auto Merge label can be merged only through the gate (owner mode, mergePolicy peer-approved, and
/// qc.requireRun or qc.requireApproval on), and then needs a QC run for the current head and a GitHub approval of that head by a person in humanApprovers. The merge verbs of the merge train (W9.3) call this; the record
/// it reads is <c>ran_sha</c>, written by <c>qc-report</c> and carried over by a new record of the same pull request.
/// </summary>
public static class QcGate
{
    /// <summary>
    /// Without the label only the gate lets an item through. Returns whether the merge goes through the gate (<c>true</c>), or is a plain Auto Merge item (<c>false</c>), or is refused:
    /// <see cref="QcReject"/> 10 'no-auto-merge-label' where the gate is not active.
    /// </summary>
    public static bool ThroughGate(QcConfig c, IEnumerable<string> labels)
    {
        if (QcJson.NameIn(c.AutoMergeLabel, labels)) return false;
        if (!c.GateActive) throw new QcReject(10, "no-auto-merge-label", $"item does not carry '{c.AutoMergeLabel}'");
        return true;
    }

    /// <summary>
    /// The two requirements, in the script's order: (a) some environment for this head reached ready at least once, unless the change is documentation only; (b) a person in humanApprovers approved the current head on GitHub
    /// (<paramref name="humanApproval"/>: the login Find-HumanApproval returned). Null when the gate lets the merge go on. Both default to on under peer-approved and off otherwise.
    /// </summary>
    public static QcGateRefusal? Check(QcConfig c, JsonObject? record, string headSha, bool docsOnly, string? humanApproval)
    {
        var gate = c.Gate;
        var short7 = headSha[..Math.Min(7, headSha.Length)];
        if (gate.RequireRun && !docsOnly && (record is null || !QcJson.Has(record, "ran_sha") || QcJson.Str(record, "ran_sha") != headSha))
            return new QcGateRefusal("qc-not-run", $"no QC environment for the current head {short7} has reached ready; a person starts one from the link on the pull request. Wait for the qc.ready or qc.approved event (or a person); do not park.", "qc.approved", headSha);
        if (gate.RequireApproval && string.IsNullOrEmpty(humanApproval))
            return new QcGateRefusal("qc-approval-missing", $"no person in humanApprovers has approved the current head {short7} on GitHub. Wait for the qc.approved event (or a person); do not park.", "qc.approved", headSha);
        return null;
    }
}
