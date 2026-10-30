using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.3 (#355): `base-watch` and `base-update-branch` on v2, against the rule script BaseUpdate.ps1: the same fields, outcomes and skip reasons, with the saga's state where the script reads the state comment. GitHub is a
/// fake that remembers what it was asked.
/// </summary>
public class BaseUpdateVerbTests
{
    sealed class GitHub : IBaseUpdateGitHub
    {
        public string Head = "m2";
        public IReadOnlyList<string>? Changed = ["docs/a.md", "src/b.cs"];
        public List<BasePull> Open = [];
        public Dictionary<int, BasePull> Pulls = [];
        public Dictionary<int, IReadOnlyList<string>> Files = [];
        public Dictionary<string, int> Behind = [];
        public int UpdateStatus = 202;
        public readonly List<string> Calls = [];

        public Task<string> BaseHead(CancellationToken ct) { Calls.Add("head"); return Task.FromResult(Head); }
        public Task<IReadOnlyList<string>?> ChangedFiles(string from, string to, CancellationToken ct) { Calls.Add($"compare {from}...{to}"); return Task.FromResult(Changed); }
        public Task<IReadOnlyList<BasePull>> OpenPullRequests(CancellationToken ct) { Calls.Add("open"); return Task.FromResult<IReadOnlyList<BasePull>>(Open); }
        public Task<BasePull> PullRequest(int number, CancellationToken ct)
        {
            Calls.Add($"pr {number}");
            var p = Pulls.TryGetValue(number, out var known) ? known : Open.Single(x => x.Number == number);
            return Task.FromResult(p);
        }
        public Task<IReadOnlyList<string>> PullRequestFiles(int number, CancellationToken ct) => Task.FromResult(Files.GetValueOrDefault(number) ?? []);
        public Task<int> BehindBy(string branch, CancellationToken ct) { Calls.Add($"behind {branch}"); return Task.FromResult(Behind.GetValueOrDefault(branch, 0)); }
        public Task<int> UpdateBranch(int pullRequest, string expectedHead, CancellationToken ct) { Calls.Add($"update {pullRequest} {expectedHead}"); return Task.FromResult(UpdateStatus); }
    }

    sealed class Items : IBaseUpdateItems
    {
        public Dictionary<int, BaseItemFacts> Facts = [];
        public Dictionary<int, string> Qc = [];
        public Task<BaseItemFacts?> FactsOf(int item, CancellationToken ct) => Task.FromResult(Facts.TryGetValue(item, out var f) ? f : null);
        public Task<string?> QcRecord(int pullRequest, CancellationToken ct) => Task.FromResult(Qc.TryGetValue(pullRequest, out var q) ? q : null);
    }

    static readonly BaseItemFacts Quiet = new(false, false, false, false, 0, "");
    static BasePull Pr(int n, string branch, string sha = "h1", bool? mergeable = true, string state = "open") => new(n, state, branch, sha, mergeable, mergeable == true ? "clean" : mergeable == false ? "dirty" : "unknown");

    static (BaseUpdateService Service, GitHub Gh, Items Items, List<TimeSpan> Pauses) Make()
    {
        var gh = new GitHub(); var items = new Items(); var pauses = new List<TimeSpan>();
        return (new BaseUpdateService(gh, items, new BackmergeSettings("factory-worker"), t => { pauses.Add(t); return Task.CompletedTask; }), gh, items, pauses);
    }

    static JsonObject J(VerbAnswer a) => (JsonObject)JsonNode.Parse(a.Output)!;
    static JsonObject B(string json) => (JsonObject)JsonNode.Parse(json)!;
    static ISet<int> Only(params int[] items) => items.ToHashSet();

    // ---- base-watch -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHeadAlone_ForTheFirstLook_HasNoRowsAndNoChange()
    {
        var (svc, gh, _, _) = Make();
        var a = await svc.Watch(B("{\"known\":\"\",\"items\":[]}"), Only(), CancellationToken.None);
        var o = J(a);
        Assert.Equal((0, "m2", "", false, 0, 0, true, "base-watch"), (a.Code, o["head"]!.ToString(), o["known"]!.ToString(), o["changed"]!.GetValue<bool>(), o["baseFiles"]!.GetValue<int>(), o["rows"]!.AsArray().Count, o["ok"]!.GetValue<bool>(), o["verb"]!.ToString()));
        Assert.Equal(["head"], gh.Calls);   // (no pull requests read for no items)
    }

    [Fact]
    public async Task WhenMasterMoved_TheFilesItChangedAreCountedAndTheOverlapOfAPullRequestIsListed()
    {
        var (svc, gh, items, _) = Make();
        gh.Open = [Pr(11, "factory-worker/7-docs", "h7")];
        gh.Files[11] = ["src/b.cs", "docs/own.md"];
        gh.Behind["factory-worker/7-docs"] = 3;
        items.Facts[7] = Quiet with { PullRequest = 11 };
        var o = J(await svc.Watch(B("{\"known\":\"m1\",\"items\":[7],\"mergedPrs\":[]}"), Only(7), CancellationToken.None));
        Assert.Equal((true, 2), (o["changed"]!.GetValue<bool>(), o["baseFiles"]!.GetValue<int>()));
        var row = o["rows"]![0]!;
        Assert.Equal((7, 11, "factory-worker/7-docs", "h7", 3, ""), (row["item"]!.GetValue<int>(), row["pr"]!.GetValue<int>(), row["branch"]!.ToString(), row["head"]!.ToString(), row["behind"]!.GetValue<int>(), row["skip"]!.ToString()));
        Assert.Equal(["src/b.cs"], row["overlap"]!.AsArray().Select(x => x!.ToString()));
        Assert.Contains("compare m1...m2", gh.Calls);
        Assert.Null(row["conflict"]);   // (the conflict modes are off unless the call asks)
    }

    [Theory]
    [InlineData(true, false, "pass-unmerged")]   // a PASS waiting for its merge: the gateway owns the next step
    [InlineData(false, true, "merged")]          // a merge being settled
    public async Task APassWaitingForItsMerge_OrAMergeBeingSettled_IsSkipped(bool pass, bool merged, string skip)
    {
        var (svc, gh, items, _) = Make();
        gh.Open = [Pr(11, "factory-worker/7-docs")];
        items.Facts[7] = new BaseItemFacts(true, pass, merged, true, 11, "");
        var row = J(await svc.Watch(B("{\"items\":[7]}"), Only(7), CancellationToken.None))["rows"]![0]!;
        Assert.Equal(skip, row["skip"]!.ToString());
        Assert.Equal(0, row["behind"]!.GetValue<int>());
        Assert.DoesNotContain("behind factory-worker/7-docs", gh.Calls);   // (a skipped item costs no compare)
    }

    [Fact]
    public async Task APullRequestTheWebhookSawMerged_ThePullRequestThatIsGone_AndAnItemWithoutABranch_AreSkipped()
    {
        var (svc, gh, items, _) = Make();
        gh.Open = [Pr(11, "factory-worker/7-x")];
        items.Facts[7] = Quiet with { PullRequest = 11 };
        items.Facts[8] = Quiet with { PullRequest = 99 };   // its pull request is not open any more
        items.Facts[9] = Quiet;                             // nothing pushed yet
        var rows = J(await svc.Watch(B("{\"items\":[7,8,9],\"mergedPrs\":[11]}"), Only(7, 8, 9), CancellationToken.None))["rows"]!.AsArray();
        Assert.Equal(["merged", "pr-closed", "no-branch"], rows.Select(r => r!["skip"]!.ToString()));
        Assert.Equal(11, rows[0]!["pr"]!.GetValue<int>());
    }

    [Fact]
    public async Task ACopilotBranch_BelongsToTheItemThatRecordedItsPullRequest()
    {
        var (svc, gh, items, _) = Make();
        gh.Open = [Pr(21, "copilot/fix-it"), Pr(22, "copilot/other")];
        items.Facts[7] = Quiet with { PullRequest = 21 };
        var row = J(await svc.Watch(B("{\"items\":[7]}"), Only(7), CancellationToken.None))["rows"]![0]!;
        Assert.Equal((21, "copilot/fix-it"), (row["pr"]!.GetValue<int>(), row["branch"]!.ToString()));
    }

    [Theory]
    [InlineData(true, false, false, false, "", "verdict-recorded")]      // a verdict on record (even a merged one)
    [InlineData(false, false, false, true, "", "in-validation")]         // in Functional Testing
    [InlineData(false, false, false, false, "{\"head\":\"H1\"}", "qc-for-head")]   // a QC run for this head (case does not matter)
    [InlineData(false, false, false, false, "{\"head\":\"other\"}", "")]
    public async Task TheBackmergeHold_IsTheScriptsInItsOrder(bool verdict, bool pass, bool merged, bool validation, string qc, string skip)
    {
        var (svc, gh, items, _) = Make();
        gh.Open = [Pr(11, "factory-worker/7-x", "h1")];
        items.Facts[7] = new BaseItemFacts(verdict, pass, merged, validation, 11, "");
        if (qc != "") items.Qc[11] = qc;
        var row = J(await svc.Watch(B("{\"items\":[7]}"), Only(7), CancellationToken.None))["rows"]![0]!;
        Assert.Equal(skip, row["skip"]!.ToString());
    }

    [Fact]
    public async Task TheConflictModes_ReadEachPullRequestOnce_AndReuseAnAnswerForTheSameHeads()
    {
        var (svc, gh, items, pauses) = Make();
        gh.Open = [Pr(11, "factory-worker/7-x", "h7"), Pr(12, "factory-worker/8-x", "h8"), Pr(13, "factory-worker/9-x", "h9")];
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h7", mergeable: false);
        gh.Pulls[12] = Pr(12, "factory-worker/8-x", "h8", mergeable: true);
        gh.Pulls[13] = Pr(13, "factory-worker/9-x", "h9", mergeable: null);
        foreach (var n in new[] { 7, 8, 9 }) items.Facts[n] = Quiet with { PullRequest = 10 + n - 6 };
        var known = "{\"8\":{\"prHead\":\"h8\",\"head\":\"m2\",\"conflict\":false}}";
        var rows = J(await svc.Watch(B("{\"known\":\"m2\",\"items\":[7,8,9],\"conflicts\":true,\"conflictKnown\":" + known + "}"), Only(7, 8, 9), CancellationToken.None))["rows"]!.AsArray();
        Assert.True(rows[0]!["conflict"]!.GetValue<bool>());
        Assert.False(rows[1]!["conflict"]!.GetValue<bool>());
        Assert.Null(rows[2]!["conflict"]);                  // (GitHub had not computed it: null, the next look has it)
        Assert.Equal(1, gh.Calls.Count(c => c == "pr 11"));
        Assert.DoesNotContain("pr 12", gh.Calls);          // (known for these heads: not read again)
        Assert.Equal(2, gh.Calls.Count(c => c == "pr 13"));   // (null once: one more read after the pause)
        Assert.Equal([TimeSpan.FromSeconds(3)], pauses);
    }

    [Fact]
    public async Task OnlyTheItemsItIsToldAreAnsweredHere_AndAnItemTheSagaLostIsAnError()
    {
        var (svc, gh, items, _) = Make();
        gh.Open = [Pr(11, "factory-worker/7-x")];
        items.Facts[7] = Quiet with { PullRequest = 11 };
        var rows = J(await svc.Watch(B("{\"items\":[7,8]}"), Only(7), CancellationToken.None))["rows"]!.AsArray();
        Assert.Equal([7], rows.Select(r => r!["item"]!.GetValue<int>()));
        rows = J(await svc.Watch(B("{\"items\":[7,8]}"), Only(7, 8), CancellationToken.None))["rows"]!.AsArray();
        Assert.Equal(("error", "item-not-found"), (rows[1]!["skip"]!.ToString(), rows[1]!["code"]!.ToString()));
    }

    // ---- base-update-branch -----------------------------------------------------------------------------------------------------------------

    static async Task<(int Code, JsonObject Body)> Update(BaseUpdateService svc, string json)
    {
        var a = await svc.UpdateBranch(B(json), CancellationToken.None);
        Assert.NotNull(a);
        return (a!.Value.Code, J(a.Value));
    }

    const string Call = "{\"item\":7,\"pr\":11,\"expected\":\"h1\"}";

    [Fact]
    public async Task AnItemTheSagaDoesNotKnow_IsTheScripts_AndTheArgumentsAreChecked()
    {
        var (svc, gh, _, _) = Make();
        Assert.Null(await svc.UpdateBranch(B(Call), CancellationToken.None));
        Assert.Empty(gh.Calls);
        var (code, body) = await Update(svc, "{\"item\":7,\"pr\":11}");
        Assert.Equal((2, "missing argument: expected"), (code, body["reason"]!.ToString()));
    }

    [Fact]
    public async Task APullRequestThatIsClosedOrMoved_IsLeftAlone()
    {
        var (svc, gh, items, _) = Make();
        items.Facts[7] = Quiet;
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h1", state: "closed");
        Assert.Equal("not-open", (await Update(svc, Call)).Body["outcome"]!.ToString());
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h2");
        Assert.Equal("head-moved", (await Update(svc, Call)).Body["outcome"]!.ToString());
    }

    [Fact]
    public async Task AVerdictOrAMergeOrAQcRunStarted_SinceTheTrigger_SkipsTheUpdate()
    {
        var (svc, gh, items, _) = Make();
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h1");
        items.Facts[7] = Quiet with { VerdictRecorded = true, PassUnmerged = true };
        var (code, body) = await Update(svc, Call);
        Assert.Equal((0, "skip", "verdict-recorded"), (code, body["outcome"]!.ToString(), body["reason"]!.ToString()));
        Assert.Equal((7, 11), (body["item"]!.GetValue<int>(), body["pr"]!.GetValue<int>()));
        items.Facts[7] = Quiet; items.Qc[11] = "{\"sha\":\"h1\"}";
        Assert.Equal("qc-for-head", (await Update(svc, Call)).Body["reason"]!.ToString());
        Assert.DoesNotContain(gh.Calls, c => c.StartsWith("update"));
    }

    [Fact]
    public async Task AConflict_AnUnknownMergeability_AndABranchThatIsCurrent_AreOutcomes()
    {
        var (svc, gh, items, pauses) = Make();
        items.Facts[7] = Quiet;
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h1", mergeable: false);
        Assert.Equal("conflict", (await Update(svc, Call)).Body["outcome"]!.ToString());
        // GitHub computes it after a push: null is read again (five times, 2 seconds apart), then it is unknown
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h1", mergeable: null);
        Assert.Equal("unknown", (await Update(svc, Call)).Body["outcome"]!.ToString());
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(2), 5), pauses);
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h1", mergeable: true);
        Assert.Equal("current", (await Update(svc, Call)).Body["outcome"]!.ToString());
        Assert.DoesNotContain(gh.Calls, c => c.StartsWith("update"));
    }

    [Fact]
    public async Task ABranchThatIsBehind_IsUpdatedWithTheHeadTheTriggerSaw_OrGitHubRefusesIt()
    {
        var (svc, gh, items, _) = Make();
        items.Facts[7] = Quiet;
        gh.Pulls[11] = Pr(11, "factory-worker/7-x", "h1");
        gh.Behind["factory-worker/7-x"] = 4;
        var (code, body) = await Update(svc, Call);
        Assert.Equal((0, "updated", 4), (code, body["outcome"]!.ToString(), body["behind"]!.GetValue<int>()));
        Assert.Contains("update 11 h1", gh.Calls);
        gh.UpdateStatus = 409;
        body = (await Update(svc, Call)).Body;
        Assert.Equal(("refused", 409), (body["outcome"]!.ToString(), body["status"]!.GetValue<int>()));
        gh.UpdateStatus = 422;
        Assert.Equal(422, (await Update(svc, Call)).Body["status"]!.GetValue<int>());
    }

    [Fact]
    public void TheFactsOfTheSaga_AreWhatTheScriptReadsFromTheStateComment()
    {
        var open = new WorkItemState { Step = StepKind.Merge, VerdictHead = "h", PullRequest = 11 };
        Assert.Equal(new BaseItemFacts(true, true, false, true, 11, ""), BaseItemFacts.From(open));
        var merged = new WorkItemState { Step = StepKind.Deliver, VerdictHead = "h", MergeCommit = "c", PullRequest = 11 };
        Assert.Equal(new BaseItemFacts(true, false, true, false, 11, ""), BaseItemFacts.From(merged));
        var developing = new WorkItemState { Step = StepKind.Develop, PullRequest = 11 };
        Assert.Equal(new BaseItemFacts(false, false, false, false, 11, ""), BaseItemFacts.From(developing));
        Assert.Equal(new BaseItemFacts(true, false, false, false, 0, ""), BaseItemFacts.From(new WorkItemState { Status = WorkItemStatus.Completed, Step = StepKind.Merge, MergeCommit = "c" }));
    }
}
