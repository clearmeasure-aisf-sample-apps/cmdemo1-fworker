using System.Text.Json.Nodes;
using Fworker.Workflows.Merge;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10q: the item verb calls that were the rule script's (the completion of Development with the CI rule and the migration check, open-pr for a copilot/* branch, item-pr for an owner task) answer as the script does. Each case is a
/// branch of Verbs.Item.ps1 Verb_complete, Prs.ps1 Get-RedHeadRuling and Get-MigrationCollision, or Verbs.Pr.ps1 Verb_open_pr, Use-CopilotDraft and Resolve-OwnerPr: the same input, the script's answer, the script's writes.
/// </summary>
public class ItemVerbFallbacksTests
{
    const string Sha = "0123456789abcdef0123456789abcdef01234567";
    const string Acceptance = "Acceptance Tests [shard 1/4]";

    // ---- the completion of Development ---------------------------------------------------------------------------------------------------------------------

    static async Task<(ItemWorld W, object Args)> AtDevelopment(string[]? files = null, string[]? labels = null, bool owner = true)
    {
        var w = new ItemWorld();
        w.AddIssue(1, "Development", "the item", labels ?? ["factory worker"]);
        if (owner) await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        else await w.Call("claim-item", new { item = 1, holder = "alice" });
        var rv = await w.Call("record-review", new { item = 1, column = "Development", role = "reviewer", text = "REVIEW: COMPLETE" });
        if (files is not null) w.AddPull(1, null, files);
        w.Writes.Clear();
        return (w, new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE", review_ref = rv["review_ref"]!.ToString() });
    }

    [Fact]
    public async Task Complete_InDevelopment_NeedsAnOpenPullRequestThatChangesFiles_AndEveryRefusalCountsForTheLadder()
    {
        var (w, args) = await AtDevelopment();
        var none = await w.Call("complete", args, 10);
        Assert.Equal("no-open-pr", none["code"]!.ToString()); Assert.Equal("no open pull request linked to this work item", none["reason"]!.ToString());
        var pr = w.AddPull(1);
        var empty = await w.Call("complete", args, 10);
        Assert.Equal("empty-pr", empty["code"]!.ToString()); Assert.Equal(pr.Number, (int)empty["prNumber"]!); Assert.Equal("pull request changes no files; an empty pull request is not a delivery", empty["reason"]!.ToString());
        Assert.Equal(2, w.Refusals[1]);
        Assert.Empty(w.Writes);
    }

    [Fact]
    public async Task Complete_InDevelopment_ADocsOnlyPullRequestNeedsTheDesignsRecord()
    {
        var (w, args) = await AtDevelopment(["docs/guide.md", "README.md"]);
        var no = await w.Call("complete", args, 10);
        Assert.Equal("development-docs-only-pr", no["code"]!.ToString());
        Assert.Contains("(<!-- aisf:docs-only --> inside the design section)", no["reason"]!.ToString());
        // the design recorded it as docs-only: the checks are the gate and no private build is owed
        w.State(1)["design"] = new JsonObject { ["docs_only"] = true, ["at"] = "2026-10-29T11:00:00.000Z" };
        var ok = await w.Call("complete", args);
        Assert.True((bool)ok["completed"]!); Assert.False((bool)ok["build_owed"]!);
    }

    [Fact]
    public async Task Complete_InDevelopment_AGreenHeadMovesTheCard_RecordsThePullRequest_AndClearsWhatTheLastRoundLeft()
    {
        var (w, args) = await AtDevelopment(["src/App.cs"]);
        var pr = w.Pulls.Single();
        w.State(1)["fix_forward_rounds"] = 2; w.State(1)["master_defect"] = new JsonObject { ["checks"] = new JsonArray("x"), ["sha"] = "old" };
        w.State(1)["pr"] = new JsonObject { ["number"] = pr.Number, ["rejection"] = "the last round's complaint" };
        var a = await w.Call("complete", args);
        Assert.True((bool)a["completed"]!); Assert.Equal("Functional Testing", a["moved_to"]!.ToString()); Assert.True((bool)a["continue"]!); Assert.Equal("Functional Testing", a["next_column"]!.ToString());
        Assert.Equal(pr.Number, (int)a["prNumber"]!); Assert.True((bool)a["build_owed"]!); Assert.Null(a["checks"]); Assert.Null(a["ci"]);
        var s = w.State(1);
        Assert.Equal(pr.Number, (int)s["pr"]!["number"]!); Assert.Equal(pr.Head, s["pr"]!["branch"]!.ToString()); Assert.Equal($"https://github.com/o/r/pull/{pr.Number}", s["pr"]!["url"]!.ToString());
        Assert.Null(s["pr"]!["rejection"]); Assert.Null(s["fix_forward_rounds"]); Assert.Null(s["master_defect"]); Assert.Null(s["awaiting_checks"]);
        Assert.Equal([Sha], w.CheckReads);
        Assert.Equal("Functional Testing", w.Issues[1].Column);
    }

    [Fact]
    public async Task Complete_InDevelopment_ARedHeadOfItsOwn_IsRefused_AndCountedOncePerHead()
    {
        var (w, args) = await AtDevelopment(["src/App.cs"]);
        var pr = w.Pulls.Single();
        w.HeadChecks[Sha] = ItemWorld.Facts("failure", failed: ["secret-scan"]);
        var a = await w.Call("complete", args, 10);
        Assert.Equal("checks-not-green", a["code"]!.ToString()); Assert.Equal("pull request checks are not green (red: secret-scan)", a["reason"]!.ToString());
        Assert.Equal(pr.Number, (int)a["prNumber"]!); Assert.Equal("failure", a["checks"]!["status"]!.ToString()); Assert.Equal("secret-scan", a["checks"]!["failed_checks"]![0]!.ToString());
        var s = w.State(1);
        Assert.Equal("secret-scan", s["pr"]!["failed_checks"]![0]!.ToString()); Assert.Equal(pr.Head, s["pr"]!["branch"]!.ToString()); Assert.Equal(1, (int)s["checks_red"]!["count"]!);
        Assert.NotNull(s["claim"]);   // the owner keeps the item
        await w.Call("complete", args, 10);
        Assert.Equal(1, (int)w.State(1)["checks_red"]!["count"]!);   // the same head is counted once
        Assert.Equal(2, w.Refusals[1]);
        Assert.Equal("Development", w.Issues[1].Column);
    }

    [Fact]
    public async Task Complete_InDevelopment_ARedHeadOnFlakyChecks_IsRerunOnce_ThenWaits_ThenIsRedForGood()
    {
        var (w, args) = await AtDevelopment(["src/App.cs"]);
        var pr = w.Pulls.Single();
        w.HeadChecks[Sha] = ItemWorld.Facts("failure", failed: ["Build result"], nongateFailed: [Acceptance]);
        w.ActionRuns[Sha] = [new JsonObject { ["id"] = 77, ["status"] = "completed", ["conclusion"] = "failure", ["run_attempt"] = 1 }];
        var first = await w.Call("complete", args, 21);
        Assert.Equal("accepted-checks-pending", first["code"]!.ToString());
        Assert.Equal("pull request checks failed and the gateway applies the CI rule (rerun: checks other than the gates failed (Acceptance Tests [shard 1/4]); known flaky: Acceptance Tests [shard 1/4]); it decides when that is over; the gateway is watching them and will advance the item when they are green. Stay connected while the checks run: do not end the session. The gateway advances the item when the checks are green.", first["reason"]!.ToString());
        Assert.Contains($"POST /repos/{{repo}}/actions/runs/77/rerun-failed-jobs 201 -", w.Writes);
        var rec = w.State(1)["pr"]!["rerun"]![Sha]!;
        Assert.Equal(77, (int)rec["runs"]![0]!["id"]!); Assert.Equal(1, (int)rec["runs"]![0]!["attempt"]!); Assert.Equal(Acceptance, rec["causes"]![0]!.ToString());
        var s = w.State(1);
        Assert.Equal("development:awaiting-checks", s["park"]!["reason"]!.ToString()); Assert.Equal(pr.Number, (int)s["awaiting_checks"]!["pr"]!); Assert.Equal(Sha, s["awaiting_checks"]!["sha"]!.ToString());
        Assert.Equal("Development", s["awaiting_checks"]!["column"]!.ToString()); Assert.Equal("REVIEW: COMPLETE", s["awaiting_checks"]!["comment"]!.ToString()); Assert.NotNull(s["claim"]);
        Assert.Contains($"PR #{pr.Number} checks failed and the gateway applies the CI rule (rerun: ", w.Issues[1].Comments.Last().Body);
        // the re-run has not finished: the first attempt's results stand until it has
        var wait = await w.Call("complete", args, 21);
        Assert.Contains("rerun-wait: the re-run of the failed jobs has not finished", wait["reason"]!.ToString());
        Assert.Single(w.Writes.Where(l => l.Contains("rerun-failed-jobs")));   // once per head
        // the re-run finished and the head is still red: red for its own reason, the pull request record keeps the re-runs
        w.ActionRuns[Sha] = [new JsonObject { ["id"] = 77, ["status"] = "completed", ["conclusion"] = "failure", ["run_attempt"] = 2 }];
        var red = await w.Call("complete", args, 10);
        Assert.Equal("checks-not-green", red["code"]!.ToString()); Assert.Equal($"pull request checks are not green (still red after one re-run of the failed jobs: {Acceptance})", red["reason"]!.ToString());
        Assert.Equal(77, (int)w.State(1)["pr"]!["rerun"]![Sha]!["runs"]![0]!["id"]!); Assert.Equal(1, (int)w.State(1)["checks_red"]!["count"]!);
    }

    [Fact]
    public async Task Complete_InDevelopment_ARunStillGoing_IsWaitedFor_AndAnUnreadableBaseBranchDefers()
    {
        var (w, args) = await AtDevelopment(["src/App.cs"]);
        w.HeadChecks[Sha] = ItemWorld.Facts("failure", failed: ["secret-scan"]);
        w.ActionRuns[Sha] = [new JsonObject { ["id"] = 5, ["status"] = "in_progress", ["conclusion"] = null, ["run_attempt"] = 1 }];
        var running = await w.Call("complete", args, 21);
        Assert.Contains("(running: a workflow run of the head is still in progress)", running["reason"]!.ToString());
        Assert.DoesNotContain(w.Writes, l => l.Contains("rerun-failed-jobs"));
        w.ActionRuns[Sha] = [new JsonObject { ["id"] = 5, ["status"] = "completed", ["conclusion"] = "failure", ["run_attempt"] = 1 }];
        w.BaseResult = new(false, [], []);
        var deferred = await w.Call("complete", args, 21);
        Assert.Contains("(deferred: the checks of master could not be read)", deferred["reason"]!.ToString());
        Assert.Null(w.State(1)["checks_red"]);   // a base branch that cannot be read costs no round
    }

    [Fact]
    public async Task Complete_InDevelopment_ADefectOfTheBaseBranch_IsReportedOnce_AndCostsNoRound()
    {
        var (w, args) = await AtDevelopment(["src/App.cs"]);
        var pr = w.Pulls.Single();
        w.HeadChecks[Sha] = ItemWorld.Facts("failure", failed: ["Build result"], nongateFailed: ["Integration Build (SQL container)"]);
        w.BaseResult = new(true, ["Integration Build (SQL container)"], []);
        var first = await w.Call("complete", args, 21);
        Assert.Contains("(master-defect: Integration Build (SQL container) also fails on master)", first["reason"]!.ToString());
        var md = w.State(1)["master_defect"]!;
        Assert.Equal("Integration Build (SQL container)", md["checks"]![0]!.ToString()); Assert.Equal(Sha, md["sha"]!.ToString()); Assert.Equal(pr.Number, (int)md["pr"]!); Assert.NotNull(md["checked_at"]);
        var reports = w.Issues[1].Comments.Where(c => c.Body.Contains("is red only on checks that fail on **master** as well")).ToList();
        Assert.Single(reports);
        Assert.Contains($"PR #{pr.Number} is red only on checks that fail on **master** as well (Integration Build (SQL container)): a defect of master, not of this change. It costs this item no fix-forward round and no demotion;", reports[0].Body);
        Assert.Null(w.State(1)["checks_red"]);
        // looked at less than a check interval ago: nothing is read and nothing is reported again
        var checkedAt = md["checked_at"]!.ToString(); var reads = w.Reads.Count;
        await w.Call("complete", args, 21);
        Assert.Equal(checkedAt, w.State(1)["master_defect"]!["checked_at"]!.ToString()); Assert.DoesNotContain(w.Reads.Skip(reads), l => l.Contains("actions/runs"));
        // after the interval it is judged again: the same checks, the same report, a new look
        w.Now = w.Now.AddMinutes(6);
        await w.Call("complete", args, 21);
        Assert.NotEqual(checkedAt, w.State(1)["master_defect"]!["checked_at"]!.ToString());
        Assert.Single(w.Issues[1].Comments.Where(c => c.Body.Contains("is red only on checks that fail on **master** as well")));
        // the base branch passes those checks again: the ruling says so (fixed), once per head, and the item still waits
        w.Now = w.Now.AddMinutes(6);
        w.BaseResult = new(true, [], ["Integration Build (SQL container)"]);
        var fixedAgain = await w.Call("complete", args, 21);
        Assert.Contains("(master-defect: Integration Build (SQL container) pass on master again)", fixedAgain["reason"]!.ToString());
    }

    [Fact]
    public async Task Complete_InDevelopment_PendingChecks_ParkTheItemUntilTheGatewayAdvancesIt()
    {
        var (w, args) = await AtDevelopment(["src/App.cs"], owner: false);
        var pr = w.Pulls.Single();
        w.HeadChecks[Sha] = ItemWorld.Facts("pending", pending: ["Build result"]);
        var a = await w.Call("complete", args, 21);
        Assert.Equal("accepted-checks-pending", a["code"]!.ToString());
        Assert.Equal("pull request checks are still running; the gateway is watching them and will advance the item when they are green. Stay connected while the checks run: do not end the session. The gateway advances the item when the checks are green.", a["reason"]!.ToString());
        Assert.Equal(pr.Number, (int)a["prNumber"]!); Assert.Equal("pending", a["checks"]!["status"]!.ToString());
        var s = w.State(1);
        Assert.Equal("development:awaiting-checks", s["park"]!["reason"]!.ToString()); Assert.Equal(pr.Number, (int)s["awaiting_checks"]!["pr"]!);
        // not the owner's: the claim ends and its ref goes
        Assert.Null(s["claim"]); Assert.Empty(w.ClaimRefs);
        Assert.Contains($"PR #{pr.Number} checks are still running; the gateway is watching them and will advance the item when green. Stay connected while the checks run.", w.Issues[1].Comments.Last().Body);
        Assert.Equal("Development", w.Issues[1].Column);
        Assert.Equal(0, w.Refusals.GetValueOrDefault(1));   // exit 21 is no data point of the ladder
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Complete_InDevelopment_WithFtWithPendingChecks_ContinuesToFunctionalTestingWhileTheChecksRun(bool asText)
    {
        var (w, args) = await AtDevelopment(["src/App.cs"]);
        w.HeadChecks[Sha] = ItemWorld.Facts("pending", pending: ["Build result"]);
        w.State(1)["fix_forward_rounds"] = 2;
        var a = await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE", review_ref = ((dynamic)args).review_ref, ftWithPendingChecks = asText ? (object)"1" : true });
        Assert.True((bool)a["completed"]!); Assert.Equal("pending", a["checks"]!.ToString()); Assert.Equal("alongside", a["ci"]!.ToString()); Assert.True((bool)a["continue"]!);
        Assert.Equal("Functional Testing", a["moved_to"]!.ToString());
        Assert.Equal(2, (int)w.State(1)["fix_forward_rounds"]!);   // alongside: nothing is cleared yet
        Assert.Null(w.State(1)["awaiting_checks"]);
    }

    [Fact]
    public async Task Complete_InDevelopment_ARedHeadWaitingOnTheCiRule_AlsoContinuesAlongside()
    {
        var (w, args) = await AtDevelopment(["src/App.cs"]);
        w.HeadChecks[Sha] = ItemWorld.Facts("failure", failed: ["Build result"], nongateFailed: [Acceptance]);
        w.ActionRuns[Sha] = [new JsonObject { ["id"] = 9, ["status"] = "completed", ["conclusion"] = "failure", ["run_attempt"] = 1 }];
        var a = await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE", review_ref = ((dynamic)args).review_ref, ftWithPendingChecks = true });
        Assert.Equal("alongside", a["ci"]!.ToString());
        Assert.NotNull(w.State(1)["pr"]!["rerun"]![Sha]);   // the re-run record travels with the pull request
        Assert.Null(w.State(1)["park"]);
    }

    [Fact]
    public async Task Complete_InDevelopment_RefusesTheMigrationNumberAnotherPullRequestTook()
    {
        var (w, args) = await AtDevelopment(["src/Database/scripts/Update/040_AddOrders.sql", "src/App.cs"]);
        var pr = w.Pulls.Single();
        w.MasterScripts.AddRange(["038_Old.sql", "040_TakenFirst.sql", "readme.txt"]);
        var a = await w.Call("complete", args, 10);
        Assert.Equal("migration-number-taken", a["code"]!.ToString());
        Assert.Equal("the pull request adds src/Database/scripts/Update/040_AddOrders.sql, but src/Database/scripts/Update/040_TakenFirst.sql on master already has that number. Renumber this branch's script to 041 (never rename the script on master: DbUp runs a renamed script again), push, then call complete again.", a["reason"]!.ToString());
        Assert.Equal(pr.Number, (int)a["prNumber"]!); Assert.Equal("src/Database/scripts/Update/040_AddOrders.sql", a["script"]!.ToString());
        Assert.Equal("src/Database/scripts/Update/040_TakenFirst.sql", a["taken_by"]!.ToString()); Assert.Equal("041", a["next"]!.ToString());
        Assert.Empty(w.CheckReads);   // refused before the checks are read
        // the number free on master: no collision; a master without the directory: none either
        w.MasterScripts.Clear(); w.MasterScripts.Add("039_Other.sql");
        Assert.True((bool)(await w.Call("complete", args))["completed"]!);
        var (w2, args2) = await AtDevelopment(["src/Database/scripts/Update/040_AddOrders.sql"]);
        w2.NoMasterScripts = true;
        Assert.True((bool)(await w2.Call("complete", args2))["completed"]!);
    }

    [Fact]
    public async Task Complete_InAColumnThatIsNotDevelopment_ReadsNoPullRequestAndNoChecks()
    {
        var w = new ItemWorld(); w.AddIssue(1, "Technical Design", "x", "factory worker");
        await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        w.Now = w.Now.AddSeconds(5);
        await w.Call("append-body", new { item = 1, content = "## Technical Design\n- do it", column = "Technical Design" });
        var rv = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "REVIEW: COMPLETE" });
        await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE", review_ref = rv["review_ref"]!.ToString() });
        Assert.Empty(w.CheckReads); Assert.DoesNotContain(w.Reads, l => l.Contains("pulls"));
        Assert.False((bool)w.State(1)["design"]!["docs_only"]!);
    }

