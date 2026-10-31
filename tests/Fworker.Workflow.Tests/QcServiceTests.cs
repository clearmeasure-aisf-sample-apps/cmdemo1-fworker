using System.Text.Json.Nodes;
using Fworker.Workflows.Qc;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.1: the qc-* verbs of the QcService against plugin/scripts/lib/Qc.ps1: for each verb the answer's fields and exit code, the refusals in the order the script makes them, the record that is saved (field for field,
/// revision and history), the journal lines and the comments it writes. The world is <see cref="FakeQcWorld"/>; no GitHub, no database.
/// </summary>
public sealed class QcServiceTests
{
    static JsonObject Rec(JsonObject answer) => (JsonObject)answer["record"]!;
    static string Sha7 = FakeQcWorld.Sha1[..7];

    static QcHarness Validated(string mergePolicy = "auto", string qc = "{}", string[]? files = null, string author = "alice", bool useTable = true)
    {
        var h = new QcHarness(mergePolicy, qc, useTable);
        h.World.AddValidatedItem(files: files, author: author);
        return h;
    }

    // ---- the answer -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAnswerCarriesTheVerbOkAndTheExitCode_ARefusalItsCodeAndReason()
    {
        var h = Validated();
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal(0, code);
        Assert.Equal(("qc-request", true, 0), (o["verb"]!.ToString(), o["ok"]!.GetValue<bool>(), o["exit"]!.GetValue<int>()));
        var (c2, bad) = await h.Call("qc-request", new { item = 5 });
        Assert.Equal(2, c2);
        Assert.Equal(("qc-request", false, "usage", "missing argument: pr", 2), (bad["verb"]!.ToString(), bad["ok"]!.GetValue<bool>(), bad["code"]!.ToString(), bad["reason"]!.ToString(), bad["exit"]!.GetValue<int>()));
    }

    [Fact]
    public async Task AnUnknownVerbIsRefused_AnEmptyArgumentIsMissing_AndAFaultIsAnInternalError()
    {
        var h = Validated();
        var (c1, o1) = await h.Call("qc-nonsense");
        Assert.Equal((2, "unknown-verb"), (c1, o1["code"]!.ToString()));
        var (c2, o2) = await h.Call("qc-request", new { item = 5, pr = "" });
        Assert.Equal((2, "missing argument: pr"), (c2, o2["reason"]!.ToString()));
        var (c3, o3) = await h.Call("qc-request", new { item = 5, pr = "abc" });
        Assert.Equal((20, "internal-error", 30), (c3, o3["code"]!.ToString(), o3["retryAfterSeconds"]!.GetValue<int>()));
        var (c4, o4) = await h.Call("qc-list", "{ not json");
        Assert.Equal((20, "internal-error"), (c4, o4["code"]!.ToString()));
    }

    [Fact]
    public async Task TheArgumentsTheGatewayAddsForTheScriptAreNotArguments()
    {
        var h = Validated();
        var (code, _) = await h.Call("qc-request", new { item = 5, pr = 9, _session = "x", _cause = "c", _lease = new { epoch = 1 } });
        Assert.Equal(0, code);
    }

