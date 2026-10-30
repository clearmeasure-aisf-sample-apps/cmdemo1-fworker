using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery.Verbs;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.4: copilot-settle on v2 (Verbs.Pr.ps1 Verb_copilot_settle): the gateway completes a Copilot Development task from its pull request.</summary>
public class DeliveryVerbsCopilotSettleTests
{
    static readonly CancellationToken Ct = CancellationToken.None;
    static string Sha(char c) => VerbFixture.Sha(c);

    sealed class OpenPr
    {
        public readonly List<JsonObject> Calls = [];
        public int Exit; public string Output = "{\"ok\":true}";
        public Task<(int Exit, string Output)> Call(string verb, JsonObject args, CancellationToken ct) { Assert.Equal("open-pr", verb); Calls.Add(args); return Task.FromResult((Exit, Output)); }
    }

    /// <summary>An item in Development with the pull request 12 on its own branch (changed files: <paramref name="changed"/>).</summary>
    static (FakeGh Gh, FakeStateStore Store, RuleKit Kit, OpenPr Open) Make(string column = "Development", bool open = true, bool withPr = true, int changed = 3, bool draft = false, string[]? files = null)
    {
        var gh = new FakeGh().WithBoard(n => Gql.Item(n, column, open, ["Auto Merge"], title: "Fix the thing"));
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        gh.Write(HttpMethod.Delete, "repos/acme/orders/git/refs/factory/factory-worker/claims/5", status: 204);
        gh.Get("repos/acme/orders/pulls?state=open&per_page=100", withPr
            ? new JsonArray(new JsonObject { ["number"] = 12, ["head"] = new JsonObject { ["ref"] = "factory-worker/5-development", ["sha"] = Sha('b') }, ["base"] = new JsonObject { ["ref"] = "master" }, ["draft"] = draft, ["html_url"] = "https://github.com/acme/orders/pull/12", ["title"] = "Fix (#5)" })
            : new JsonArray());
        gh.Get("repos/acme/orders/pulls/12", new JsonObject { ["number"] = 12, ["changed_files"] = changed, ["draft"] = draft, ["node_id"] = "PR_12", ["html_url"] = "https://github.com/acme/orders/pull/12", ["head"] = new JsonObject { ["ref"] = "factory-worker/5-development", ["sha"] = Sha('b') } });
        gh.Get("repos/acme/orders/pulls/12/files", new JsonArray((files ?? ["src/a.cs"]).Select(f => (JsonNode?)new JsonObject { ["filename"] = f }).ToArray()));
        var store = new FakeStateStore();
        var o = new OpenPr();
        return (gh, store, VerbFixture.Kit(gh, store), o);
    }

    static Task<JsonObject> Run(RuleKit kit, OpenPr o, params (string, object?)[] a) => CopilotSettleVerb.Run(kit, J.Obj(a), o.Call, Ct);

