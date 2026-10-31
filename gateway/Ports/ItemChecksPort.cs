using System.Text.Json.Nodes;
using Fworker.Workflows.Qc;
using Fworker.Workflows.ReadModels;
using Fworker.Workflows.WorkItem.Verbs;

// W9.10q: the two outside calls the item verbs used to leave to the rule script.
//  - ItemChecksAdapter: the checks of a head and the base branch's failures for the CI rule of the completion of Development, over the checks read model of W9.5 (the same wire, identity and rate numbers as pr-checks). The read model
//    refuses with its own exception; the verbs refuse with theirs, with the same exit, code and reason.
//  - ItemQcAdapter: the accept of a QC environment (Invoke-QcAccept) is the QC gate's: QcService.Accept, whose refusal becomes the verb's.

/// <summary>The checks for the CI rule of the completion of Development.</summary>
sealed class ItemChecksAdapter(ChecksReadModel checks) : IItemChecks
{
    public async Task<ChecksFacts> ChecksFor(string sha, CancellationToken ct)
    {
        try { return await checks.ChecksFor(sha, null, ct); }
        catch (VerbRefusal e) { throw new ItemRefusal(e.Exit, e.Code, e.Message, e.Extra); }
    }

    public async Task<BaseFailures> BaseFailures(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        try { return await checks.BaseFailures(names, ct); }
        catch (VerbRefusal e) { throw new ItemRefusal(e.Exit, e.Code, e.Message, e.Extra); }
    }
}

/// <summary>The accept of a QC environment.</summary>
sealed class ItemQcAdapter(QcService qc) : IItemQc
{
    public async Task<JsonObject> Accept(int item, string assignment, string holder, CancellationToken ct)
    {
        try { return await qc.Accept(item, assignment, holder, ct); }
        catch (QcReject e) { throw new ItemRefusal(e.Exit, e.Code, e.Message, e.Extra); }
    }
}