    // ---- open-pr for a copilot/* branch --------------------------------------------------------------------------------------------------------------------

    static ItemWorld AtDevelopmentItem(params string[] labels)
    {
        var w = new ItemWorld(); w.AddIssue(1, "Development", "the item", labels.Length > 0 ? labels : ["factory worker"]);
        return w;
    }

    static ItemWorld.Pull Draft(ItemWorld w, string head, string title = "[WIP] Fix the total", string body = "does it", string baseRef = "master")
    {
        var p = w.AddPull(1, head, "src/App.cs"); p.Draft = true; p.Title = title; p.Body = body; p.Base = baseRef;
        return p;
    }

    [Fact]
    public async Task OpenPr_ForACopilotBranch_TakesOverTheDraft_NamesTheItem_MarksItReady_AndRecordsIt()
    {
        var w = AtDevelopmentItem();
        var draft = Draft(w, "copilot/fix-total");
        var a = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" });
        Assert.False((bool)a["created"]!); Assert.Equal(draft.Number, (int)a["pullRequest"]!["number"]!); Assert.Equal("copilot/fix-total", a["pullRequest"]!["branch"]!.ToString()); Assert.True((bool)a["build_owed"]!);
        Assert.Equal("Fix the total (#1)", draft.Title); Assert.False(draft.Draft); Assert.Single(w.Pulls);
        Assert.Equal(draft.Number, (int)w.State(1)["pr"]!["number"]!); Assert.Equal("copilot/fix-total", w.State(1)["pr"]!["branch"]!.ToString());
        var writes = w.Writes.Where(l => !l.StartsWith("PATCH /repos/{repo}/issues/comments")).ToList();
        Assert.StartsWith($"PATCH /repos/{{repo}}/pulls/{{#{draft.Number}}} 200 {{\"title\":\"Fix the total (#1)\"}}", writes[0]);
        Assert.StartsWith("POST /graphql markPullRequestReadyForReview 200", writes[1]);
        // the same call again: the recorded pull request is the item's, nothing more is written
        w.Writes.Clear();
        var again = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" });
        Assert.False((bool)again["created"]!); Assert.Equal(draft.Number, (int)again["pullRequest"]!["number"]!);
        Assert.DoesNotContain(w.Writes, l => l.StartsWith("PATCH /repos/{repo}/pulls") || l.Contains("markPullRequestReadyForReview"));
    }