    // ---- Start ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartMakesARequestedRecord_WithTheScriptsFields_AndJournalsIt()
    {
        var h = Validated();
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9, link_nonce = "n`1" });
        Assert.Equal(0, code);
        Assert.Equal(("started", 5, 9), (o["outcome"]!.ToString(), o["id"]!.GetValue<int>(), o["pr"]!.GetValue<int>()));
        Assert.False(o.ContainsKey("previous"));
        var rec = Rec(o);
        Assert.Equal("requested", QcJson.Str(rec, "state"));
        Assert.Equal(FakeQcWorld.Sha1, QcJson.Str(rec, "sha"));
        Assert.Equal(1, QcJson.Int(rec, "attempt"));
        Assert.Equal($"qc-9-{Sha7}-1", QcJson.Str(rec, "env_id"));
        Assert.Equal(1, QcJson.Int(rec, "rev"));
        Assert.Equal("alice", QcJson.Str(rec, "author"));
        Assert.Equal("2026-10-29T09:00:00.000Z", QcJson.Str(rec, "requested_at"));
        Assert.Equal("n'1", QcJson.Str(rec, "link_nonce"));
        Assert.Equal(5, QcJson.Int(rec, "item"));
        Assert.Equal("", QcJson.Str(rec, "assignment"));
        Assert.False(QcJson.Has(rec, "trust_confirmed"));
        Assert.Equal(["requested"], ((JsonArray)rec["history"]!).Select(x => QcJson.Str((JsonObject)x!, "state")));
        // the state comment holds the record, the index lists the item, the journal has its line
        Assert.Equal(rec.ToJsonString(), h.World.Rec(5, 9)!.ToJsonString());
        Assert.Equal([5], h.Index.Items());
        var line = Assert.Single(h.World.Journals);
        Assert.Equal((5, "qc-requested", $"9|{FakeQcWorld.Sha1}|1"), (line.Item, line.Event, line.Key));
        Assert.Equal((9, Sha7, 1), (QcJson.Int(line.Fields, "pr"), QcJson.Str(line.Fields, "sha7"), QcJson.Int(line.Fields, "attempt")));
    }

    [Fact]
    public async Task StartingAgainWhileTheRecordIsLiveForTheHeadChangesNothing()
    {
        var h = Validated();
        var first = await h.Start();
        var revision = h.World.Items[5].Revision;
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((0, "existing"), (code, o["outcome"]!.ToString()));
        Assert.Equal(Rec(first).ToJsonString(), Rec(o).ToJsonString());
        Assert.Equal(revision, h.World.Items[5].Revision);
        Assert.Single(h.World.Journals);
    }

    [Fact]
    public async Task StartingAfterAFailureOfTheSameHeadIsTheNextAttempt_AndKeepsTheRunOfTheMergeGate()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        var (c, failed) = await h.Call("qc-fail", new { item = 5, pr = 9, assignment = "a-1", code = "app-down", reason = "crashed" });
        Assert.Equal(0, c);
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal(0, code);
        var rec = Rec(o);
        Assert.Equal((2, "requested", $"qc-9-{Sha7}-2"), (QcJson.Int(rec, "attempt"), QcJson.Str(rec, "state"), QcJson.Str(rec, "env_id")));
        Assert.Equal(FakeQcWorld.Sha1, QcJson.Str(rec, "ran_sha"));       // a new record for the same pull request carries the run over
        Assert.False(o.ContainsKey("previous"));                           // (a failed record is not live: nothing to stop)
        Assert.Equal(2, h.World.Journals.Count(j => j.Event == "qc-requested"));
    }

    [Fact]
    public async Task StartingForANewHeadReplacesTheLiveRecord_AndAnswersTheOldOneAsPrevious()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        h.World.Push(9, FakeQcWorld.Sha2);
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9, sha = FakeQcWorld.Sha2 });
        Assert.Equal(0, code);
        var rec = Rec(o);
        Assert.Equal((1, "requested", FakeQcWorld.Sha2), (QcJson.Int(rec, "attempt"), QcJson.Str(rec, "state"), QcJson.Str(rec, "sha")));
        var previous = (JsonObject)o["previous"]!;
        Assert.Equal((FakeQcWorld.Sha1, "a-1", "qw1"), (QcJson.Str(previous, "sha"), QcJson.Str(previous, "assignment"), QcJson.Str(previous, "worker")));
        Assert.Equal("assigned", QcJson.Str(previous, "state"));
    }

    [Fact]
    public async Task StartForAnOlderHeadIsStale()
    {
        var h = Validated();
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9, sha = FakeQcWorld.Sha2 });
        Assert.Equal((11, "stale-head", FakeQcWorld.Sha1), (code, o["code"]!.ToString(), o["current_sha"]!.ToString()));
        Assert.Null(h.World.Rec(5, 9));
        Assert.Empty(h.World.Journals);
    }

    [Fact]
    public async Task StartRefusesWhatIsNotEligible_WithTheReasonsInTheScriptsOrder()
    {
        var h = Validated();
        h.World.Pulls[9]["draft"] = true;
        h.World.Pulls[9]["head"]!["repo"]!["full_name"] = "evil/r";
        h.World.Items[5].Info = h.World.Items[5].Info with { Column = "Development", Labels = ["factory worker", "Auto Merge"] };
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((10, "not-eligible"), (code, o["code"]!.ToString()));
        Assert.Equal(["draft", "fork", "not-in-validation", "auto-merge"], ((JsonArray)o["reasons"]!).Select(x => x!.ToString()));
        Assert.Equal("PR #9 is not eligible for a QC environment: draft, fork, not-in-validation, auto-merge", o["reason"]!.ToString());
    }

    [Fact]
    public async Task StartRefusesADocumentationOnlyChange()
    {
        var h = Validated(files: ["docs/a.md"]);
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((10, "not-eligible"), (code, o["code"]!.ToString()));
        Assert.Equal(["docs-only"], ((JsonArray)o["reasons"]!).Select(x => x!.ToString()));
    }

    [Fact]
    public async Task StartForAnItemOrPullRequestThatDoesNotExist()
    {
        var h = Validated();
        var (c1, o1) = await h.Call("qc-request", new { item = 6, pr = 9 });
        Assert.Equal((12, "item-not-found"), (c1, o1["code"]!.ToString()));
        var (c2, o2) = await h.Call("qc-request", new { item = 5, pr = 77 });
        Assert.Equal((12, "no-such-pr", "pull request #77 does not exist"), (c2, o2["code"]!.ToString(), o2["reason"]!.ToString()));
    }

    [Fact]
    public async Task AnAuthorWhoIsNotTrustedNeedsTheOperatorsConfirmation()
    {
        var h = Validated(author: "mallory");
        var (c1, o1) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((10, "trust-confirm-needed"), (c1, o1["code"]!.ToString()));
        Assert.Equal(["author-not-trusted:mallory"], ((JsonArray)o1["reasons"]!).Select(x => x!.ToString()));
        // a click cannot confirm; confirm means something only when the operator sets it
        var (c2, o2) = await h.Call("qc-request", new { item = 5, pr = 9, confirm = true });
        Assert.Equal((10, "trust-confirm-needed"), (c2, o2["code"]!.ToString()));
        var (c3, o3) = await h.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
        Assert.Equal(0, c3);
        Assert.True(QcJson.Bool(Rec(o3), "trust_confirmed"));
    }

    [Fact]
    public async Task TheInspectorsMandatoryCodesNeedConfirmationToo_AndTheGatewaysOwnLoginIsTrusted()
    {
        var h = Validated(author: "factory-bot");
        var (c0, _) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal(0, c0);
        var g = Validated();
        g.World.Mandatory = ["secret-like", "not-a-trust-code"];
        var (c1, o1) = await g.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((10, "secret-like"), (c1, string.Join(",", ((JsonArray)o1["reasons"]!).Select(x => x!.ToString()))));
    }

    // ---- Reset ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetMakesTheNextAttempt_KeepsTheOldAssignmentsTheRunAndTheTrust_AndAnswersWhatToStop()
    {
        var h = Validated(author: "mallory");
        await h.Start(); await h.Place(); await h.Ready();
        var (code, o) = await h.Call("qc-reset", new { item = 5, pr = 9 });
        Assert.Equal(0, code);
        Assert.Equal("reset", o["outcome"]!.ToString());
        var rec = Rec(o);
        Assert.Equal(("reset", 2, $"qc-9-{Sha7}-2"), (QcJson.Str(rec, "state"), QcJson.Int(rec, "attempt"), QcJson.Str(rec, "env_id")));
        Assert.Equal(["a-1"], ((JsonArray)rec["old_assignments"]!).Select(x => x!.ToString()));
        Assert.Equal(FakeQcWorld.Sha1, QcJson.Str(rec, "ran_sha"));
        Assert.True(QcJson.Bool(rec, "trust_confirmed"));
        Assert.False(QcJson.Has(rec, "accepted_at"));
        var prev = (JsonObject)o["previous"]!;
        Assert.Equal(("qw1", "a-1", $"qc-9-{Sha7}-1", FakeQcWorld.Sha1, true), (QcJson.Str(prev, "worker"), QcJson.Str(prev, "assignment"), QcJson.Str(prev, "env_id"), QcJson.Str(prev, "sha"), QcJson.Bool(prev, "accepted")));
        Assert.Contains(h.World.Journals, j => j.Event == "qc-reset" && j.Key == $"9|{FakeQcWorld.Sha1}|2");
    }

    [Fact]
    public async Task ResetAtANewHeadStartsAtAttemptOne_AndAnOldHeadIsStale()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        h.World.Push(9, FakeQcWorld.Sha2);
        var (c0, o0) = await h.Call("qc-reset", new { item = 5, pr = 9, sha = FakeQcWorld.Sha1 });
        Assert.Equal((11, "stale-head", FakeQcWorld.Sha2), (c0, o0["code"]!.ToString(), o0["current_sha"]!.ToString()));
        var (c1, o1) = await h.Call("qc-reset", new { item = 5, pr = 9 });
        Assert.Equal(0, c1);
        Assert.Equal((1, FakeQcWorld.Sha2), (QcJson.Int(Rec(o1), "attempt"), QcJson.Str(Rec(o1), "sha")));
    }

    [Fact]
    public async Task ResetWithoutAnEnvironmentOrAfterATeardownIsRefused()
    {
        var h = Validated();
        var (c0, o0) = await h.Call("qc-reset", new { item = 5, pr = 9 });
        Assert.Equal((12, "no-environment", "no QC environment exists for PR #9; press Start"), (c0, o0["code"]!.ToString(), o0["reason"]!.ToString()));
        await h.Start();
        await h.Call("qc-teardown", new { item = 5, pr = 9 });
        var (c1, o1) = await h.Call("qc-reset", new { item = 5, pr = 9 });
        Assert.Equal((12, "no-environment"), (c1, o1["code"]!.ToString()));
    }

    // ---- Teardown -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TeardownOfARecordNobodyAcceptedIsDoneAtOnce()
    {
        var h = Validated();
        await h.Start();
        var (code, o) = await h.Call("qc-teardown", new { item = 5, pr = 9, reason = "lifetime" });
        Assert.Equal((0, "torn-down", false), (code, o["outcome"]!.ToString(), o["needs_stop"]!.GetValue<bool>()));
        var rec = Rec(o);
        Assert.Equal("torn-down", QcJson.Str(rec, "state"));
        Assert.Equal("lifetime", QcJson.Str((JsonObject)rec["teardown"]!, "reason"));
        Assert.Equal(("", "", $"qc-9-{Sha7}-1"), (QcJson.Str((JsonObject)o["env"]!, "worker"), QcJson.Str((JsonObject)o["env"]!, "assignment"), QcJson.Str((JsonObject)o["env"]!, "env_id")));
        Assert.Contains(h.World.Journals, j => j.Event == "qc-torn-down" && QcJson.Str(j.Fields, "reason") == "lifetime");
        Assert.Empty(h.Index.Items());
        var (c2, o2) = await h.Call("qc-teardown", new { item = 5, pr = 9 });
        Assert.Equal((0, "already"), (c2, o2["outcome"]!.ToString()));
    }

    [Theory]
    [InlineData("merged", "merged")] [InlineData("nonsense", "person")] [InlineData(null, "person")]
    public async Task TeardownKnowsTheReasonsOfTheScript_AndTakesAnythingElseForAPerson(string? given, string stored)
    {
        var h = Validated();
        await h.Start();
        var (_, o) = await h.Call("qc-teardown", given is null ? new { item = 5, pr = 9 } : new { item = 5, pr = 9, reason = given });
        Assert.Equal(stored, QcJson.Str((JsonObject)Rec(o)["teardown"]!, "reason"));
    }

    [Fact]
    public async Task TeardownOfAnAcceptedEnvironmentWaitsForTheWorkersStop_TheFirstReasonStands()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        var (c1, o1) = await h.Call("qc-teardown", new { item = 5, pr = 9, reason = "idle" });
        Assert.Equal((0, "stopping", true), (c1, o1["outcome"]!.ToString(), o1["needs_stop"]!.GetValue<bool>()));
        Assert.Equal("ready", QcJson.Str(Rec(o1), "state"));
        Assert.Equal(("qw1", "a-1"), (QcJson.Str((JsonObject)o1["env"]!, "worker"), QcJson.Str((JsonObject)o1["env"]!, "assignment")));
        var rev = QcJson.Int(Rec(o1), "rev");
        var revision = h.World.Items[5].Revision;
        var (c2, o2) = await h.Call("qc-teardown", new { item = 5, pr = 9, reason = "person" });
        Assert.Equal((0, "stopping"), (c2, o2["outcome"]!.ToString()));
        Assert.Equal("idle", QcJson.Str((JsonObject)Rec(o2)["teardown"]!, "reason"));
        Assert.Equal((rev, revision), (QcJson.Int(Rec(o2), "rev"), h.World.Items[5].Revision));    // nothing written again
        // the worker confirms: torn down, with the reason that was asked first
        var (c3, o3) = await h.Call("qc-stopped", new { item = 5, env_id = $"qc-9-{Sha7}-1", assignment = "a-1" });
        Assert.Equal((0, "torn-down", 9), (c3, o3["outcome"]!.ToString(), o3["pr"]!.GetValue<int>()));
        Assert.Equal(("torn-down", "idle"), (QcJson.Str(Rec(o3), "state"), QcJson.Str((JsonObject)Rec(o3)["teardown"]!, "reason")));
        var (c4, o4) = await h.Call("qc-stopped", new { item = 5, env_id = $"qc-9-{Sha7}-1", assignment = "a-1" });
        Assert.Equal((0, "already"), (c4, o4["outcome"]!.ToString()));
    }

    [Fact]
    public async Task TeardownOfAnItemThatVanishedHasNothingToTearDown()
    {
        var h = Validated();
        var (code, o) = await h.Call("qc-teardown", new { item = 99, pr = 9 });
        Assert.Equal((0, "already"), (code, o["outcome"]!.ToString()));
        var (c2, o2) = await h.Call("qc-teardown", new { item = 5, pr = 9 });
        Assert.Equal((0, "already"), (c2, o2["outcome"]!.ToString()));
    }

    // ---- qc-transition --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlacingAJobRecordsTheWorkerAndTheAssignment_AndNamesTheWorkerInTheOfferComments()
    {
        var h = Validated();
        await h.Call("qc-link", new { item = 5, pr = 9 });
        await h.Start();
        var (code, o) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "qw`1", assignment = "a-1", mode = "first", sha = FakeQcWorld.Sha1 });
        Assert.Equal(0, code);
        var rec = Rec(o);
        Assert.Equal(("assigned", "qw'1", "a-1", "first", 2), (QcJson.Str(rec, "state"), QcJson.Str(rec, "worker"), QcJson.Str(rec, "assignment"), QcJson.Str(rec, "mode"), QcJson.Int(rec, "rev")));
        // both offer comments now say where the environment runs, once
        foreach (var number in new[] { 9, 5 })
        {
            var offer = Assert.Single(h.World.Offers(number));
            Assert.Contains("runs on **qw'1**'s computer", offer.Body);
            Assert.Contains("This environment was requested and runs on that worker.", offer.Body);
            Assert.Contains("[QC page](https://gateway.example.test/qc/9?t=q1.", offer.Body);
        }
        var (c2, o2) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "qw2", assignment = "a-2" });
        Assert.Equal(0, c2);
        Assert.Equal(("a-1", "a-2", "assigned", 3), (string.Join(",", ((JsonArray)Rec(o2)["old_assignments"]!).Select(x => x!.ToString())), QcJson.Str(Rec(o2), "assignment"), QcJson.Str(Rec(o2), "state"), QcJson.Int(Rec(o2), "rev")));
        // the second placement edited nothing, and the log says which skip it took, for each of the two comments (#592)
        Assert.Equal(2, h.Log.Count(l => l.Contains("already says it runs on a worker")));
        Assert.DoesNotContain(h.Log, l => l.Contains("offer not found"));
    }

    [Fact]
    public async Task PlacingAJobWithNoOfferCommentLogsThatTheOfferWasNotFound()
    {
        var h = Validated();
        await h.Start();
        var (code, _) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "qw1", assignment = "a-1", mode = "first", sha = FakeQcWorld.Sha1 });
        Assert.Equal(0, code);
        Assert.Empty(h.World.Offers(9));
        Assert.Equal(2, h.Log.Count(l => l.Contains("offer not found")));
        Assert.DoesNotContain(h.Log, l => l.Contains("already says"));
    }

    [Fact]
    public async Task ATransitionIsRefusedForAnotherHeadOrAnotherAssignment_OrForAMoveTheMachineDoesNotKnow()
    {
        var h = Validated();
        await h.Start();
        var (c0, o0) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "w", assignment = "a", sha = FakeQcWorld.Sha2 });
        Assert.Equal((11, "stale-head"), (c0, o0["code"]!.ToString()));
        await h.Place(accept: false);
        var (c1, o1) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "failed", assignment_was = "other" });
        Assert.Equal((11, "stale-assignment"), (c1, o1["code"]!.ToString()));
        var (c2, o2) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "running" });
        Assert.Equal((10, "bad-transition", "a QC environment cannot go from assigned to running"), (c2, o2["code"]!.ToString(), o2["reason"]!.ToString()));
        var (c3, o3) = await h.Call("qc-transition", new { item = 5, pr = 8, to = "failed" });
        Assert.Equal((12, "no-environment", "no QC record for PR #8"), (c3, o3["code"]!.ToString(), o3["reason"]!.ToString()));
        await h.Progress("building");
        var (c4, o4) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "w", assignment = "b" });
        Assert.Equal((10, "bad-transition", "cannot assign from building"), (c4, o4["code"]!.ToString(), o4["reason"]!.ToString()));
        var (c5, o5) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned" });
        Assert.Equal(10, c5);   // (building: refused before the missing arguments)
    }

    [Fact]
    public async Task AssigningNeedsTheWorkerAndTheAssignment()
    {
        var h = Validated();
        await h.Start();
        var (c0, o0) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned" });
        Assert.Equal((2, "missing argument: assignment"), (c0, o0["reason"]!.ToString()));
        var (c1, o1) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", assignment = "a-1" });
        Assert.Equal((2, "missing argument: worker"), (c1, o1["reason"]!.ToString()));
    }

    [Fact]
    public async Task AFailureTheGatewayNoticesIsRecordedWithAKnownCode_ElseStartFailed()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        var (c, o) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "failed", code = "app-down", reason = "the worker reported that the application is not running", sha = FakeQcWorld.Sha1 });
        Assert.Equal(0, c);
        Assert.Equal(("failed", "app-down"), (QcJson.Str(Rec(o), "state"), QcJson.Str((JsonObject)Rec(o)["fail"]!, "code")));
        Assert.Contains(h.World.Journals, j => j.Event == "qc-failed" && QcJson.Str(j.Fields, "code") == "app-down");
        var g = Validated();
        await g.Start();
        var (_, o2) = await g.Call("qc-transition", new { item = 5, pr = 9, to = "failed", code = "made-up" });
        Assert.Equal("start-failed", QcJson.Str((JsonObject)Rec(o2)["fail"]!, "code"));
    }

    [Fact]
    public async Task TheGatewayTearsDownAfterTheWorkersLeaseRanOut_WithTheReasonAskedFirst_ElseAPerson()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        await h.Call("qc-teardown", new { item = 5, pr = 9, reason = "idle" });
        var (c, o) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "torn-down" });
        Assert.Equal(0, c);
        Assert.Equal(("torn-down", "idle"), (QcJson.Str(Rec(o), "state"), QcJson.Str((JsonObject)Rec(o)["teardown"]!, "reason")));
        var g = Validated();
        await g.Start();
        var (_, o2) = await g.Call("qc-transition", new { item = 5, pr = 9, to = "torn-down" });
        Assert.Equal("person", QcJson.Str((JsonObject)Rec(o2)["teardown"]!, "reason"));
        var k = Validated();
        await k.Start();
        var (_, o3) = await k.Call("qc-transition", new { item = 5, pr = 9, to = "torn-down", reason = "merged" });
        Assert.Equal("merged", QcJson.Str((JsonObject)Rec(o3)["teardown"]!, "reason"));
    }

    [Fact]
    public async Task ALostWorkerIsRelaunchedUpToTheLimit_ThenTheEnvironmentFailsWithNoWorker()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        for (var i = 1; i <= 2; i++)
        {
            var (c, o) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", relaunch = true, assignment_was = $"a-{i}", max_relaunches = 2 });
            Assert.Equal((0, "relaunch"), (c, o["outcome"]!.ToString()));
            var rec = Rec(o);
            Assert.Equal(("assigned", "", "", "relaunch", i), (QcJson.Str(rec, "state"), QcJson.Str(rec, "assignment"), QcJson.Str(rec, "worker"), QcJson.Str(rec, "mode"), QcJson.Int(rec, "relaunches")));
            foreach (var gone in new[] { "accepted_at", "ready_at", "expires_at", "port", "probe", "fail" }) Assert.False(QcJson.Has(rec, gone));
            Assert.Contains($"a-{i}", ((JsonArray)rec["old_assignments"]!).Select(x => x!.ToString()));
            await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "qw2", assignment = $"a-{i + 1}" });
        }
        var (cf, of) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", relaunch = true, max_relaunches = 2 });
        Assert.Equal((0, "failed"), (cf, of["outcome"]!.ToString()));
        var fail = (JsonObject)Rec(of)["fail"]!;
        Assert.Equal(("no-worker", "the worker was lost 2 times; the environment was not started again"), (QcJson.Str(fail, "code"), QcJson.Str(fail, "reason")));
        Assert.Contains(h.World.Journals, j => j.Event == "qc-failed" && QcJson.Str(j.Fields, "code") == "no-worker");
    }

    [Fact]
    public async Task TheLimitOfRelaunchesComesFromTheConfigurationWhenTheCallNamesNone_AndAFinishedRecordCannotBeRelaunched()
    {
        var h = Validated();
        await h.Start();
        var (c0, o0) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", relaunch = true });
        Assert.Equal((10, "bad-transition", "cannot relaunch from requested"), (c0, o0["code"]!.ToString(), o0["reason"]!.ToString()));
        await h.Place();
        for (var i = 0; i < 3; i++)
        {
            var (c, _) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", relaunch = true });
            Assert.Equal(0, c);
            await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "w", assignment = $"b-{i}" });
        }
        var (c3, o3) = await h.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", relaunch = true });
        Assert.Equal("failed", o3["outcome"]!.ToString());   // timing.qcMaxRelaunches is 3 in the test configuration
    }

    // ---- the worker's side ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AcceptStampsTheRecordOnce_AndJournalsTheAssignment()
    {
        var h = Validated();
        await h.Start(); await h.Place(accept: false);
        var o = await h.Service.Accept(5, "a-1", "q`w1");
        Assert.Equal((true, true, "qc-env", "qc", "a-1", "assigned", "first", 9), (o["ok"]!.GetValue<bool>(), o["accepted"]!.GetValue<bool>(), o["kind"]!.ToString(), o["role"]!.ToString(), o["assignment"]!.ToString(), o["state"]!.ToString(), o["mode"]!.ToString(), o["pr"]!.GetValue<int>()));
        var rec = h.World.Rec(5, 9)!;
        Assert.Equal("q'w1", QcJson.Str(rec, "worker"));
        Assert.Equal("2026-10-29T09:00:00.000Z", QcJson.Str(rec, "accepted_at"));
        var rev = QcJson.Int(rec, "rev");
        Assert.Contains(h.World.Journals, j => j.Event == "qc-assigned" && j.Key == "a-1" && QcJson.Str(j.Fields, "worker") == "q'w1");
        h.Now = h.Now.AddMinutes(5);
        await h.Service.Accept(5, "a-1", "someone-else");
        Assert.Equal((rev, "q'w1", "2026-10-29T09:00:00.000Z"), (QcJson.Int(h.World.Rec(5, 9)!, "rev"), QcJson.Str(h.World.Rec(5, 9)!, "worker"), QcJson.Str(h.World.Rec(5, 9)!, "accepted_at")));
        Assert.Single(h.World.Journals, j => j.Event == "qc-assigned");
    }

    [Fact]
    public async Task AcceptOfAJobTheRecordNoLongerHoldsIsStale()
    {
        var h = Validated();
        await h.Start(); await h.Place(accept: false);
        var e = await Assert.ThrowsAsync<QcReject>(() => h.Service.Accept(5, "a-9", "qw1"));
        Assert.Equal((11, "stale-assignment", "this QC assignment is no longer current"), (e.Exit, e.Code, e.Message));
        await h.Call("qc-teardown", new { item = 5, pr = 9 });
        var e2 = await Assert.ThrowsAsync<QcReject>(() => h.Service.Accept(5, "a-1", "qw1"));
        Assert.Equal(11, e2.Exit);
    }

    [Fact]
    public async Task TheJobHasTheLimitsAndTheUntrustedTitle()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        h.World.Pulls[9]["title"] = "Fix `it`";
        var (code, o) = await h.Call("qc-job", new { item = 5, pr = 9, assignment = "a-1" });
        Assert.Equal(0, code);
        Assert.Equal((FakeQcWorld.Sha1, "factory-worker/5-development", $"qc-9-{Sha7}-1", 1), (o["sha"]!.ToString(), o["branch"]!.ToString(), o["env_id"]!.ToString(), o["attempt"]!.GetValue<int>()));
        var limits = (JsonObject)o["limits"]!;
        Assert.Equal((8d, 480d, 8d), (limits["buildCapMinutes"]!.GetValue<double>(), limits["lifetimeMinutes"]!.GetValue<double>(), limits["lifetimeHours"]!.GetValue<double>()));
        Assert.Equal(("Fix `it`", true), (QcJson.Str((JsonObject)o["title"]!, "text"), QcJson.Bool((JsonObject)o["title"]!, "untrusted")));
        Assert.True(QcJson.Bool((JsonObject)o["trust"]!, "ok"));
        Assert.Empty((JsonArray)((JsonObject)o["trust"]!)["reasons"]!);
    }

    [Fact]
    public async Task TheJobIsRefusedForAnotherAssignment_AFork_AMovedHead_AndAnUnconfirmedUntrustedRun()
    {
        var h = Validated(author: "mallory");
        await h.Start(); await h.Place();
        var (c0, o0) = await h.Call("qc-job", new { item = 5, pr = 9, assignment = "a-1" });
        Assert.Equal(0, c0);   // (the operator confirmed at Start: Start() confirms)
        var (c1, o1) = await h.Call("qc-job", new { item = 5, pr = 9, assignment = "other" });
        Assert.Equal((40, "claim-held"), (c1, o1["code"]!.ToString()));
        h.World.Pulls[9]["head"]!["repo"]!["full_name"] = "evil/r";
        var (c2, o2) = await h.Call("qc-job", new { item = 5, pr = 9, assignment = "a-1" });
        Assert.Equal((10, "fork-pr", "pull requests from forks are never run"), (c2, o2["code"]!.ToString(), o2["reason"]!.ToString()));
        h.World.Pulls[9]["head"]!["repo"]!["full_name"] = "o/r";
        h.World.Push(9, FakeQcWorld.Sha2);
        var (c3, o3) = await h.Call("qc-job", new { item = 5, pr = 9, assignment = "a-1" });
        Assert.Equal((11, "stale-head", FakeQcWorld.Sha2), (c3, o3["code"]!.ToString(), o3["current_sha"]!.ToString()));
        Assert.Contains("press Reset", o3["reason"]!.ToString());
    }

    [Fact]
    public async Task AJobNobodyConfirmedIsRefusedAtTheWorker()
    {
        var h = Validated(author: "alice");
        await h.Start(); await h.Place();
        h.World.Pulls[9]["user"]!["login"] = "mallory";    // the author changed after Start (or the inspector found a reason since)
        var (c, o) = await h.Call("qc-job", new { item = 5, pr = 9, assignment = "a-1" });
        Assert.Equal((10, "qc-trust-refused"), (c, o["code"]!.ToString()));
        Assert.Equal(["author-not-trusted:mallory"], ((JsonArray)o["reasons"]!).Select(x => x!.ToString()));
        Assert.Equal((10, "wrong-state"), await WrongState(h));
    }

    static async Task<(int, string)> WrongState(QcHarness h)
    {
        await h.Call("qc-teardown", new { item = 5, pr = 9 });   // (accepted: waits for the worker)
        await h.Call("qc-stopped", new { item = 5, env_id = $"qc-9-{Sha7}-1", assignment = "a-1" });
        var (c, o) = await h.Call("qc-job", new { item = 5, pr = 9, assignment = "a-1" });
        return (c, o["code"]!.ToString());
    }

    [Fact]
    public async Task ProgressMovesBuildingThenRunning_AndAnswersTheCleanedLogTail()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        var (c0, o0) = await h.Call("qc-progress", new { item = 5, pr = 9, assignment = "a-1", state = "ready" });
        Assert.Equal((2, "usage", "state must be building or running"), (c0, o0["code"]!.ToString(), o0["reason"]!.ToString()));
        var (c1, o1) = await h.Call("qc-progress", new { item = 5, pr = 9, assignment = "a-1", state = "running" });
        Assert.Equal((10, "bad-transition"), (c1, o1["code"]!.ToString()));
        var (c2, o2) = await h.Call("qc-progress", new { item = 5, pr = 9, assignment = "a-1", state = "building", log_tail = "a`b" + new string('x', 2000) });
        Assert.Equal((0, "building"), (c2, o2["state"]!.ToString()));
        Assert.Equal(1500, o2["log_tail"]!.ToString().Length);
        Assert.StartsWith("a'b", o2["log_tail"]!.ToString());
        var rev = QcJson.Int(h.World.Rec(5, 9)!, "rev");
        var (c3, o3) = await h.Call("qc-progress", new { item = 5, pr = 9, assignment = "a-1", state = "building" });
        Assert.Equal((0, "building"), (c3, o3["state"]!.ToString()));
        Assert.Equal(rev, QcJson.Int(h.World.Rec(5, 9)!, "rev"));   // the same state again writes nothing
    }

    [Fact]
    public async Task TheAssignmentOfTheTokenMustBeTheRecords()
    {
        var h = Validated();
        var (c0, o0) = await h.Call("qc-progress", new { item = 5, pr = 9, assignment = "a-1", state = "building" });
        Assert.Equal((12, "no-environment", "no QC environment is recorded for this pull request"), (c0, o0["code"]!.ToString(), o0["reason"]!.ToString()));
        await h.Start(); await h.Place();
        var (c1, o1) = await h.Call("qc-progress", new { item = 5, pr = 9, assignment = "someone-elses", state = "building" });
        Assert.Equal((40, "claim-held", "this QC environment is not assigned to you"), (c1, o1["code"]!.ToString(), o1["reason"]!.ToString()));
        await h.Call("qc-reset", new { item = 5, pr = 9 });
        var (c2, o2) = await h.Call("qc-progress", new { item = 5, pr = 9, assignment = "a-1", state = "building" });
        Assert.Equal((11, "stale-assignment"), (c2, o2["code"]!.ToString()));
        Assert.Contains("stop and remove what it built", o2["reason"]!.ToString());
    }

    [Fact]
    public async Task ReportRecordsTheRunForTheMergeGate_Once()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        await h.Progress("building"); await h.Progress("running");
        var env = $"qc-9-{Sha7}-1";
        var (c0, o0) = await h.Call("qc-report", new { item = 5, pr = 9, assignment = "a-1", sha = FakeQcWorld.Sha2, env_id = env });
        Assert.Equal((11, "stale-head", FakeQcWorld.Sha1), (c0, o0["code"]!.ToString(), o0["current_sha"]!.ToString()));
        var (c1, o1) = await h.Call("qc-report", new { item = 5, pr = 9, assignment = "a-1", sha = FakeQcWorld.Sha1, env_id = "qc-other" });
        Assert.Equal((11, "stale-assignment"), (c1, o1["code"]!.ToString()));
        h.Now = h.Now.AddMinutes(3);
        var (c, o) = await h.Call("qc-report", new { item = 5, pr = 9, assignment = "a-1", sha = FakeQcWorld.Sha1, env_id = env, probe = "verified", url = "http://qw1.test:5050", port = "5050" });
        Assert.Equal((0, "ready", "verified"), (c, o["outcome"]!.ToString(), o["probe"]!.ToString()));
        var rec = Rec(o);
        Assert.Equal(("ready", FakeQcWorld.Sha1, "verified", 5050), (QcJson.Str(rec, "state"), QcJson.Str(rec, "ran_sha"), QcJson.Str(rec, "probe"), QcJson.Int(rec, "port")));
        Assert.Equal(("2026-10-29T09:03:00.000Z", "2026-10-29T17:03:00.000Z"), (QcJson.Str(rec, "ready_at"), QcJson.Str(rec, "expires_at")));
        Assert.False(QcJson.Has(rec, "url"));   // (the URL is the gateway's memory, never the record)
        var ready = Assert.Single(h.World.Journals, j => j.Event == "qc-ready");
        Assert.Equal(("http://qw1.test:5050", "qw1", $"9|{FakeQcWorld.Sha1}|1"), (QcJson.Str(ready.Fields, "url"), QcJson.Str(ready.Fields, "worker"), ready.Key));
        // a later report with another probe result changes the probe and the revision only
        var rev = QcJson.Int(rec, "rev");
        h.Now = h.Now.AddMinutes(1);
        var (c2, o2) = await h.Call("qc-report", new { item = 5, pr = 9, assignment = "a-1", sha = FakeQcWorld.Sha1, env_id = env, probe = "bogus" });
        Assert.Equal("unverified", o2["probe"]!.ToString());
        Assert.Equal((rev + 1, "2026-10-29T09:03:00.000Z"), (QcJson.Int(Rec(o2), "rev"), QcJson.Str(Rec(o2), "ready_at")));
        Assert.Single(h.World.Journals, j => j.Event == "qc-ready");
    }

    [Fact]
    public async Task ReportIsRefusedBeforeTheEnvironmentRuns()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        var (c, o) = await h.Call("qc-report", new { item = 5, pr = 9, assignment = "a-1", sha = FakeQcWorld.Sha1, env_id = $"qc-9-{Sha7}-1" });
        Assert.Equal((10, "bad-transition", "a QC environment cannot go from assigned to ready"), (c, o["code"]!.ToString(), o["reason"]!.ToString()));
    }

    [Fact]
    public async Task FailRecordsTheCodeAndACleanReason_AndJournalsItWithoutAMention()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        var (c0, o0) = await h.Call("qc-fail", new { item = 5, pr = 9, assignment = "a-1", code = "nope" });
        Assert.Equal((2, "usage", "unknown failure code 'nope'"), (c0, o0["code"]!.ToString(), o0["reason"]!.ToString()));
        var token = "ghp_" + new string('B', 26);
        var (c, o) = await h.Call("qc-fail", new { item = 5, pr = 9, assignment = "a-1", code = "build-failed", reason = $"ping @bob `dotnet build` failed {token}", log_tail = "tail`" });
        Assert.Equal((0, "failed", "tail'"), (c, o["state"]!.ToString(), o["log_tail"]!.ToString()));
        var fail = (JsonObject)Rec(o)["fail"]!;
        Assert.Equal(("build-failed", "ping @bob 'dotnet build' failed [redacted]"), (QcJson.Str(fail, "code"), QcJson.Str(fail, "reason")));
        var line = Assert.Single(h.World.Journals, j => j.Event == "qc-failed");
        Assert.Equal("ping @​bob 'dotnet build' failed [redacted]", QcJson.Str(line.Fields, "reason"));
        var (c2, o2) = await h.Call("qc-fail", new { item = 5, pr = 9, assignment = "a-1", code = "build-failed" });
        Assert.Equal((10, "bad-transition"), (c2, o2["code"]!.ToString()));
    }

    [Fact]
    public async Task StoppedAnswersOldEnvironmentsAndUnknownOnes()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Call("qc-reset", new { item = 5, pr = 9 });
        var (c0, o0) = await h.Call("qc-stopped", new { item = 5, env_id = "qc-9-nothing-1", assignment = "a-1" });
        Assert.Equal((0, "old-environment"), (c0, o0["outcome"]!.ToString()));   // an assignment the record used to have
        var (c1, o1) = await h.Call("qc-stopped", new { item = 5, env_id = "qc-9-nothing-1", assignment = "zzz" });
        Assert.Equal((12, "no-environment"), (c1, o1["code"]!.ToString()));
        Assert.Equal("no QC environment 'qc-9-nothing-1' is recorded for this item", o1["reason"]!.ToString());
        var (c2, o2) = await h.Call("qc-stopped", new { item = 5, env_id = $"qc-9-{Sha7}-2", assignment = "a-1" });
        Assert.Equal((11, "stale-assignment"), (c2, o2["code"]!.ToString()));    // the record of that environment no longer holds the old assignment
        var (c3, o3) = await h.Call("qc-stopped", new { item = 5, env_id = $"qc-9-{Sha7}-2", assignment = "other" });
        Assert.Equal((40, "claim-held"), (c3, o3["code"]!.ToString()));
    }

    // ---- the approval ---------------------------------------------------------------------------------------------------------------------------------

    static JsonObject Approved(string by = "carol", string sha = FakeQcWorld.Sha1, string state = "APPROVED") =>
        new() { ["user"] = new JsonObject { ["login"] = by }, ["state"] = state, ["commit_id"] = sha };

    [Fact]
    public async Task ApprovalFollowsGitHub_SetAndClearedOnTheRecord_AndSaysWhetherTheRunIsRecorded()
    {
        var h = new QcHarness("peer-approved", humanApprovers: "[\"carol\"]");
        h.World.AddValidatedItem();
        await h.Start(); await h.Place(); await h.Ready();
        var (c0, o0) = await h.Call("qc-approval", new { item = 5, pr = 9 });
        Assert.Equal((0, false, "", true, false), (c0, o0["approved"]!.GetValue<bool>(), o0["by"]!.ToString(), o0["ran"]!.GetValue<bool>(), o0["changed"]!.GetValue<bool>()));
        Assert.Equal(FakeQcWorld.Sha1, o0["sha"]!.ToString());
        h.World.ReviewsOf[9] = [Approved()];
        h.Now = h.Now.AddMinutes(1);
        var rev = QcJson.Int(h.World.Rec(5, 9)!, "rev");
        var (c1, o1) = await h.Call("qc-approval", new { item = 5, pr = 9 });
        Assert.Equal((0, true, "carol", true, true), (c1, o1["approved"]!.GetValue<bool>(), o1["by"]!.ToString(), o1["ran"]!.GetValue<bool>(), o1["changed"]!.GetValue<bool>()));
        var approved = (JsonObject)h.World.Rec(5, 9)!["approved"]!;
        Assert.Equal(("carol", FakeQcWorld.Sha1, "2026-10-29T09:01:00.000Z"), (QcJson.Str(approved, "by"), QcJson.Str(approved, "sha"), QcJson.Str(approved, "at")));
        Assert.Equal(rev + 1, QcJson.Int(h.World.Rec(5, 9)!, "rev"));
        var (_, o2) = await h.Call("qc-approval", new { item = 5, pr = 9 });
        Assert.False(o2["changed"]!.GetValue<bool>());                    // the copy already follows GitHub
        h.World.ReviewsOf[9] = [Approved(), Approved(state: "DISMISSED")];
        var (_, o3) = await h.Call("qc-approval", new { item = 5, pr = 9 });
        Assert.Equal((false, true), (o3["approved"]!.GetValue<bool>(), o3["changed"]!.GetValue<bool>()));
        Assert.False(QcJson.Has(h.World.Rec(5, 9), "approved"));
    }

    [Fact]
    public async Task ApprovalWithoutARecordChangesNothing()
    {
        var h = new QcHarness("peer-approved", humanApprovers: "[\"carol\"]");
        h.World.AddValidatedItem();
        h.World.ReviewsOf[9] = [Approved()];
        var (c, o) = await h.Call("qc-approval", new { item = 5, pr = 9 });
        Assert.Equal((0, true, false, false), (c, o["approved"]!.GetValue<bool>(), o["ran"]!.GetValue<bool>(), o["changed"]!.GetValue<bool>()));
        Assert.Equal(0, h.World.Items[5].Revision);
    }

    // ---- links and pages ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LinkOffersTheEnvironmentOnThePullRequestAndTheItem_OnceForTheHead()
    {
        var h = Validated();
        var (c, o) = await h.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal((0, "posted", 5, 9), (c, o["outcome"]!.ToString(), o["id"]!.GetValue<int>(), o["pr"]!.GetValue<int>()));
        var onPr = Assert.Single(h.World.Offers(9)); var onItem = Assert.Single(h.World.Offers(5));
        Assert.StartsWith($"QC environment available for PR #9 at head {Sha7}: [Start the QC environment](https://gateway.example.test/qc/9?t=q1.", onPr.Body);
        Assert.Contains("The link works for 7 days and only for this commit.", onPr.Body);
        Assert.Contains($"<!-- qc link pr=9 sha={FakeQcWorld.Sha1} exp=", onPr.Body);
        Assert.Equal(("factory-bot", onPr.Login), ("factory-bot", onItem.Login));
        var journal = Assert.Single(h.World.Journals);
        Assert.Equal(("qc-offered", $"9|{FakeQcWorld.Sha1}"), (journal.Event, journal.Key));
        var (_, again) = await h.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal("unchanged", again["outcome"]!.ToString());
        Assert.Single(h.World.Journals);
        // a push makes a new offer, edited in place, for the new head
        h.World.Push(9, FakeQcWorld.Sha2);
        var (_, edited) = await h.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal("edited", edited["outcome"]!.ToString());
        Assert.Contains($"sha={FakeQcWorld.Sha2}", Assert.Single(h.World.Offers(9)).Body);
        Assert.Equal(2, h.World.Journals.Count);
    }

    [Fact]
    public async Task AnOfferWithAnHourToLiveIsRenewed()
    {
        var h = Validated();
        await h.Call("qc-link", new { item = 5, pr = 9 });
        h.Now = h.Now.AddDays(7).AddHours(-23);   // less than a day left on the link
        var (_, o) = await h.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal("edited", o["outcome"]!.ToString());
    }

    [Fact]
    public async Task LinkIsRefusedWithoutTheGatewaysSecretOrAddress_Or_ForWhatIsNotEligible()
    {
        var noSecret = new QcHarness(env: n => n == "FACTORY_PUBLIC_URL" ? "https://g.test" : null);
        noSecret.World.AddValidatedItem();
        var (c0, o0) = await noSecret.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal((30, "no-token-secret", "FACTORY_TOKEN_SECRET is not set on the gateway"), (c0, o0["code"]!.ToString(), o0["reason"]!.ToString()));
        var noUrl = new QcHarness(env: n => n == "FACTORY_TOKEN_SECRET" ? QcHarness.Secret : null);
        noUrl.World.AddValidatedItem();
        var (c1, o1) = await noUrl.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal((30, "public-url-not-reachable"), (c1, o1["code"]!.ToString()));
        var h = Validated();
        h.World.Pulls[9]["draft"] = true;
        var (c2, o2) = await h.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal((10, "not-eligible"), (c2, o2["code"]!.ToString()));
        Assert.Empty(h.World.Offers(9));
    }

    [Fact]
    public async Task ViewShowsThePullRequestItsEligibilityAndRecord_AndWhatNeedsConfirmation()
    {
        var h = Validated(author: "mallory");
        var (c, o) = await h.Call("qc-view", new { item = 5, pr = 9, sha = FakeQcWorld.Sha1 });
        Assert.Equal(0, c);
        Assert.Equal(("Trial", "mallory", FakeQcWorld.Sha1, "open", true), (o["title"]!.ToString(), o["author"]!.ToString(), o["head_sha"]!.ToString(), o["pr_state"]!.ToString(), o["eligible"]!.GetValue<bool>()));
        Assert.Equal(["author-not-trusted:mallory"], ((JsonArray)o["needs_confirmation"]!).Select(x => x!.ToString()));
        Assert.False(o.ContainsKey("record"));
        await h.Start();
        var (_, live) = await h.Call("qc-view", new { item = 5, pr = 9 });
        Assert.Empty((JsonArray)live["needs_confirmation"]!);    // (a live environment needs no confirmation)
        Assert.Equal("requested", QcJson.Str((JsonObject)live["record"]!, "state"));
        h.World.Pulls[9]["merged"] = true; h.World.Pulls[9]["state"] = "closed";
        var (_, merged) = await h.Call("qc-view", new { item = 5, pr = 9 });
        Assert.Equal(("merged", false), (merged["pr_state"]!.ToString(), merged["eligible"]!.GetValue<bool>()));
        Assert.Contains("pr-not-open", ((JsonArray)merged["reasons"]!).Select(x => x!.ToString()));
    }

    [Fact]
    public async Task ViewOfAnOldHeadIsStale_AndHandsOutTheLinkForTheCurrentOne()
    {
        var h = Validated();
        var (c, o) = await h.Call("qc-view", new { item = 5, pr = 9, sha = FakeQcWorld.Sha2 });
        Assert.Equal((11, "stale-head", "this link is for an older commit of the pull request"), (c, o["code"]!.ToString(), o["reason"]!.ToString()));
        Assert.Equal((FakeQcWorld.Sha1, FakeQcWorld.Sha1, "Trial", 9, 5), (o["current_sha"]!.ToString(), o["head_sha"]!.ToString(), o["title"]!.ToString(), o["pr"]!.GetValue<int>(), o["item"]!.GetValue<int>()));
        Assert.StartsWith("q1.", o["current_token"]!.ToString());
        Assert.False(o.ContainsKey("eligible"));
    }

    // ---- the list -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListShowsWhatCanStart_AndTheRecordsWithTheirTitleAndHead()
    {
        var h = Validated();
        h.World.AddValidatedItem(6, 10, FakeQcWorld.Sha2, labels: ["factory worker"]);
        h.World.AddValidatedItem(7, 11, labels: ["other"]);                                     // not the factory's
        h.World.AddValidatedItem(8, 12, column: "Development");                                 // not in validation
        var (c, o) = await h.Call("qc-list");
        Assert.Equal(0, c);
        Assert.Equal([(5, 9), (6, 10)], ((JsonArray)o["available"]!).Select(x => (QcJson.Int((JsonObject)x!, "item"), QcJson.Int((JsonObject)x!, "pr"))));
        var first = (JsonObject)((JsonArray)o["available"]!)[0]!;
        Assert.Equal((FakeQcWorld.Sha1, "Trial", "alice", "2026-10-29T08:00:00Z"), (QcJson.Str(first, "sha"), QcJson.Str(first, "title"), QcJson.Str(first, "author"), QcJson.Str(first, "created_at")));
        Assert.Empty((JsonArray)o["records"]!);
        await h.Start(5, 9);
        await h.Call("qc-teardown", new { item = 5, pr = 9 });
        await h.Start(5, 9);
        h.World.Push(9, FakeQcWorld.Sha2);
        var (_, o2) = await h.Call("qc-list");
        Assert.Equal([(6, 10)], ((JsonArray)o2["available"]!).Select(x => (QcJson.Int((JsonObject)x!, "item"), QcJson.Int((JsonObject)x!, "pr"))));
        var rec = (JsonObject)Assert.Single((JsonArray)o2["records"]!)!;
        Assert.Equal(("requested", "Trial", FakeQcWorld.Sha2, true), (QcJson.Str(rec, "state"), QcJson.Str(rec, "title"), QcJson.Str(rec, "head_sha"), QcJson.Bool(rec, "pr_open")));
        Assert.False(QcJson.Has(h.World.Rec(5, 9), "title"));      // (a copy: nothing here is saved)
        var (_, all) = await h.Call("qc-list", new { include = "all" });
        Assert.Single((JsonArray)all["records"]!);
    }

    [Fact]
    public async Task ListWithAllShowsTheRecordsThatAreOver()
    {
        var h = Validated();
        await h.Start();
        await h.Call("qc-teardown", new { item = 5, pr = 9 });
        var (_, live) = await h.Call("qc-list");
        Assert.Empty((JsonArray)live["records"]!);
        var (_, all) = await h.Call("qc-list", new { include = "all" });
        Assert.Equal("torn-down", QcJson.Str((JsonObject)Assert.Single((JsonArray)all["records"]!)!, "state"));
    }

    // ---- the copy in the table, the state comment's conflicts, the lock -------------------------------------------------------------------------------

    [Fact]
    public async Task EverySaveOfARecordIsFollowedByItsCopyInTheTable()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        var row = Assert.Single(h.Table.Rows).Value;
        Assert.Equal(("o/r!9", "o/r", "o/r#5", "ready"), (row.Key, row.Repository, row.ItemKey, row.Status));
        Assert.Equal(h.World.Rec(5, 9)!.ToJsonString(), row.State.ToJsonString());
        Assert.True(row.Revision >= 4);
        var live = await h.Table.Live("o/r", default);
        Assert.Single(live);
    }

    [Fact]
    public async Task AFaultOfTheTableDoesNotFailTheVerb_ItIsLogged()
    {
        var h = Validated();
        h.Table.SaveFault = new InvalidOperationException("the database is down");
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal(0, code);
        Assert.Equal("requested", QcJson.Str(h.World.Rec(5, 9), "state"));
        Assert.Contains(h.Log, l => l.Contains("the database is down"));
    }

    [Fact]
    public async Task AGatewayWithoutATableWorksAsBefore()
    {
        var h = Validated(useTable: false);
        await h.Start(); await h.Place(); await h.Ready();
        Assert.Equal("ready", h.State());
        Assert.Empty(h.Table.Rows);
    }

    [Fact]
    public async Task ARecordTheCommentLacksIsTakenFromTheTable()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        h.World.State(5).Remove("qc");      // the state comment was lost (deleted by a person, or an item taken over)
        var (c, o) = await h.Call("qc-view", new { item = 5, pr = 9 });
        Assert.Equal(0, c);
        Assert.Equal("ready", QcJson.Str((JsonObject)o["record"]!, "state"));
        var (c2, o2) = await h.Call("qc-reset", new { item = 5, pr = 9 });
        Assert.Equal(0, c2);
        Assert.Equal((2, "reset"), (QcJson.Int(Rec(o2), "attempt"), QcJson.Str(Rec(o2), "state")));
        Assert.Equal(FakeQcWorld.Sha1, QcJson.Str(h.World.Rec(5, 9), "ran_sha"));      // and the comment holds it again
    }

    [Fact]
    public async Task AStateCommentThatMovedUnderTheVerbIsReadAgain()
    {
        var h = Validated();
        var moved = false;
        h.World.BeforeSave = item => { if (!moved) { moved = true; h.World.Items[item].Revision++; } };    // someone else saved first
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal(0, code);
        Assert.Equal("requested", QcJson.Str(h.World.Rec(5, 9), "state"));
    }

    [Fact]
    public async Task AStateCommentThatKeepsMovingIsRefusedAsStateConflict()
    {
        var h = Validated();
        h.World.BeforeSave = item => h.World.Items[item].Revision++;
        var (code, o) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((11, "state-conflict"), (code, o["code"]!.ToString()));
        Assert.Null(h.World.Rec(5, 9));
    }

    [Fact]
    public async Task TwoVerbsOnOneItemRunOneAfterTheOther()
    {
        var h = Validated();
        var gate = new TaskCompletionSource();
        var inside = new TaskCompletionSource();
        h.World.OnRead = async item => { inside.TrySetResult(); await gate.Task; };
        var first = h.Call("qc-request", new { item = 5, pr = 9 });
        await inside.Task;
        h.World.OnRead = null;
        var second = h.Call("qc-request", new { item = 5, pr = 9 });
        await Task.Delay(100);
        Assert.False(second.IsCompleted);    // waits for the lock (45 seconds at most)
        gate.SetResult();
        var (c1, o1) = await first; var (c2, o2) = await second;
        Assert.Equal((0, "started"), (c1, o1["outcome"]!.ToString()));
        Assert.Equal((0, "existing"), (c2, o2["outcome"]!.ToString()));
    }

    [Fact]
    public async Task AJournalLineIsBestEffort_TheVerbStandsWhenItFails()
    {
        var h = Validated();
        h.World.JournalFault = new InvalidOperationException("journal is down");
        var (code, _) = await h.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal(0, code);      // a journal line is best effort: the verb stands
        Assert.Contains(h.Log, l => l.Contains("journal is down"));
        Assert.Equal("requested", h.State());
    }
}
