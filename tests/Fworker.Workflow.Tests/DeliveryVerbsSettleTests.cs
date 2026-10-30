using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery.Verbs;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.4: settle on v2 (Verbs.Delivery.ps1 Invoke-Settle): the post-merge flow of one item, one idempotent step per call.</summary>
public class DeliveryVerbsSettleTests
{
    static readonly CancellationToken Ct = CancellationToken.None;
    static readonly string Merge = VerbFixture.Sha('c');
    const string ChecksPath = "repos/acme/orders/commits/";

    static JsonObject CheckRuns(params (string Name, string Status, string? Conclusion)[] runs) =>
        new() { ["check_runs"] = new JsonArray(runs.Select((r, i) => (JsonNode?)new JsonObject { ["name"] = r.Name, ["status"] = r.Status, ["conclusion"] = r.Conclusion, ["started_at"] = $"2026-10-29T11:0{i}:00Z" }).ToArray()) };

    static (FakeGh Gh, FakeStateStore Store, RuleKit Kit) Make(string column = "Functional Testing", bool open = true, Action<JsonObject>? merge = null, DateTime? now = null)
    {
        var gh = new FakeGh().WithBoard(n => Gql.Item(n, column, open, ["Auto Merge"]));
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        gh.Write(HttpMethod.Patch, "repos/acme/orders/issues/5");
        var store = new FakeStateStore();
        var m = new JsonObject { ["sha"] = Merge, ["awaiting"] = "master-green", ["merged_at"] = "2026-10-29T11:50:00.000Z" };
        merge?.Invoke(m);
        store.Of(5)["merge"] = m;
        return (gh, store, VerbFixture.Kit(gh, store, now: now is null ? null : () => now.Value));
    }

    static Task<JsonObject> Step(RuleKit kit) => SettleVerb.Run(kit, J.Obj(("item", 5)), Ct);
    static string Outcome(JsonObject r) => r["outcome"]!.GetValue<string>();
    static JsonObject M(FakeStateStore s) => (JsonObject)s.States[5]["merge"]!;
    static string Comment(FakeGh gh, int index = -1) { var cs = gh.Writes.Where(w => w.Path.EndsWith("/comments")).ToList(); return cs[index < 0 ? cs.Count + index : index].Body!["body"]!.GetValue<string>(); }

    [Fact]
    public async Task NothingToSettle_AnItemWithNoWait_AndAnItemClosedWithoutAMerge()
    {
        var (_, store, kit) = Make();
        M(store)["awaiting"] = null;
        var r = await Step(kit);
        Assert.Equal(("nothing-to-settle", true, 5), (Outcome(r), r["ok"]!.GetValue<bool>(), r["id"]!.GetValue<int>()));
        store.Of(5).Remove("merge");
        Assert.Equal("nothing-to-settle", Outcome(await Step(kit)));
        var (_, store2, closed) = Make(open: false, merge: m => { m.Remove("sha"); });
        Assert.Equal("closed", Outcome(await Step(closed)));
        Assert.True(M(store2).ContainsKey("awaiting") && M(store2)["awaiting"] is null);
    }

    [Fact]
    public async Task MasterGreen_GreenChecksAdvanceToTheReleaseCandidate()
    {
        var (gh, store, kit) = Make();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success"), ("release", "in_progress", null)));
        var r = await Step(kit);
        Assert.Equal(("advanced", "release-candidate"), (Outcome(r), r["awaiting"]!.GetValue<string>()));
        var m = M(store);
        Assert.Equal(("release-candidate", "2026-10-29T12:00:00.000Z", "2026-10-29T12:20:00.000Z"), (m["awaiting"]!.GetValue<string>(), m["green_at"]!.GetValue<string>(), m["rc_deadline"]!.GetValue<string>()));
        Assert.Equal("\U0001F916 factory-worker: master is GREEN at `ccccccc` - waiting for the release candidate that carries it.\n\n<!-- factory-journal:factory-worker -->", Comment(gh));
    }

