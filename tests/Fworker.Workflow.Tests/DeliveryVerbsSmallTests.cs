using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery.Verbs;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.4: private-build, copilot-owner and ci-release on v2, against what the rule scripts do (Verbs.Pr.ps1): the same refusals with the same codes, the same GitHub calls in the same order, the same state.
/// </summary>
public class DeliveryVerbsSmallTests
{
    static JsonObject Args(params (string, object?)[] a) => J.Obj(a);
    static readonly CancellationToken Ct = CancellationToken.None;

    static (FakeGh Gh, FakeStateStore Store, RuleKit Kit) Make(int item = 5, string column = "Development", bool open = true, string[]? labels = null, Func<string, string?>? env = null)
    {
        var gh = new FakeGh().WithBoard(n => Gql.Item(n, column, open, labels));
        var store = new FakeStateStore();
        return (gh, store, VerbFixture.Kit(gh, store, env: env));
    }

    // ---- private-build ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PrivateBuild_RecordsTheAttestation_AndAnswersWhatTheScriptAnswers()
    {
        var (gh, store, kit) = Make();
        store.Of(5)["checks_red"] = new JsonObject { ["count"] = 2, ["shas"] = new JsonArray("aa") };
        var r = await PrivateBuildVerb.Run(kit, Args(("item", 5), ("attest", VerbFixture.Sha('A')), ("holder", "w1")), Ct);
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(5, r["id"]!.GetValue<int>());
        Assert.True(r["attested"]!.GetValue<bool>());
        Assert.Equal(VerbFixture.Sha('a'), r["sha"]!.GetValue<string>());   // lower case
        Assert.Equal(2, r["checks_red"]!.GetValue<int>());
        Assert.Equal(2, r["gate"]!.GetValue<int>());
        var pb = store.States[5]["private_build"]!;
        Assert.Equal(VerbFixture.Sha('a'), pb["sha"]!.GetValue<string>());
        Assert.Equal("w1", pb["holder"]!.GetValue<string>());
        Assert.Equal("2026-10-29T12:00:00.000Z", pb["at"]!.GetValue<string>());
        Assert.Empty(gh.Writes.Where(w => w.Path != "graphql"));   // nothing is written to GitHub but through the state comment
    }