    [Fact]
    public async Task OpenPr_ForACopilotBranch_IsNotForAnItemWithAnotherRuntime()
    {
        var w = AtDevelopmentItem("factory worker", "runtime:cursor-cloud");
        Draft(w, "copilot/fix-total");
        var a = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "t" }, 10);
        Assert.Equal("bad-branch-name", a["code"]!.ToString());
    }

    [Fact]
    public async Task OpenPr_ForACopilotBranch_RefusesADraftThatNamesAnotherItem_ButNotOneThatNamesAPullRequest()
    {
        var w = AtDevelopmentItem();
        var onItem = w.AddPull(1, "factory-worker/1-old", "src/Old.cs");
        var draft = Draft(w, "copilot/fix-total", body: "which merges into #" + onItem.Number);
        w.Compares[$"{onItem.Sha}...copilot/fix-total"] = "ahead";
        draft.Body = "Fixes #77 and merges into #" + onItem.Number;
        var no = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" }, 10);
        Assert.Equal("branch-of-other-item", no["code"]!.ToString()); Assert.Equal("the pull request on 'copilot/fix-total' names #77, not #1", no["reason"]!.ToString());
        Assert.True(draft.Draft); Assert.Equal(1, w.Refusals[1]);
        draft.Body = "merges into #" + onItem.Number;   // a pull request it names is no other item
        var ok = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" });
        Assert.Equal(draft.Number, (int)ok["pullRequest"]!["number"]!);
    }

    [Fact]
    public async Task OpenPr_ForACopilotBranch_RetargetsAStackedDraft_OrClosesItWhenGitHubRefuses()
    {
        var w = AtDevelopmentItem();
        var draft = Draft(w, "copilot/fix-total", baseRef: "factory-worker/1-old");
        var a = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" });
        Assert.False((bool)a["created"]!); Assert.Equal("master", draft.Base);
        Assert.Contains($"PATCH /repos/{{repo}}/pulls/{{#{draft.Number}}} 200 {{\"base\":\"master\"}}", w.Writes);
        // GitHub does not retarget a draft that is part of a stack: it is closed with a comment, its branch kept, and the item's pull request is opened on the branch itself
        var w2 = AtDevelopmentItem(); w2.RefuseRetarget = true;
        var stacked = Draft(w2, "copilot/fix-total", baseRef: "factory-worker/1-old");
        var b = await w2.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" });
        Assert.True((bool)b["created"]!); Assert.False(stacked.Open); Assert.Equal(2, w2.Pulls.Count);
        Assert.Contains(w2.Writes, l => l.Contains($"/issues/{{#{stacked.Number}}}/comments 201") && l.Contains("GitHub does not retarget this stacked draft to master; the pull request of #1 is opened on the same branch instead."));
        Assert.DoesNotContain(w2.Writes, l => l.StartsWith("DELETE"));
        Assert.Equal(w2.Pulls[1].Number, (int)b["pullRequest"]!["number"]!); Assert.Equal("master", w2.Pulls[1].Base);
    }

    [Fact]
    public async Task OpenPr_ForACopilotBranch_ASecondBranchMustHoldTheFirstOnesCommits_ThenSupersedesIt()
    {
        var w = AtDevelopmentItem();
        var old = w.AddPull(1, "factory-worker/1-old", "src/Old.cs");
        var draft = Draft(w, "copilot/fix-total");
        var no = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" }, 10);
        Assert.Equal("not-on-item-branch", no["code"]!.ToString());
        Assert.Equal($"#1 already has its pull request #{old.Number} on 'factory-worker/1-old'; 'copilot/fix-total' does not hold its commits. Work on a branch made from 'factory-worker/1-old' (the task's own branch is) and pass that.", no["reason"]!.ToString());
        Assert.True(old.Open); Assert.True(draft.Draft);
        w.Compares[$"{old.Sha}...copilot/fix-total"] = "diverged";
        Assert.Equal("not-on-item-branch", (await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" }, 10))["code"]!.ToString());
        w.Compares[$"{old.Sha}...copilot/fix-total"] = "ahead";
        w.Writes.Clear();
        var a = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" });
        Assert.False((bool)a["created"]!); Assert.Equal(draft.Number, (int)a["pullRequest"]!["number"]!);
        Assert.False(old.Open);
        var closing = w.Writes.Where(l => l.Contains($"{{#{old.Number}}}")).ToList();
        Assert.Equal($"Superseded by #{draft.Number}, the pull request of #1 now: it holds this branch's commits and the fix. Closed by the factory worker gateway.", JsonNode.Parse(closing[0][(closing[0].IndexOf("201 ", StringComparison.Ordinal) + 4)..])!["body"]!.ToString());
        Assert.Contains($"PATCH /repos/{{repo}}/pulls/{{#{old.Number}}} 200 {{\"state\":\"closed\"}}", closing);
        Assert.Contains("DELETE /repos/{repo}/git/refs/heads/factory-worker/1-old 204 -", w.Writes);
    }

    [Fact]
    public async Task OpenPr_ForACopilotBranch_WithNoDraft_OpensThePullRequestOnItAndSupersedesThePreviousOne()
    {
        var w = AtDevelopmentItem();
        var old = w.AddPull(1, "factory-worker/1-old", "src/Old.cs");
        w.Compares[$"{old.Sha}...copilot/fix-total"] = "identical";
        var a = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total", body = "the fix" });
        Assert.False((bool)a["created"]!); Assert.False(old.Open);   // (the script answers created: false once the pull request is the item's own, whoever opened it)
        var made = w.Pulls.Single(p => p.Head == "copilot/fix-total");
        Assert.Equal("Fix the total (#1)", made.Title); Assert.Equal("the fix\n\nRefs #1", made.Body); Assert.Equal("master", made.Base); Assert.Equal(made.Number, (int)a["pullRequest"]!["number"]!);
        Assert.Equal(made.Number, (int)w.State(1)["pr"]!["number"]!); Assert.Equal("copilot/fix-total", w.State(1)["pr"]!["branch"]!.ToString());
        Assert.Contains("DELETE /repos/{repo}/git/refs/heads/factory-worker/1-old 204 -", w.Writes);
    }

    [Fact]
    public async Task OpenPr_ForACopilotBranch_ClosesTheStrayDraftsATaskOpenedAgainstTheItemsBranches()
    {
        var w = AtDevelopmentItem();
        var draft = Draft(w, "copilot/fix-total");
        var stray = Draft(w, "copilot/stray", baseRef: "copilot/fix-total");
        var other = Draft(w, "copilot/else", baseRef: "someone/else");
        var a = await w.Call("open-pr", new { item = 1, head = "copilot/fix-total", title = "Fix the total" });
        Assert.Equal(draft.Number, (int)a["pullRequest"]!["number"]!);
        Assert.False(stray.Open); Assert.True(other.Open);
        Assert.Contains(w.Writes, l => l.Contains($"/issues/{{#{stray.Number}}}/comments 201") && l.Contains("an extra draft of a task of #1;"));
        Assert.Contains("DELETE /repos/{repo}/git/refs/heads/copilot/stray 204 -", w.Writes);
    }

    // ---- item-pr for an owner task -------------------------------------------------------------------------------------------------------------------------

    static void OwnerTask(ItemWorld w, string branch, int pr = 0, int recorded = 0, string? doneAt = null)
    {
        var s = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["copilot_task"] = new JsonObject { ["branch"] = branch, ["pr"] = pr } };
        if (doneAt is not null) s["copilot_task"]!["done_at"] = doneAt;
        if (recorded > 0) s["pr"] = new JsonObject { ["number"] = recorded, ["branch"] = "copilot/old", ["url"] = "u" };
        w.States[1] = s; w.StateComments[1] = 4;
    }

    [Fact]
    public async Task ItemPr_OfAnOwnerTask_IsTheOpenPullRequestOfTheTasksBranch_UnderTheItemLock()
    {
        var w = AtDevelopmentItem();
        var pr = w.AddPull(1, "copilot/x", "src/App.cs");
        OwnerTask(w, "copilot/x");
        var a = await w.Call("item-pr", new { item = 1 });
        Assert.True((bool)a["found"]!); Assert.Equal(pr.Number, (int)a["number"]!); Assert.Equal("copilot/x", a["branch"]!.ToString());
        Assert.Equal(["lock 1", "unlock 1"], w.Log);
        // none recorded yet (a Technical Design draft) stays unrecorded; the task's own pull request number is noted
        Assert.Null(w.State(1)["pr"]); Assert.Equal(pr.Number, (int)w.State(1)["copilot_task"]!["pr"]!);
    }

    [Fact]
    public async Task ItemPr_OfAnOwnerTask_ReplacesARecordedPullRequestThatWasClosedOrSuperseded()
    {
        var w = AtDevelopmentItem();
        var pr = w.AddPull(1, "copilot/x", "src/App.cs"); pr.Draft = true; pr.Title = "Work in progress";
        w.States.Clear();
        OwnerTask(w, "copilot/x", recorded: 90);
        var a = await w.Call("item-pr", new { item = 1 });
        Assert.Equal(pr.Number, (int)a["number"]!);
        var s = w.State(1);
        Assert.Equal(pr.Number, (int)s["pr"]!["number"]!); Assert.Equal("copilot/x", s["pr"]!["branch"]!.ToString()); Assert.Equal($"https://github.com/o/r/pull/{pr.Number}", s["pr"]!["url"]!.ToString()); Assert.Equal(pr.Number, (int)s["copilot_task"]!["pr"]!);
        Assert.Equal("Work in progress (#1)", pr.Title); Assert.False(pr.Draft);   // a recorded owner pull request names the item and is never left a draft
        Assert.Contains($"The item's pull request is now #{pr.Number}, the open pull request of the owner task's branch `copilot/x` (the recorded #90 is closed or superseded).", w.Issues[1].Comments.Last().Body);
    }

    [Fact]
    public async Task ItemPr_OfAnOwnerTask_FollowsAStackedSuccessorAndATaskBranchNewerThanTheOne()
    {
        var w = AtDevelopmentItem();
        var first = w.AddPull(1, "copilot/x", "src/App.cs");
        var successor = w.AddPull(1, "copilot/y", "src/More.cs"); successor.Base = "copilot/x";
        OwnerTask(w, "copilot/x");
        w.Compares[$"{first.Sha}...{successor.Sha}"] = "ahead";
        var a = await w.Call("item-pr", new { item = 1 });
        // the owner's work goes on on the successor, which is retargeted to the default branch, and the first pull request is superseded
        Assert.Equal(successor.Number, (int)a["number"]!); Assert.Equal("copilot/y", a["branch"]!.ToString());
        Assert.Equal("copilot/y", w.State(1)["copilot_task"]!["branch"]!.ToString());
        Assert.Equal("master", successor.Base);
    }

    [Fact]
    public async Task ItemPr_OfAnOwnerTask_ADraftStackedOnAnOpenPullRequest_IsConsolidatedOnlyWhenItHoldsItsCommits()
    {
        var w = AtDevelopmentItem();
        var basePr = w.AddPull(1, "copilot/base", "src/A.cs");
        var stacked = w.AddPull(1, "copilot/x", "src/B.cs"); stacked.Base = "copilot/base";
        OwnerTask(w, "copilot/x");
        w.Writes.Clear();
        var refused = await w.Call("item-pr", new { item = 1 });
        Assert.Equal(stacked.Number, (int)refused["number"]!);
        Assert.Equal("copilot/base", stacked.Base); Assert.True(basePr.Open);   // the owner's branch does not hold the base's commits: nothing is written
        Assert.DoesNotContain(w.Writes, l => l.Contains("/pulls/"));
        w.Compares[$"{basePr.Sha}...{stacked.Sha}"] = "ahead";
        var ok = await w.Call("item-pr", new { item = 1 });
        Assert.Equal(stacked.Number, (int)ok["number"]!); Assert.Equal("master", stacked.Base); Assert.False(basePr.Open);
        // (the script names the base after the retarget, from the pull request it just read again: `master`)
        Assert.Contains($"PR #{stacked.Number}, the pull request of the owner task's branch `copilot/x`, was stacked on `master` (#{basePr.Number}); the gateway retargeted it to **master**, where CI runs.", w.Issues[1].Comments.Select(c => c.Body).Last(b => b.Contains("retargeted")));
        Assert.Contains(w.Writes, l => l.Contains($"{{#{basePr.Number}}}") && l.Contains("Superseded by #" + stacked.Number));
        Assert.Contains("DELETE /repos/{repo}/git/refs/heads/copilot/base 204 -", w.Writes);
    }

    [Fact]
    public async Task ItemPr_OfAnOwnerTask_WhoseTaskIsDone_OrWhoseLockIsBusy_IsThePlainLookup()
    {
        var w = AtDevelopmentItem();
        var pr = w.AddPull(1, "copilot/x", "src/App.cs");
        OwnerTask(w, "copilot/x", doneAt: "2026-10-29T10:00:00.000Z");
        // done: the task owns nothing; copilot/x is no branch of the item (nothing records it)
        Assert.False((bool)(await w.Call("item-pr", new { item = 1 }))["found"]!);
        // busy: what the state records
        w.States.Clear();
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["copilot_task"] = new JsonObject { ["branch"] = "copilot/x" }, ["pr"] = new JsonObject { ["number"] = pr.Number, ["branch"] = "copilot/x" } }; w.StateComments[1] = 4;
        w.Busy = 1; w.Log.Clear();
        var busy = await w.Call("item-pr", new { item = 1 });
        Assert.True((bool)busy["found"]!); Assert.Equal(pr.Number, (int)busy["number"]!);
        Assert.Empty(w.Log);
        // no owner branch with an open pull request: not found
        w.Busy = null;
        pr.Open = false;
        Assert.False((bool)(await w.Call("item-pr", new { item = 1 }))["found"]!);
    }
}
