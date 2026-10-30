using System.Text.Json.Nodes;
using Fworker.Workflows.Qc;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.1: the merge gate of the QC environments as Verbs.Merge.ps1 applies it: the refusals in the script's words and order (qc-not-run before qc-approval-missing), the docs-only exemption of the run, the two switches,
/// and the record of a run that the worker's report leaves for it.
/// </summary>
public sealed class QcGateTests
{
    static QcConfig Cfg(string policy = "peer-approved", string qc = "{}") =>
        QcConfig.From(JsonNode.Parse(QcHarness.ConfigJson(policy, qc)), n => n switch { "FACTORY_PUBLIC_URL" => "https://g.test", "FACTORY_TOKEN_SECRET" => QcHarness.Secret, "FACTORY_OWNER_MODE" => "1", _ => null });

    static JsonObject Ran(string sha) => new() { ["state"] = "ready", ["ran_sha"] = sha };

    [Fact]
    public void AnItemWithTheAutoMergeLabelDoesNotNeedTheGate_AnotherNeedsItToBeActive()
    {
        Assert.False(QcGate.ThroughGate(Cfg("auto"), ["factory worker", "auto merge"]));
        Assert.False(QcGate.ThroughGate(Cfg(), ["Auto Merge"]));
        Assert.True(QcGate.ThroughGate(Cfg(), ["factory worker"]));
        var e = Assert.Throws<QcReject>(() => QcGate.ThroughGate(Cfg("auto"), ["factory worker"]));
        Assert.Equal((10, "no-auto-merge-label", "item does not carry 'Auto Merge'"), (e.Exit, e.Code, e.Message));
        Assert.Throws<QcReject>(() => QcGate.ThroughGate(Cfg(qc: "{\"requireRun\":false,\"requireApproval\":false}"), ["factory worker"]));   // both off: the label is required again
    }

    [Fact]
    public void TheRunComesFirst_ThenThePersonsApproval()
    {
        var sha = FakeQcWorld.Sha1;
        var noRun = QcGate.Check(Cfg(), null, sha, false, null)!;
        Assert.Equal(("qc-not-run", "qc.approved", sha), (noRun.Code, noRun.WaitFor, noRun.Sha));
        Assert.Equal("no QC environment for the current head 1111111 has reached ready; a person starts one from the link on the pull request. Wait for the qc.ready or qc.approved event (or a person); do not park.", noRun.Reason);
        Assert.Equal("qc-not-run", QcGate.Check(Cfg(), new JsonObject { ["state"] = "ready" }, sha, false, "carol")!.Code);          // (ready, but never recorded as a run)
        Assert.Equal("qc-not-run", QcGate.Check(Cfg(), Ran(FakeQcWorld.Sha2), sha, false, "carol")!.Code);                          // a run of an older head
        var noApproval = QcGate.Check(Cfg(), Ran(sha), sha, false, null)!;
        Assert.Equal(("qc-approval-missing", "qc.approved"), (noApproval.Code, noApproval.WaitFor));
        Assert.Equal("no person in humanApprovers has approved the current head 1111111 on GitHub. Wait for the qc.approved event (or a person); do not park.", noApproval.Reason);
        Assert.Null(QcGate.Check(Cfg(), Ran(sha), sha, false, "carol"));
    }

    [Fact]
    public void ADocumentationOnlyChangeNeedsNoRun_ButStillAnApproval()
    {
        var sha = FakeQcWorld.Sha1;
        Assert.Equal("qc-approval-missing", QcGate.Check(Cfg(), null, sha, true, null)!.Code);
        Assert.Null(QcGate.Check(Cfg(), null, sha, true, "carol"));
    }

    [Fact]
    public void EachRequirementCanBeSwitchedOff()
    {
        var sha = FakeQcWorld.Sha1;
        Assert.Equal("qc-approval-missing", QcGate.Check(Cfg(qc: "{\"requireRun\":false}"), null, sha, false, null)!.Code);
        Assert.Equal("qc-not-run", QcGate.Check(Cfg(qc: "{\"requireApproval\":false}"), null, sha, false, null)!.Code);
        Assert.Null(QcGate.Check(Cfg(qc: "{\"requireApproval\":false}"), Ran(sha), sha, false, null));
        Assert.Null(QcGate.Check(Cfg("auto"), null, sha, false, null));          // under auto the gate is not what stops a merge
    }

    [Fact]
    public async Task TheWorkersReportLeavesTheRunTheGateReads_AndAReportOfAnOlderHeadDoesNot()
    {
        var h = new QcHarness("peer-approved", humanApprovers: "[\"carol\"]");
        h.World.AddValidatedItem();
        await h.Start(); await h.Place();
        Assert.Equal("qc-not-run", QcGate.Check(h.Config, h.World.Rec(5, 9), FakeQcWorld.Sha1, false, "carol")!.Code);
        await h.Ready();
        Assert.Null(QcGate.Check(h.Config, h.World.Rec(5, 9), FakeQcWorld.Sha1, false, "carol"));
        // a push: the run is for the old head
        h.World.Push(9, FakeQcWorld.Sha2);
        Assert.Equal("qc-not-run", QcGate.Check(h.Config, h.World.Rec(5, 9), FakeQcWorld.Sha2, false, "carol")!.Code);
        // a reset of the record keeps the run of the head it was made for, and a new head's Start carries it over without making it that head's
        var (code, started) = await h.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
        Assert.Equal((0, FakeQcWorld.Sha2), (code, QcJson.Str((JsonObject)started["record"]!, "sha")));
        Assert.Equal(FakeQcWorld.Sha1, QcJson.Str(h.World.Rec(5, 9), "ran_sha"));
        Assert.Equal("qc-not-run", QcGate.Check(h.Config, h.World.Rec(5, 9), FakeQcWorld.Sha2, false, "carol")!.Code);
    }
}