    [Fact]
    public async Task MasterGreen_ThePassingJudgmentIgnoresTheChecksOfTheWorkflowsABuildStarts()
    {
        var (gh, _, kit) = Make();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success"), ("release", "completed", "failure"), ("architecture", "completed", "failure")));
        Assert.Equal("advanced", Outcome(await Step(kit)));
    }

    [Fact]
    public async Task MasterGreen_PendingChecksWait_AndAnOldMergeParksWithATimeout()
    {
        var (gh, store, kit) = Make();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "in_progress", null)));
        var r = await Step(kit);
        Assert.Equal(("waiting", "master-green", "pending"), (Outcome(r), r["awaiting"]!.GetValue<string>(), r["checks"]!["status"]!.GetValue<string>()));
        Assert.Empty(store.Saves);
        var (gh2, store2, late) = Make(merge: m => m["merged_at"] = "2026-10-29T11:00:00.000Z");
        gh2.Get(ChecksPath + Merge, CheckRuns(("Build result", "in_progress", null)));
        var p = await Step(late);
        Assert.Equal(("parked", "master-green-timeout"), (Outcome(p), p["reason"]!.GetValue<string>()));
        Assert.Equal("master-timeout", M(store2)["awaiting"]!.GetValue<string>());
        Assert.Equal(("master-green-timeout", "2026-10-30T12:00:00.000Z"), (store2.States[5]["park"]!["reason"]!.GetValue<string>(), store2.States[5]["park"]!["until"]!.GetValue<string>()));
        Assert.Contains("master CI did not go green within 30 min of the merge - parked for a human.", Comment(gh2));
    }

    static void Failed(FakeGh gh, params (long Id, string Status, string? Conclusion, int Attempt)[] runs)
    {
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "failure"), ("Acceptance Tests [shard 1/3]", "completed", "failure")));
        gh.Get($"repos/acme/orders/actions/runs?head_sha={Merge}", new JsonObject { ["workflow_runs"] = new JsonArray(runs.Select(r => (JsonNode?)new JsonObject { ["id"] = r.Id, ["status"] = r.Status, ["conclusion"] = r.Conclusion, ["run_attempt"] = r.Attempt }).ToArray()) });
        gh.Get("repos/acme/orders/commits?sha=master&per_page=6", new JsonArray());
    }

    [Fact]
    public async Task MasterGreen_AFailureIsReRunOnce_NamingTheRunsAndWhyItLooksFlaky()
    {
        var (gh, store, kit) = Make();
        Failed(gh, (900, "completed", "failure", 1), (901, "completed", "success", 1));
        gh.Write(HttpMethod.Post, "repos/acme/orders/actions/runs/900/rerun-failed-jobs", status: 201);
        var r = await Step(kit);
        Assert.Equal(("rerun", 900L), (Outcome(r), r["runs"]![0]!["id"]!.GetValue<long>()));
        Assert.Equal(["Build result", "Acceptance Tests [shard 1/3]"], r["failed"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var m = M(store);
        Assert.True(m["rerun_attempted"]!.GetValue<bool>());
        Assert.Equal("2026-10-29T12:00:00.000Z", m["rerun_at"]!.GetValue<string>());
        Assert.Equal((900L, 1), (m["rerun_runs"]![0]!["id"]!.GetValue<long>(), m["rerun_runs"]![0]!["attempt"]!.GetValue<int>()));
        Assert.Equal("\U0001F916 factory-worker: master checks failed at `ccccccc` (Build result, Acceptance Tests [shard 1/3]) - known flaky: Acceptance Tests [shard 1/3]; re-running the failed jobs once (workflow run 900), then judging the new attempt.\n\n<!-- factory-journal:factory-worker -->", Comment(gh));
    }

    [Fact]
    public async Task MasterGreen_AFailureWaitsWhileAnyRunOfTheCommitIsStillGoing()
    {
        var (gh, _, kit) = Make();
        Failed(gh, (900, "completed", "failure", 1), (902, "in_progress", null, 1));
        var r = await Step(kit);
        Assert.Equal(("waiting", "failed-run-in-progress"), (Outcome(r), r["reason"]!.GetValue<string>()));
        Assert.DoesNotContain(gh.Calls, c => c.Path.Contains("rerun-failed-jobs"));
    }

    [Fact]
    public async Task MasterGreen_AReRunNothingCouldStart_IsSaidAndMasterIsBroken()
    {
        var (gh, store, kit) = Make();
        Failed(gh, (900, "completed", "failure", 1));
        gh.On(HttpMethod.Post, "repos/acme/orders/actions/runs/900/rerun-failed-jobs", _ => new GhResult(403, new JsonObject { ["message"] = "no" }));
        var r = await Step(kit);
        Assert.Equal(("parked", "master-broken"), (Outcome(r), r["reason"]!.GetValue<string>()));
        var comments = gh.Writes.Where(w => w.Path.EndsWith("/comments")).Select(w => w.Body!["body"]!.GetValue<string>()).ToList();
        Assert.Contains("the failed jobs could not be re-run (GitHub refused: 900: 403).", comments[0]);
        Assert.Contains("**master is BROKEN** at `ccccccc` after the merge (Build result, Acceptance Tests [shard 1/3]) - parked for a human.", comments[1]);
        Assert.DoesNotContain("also after one re-run", comments[1]);
        var s = store.States[5];
        Assert.Equal(("master-broken", Merge, "master-broken"), (s["park"]!["reason"]!.GetValue<string>(), s["park"]!["sha"]!.GetValue<string>(), M(store)["awaiting"]!.GetValue<string>()));
        Assert.Equal(["Build result", "Acceptance Tests [shard 1/3]"], s["park"]!["failed"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task MasterGreen_TheVerdictWaitsForANewerAttemptOfEveryReRun_ThenMasterIsBrokenAfterOneReRun()
    {
        var (gh, store, kit) = Make(merge: m => { m["rerun_attempted"] = true; m["rerun_at"] = "2026-10-29T11:55:00.000Z"; m["rerun_runs"] = new JsonArray(new JsonObject { ["id"] = 900, ["attempt"] = 1 }); });
        Failed(gh, (900, "completed", "failure", 1));   // still the old attempt
        Assert.Equal("rerun-in-progress", (await Step(kit))["reason"]!.GetValue<string>());
        Failed(gh, (900, "in_progress", null, 2));
        Assert.Equal("rerun-in-progress", (await Step(kit))["reason"]!.GetValue<string>());
        Failed(gh, (900, "completed", "failure", 2));   // the new attempt completed and failed too
        var parked = await Step(kit);
        Assert.Equal(("parked", "master-broken"), (Outcome(parked), parked["reason"]!.GetValue<string>()));
        Assert.Contains("also after one re-run of the failed jobs", Comment(gh));
        Assert.Equal("master-broken", M(store)["awaiting"]!.GetValue<string>());
    }

    [Fact]
    public async Task MasterBroken_WaitsWhileThePark_IsLive_AndGoesOnOnceAGreenMasterContainsTheMerge()
    {
        var (gh, store, kit) = Make(merge: m => m["awaiting"] = "master-broken");
        store.Of(5)["park"] = new JsonObject { ["reason"] = "master-broken", ["until"] = "2026-10-30T12:00:00.000Z" };
        Assert.Equal(("waiting", "master-broken"), (Outcome(await Step(kit)), (await Step(kit))["awaiting"]!.GetValue<string>()));
        Assert.Empty(gh.Calls.Where(c => c.Path.StartsWith(ChecksPath)));   // (no read while the park stands)
        store.Of(5)["park"]!["until"] = "2026-10-29T11:00:00.000Z";   // the park ran out
        var later = VerbFixture.Sha('d');
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "failure")));
        gh.Get(ChecksPath + later, CheckRuns(("Build result", "completed", "success")));
        gh.Get("repos/acme/orders/commits?sha=master&per_page=11", new JsonArray(new JsonObject { ["sha"] = later }, new JsonObject { ["sha"] = Merge }));
        gh.Get($"repos/acme/orders/compare/{Merge}...{later}?per_page=1", new JsonObject { ["status"] = "ahead" });
        var r = await Step(kit);
        Assert.Equal(("advanced", "release-candidate", "master-broken"), (Outcome(r), r["awaiting"]!.GetValue<string>(), r["recovered"]!.GetValue<string>()));
        var m = M(store);
        Assert.Equal((later, "release-candidate"), (m["green_via"]!.GetValue<string>(), m["awaiting"]!.GetValue<string>()));
        Assert.False(store.States[5].ContainsKey("park"));
        Assert.Contains("master is GREEN at `ddddddd`, which contains `ccccccc` - waiting for the release candidate that carries it.", Comment(gh));
    }

    [Fact]
    public async Task MasterBroken_NoGreenMasterYet_KeepsWaiting()
    {
        var (gh, _, kit) = Make(merge: m => m["awaiting"] = "master-timeout");
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "in_progress", null)));
        gh.Get("repos/acme/orders/commits?sha=master&per_page=11", new JsonArray(new JsonObject { ["sha"] = Merge }));
        var r = await Step(kit);
        Assert.Equal(("waiting", "master-timeout"), (Outcome(r), r["awaiting"]!.GetValue<string>()));
    }

    // ---- the release candidate and the Release Queue -----------------------------------------------------------------------------------------------------

    static void Octopus(FakeGh gh, bool prodSuccess = false, string? prodState = null, string commitInRelease = "c")
    {
        gh.Octopus["api/Spaces-1/projects/orders-ui"] = new JsonObject { ["Id"] = "Projects-1" };
        gh.Octopus["api/Spaces-1/projects/Projects-1/releases?take=30"] = new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Id"] = "R1", ["Version"] = "1.0.5", ["ReleaseNotes"] = $"built from {VerbFixture.Sha(commitInRelease[0])}", ["Assembled"] = "2026-10-29T11:55:00Z" }) };
        gh.Octopus["api/Spaces-1/environments?take=1000"] = new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Id"] = "E1", ["Name"] = "tdd" }, new JsonObject { ["Id"] = "E3", ["Name"] = "Prod" }) };
        var deps = new JsonArray(new JsonObject { ["Id"] = "D1", ["ReleaseId"] = "R1", ["EnvironmentId"] = "E1", ["TaskId"] = "T1", ["Created"] = "2026-10-29T11:56:00Z" });
        if (prodState is not null || prodSuccess) deps.Add(new JsonObject { ["Id"] = "D3", ["ReleaseId"] = "R1", ["EnvironmentId"] = "E3", ["TaskId"] = "T3", ["Created"] = "2026-10-29T11:57:00Z" });
        gh.Octopus["api/Spaces-1/deployments?projects=Projects-1&take=30"] = new JsonObject { ["Items"] = deps };
        gh.Octopus["api/Spaces-1/tasks/T1"] = new JsonObject { ["State"] = "Success", ["IsCompleted"] = true };
        gh.Octopus["api/Spaces-1/tasks/T3"] = new JsonObject { ["State"] = prodState ?? "Success", ["IsCompleted"] = prodState is not "Executing" };
    }

    [Fact]
    public async Task ReleaseCandidate_TheReleaseThatCarriesTheMergeMovesTheCardAndWaitsInTheReleaseQueue()
    {
        var (gh, store, kit) = Make(merge: m => { m["awaiting"] = "release-candidate"; m["green_at"] = "2026-10-29T11:55:00.000Z"; m["rc_deadline"] = "2026-10-29T12:15:00.000Z"; m["docs_only"] = false; });
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success"), ("release", "completed", "success")));
        Octopus(gh);
        var r = await Step(kit);
        Assert.Equal(("advanced", "release-queue", "1.0.5"), (Outcome(r), r["awaiting"]!.GetValue<string>(), r["release"]!.GetValue<string>()));
        Assert.Equal("2026-10-29T12:00:00.000Z", r["queued_at"]!.GetValue<string>());
        var move = gh.Writes.Single(w => w.Path == "graphql");
        Assert.Equal(("C_5", "o4"), (move.Body!["variables"]!["i"]!.GetValue<string>(), move.Body["variables"]!["o"]!.GetValue<string>()));   // Functional Testing to the next column, the Release Queue
        Assert.Contains("release candidate **1.0.5** carries `ccccccc` - waiting in **Release Queue** for the production deployment.", Comment(gh));
        Assert.Equal(("release-queue", "1.0.5"), (M(store)["awaiting"]!.GetValue<string>(), M(store)["release"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ReleaseCandidate_AnItemThatIsNotInFunctionalTestingStaysWhereItIs()
    {
        var (gh, _, kit) = Make(column: "Release Queue", merge: m => { m["awaiting"] = "release-candidate"; m["rc_deadline"] = "2026-10-29T12:15:00.000Z"; m["docs_only"] = false; });
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        Octopus(gh);
        await Step(kit);
        Assert.DoesNotContain(gh.Writes, w => w.Path == "graphql");
        Assert.Contains("waiting in **Release Queue** for the production deployment", Comment(gh));
    }

    [Fact]
    public async Task ReleaseCandidate_ALaterReleaseThatContainsTheMergeIsTheCandidate()
    {
        var (gh, _, kit) = Make(merge: m => { m["awaiting"] = "release-candidate"; m["rc_deadline"] = "2026-10-29T12:15:00.000Z"; m["docs_only"] = false; });
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        Octopus(gh, commitInRelease: "e");
        gh.Get($"repos/acme/orders/compare/{Merge}...{VerbFixture.Sha('e')}", new JsonObject { ["status"] = "ahead" });
        Assert.Equal("release-queue", (await Step(kit))["awaiting"]!.GetValue<string>());
        var (gh2, _, kit2) = Make(merge: m => { m["awaiting"] = "release-candidate"; m["rc_deadline"] = "2026-10-29T12:15:00.000Z"; m["docs_only"] = false; });
        gh2.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        Octopus(gh2, commitInRelease: "e");
        gh2.Get($"repos/acme/orders/compare/{Merge}...{VerbFixture.Sha('e')}", new JsonObject { ["status"] = "diverged" });
        Assert.Equal("waiting", Outcome(await Step(kit2)));
    }

    [Fact]
    public async Task ReleaseCandidate_ADocsOnlyMergeWhoseReleaseWasSkippedIsDone_StraightToTheLastColumn()
    {
        var (gh, store, kit) = Make(merge: m => { m["awaiting"] = "release-candidate"; m["rc_deadline"] = "2026-10-29T12:15:00.000Z"; });
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success"), ("Integration Build", "completed", "skipped"), ("release", "completed", "skipped")));
        var r = await Step(kit);
        Assert.Equal(("done", "docs-only"), (Outcome(r), r["path"]!.GetValue<string>()));
        var m = M(store);
        Assert.True(m["docs_only"]!.GetValue<bool>()); Assert.Equal("build", m["docs_only_from"]!.GetValue<string>());
        Assert.Null(m["awaiting"]); Assert.Equal("2026-10-29T12:00:00.000Z", m["done_at"]!.GetValue<string>());
        Assert.Equal(["POST repos/acme/orders/issues/5/comments", "graphql", "PATCH repos/acme/orders/issues/5"], gh.Writes.Select(w => w.Path == "graphql" ? "graphql" : $"{w.Method.Method} {w.Path}").ToArray());
        Assert.Equal(("o5", "closed", "completed"), (gh.Writes.Single(w => w.Path == "graphql").Body!["variables"]!["o"]!.GetValue<string>(), gh.Writes.Last().Body!["state"]!.GetValue<string>(), gh.Writes.Last().Body!["state_reason"]!.GetValue<string>()));
        Assert.Contains("the master build published no release candidate for `ccccccc` - its publish step was skipped because no code changed. A docs-only change has nothing to deploy, so it is moved straight to **Done**.", Comment(gh));
    }

    [Fact]
    public async Task ReleaseCandidate_ADocsOnlyMergeWithNoReleaseCheckAtAll_IsDoneAfterTheGracePeriod()
    {
        var (gh, _, kit) = Make(merge: m => { m["awaiting"] = "release-candidate"; m["green_at"] = "2026-10-29T11:59:00.000Z"; m["rc_deadline"] = "2026-10-29T12:15:00.000Z"; m["pr"] = 12; });
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        gh.Get("repos/acme/orders/pulls/12/files", new JsonArray(new JsonObject { ["filename"] = "docs/a.md" }));
        Octopus(gh, commitInRelease: "f");
        Assert.Equal("waiting", Outcome(await Step(kit)));   // 60 s after green: inside the grace period of 120 s (and the release is not there)
        var (gh2, store2, late) = Make(merge: m => { m["awaiting"] = "release-candidate"; m["green_at"] = "2026-10-29T11:57:00.000Z"; m["rc_deadline"] = "2026-10-29T12:15:00.000Z"; m["pr"] = 12; });
        gh2.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        gh2.Get("repos/acme/orders/pulls/12/files", new JsonArray(new JsonObject { ["filename"] = "docs/a.md" }));
        var r = await Step(late);
        Assert.Equal(("done", "docs-only-no-release"), (Outcome(r), r["path"]!.GetValue<string>()));
        Assert.Equal("pr-files", M(store2)["docs_only_from"]!.GetValue<string>());
        Assert.Contains("(120 s after master went green)", Comment(gh2));
    }

    [Fact]
    public async Task ReleaseCandidate_NoCandidateInTime_ParksOnceAndKeepsLooking_ThenSaysSoOnceMore()
    {
        var (gh, store, kit) = Make(merge: m => { m["awaiting"] = "release-candidate"; m["rc_deadline"] = "2026-10-29T11:59:00.000Z"; m["docs_only"] = false; });
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        Octopus(gh, commitInRelease: "f");
        var r = await Step(kit);
        Assert.Equal(("parked", "release-candidate-timeout", true), (Outcome(r), r["reason"]!.GetValue<string>(), r["still_looking"]!.GetValue<bool>()));
        var m = M(store);
        Assert.Equal(("late-rc", "2026-10-30T12:00:00.000Z"), (m["awaiting"]!.GetValue<string>(), m["late_deadline"]!.GetValue<string>()));
        Assert.Equal("fv:release-candidate-timeout", store.States[5]["park"]!["reason"]!.GetValue<string>());
        Assert.Contains("master went green but no release candidate carrying it was published within 20 min - parked for a human. The worker keeps looking for 24 h.", Comment(gh));
        // a day later: said once
        var (gh2, store2, kit2) = Make(merge: m2 => { m2["awaiting"] = "late-rc"; m2["late_deadline"] = "2026-10-29T11:00:00.000Z"; m2["docs_only"] = false; });
        gh2.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        Octopus(gh2, commitInRelease: "f");
        Assert.Equal("parked", Outcome(await Step(kit2)));
        Assert.Contains("still no release candidate carrying `ccccccc` after 24 h - parked for a human. The gateway keeps looking.", Comment(gh2));
        Assert.Equal("late-rc", M(store2)["awaiting"]!.GetValue<string>());
        Assert.Equal("waiting", Outcome(await Step(kit2)));   // (reported already)
    }

    [Fact]
    public async Task ReleaseQueue_ADeliveryToProdFinishesTheItem()
    {
        var (gh, store, kit) = Make(column: "Release Queue", merge: m => { m["awaiting"] = "release-queue"; m["release"] = "1.0.5"; });
        Octopus(gh, prodSuccess: true);
        var r = await Step(kit);
        Assert.Equal(("done", "prod"), (Outcome(r), r["path"]!.GetValue<string>()));
        Assert.Contains("Delivered to **Prod** - moved to **Done**. Release 1.0.5. Production: https://prod.example", Comment(gh));
        Assert.Null(M(store)["awaiting"]);
    }

    [Fact]
    public async Task ReleaseQueue_ALaterReleaseInProdThatContainsTheChangeFinishesItToo()
    {
        var (gh, _, kit) = Make(column: "Release Queue", merge: m => { m["awaiting"] = "release-queue"; m["release"] = "1.0.5"; });
        Octopus(gh, prodSuccess: true, commitInRelease: "e");
        gh.Octopus["api/Spaces-1/releases/R1"] = gh.Octopus["api/Spaces-1/projects/Projects-1/releases?take=30"]!["Items"]![0]!.DeepClone();
        gh.Octopus["api/Spaces-1/projects/Projects-1/releases?take=30"] = new JsonObject { ["Items"] = new JsonArray() };   // the item's own release scrolled out of the recent list
        gh.Get($"repos/acme/orders/compare/{Merge}...{VerbFixture.Sha('e')}", new JsonObject { ["status"] = "identical" });
        var r = await Step(kit);
        Assert.Equal(("done", "prod-later-release", "1.0.5"), (Outcome(r), r["path"]!.GetValue<string>(), r["release"]!.GetValue<string>()));
        Assert.Contains("Delivered to **Prod** in release 1.0.5, which contains this change - moved to **Done**.", Comment(gh));
    }

    [Fact]
    public async Task ReleaseQueue_AFailedProdDeploymentIsSaidOncePerDeployment_AndTheItemWaits()
    {
        var (gh, store, kit) = Make(column: "Release Queue", merge: m => { m["awaiting"] = "release-queue"; m["release"] = "1.0.5"; m["queued_at"] = "2026-10-29T11:58:00.000Z"; });
        Octopus(gh, prodState: "Failed");
        var r = await Step(kit);
        Assert.Equal(("waiting", "release-queue", "1.0.5", "2026-10-29T11:58:00.000Z"), (Outcome(r), r["awaiting"]!.GetValue<string>(), r["release"]!.GetValue<string>(), r["queued_at"]!.GetValue<string>()));
        Assert.Equal("Failed", r["deployments"]!["Prod"]!["state"]!.GetValue<string>());
        Assert.Equal("D3", M(store)["prod_failed"]!.GetValue<string>());
        Assert.Contains("**Prod deployment FAILED** for release 1.0.5 (D3, Failed).", Comment(gh));
        var before = gh.Writes.Count(w => w.Path.EndsWith("/comments"));
        await Step(kit);
        Assert.Equal(before, gh.Writes.Count(w => w.Path.EndsWith("/comments")));
    }

    [Fact]
    public async Task ReleaseQueue_AWaitSinceBeforeQueuedAtWasRecordedCountsFromGreenOrTheMerge()
    {
        var (gh, _, kit) = Make(column: "Release Queue", merge: m => { m["awaiting"] = "release-queue"; m["release"] = "1.0.5"; m["green_at"] = "2026-10-29T11:55:00.000Z"; });
        Octopus(gh, prodState: "Executing");
        Assert.Equal("2026-10-29T11:55:00.000Z", (await Step(kit))["queued_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnknownWait_IsReportedAsUnknown()
    {
        var (_, _, kit) = Make(merge: m => m["awaiting"] = "something-else");
        var r = await Step(kit);
        Assert.Equal(("unknown-state", "something-else"), (Outcome(r), r["awaiting"]!.GetValue<string>()));
    }

    [Fact]
    public async Task TheProjectMissingInOctopus_IsARefusal()
    {
        var (gh, _, kit) = Make(merge: m => { m["awaiting"] = "release-queue"; m["release"] = "1.0.5"; });
        var e = await Assert.ThrowsAsync<VerbReject>(() => Step(kit));
        Assert.Equal((12, "octopus-project-not-found", "project 'orders-ui' not found in Spaces-1"), (e.Exit, e.Code, e.Message));
        Assert.Empty(gh.Writes);
    }

    [Fact]
    public async Task AFencedGatewayThatLostItsLeaseWritesNothing()
    {
        var (gh, store, kit) = Make();
        gh.Fenced = true; gh.Lease = () => false;
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success")));
        var e = await Assert.ThrowsAsync<VerbReject>(() => Step(kit));
        Assert.Equal("lease-lost", e.Code);
        Assert.Empty(gh.Writes); Assert.Empty(store.Saves);
    }
}