    [Fact]
    public async Task PrivateBuild_WithoutAHolder_IsAnonymous_AndTheGateIsThePolicysOrTwo()
    {
        var (_, store, kit) = Make();
        var r = await PrivateBuildVerb.Run(kit, Args(("item", 5), ("attest", VerbFixture.Sha('1'))), Ct);
        Assert.Equal("anonymous", store.States[5]["private_build"]!["holder"]!.GetValue<string>());
        Assert.Equal(0, r["checks_red"]!.GetValue<int>());
        var kit4 = VerbFixture.Kit(new FakeGh().WithBoard(n => Gql.Item(n, "Development")), store, VerbFixture.Config(c => c["timing"]!["redChecksGate"] = 4));
        Assert.Equal(4, (await PrivateBuildVerb.Run(kit4, Args(("item", 5), ("attest", VerbFixture.Sha('1'))), Ct))["gate"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ZZZZ")]
    [InlineData("true")]
    public async Task PrivateBuild_TakesTheFullShaOnly(string attest)
    {
        var (_, store, kit) = Make();
        var e = await Assert.ThrowsAsync<VerbReject>(() => PrivateBuildVerb.Run(kit, Args(("item", 5), ("attest", attest)), Ct));
        Assert.Equal((2, "usage"), (e.Exit, e.Code));
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task PrivateBuild_NeedsItsArguments_AndAnOpenItem()
    {
        var (_, _, kit) = Make();
        var e = await Assert.ThrowsAsync<VerbReject>(() => PrivateBuildVerb.Run(kit, Args(("attest", VerbFixture.Sha('1'))), Ct));
        Assert.Equal("missing argument: item", e.Message);
        e = await Assert.ThrowsAsync<VerbReject>(() => PrivateBuildVerb.Run(kit, Args(("item", 5)), Ct));
        Assert.Equal("missing argument: attest", e.Message);
        var (_, _, closed) = Make(open: false);
        e = await Assert.ThrowsAsync<VerbReject>(() => PrivateBuildVerb.Run(closed, Args(("item", 5), ("attest", VerbFixture.Sha('1'))), Ct));
        Assert.Equal((11, "item-closed"), (e.Exit, e.Code));
    }

    // ---- copilot-owner ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CopilotOwner_GetAnswersNullUntilATaskIsRecorded_AndNullOnceItIsDone()
    {
        var (_, store, kit) = Make();
        Assert.Null((await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "get")), Ct))["task"]);
        store.Of(5)["copilot_task"] = new JsonObject { ["id"] = "t1", ["branch"] = "copilot/x" };
        Assert.Equal("t1", (await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "get")), Ct))["task"]!["id"]!.GetValue<string>());
        store.Of(5)["copilot_task"]!["done_at"] = "2026-10-29T10:00:00.000Z";
        Assert.Null((await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "get")), Ct))["task"]);
    }

    [Fact]
    public async Task CopilotOwner_TheFirstSetRecordsTheTask_AndCommentsOwnership()
    {
        var (gh, store, kit) = Make();
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        var task = Args(("id", "task-1"), ("url", "https://github.com/copilot/tasks/1"), ("branch", "copilot/fix-5"), ("pr", "12"), ("phase", "Development"));
        var r = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", task)), Ct);
        Assert.True(r["first"]!.GetValue<bool>());
        var t = store.States[5]["copilot_task"]!;
        Assert.Equal(("task-1", "copilot/fix-5", 12, "Development", "copilot-cloud"), (t["id"]!.GetValue<string>(), t["branch"]!.GetValue<string>(), t["pr"]!.GetValue<int>(), t["phase"]!.GetValue<string>(), t["runtime"]!.GetValue<string>()));
        Assert.Empty(t["previous"]!.AsArray());
        var comment = gh.Writes.Single().Body!["body"]!.GetValue<string>();
        Assert.StartsWith("\U0001F916 factory-worker: **Owner:** Copilot task [task-1](https://github.com/copilot/tasks/1) owns #5 from Development until its pull request is merged and master is green.", comment);
        Assert.EndsWith("\n\n<!-- factory-journal:factory-worker -->", comment);
    }

    [Fact]
    public async Task CopilotOwner_ALaterSetOfTheSameTask_UpdatesBranchPrUrlAndPhase_WithoutAComment()
    {
        var (gh, store, kit) = Make();
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", Args(("id", "task-1"), ("branch", "copilot/a"), ("phase", "Development")))), Ct);
        gh.Calls.Clear();
        var r = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", Args(("id", "task-1"), ("branch", "copilot/b"), ("pr", 9), ("url", "https://x.example/t"), ("phase", "Functional Testing")))), Ct);
        Assert.False(r["first"]!.GetValue<bool>());
        var t = store.States[5]["copilot_task"]!;
        Assert.Equal(("copilot/b", 9, "https://x.example/t", "Functional Testing"), (t["branch"]!.GetValue<string>(), t["pr"]!.GetValue<int>(), t["url"]!.GetValue<string>(), t["phase"]!.GetValue<string>()));
        Assert.Empty(gh.Writes);
    }

    [Fact]
    public async Task CopilotOwner_ANewTaskKeepsTheOldOnesInPrevious_AndACursorAgentSaysSo()
    {
        var (gh, store, kit) = Make();
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", Args(("id", "t1")))), Ct);
        await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", Args(("id", "t2")))), Ct);
        await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", Args(("id", "t3"), ("runtime", "cursor-cloud"), ("phase", "Development")))), Ct);
        var t = store.States[5]["copilot_task"]!;
        Assert.Equal(["t1", "t2"], t["previous"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal("cursor-cloud", t["runtime"]!.GetValue<string>());
        Assert.Contains("**Owner:** Cursor agent `t3` owns #5 from Development until the change is in production.", gh.Writes.Last().Body!["body"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    public async Task CopilotOwner_TheTaskIdIsCheckedAndNothingIsRecorded(string id)
    {
        var (_, store, kit) = Make();
        var e = await Assert.ThrowsAsync<VerbReject>(() => CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", Args(("id", id)))), Ct));
        Assert.Equal((2, "usage"), (e.Exit, e.Code));
        Assert.Empty(store.Saves);
        e = await Assert.ThrowsAsync<VerbReject>(() => CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "bogus")), Ct));
        Assert.Equal("action is get, set, close or status", e.Message);
    }

    [Fact]
    public async Task CopilotOwner_ABadUrlOrBranchIsDropped_NotRefused()
    {
        var (gh, store, kit) = Make();
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "set"), ("task", Args(("id", "t1"), ("url", "http://insecure"), ("branch", "bad branch!"), ("pr", "x12")))), Ct);
        var t = store.States[5]["copilot_task"]!;
        Assert.Equal(("", "", 0), (t["url"]!.GetValue<string>(), t["branch"]!.GetValue<string>(), t["pr"]!.GetValue<int>()));
    }

    [Fact]
    public async Task CopilotOwner_CloseCommentsTheMergeAndTheRelease_Once()
    {
        var (gh, store, kit) = Make();
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        store.Of(5)["copilot_task"] = new JsonObject { ["id"] = "t1", ["url"] = "https://x.example/t", ["pr"] = 12 };
        store.Of(5)["merge"] = new JsonObject { ["sha"] = VerbFixture.Sha('c'), ["release"] = "1.0.7" };
        Assert.True((await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "close")), Ct))["closed"]!.GetValue<bool>());
        Assert.Equal("2026-10-29T12:00:00.000Z", store.States[5]["copilot_task"]!["done_at"]!.GetValue<string>());
        Assert.Equal("\U0001F916 factory-worker: **Owner done:** Copilot task [t1](https://x.example/t) finished #5: PR #12 merged as `ccccccc`, master is green and the change is delivered to production in release 1.0.7 (https://prod.example). Session link: https://x.example/t\n\n<!-- factory-journal:factory-worker -->",
            gh.Writes.Single().Body!["body"]!.GetValue<string>());
        var again = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "close")), Ct);
        Assert.Equal(("already", false), (again["reason"]!.GetValue<string>(), again["closed"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task CopilotOwner_CloseWithoutAnOwner_AndCloseUnmerged()
    {
        var (gh, store, kit) = Make();
        var none = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "close")), Ct);
        Assert.Equal(("no-owner", false), (none["reason"]!.GetValue<string>(), none["closed"]!.GetValue<bool>()));
        store.Of(5)["copilot_task"] = new JsonObject { ["id"] = "t1" };
        var r = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "close"), ("unmerged", true)), Ct);
        Assert.True(r["unmerged"]!.GetValue<bool>());
        Assert.Equal("closed-unmerged", store.States[5]["copilot_task"]!["ended"]!.GetValue<string>());
        Assert.Empty(gh.Writes.Where(w => w.Path != "graphql"));   // no comment for a task that ended unmerged
        Assert.Equal(2, store.Saves.Count);   // done_at, then the reason
    }

    [Theory]
    [InlineData("release-queue", null, "prod-deploy")]
    [InlineData("release-queue", "dep-1", "prod-failed")]
    [InlineData("release-candidate", null, "release-candidate")]
    [InlineData("late-rc", null, "release-candidate")]
    [InlineData("master-green", null, "master-green")]
    public async Task CopilotOwner_StatusNamesWhatAMergedItemWaitsOn(string awaiting, string? prodFailed, string waiting)
    {
        var (_, store, kit) = Make();
        store.Of(5)["merge"] = new JsonObject { ["sha"] = VerbFixture.Sha('c'), ["awaiting"] = awaiting };
        if (prodFailed is not null) store.Of(5)["merge"]!["prod_failed"] = prodFailed;
        var r = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "status")), Ct);
        Assert.Equal(waiting, r["waiting"]!.GetValue<string>());
        Assert.Equal(VerbFixture.Sha('c'), r["merge_sha"]!.GetValue<string>());
        Assert.Equal("Development", r["column"]!.GetValue<string>());
        Assert.True(r["open"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CopilotOwner_StatusOfAnItemThatIsDone_ClosedBrokenParkedAndWaitingForChecks()
    {
        var (_, store, kit) = Make();
        store.Of(5)["merge"] = new JsonObject { ["sha"] = VerbFixture.Sha('c'), ["done_at"] = "2026-10-29T10:00:00.000Z", ["release"] = "1.0.7" };
        var done = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "status")), Ct);
        Assert.Equal(("done", "1.0.7"), (done["waiting"]!.GetValue<string>(), done["release"]!.GetValue<string>()));

        store.Of(5)["merge"] = new JsonObject { ["sha"] = VerbFixture.Sha('c'), ["awaiting"] = "master-broken" };
        store.Of(5)["park"] = new JsonObject { ["reason"] = "master-broken", ["until"] = "2026-10-30T12:00:00.000Z", ["failed"] = new JsonArray("Build result") };
        var broken = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "status")), Ct);
        Assert.Equal("master-broken", broken["waiting"]!.GetValue<string>());
        Assert.Equal(["Build result"], broken["failed"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());

        store.Of(5).Remove("merge");
        store.Of(5)["park"] = new JsonObject { ["reason"] = "worker-failure", ["until"] = "2026-10-30T12:00:00.000Z" };
        Assert.Equal("parked:worker-failure", (await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "status")), Ct))["waiting"]!.GetValue<string>());
        store.Of(5)["park"]!["until"] = "2026-10-29T11:00:00.000Z";   // an expired park holds nothing
        store.Of(5)["awaiting_checks"] = new JsonObject { ["pr"] = 9 };
        store.Of(5)["pr"] = new JsonObject { ["number"] = 9, ["rejection"] = new string('r', 700) };
        var ci = await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "status")), Ct);
        Assert.Equal(("ci", 9, 600), (ci["waiting"]!.GetValue<string>(), ci["pr"]!.GetValue<int>(), ci["rework"]!.GetValue<string>().Length));

        store.Of(5).Remove("awaiting_checks"); store.Of(5).Remove("park");
        Assert.Equal("next-phase", (await CopilotOwnerVerb.Run(kit, Args(("item", 5), ("action", "status")), Ct))["waiting"]!.GetValue<string>());
        var (_, _, closed) = Make(open: false);
        Assert.Equal("closed", (await CopilotOwnerVerb.Run(closed, Args(("item", 5), ("action", "status")), Ct))["waiting"]!.GetValue<string>());
    }

    // ---- ci-release ------------------------------------------------------------------------------------------------------------------------------------

    static JsonObject Run(long id, string branch, string sha, int attempt = 1) => new() { ["id"] = id, ["head_branch"] = branch, ["head_sha"] = sha, ["run_attempt"] = attempt };

    static (FakeGh Gh, FakeStateStore Store, RuleKit Kit) CiRelease(JsonObject[] runs, string[]? labels = null)
    {
        var gh = new FakeGh().WithBoard(n => Gql.Item(n, "Development", true, labels));
        gh.Get("repos/acme/orders/actions/runs?status=action_required", new JsonObject { ["workflow_runs"] = new JsonArray(runs.Select(r => (JsonNode?)r).ToArray()) });
        var store = new FakeStateStore();
        store.Of(5)["pr"] = new JsonObject { ["number"] = 12, ["branch"] = "copilot/fix-5" };
        gh.Get("repos/acme/orders/pulls/12", new JsonObject { ["number"] = 12, ["state"] = "open", ["head"] = new JsonObject { ["ref"] = "copilot/fix-5", ["sha"] = "abc" } });
        gh.Write(HttpMethod.Post, "repos/acme/orders/actions/runs/", status: 201);
        return (gh, store, VerbFixture.Kit(gh, store));
    }

    [Fact]
    public async Task CiRelease_ReRunsTheHeldRunOfAnItemsOwnPullRequestHead_Once()
    {
        var (gh, _, kit) = CiRelease([Run(101, "copilot/fix-5", "abc")]);
        var r = await CiReleaseVerb.Run(kit, Args(("items", new JsonArray(5)), ("skip", new JsonArray())), _ => null, Ct);
        Assert.Equal(1, r["pending"]!.GetValue<int>());
        var run = r["runs"]![0]!;
        Assert.Equal(("released", 201, 5, 12, "copilot/fix-5"), (run["result"]!.GetValue<string>(), run["status"]!.GetValue<int>(), run["item"]!.GetValue<int>(), run["pr"]!.GetValue<int>(), run["branch"]!.GetValue<string>()));
        Assert.Equal("POST repos/acme/orders/actions/runs/101/rerun", gh.WriteLines());
        Assert.Null(r["actions"]);
    }

    [Theory]
    [InlineData("copilot/fix-5", "zzz", 1, "skipped", "head-mismatch")]
    [InlineData("copilot/fix-5", "abc", 2, "skipped", "already-rerun")]
    [InlineData("copilot/other", "abc", 1, "skipped", "no-item")]
    public async Task CiRelease_SkipsARunThatIsNotTheItemsHeadOrWasReRun(string branch, string sha, int attempt, string result, string reason)
    {
        var (gh, _, kit) = CiRelease([Run(101, branch, sha, attempt)]);
        var r = await CiReleaseVerb.Run(kit, Args(("items", new JsonArray(5))), _ => null, Ct);
        Assert.Equal((result, reason), (r["runs"]![0]!["result"]!.GetValue<string>(), r["runs"]![0]!["reason"]!.GetValue<string>()));
        Assert.Empty(gh.Writes.Where(w => w.Path != "graphql"));
    }

    [Fact]
    public async Task CiRelease_AnItemOfAnotherRuntime_IsSkipped_AndAClosedItemIsLeftOut()
    {
        var (_, _, kit) = CiRelease([Run(101, "copilot/fix-5", "abc")], ["runtime:cursor-cloud"]);
        var r = await CiReleaseVerb.Run(kit, Args(("items", new JsonArray(5))), _ => null, Ct);
        Assert.Equal(("skipped", "other-runtime"), (r["runs"]![0]!["result"]!.GetValue<string>(), r["runs"]![0]!["reason"]!.GetValue<string>()));
        Assert.Equal("no-item", r["runs"]![1]!["reason"]!.GetValue<string>());
        var gh = new FakeGh().WithBoard(n => Gql.Item(n, "Development", false));
        gh.Get("repos/acme/orders/actions/runs?status=action_required", new JsonObject { ["workflow_runs"] = new JsonArray(Run(101, "copilot/fix-5", "abc")) });
        var closed = await CiReleaseVerb.Run(VerbFixture.Kit(gh, new FakeStateStore()), Args(("items", new JsonArray(5))), _ => null, Ct);
        Assert.Equal("no-item", closed["runs"]![0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task CiRelease_AHeldRunOfABranchThatIsNotCopilotsOrWasHandled_IsNotListed()
    {
        var (_, _, kit) = CiRelease([Run(101, "feature/x", "abc"), Run(102, "copilot/fix-5", "abc")]);
        var r = await CiReleaseVerb.Run(kit, Args(("items", new JsonArray(5)), ("skip", new JsonArray("102"))), _ => null, Ct);
        Assert.Equal(0, r["pending"]!.GetValue<int>());
        Assert.Empty(r["runs"]!.AsArray());
    }

    [Fact]
    public async Task CiRelease_RouteAllTakesTheItemOfTheRunsPullRequestTitle()
    {
        var (gh, _, kit) = CiRelease([Run(101, "copilot/fix-5", "abc")]);
        gh.On(HttpMethod.Get, "repos/acme/orders/pulls?state=open", _ => new GhResult(200, new JsonArray(new JsonObject { ["title"] = "Fix it (#5)", ["head"] = new JsonObject { ["ref"] = "copilot/fix-5" } })));
        var r = await CiReleaseVerb.Run(kit, Args(("items", new JsonArray()), ("routeAll", true)), _ => null, Ct);
        Assert.Equal("released", r["runs"]![0]!["result"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(403, "Resource not accessible by integration", "refused")]
    [InlineData(500, "boom", "failed")]
    [InlineData(403, "You have exceeded a secondary rate limit", "failed")]
    public async Task CiRelease_ARefusedReRunIsReported_NeverRetriedWithAnotherCredential(int status, string message, string result)
    {
        var (gh, _, kit) = CiRelease([Run(101, "copilot/fix-5", "abc")]);
        gh.On(HttpMethod.Post, "repos/acme/orders/actions/runs/101/rerun", _ => new GhResult(status, new JsonObject { ["message"] = message }));
        var r = await CiReleaseVerb.Run(kit, Args(("items", new JsonArray(5))), _ => null, Ct);
        Assert.Equal((result, status, message), (r["runs"]![0]!["result"]!.GetValue<string>(), r["runs"]![0]!["status"]!.GetValue<int>(), r["runs"]![0]!["message"]!.GetValue<string>()));
        Assert.Single(gh.Calls.Where(c => c.Path.EndsWith("/rerun")));
    }

    [Fact]
    public async Task CiRelease_TheProbeReadsTheAppsActionsPermission_OrTheFakeOne()
    {
        var (gh, _, kit) = CiRelease([]);
        gh.AppActions = "read";
        Func<string, string?> real = n => n == "FACTORY_AUTHOR_APP" ? "1" : null;
        Assert.Equal("read", (await CiReleaseVerb.Run(kit, Args(("items", new JsonArray()), ("probe", true)), real, Ct))["actions"]!.GetValue<string>());
        Func<string, string?> fake = n => n switch { "FACTORY_AUTHOR_APP" => "1", "FACTORY_FAKE_APPS" => "1", "FACTORY_FAKE_APP_ACTIONS" => "write", _ => null };
        Assert.Equal("write", (await CiReleaseVerb.Run(kit, Args(("items", new JsonArray()), ("probe", true)), fake, Ct))["actions"]!.GetValue<string>());
        Assert.Null((await CiReleaseVerb.Run(kit, Args(("items", new JsonArray())), real, Ct))["actions"]);   // not asked
    }
}
