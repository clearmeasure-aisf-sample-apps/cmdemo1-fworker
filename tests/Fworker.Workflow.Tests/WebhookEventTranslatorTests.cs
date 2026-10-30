using System.Text.Json.Nodes;
using Fworker.Workflows.Reconcilers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.8: the translation of a webhook payload into the factory's events, pinned to Verb_ingest_event of plugin/scripts/lib/Verbs.Sweep.ps1 (the gateway's Gateway.Tests/IngestEventParityTests run the real rule script
/// on the same payloads and compare). Here: each event kind, the cases the script drops, and the reads (the column of a labeled item, the issue behind a board card).
/// </summary>
public class WebhookEventTranslatorTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 29, 9, 30, 15, 123, TimeSpan.Zero);
    static readonly WebhookTranslatorConfig Config = new("acme", "orders", "factory-worker", "master", "factory worker", "");

    sealed class Lookups : IWebhookLookups
    {
        public WebhookItemFacts? Item = new(["factory worker"], "Development");
        public WebhookIssueNode? Node;
        public Exception? ItemFails, NodeFails;
        public List<string> Calls = [];
        public Task<WebhookItemFacts?> ReadItem(int item, CancellationToken ct = default) { Calls.Add("item " + item); return ItemFails is null ? Task.FromResult(Item) : Task.FromException<WebhookItemFacts?>(ItemFails); }
        public Task<WebhookIssueNode?> ReadIssueNode(string nodeId, CancellationToken ct = default) { Calls.Add("node " + nodeId); return NodeFails is null ? Task.FromResult(Node) : Task.FromException<WebhookIssueNode?>(NodeFails); }
    }

    static WebhookEventTranslator Make(Lookups? l = null, WebhookTranslatorConfig? c = null) => new(c ?? Config, l ?? new Lookups(), () => T0);
    static JsonNode Json(string s) => JsonNode.Parse(s)!;
    static async Task<WebhookTranslation> Run(string ev, string payload, Lookups? l = null, WebhookTranslatorConfig? c = null) => await Make(l, c).Translate(ev, "d-1", Json(payload));

    [Fact]
    public async Task ALabeledItem_MakesItemLabeled_WithItsColumnFromTheRead()
    {
        var l = new Lookups();
        var t = await Run("issues", """{"action":"labeled","issue":{"number":7,"labels":[{"name":"factory worker"}]},"label":{"name":"Factory Worker"}}""", l);
        var e = Assert.Single(t.Events);
        Assert.Equal(("github", "item.labeled", 7, "Development", "d-1", "2026-10-29T09:30:15.123Z"), (e["provider"]!.ToString(), e["event"]!.ToString(), (int)e["item"]!, e["column"]!.ToString(), e["ref"]!.ToString(), e["at"]!.ToString()));
        Assert.Equal(["item 7"], l.Calls);
    }

    [Fact]
    public async Task ALabeledItem_WhoseReadFails_HasNoColumn_AndStillMakesTheEvent()
    {
        var l = new Lookups { ItemFails = new WebhookLookupException(30, "secret-missing", "GH_TOKEN is not set on the gateway") };
        var e = Assert.Single((await Run("issues", """{"action":"labeled","issue":{"number":7,"labels":[{"name":"factory worker"}]},"label":{"name":"factory worker"}}""", l)).Events);
        Assert.Null(e["column"]);
    }

    [Fact]
    public async Task ALabeledItem_ReadWithoutItsEnvLabel_HasNoColumn_InAScopedEnvironment()
    {
        var scoped = Config with { EnvLabel = "env:tdd" };
        var l = new Lookups { Item = new(["factory worker"], "Development") };   // the fresh read has lost its env label: out of scope
        var e = Assert.Single((await Run("issues", """{"action":"labeled","issue":{"number":7,"labels":[{"name":"factory worker"},{"name":"env:tdd"}]},"label":{"name":"env:TDD"}}""", l, scoped)).Events);
        Assert.Null(e["column"]);
    }

    [Theory]
    [InlineData("closed", "item.closed")]
    [InlineData("reopened", "item.reopened")]
    public async Task AClosedOrReopenedItem_MakesItsEvent_WithNoColumn(string action, string name)
    {
        var e = Assert.Single((await Run("issues", $$$"""{"action":"{{{action}}}","issue":{"number":3,"labels":[{"name":"FACTORY WORKER"}]}}""")).Events);
        Assert.Equal((name, 3), (e["event"]!.ToString(), (int)e["item"]!));
        Assert.Null(e["column"]);
    }

    [Theory]
    [InlineData("""{"action":"closed","issue":{"number":3,"labels":[{"name":"bug"}]}}""")]                                        // not a factory item
    [InlineData("""{"action":"edited","issue":{"number":3,"labels":[{"name":"factory worker"}]}}""")]                             // an action the script ignores
    [InlineData("""{"action":"labeled","issue":{"number":3,"labels":[{"name":"factory worker"}]},"label":{"name":"bug"}}""")]     // another label was added
    public async Task AnIssueEventThatIsNotNews_MakesNothing(string payload) => Assert.Empty((await Run("issues", payload)).Events);

    [Fact]
    public async Task AScopedEnvironment_SeesOnlyItemsWithExactlyItsEnvLabel()
    {
        var scoped = Config with { EnvLabel = "env:tdd" };
        Assert.Single((await Run("issues", """{"action":"closed","issue":{"number":3,"labels":[{"name":"factory worker"},{"name":"ENV:TDD"}]}}""", c: scoped)).Events);
        Assert.Empty((await Run("issues", """{"action":"closed","issue":{"number":3,"labels":[{"name":"factory worker"}]}}""", c: scoped)).Events);
        Assert.Empty((await Run("issues", """{"action":"closed","issue":{"number":3,"labels":[{"name":"factory worker"},{"name":"env:uat"}]}}""", c: scoped)).Events);
        Assert.Empty((await Run("issues", """{"action":"closed","issue":{"number":3,"labels":[{"name":"factory worker"},{"name":"env:tdd"},{"name":"env:uat"}]}}""", c: scoped)).Events);
    }

    [Fact]
    public async Task ABranchBuild_AndAMasterBuild_AreToldApart_WithTheRunsFields()
    {
        var branch = Assert.Single((await Run("workflow_run", """{"action":"completed","workflow_run":{"head_branch":"factory-worker/12-add-thing","head_sha":"abc","conclusion":"success","name":"Build","id":99,"run_attempt":2}}""")).Events);
        Assert.Equal(("build.branch_completed", 12, "abc", "success", "Build", 99, 2), (branch["event"]!.ToString(), (int)branch["item"]!, branch["sha"]!.ToString(), branch["conclusion"]!.ToString(), branch["workflow"]!.ToString(), (int)branch["run_id"]!, (int)branch["run_attempt"]!));
        var master = Assert.Single((await Run("workflow_run", """{"action":"in_progress","workflow_run":{"head_branch":"MASTER","head_sha":"def","conclusion":null,"name":"Build"}}""")).Events);
        Assert.Equal("build.master_started", master["event"]!.ToString());
        Assert.Null(master["item"]); Assert.Null(master["conclusion"]); Assert.Null(master["run_id"]); Assert.Null(master["run_attempt"]);
        Assert.Empty((await Run("workflow_run", """{"action":"requested","workflow_run":{"head_branch":"master"}}""")).Events);
        Assert.Empty((await Run("workflow_run", """{"action":"completed","workflow_run":{"head_branch":"some-feature"}}""")).Events);
    }

    [Fact]
    public async Task ACardMovedToAnotherColumn_MakesItemColumnChanged_ForAnIssueOfThisRepositoryOnly()
    {
        const string payload = """{"action":"edited","projects_v2_item":{"content_node_id":"I_5"},"changes":{"field_value":{"field_name":"Status","to":{"name":"Development"}}}}""";
        var home = new Lookups { Node = new(5, "Acme/Orders", ["Factory Worker"]) };
        var e = Assert.Single((await Run("projects_v2_item", payload, home)).Events);
        Assert.Equal(("item.column_changed", 5, "Development"), (e["event"]!.ToString(), (int)e["item"]!, e["column"]!.ToString()));
        Assert.Equal(["node I_5"], home.Calls);
        Assert.Empty((await Run("projects_v2_item", payload, new Lookups { Node = new(5, "acme/other", ["factory worker"]) })).Events);   // an issue of another repository on the board
        Assert.Empty((await Run("projects_v2_item", payload, new Lookups { Node = null })).Events);                                        // a draft card or a pull request
        Assert.Empty((await Run("projects_v2_item", payload, new Lookups { Node = new(5, "acme/orders", ["bug"]) })).Events);              // not a factory item
    }

    [Fact]
    public async Task ACardEventThatIsNotAStatusEdit_ReadsNothing()
    {
        var l = new Lookups { Node = new(5, "acme/orders", ["factory worker"]) };
        Assert.Empty((await Run("projects_v2_item", """{"action":"created","projects_v2_item":{"content_node_id":"I_5"}}""", l)).Events);
        Assert.Empty((await Run("projects_v2_item", """{"action":"edited","projects_v2_item":{"content_node_id":"I_5"},"changes":{"field_value":{"field_name":"Priority"}}}""", l)).Events);
        Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task AFailedNodeRead_IsTheCallersFailure_AsTheScriptsRejectIs()
    {
        var l = new Lookups { NodeFails = new WebhookLookupException(20, "github-rate-limited", "API rate limit", 60) };
        var e = await Assert.ThrowsAsync<WebhookLookupException>(() => Run("projects_v2_item", """{"action":"edited","projects_v2_item":{"content_node_id":"I_5"},"changes":{"field_value":{"field_name":"Status","to":{"name":"Done"}}}}""", l));
        Assert.Equal((20, "github-rate-limited", 60), (e.Exit, e.Code, e.RetryAfterSeconds!.Value));
    }

    [Theory]
    [InlineData("opened", "pr.opened")]
    [InlineData("reopened", "pr.opened")]
    [InlineData("ready_for_review", "pr.opened")]
    [InlineData("synchronize", "pr.head_changed")]
    [InlineData("converted_to_draft", "pr.drafted")]
    public async Task AnItemsPullRequest_MakesItsEvent_WithPrShaAndLogin(string action, string name)
    {
        var e = Assert.Single((await Run("pull_request", """{"action":"ACTION","sender":{"login":"alice"},"pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h1","repo":{"full_name":"Acme/Orders"}},"base":{"ref":"master"}}}""".Replace("ACTION", action))).Events);
        Assert.Equal((name, 12, 40, "h1", "alice"), (e["event"]!.ToString(), (int)e["item"]!, (int)e["pr"]!, e["sha"]!.ToString(), e["login"]!.ToString()));
    }

    [Fact]
    public async Task AClosedPullRequest_IsMergedOrClosed_AndAMergeIntoMasterAlsoMovesMaster()
    {
        var events = (await Run("pull_request", """{"action":"closed","pull_request":{"number":40,"merged":true,"merge_commit_sha":"m1","head":{"ref":"factory-worker/12-x","sha":"h1","repo":{"full_name":"acme/orders"}},"base":{"ref":"master"}}}""")).Events;
        Assert.Equal(["base.merged", "pr.merged"], events.Select(e => e["event"]!.ToString()));
        Assert.Equal(("m1", 40), (events[0]["sha"]!.ToString(), (int)events[0]["pr"]!)); Assert.Null(events[0]["item"]);
        Assert.Equal("", events[1]["login"]!.ToString());   // no sender
        var closed = Assert.Single((await Run("pull_request", """{"action":"closed","pull_request":{"number":41,"merged":false,"head":{"ref":"factory-worker/13-y","sha":"h2","repo":{"full_name":"acme/orders"}},"base":{"ref":"master"}}}""")).Events);
        Assert.Equal("pr.closed", closed["event"]!.ToString());
        // a pull request of someone else's branch that merged into master moves master and nothing else
        var other = Assert.Single((await Run("pull_request", """{"action":"closed","pull_request":{"number":42,"merged":true,"merge_commit_sha":"m2","head":{"ref":"dependabot/x","sha":"h3"},"base":{"ref":"master"}}}""")).Events);
        Assert.Equal("base.merged", other["event"]!.ToString());
    }

    [Fact]
    public async Task APullRequestFromAFork_IsDropped_EvenWhenItMergedIntoMaster()
    {
        var t = await Run("pull_request", """{"action":"closed","pull_request":{"number":40,"merged":true,"merge_commit_sha":"m1","head":{"ref":"factory-worker/12-x","sha":"h1","repo":{"full_name":"mallory/orders"}},"base":{"ref":"master"}}}""");
        Assert.Empty(t.Events); Assert.Equal("pr.fork", t.Dropped);
        var noRepo = await Run("pull_request", """{"action":"opened","pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h1"},"base":{"ref":"master"}}}""");
        Assert.Equal("pr.fork", noRepo.Dropped);   // a deleted fork has no repo
    }

    [Fact]
    public async Task AReview_MakesPrReviewed_WithTheStateUpperCased()
    {
        var e = Assert.Single((await Run("pull_request_review", """{"action":"submitted","review":{"user":{"login":"bob"},"state":"approved","commit_id":"c1"},"pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h1"}}}""")).Events);
        Assert.Equal(("pr.reviewed", 12, 40, "h1", "bob", "APPROVED", "c1", "submitted"), (e["event"]!.ToString(), (int)e["item"]!, (int)e["pr"]!, e["sha"]!.ToString(), e["login"]!.ToString(), e["state"]!.ToString(), e["commit_id"]!.ToString(), e["action"]!.ToString()));
        Assert.Empty((await Run("pull_request_review", """{"action":"edited","pull_request":{"number":40,"head":{"ref":"factory-worker/12-x"}}}""")).Events);
        Assert.Empty((await Run("pull_request_review", """{"action":"submitted","pull_request":{"number":40,"head":{"ref":"someone/else"}}}""")).Events);
    }

    [Fact]
    public async Task APayloadOfAnotherRepository_IsDropped_BeforeAnythingIsRead()
    {
        var l = new Lookups();
        var t = await Run("issues", """{"repository":{"full_name":"acme/other"},"action":"labeled","issue":{"number":7,"labels":[{"name":"factory worker"}]},"label":{"name":"factory worker"}}""", l);
        Assert.Empty(t.Events); Assert.Equal("other-repository", t.Dropped); Assert.Empty(l.Calls);
        Assert.Single((await Run("issues", """{"repository":{"full_name":"ACME/Orders"},"action":"closed","issue":{"number":7,"labels":[{"name":"factory worker"}]}}""")).Events);
    }

    [Fact]
    public async Task AnEventKindTheScriptDoesNotKnow_MakesNothing()
        => Assert.Empty((await Run("push", """{"ref":"refs/heads/master"}""")).Events);
}