    [Theory]
    [InlineData("launch", "launch")]
    [InlineData("end", "none")]
    public async Task AClosedItemOrOneOutsideDevelopment_IsLeftAlone_AndALaunchStillLaunches(string stage, string action)
    {
        var (_, _, closed, o) = Make(open: false);
        var r = await Run(closed, o, ("item", 5), ("stage", stage));
        Assert.Equal((action, "closed"), (r["action"]!.GetValue<string>(), r["reason"]!.GetValue<string>()));
        var (_, _, design, o2) = Make(column: "Technical Design");
        r = await Run(design, o2, ("item", 5), ("stage", stage));
        Assert.Equal((action, "not-in-development"), (r["action"]!.GetValue<string>(), r["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task TheStageIsLaunchOrEnd()
    {
        var (_, _, kit, o) = Make();
        Assert.Equal("stage is launch or end", (await Assert.ThrowsAsync<VerbReject>(() => Run(kit, o, ("item", 5), ("stage", "middle")))).Message);
        Assert.Equal("missing argument: stage", (await Assert.ThrowsAsync<VerbReject>(() => Run(kit, o, ("item", 5)))).Message);
    }

    [Fact]
    public async Task AnItemThatAlreadyWaitsForItsChecks_IsAnsweredAwaiting_WithoutTouchingIt()
    {
        var (gh, store, kit, o) = Make();
        store.Of(5)["awaiting_checks"] = new JsonObject { ["pr"] = 12, ["sha"] = Sha('b') };
        var r = await Run(kit, o, ("item", 5), ("stage", "end"));
        Assert.Equal(("awaiting", true, 12), (r["action"]!.GetValue<string>(), r["already"]!.GetValue<bool>(), r["pr"]!.GetValue<int>()));
        Assert.Empty(store.Saves); Assert.Empty(gh.Writes.Where(w => w.Path != "graphql"));
    }

    [Fact]
    public async Task AMergedItemAndAParkOfAnotherKindAreLeftAlone_AWorkerFailureParkIsOverridden()
    {
        var (_, store, kit, o) = Make();
        store.Of(5)["merge"] = new JsonObject { ["sha"] = Sha('c') };
        Assert.Equal("merged", (await Run(kit, o, ("item", 5), ("stage", "end")))["reason"]!.GetValue<string>());
        store.Of(5).Remove("merge");
        store.Of(5)["park"] = new JsonObject { ["reason"] = "credit-guard", ["until"] = "2026-10-30T12:00:00.000Z" };
        Assert.Equal("parked credit-guard", (await Run(kit, o, ("item", 5), ("stage", "end")))["reason"]!.GetValue<string>());
        store.Of(5)["park"] = new JsonObject { ["reason"] = "worker-failure", ["until"] = "2026-10-30T12:00:00.000Z" };
        var r = await Run(kit, o, ("item", 5), ("stage", "end"));
        Assert.Equal(("awaiting", "worker-failure"), (r["action"]!.GetValue<string>(), r["overrode"]!.GetValue<string>()));
        Assert.Equal("development:awaiting-checks", store.States[5]["park"]!["reason"]!.GetValue<string>());   // the new wait is the park now
    }

    [Fact]
    public async Task Launch_WithoutAPullRequest_LaunchesATask_WithOneAndNoRework_TheGatewayWaitsForItsChecks()
    {
        var (_, _, none, o) = Make(withPr: false);
        Assert.Equal(("launch", "no-pr"), ((await Run(none, o, ("item", 5), ("stage", "launch")))["action"]!.GetValue<string>(), (await Run(none, o, ("item", 5), ("stage", "launch")))["reason"]!.GetValue<string>()));
        var (gh, store, kit, o2) = Make();
        var r = await Run(kit, o2, ("item", 5), ("stage", "launch"));
        Assert.Equal(("awaiting", 12, Sha('b')), (r["action"]!.GetValue<string>(), r["pr"]!.GetValue<int>(), r["sha"]!.GetValue<string>()));
        var s = store.States[5];
        var ac = s["awaiting_checks"]!;
        Assert.Equal((12, Sha('b'), "Completed by the gateway: it already has its pull request with changes; no new Development task is started.", "2026-10-29T12:00:00.000Z", "Development"),
            (ac["pr"]!.GetValue<int>(), ac["sha"]!.GetValue<string>(), ac["comment"]!.GetValue<string>(), ac["since"]!.GetValue<string>(), ac["column"]!.GetValue<string>()));
        Assert.Equal(("development:awaiting-checks", "2026-10-29T13:00:00.000Z"), (s["park"]!["reason"]!.GetValue<string>(), s["park"]!["until"]!.GetValue<string>()));
        Assert.Equal((12, "factory-worker/5-development", "https://github.com/acme/orders/pull/12"), (s["pr"]!["number"]!.GetValue<int>(), s["pr"]!["branch"]!.GetValue<string>(), s["pr"]!["url"]!.GetValue<string>()));
        Assert.Equal(["DELETE repos/acme/orders/git/refs/factory/factory-worker/claims/5", "POST repos/acme/orders/issues/5/comments"], gh.Writes.Select(w => $"{w.Method.Method} {w.Path}").ToArray());
        Assert.Equal("\U0001F916 factory-worker: Development is done pending checks: it already has its pull request with changes; no new Development task is started, and PR #12 carries the change (head `bbbbbbb`). The gateway waits for its checks and moves the item on when they are green; red checks go back to Development to fix forward.\n\n<!-- factory-journal:factory-worker -->",
            gh.Writes.Last().Body!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Launch_OfAReworkRecordsTheHeadItStartsFrom()
    {
        var (gh, store, kit, o) = Make();
        store.Of(5)["pr"] = new JsonObject { ["number"] = 12, ["rejection"] = "the validation failed" };
        var r = await Run(kit, o, ("item", 5), ("stage", "launch"));
        Assert.Equal(("launch", "rework", 12), (r["action"]!.GetValue<string>(), r["reason"]!.GetValue<string>(), r["pr"]!.GetValue<int>()));
        Assert.Equal(Sha('b'), store.States[5]["copilot_base"]!.GetValue<string>());
        Assert.False(store.States[5].ContainsKey("awaiting_checks"));
        Assert.Empty(gh.Writes.Where(w => w.Path != "graphql"));
    }

    [Fact]
    public async Task End_WithNothingNewOnThePullRequest_DoesNotWaitForChecks()
    {
        var (_, store, kit, o) = Make();
        store.Of(5)["copilot_base"] = Sha('b');   // the rework started from this head and pushed nothing
        Assert.Equal("no-new-commits", (await Run(kit, o, ("item", 5), ("stage", "end")))["reason"]!.GetValue<string>());
        var (_, _, empty, o2) = Make(changed: 0);
        Assert.Equal("empty-pr", (await Run(empty, o2, ("item", 5), ("stage", "end")))["reason"]!.GetValue<string>());
        var (_, _, docs, o3) = Make(files: ["docs/a.md"]);
        Assert.Equal("docs-only-pr", (await Run(docs, o3, ("item", 5), ("stage", "end")))["reason"]!.GetValue<string>());
        var (_, store4, docs4, o4) = Make(files: ["docs/a.md"]);
        store4.Of(5)["design"] = new JsonObject { ["docs_only"] = true };   // the design recorded the item as docs-only: its documentation pull request is the change
        Assert.Equal("awaiting", (await Run(docs4, o4, ("item", 5), ("stage", "end")))["action"]!.GetValue<string>());
        var (_, _, nopr, o5) = Make(withPr: false);
        Assert.Equal("no-pr", (await Run(nopr, o5, ("item", 5), ("stage", "end")))["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task End_AKeptReRunRecordOfTheSamePullRequestIsCarriedOver()
    {
        var (_, store, kit, o) = Make();
        store.Of(5)["pr"] = new JsonObject { ["number"] = 12, ["rerun"] = new JsonObject { ["x"] = 1 } };
        await Run(kit, o, ("item", 5), ("stage", "end"));
        Assert.Equal(1, store.States[5]["pr"]!["rerun"]!["x"]!.GetValue<int>());
        var (_, store2, kit2, o2) = Make();
        store2.Of(5)["pr"] = new JsonObject { ["number"] = 99, ["rerun"] = new JsonObject { ["x"] = 1 } };
        await Run(kit2, o2, ("item", 5), ("stage", "end"));
        Assert.False(store2.States[5]["pr"]!.AsObject().ContainsKey("rerun"));
    }

    [Fact]
    public async Task End_ADraftIsMarkedReadyForReview()
    {
        var (gh, _, kit, o) = Make(draft: true);
        await Run(kit, o, ("item", 5), ("stage", "end"));
        var m = gh.Writes.Single(w => w.Path == "graphql");
        Assert.Contains("markPullRequestReadyForReview", m.Body!["query"]!.GetValue<string>());
        Assert.Equal("PR_12", m.Body["variables"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task End_TheTasksOwnBranchWithChangesIsTakenOverThroughOpenPr()
    {
        var (gh, store, kit, o) = Make(withPr: false);
        var opened = false;
        gh.On(HttpMethod.Get, "repos/acme/orders/pulls?state=open&per_page=100", _ => new GhResult(200, opened ? new JsonArray(new JsonObject { ["number"] = 12, ["head"] = new JsonObject { ["ref"] = "copilot/fix-5", ["sha"] = Sha('b') }, ["base"] = new JsonObject { ["ref"] = "master" }, ["html_url"] = "u" }) : new JsonArray()));
        gh.Get("repos/acme/orders/compare/master...copilot%2Ffix-5", new JsonObject { ["status"] = "ahead", ["ahead_by"] = 2, ["files"] = new JsonArray(new JsonObject { ["filename"] = "a" }) });
        var call = new OpenPr();
        var r = await CopilotSettleVerb.Run(kit, J.Obj(("item", 5), ("stage", "end"), ("head", "copilot/fix-5")), (v, a, c) => { opened = true; store.Of(5)["pr"] = new JsonObject { ["number"] = 12, ["branch"] = "copilot/fix-5" }; return call.Call(v, a, c); }, Ct);
        Assert.Equal("awaiting", r["action"]!.GetValue<string>());
        var args = call.Calls.Single();
        Assert.Equal((5, "copilot/fix-5", "Fix the thing"), (args["item"]!.GetValue<int>(), args["head"]!.GetValue<string>(), args["title"]!.GetValue<string>()));
    }

    [Fact]
    public async Task End_ARefusedTakeoverIsNoneWithItsCode_ANonAheadBranchIsNotTaken()
    {
        var (gh, _, kit, o) = Make(withPr: false);
        gh.Get("repos/acme/orders/compare/master...copilot%2Ffix-5", new JsonObject { ["status"] = "ahead", ["ahead_by"] = 2, ["files"] = new JsonArray(new JsonObject { ["filename"] = "a" }) });
        o.Exit = 10; o.Output = "{\"ok\":false,\"code\":\"branch-of-other-item\",\"exit\":10}";
        var r = await Run(kit, o, ("item", 5), ("stage", "end"), ("head", "copilot/fix-5"));
        Assert.Equal(("none", "takeover-refused branch-of-other-item"), (r["action"]!.GetValue<string>(), r["reason"]!.GetValue<string>()));
        o.Calls.Clear();
        o.Exit = 20; o.Output = "{\"ok\":false,\"code\":\"internal-error\",\"exit\":20}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(kit, o, ("item", 5), ("stage", "end"), ("head", "copilot/fix-5")));   // not the rule's refusal: the verb fails
        o.Calls.Clear();
        gh.Get("repos/acme/orders/compare/master...copilot%2Ffix-5", new JsonObject { ["status"] = "behind", ["ahead_by"] = 0 });
        Assert.Equal("no-pr", (await Run(kit, o, ("item", 5), ("stage", "end"), ("head", "copilot/fix-5")))["reason"]!.GetValue<string>());
        Assert.Empty(o.Calls);
        gh.Get("repos/acme/orders/compare/master...copilot%2Ffix-5", null, 404);
        Assert.Equal("no-pr", (await Run(kit, o, ("item", 5), ("stage", "end"), ("head", "copilot/fix-5")))["reason"]!.GetValue<string>());
        Assert.Empty(o.Calls);
        Assert.Equal("no-pr", (await Run(kit, o, ("item", 5), ("stage", "end"), ("head", "not-copilot/x")))["reason"]!.GetValue<string>());   // only a copilot/ branch is the task's
    }
}
