using System.Diagnostics;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10i, the rollback path as the plan's row asks for it: an item mid-flow on v2, the kill switch on (v1 acts again: its rule script sweeps the board), and v1 makes no duplicate or stale write. The saga's decisions are
/// projected into the state comment by the real projection over the fake GitHub, and the real rule script (pwsh plugin/scripts/factory.ps1 --server sync-board) reads that comment. Each case has its control: the same item as
/// it stood before the projection existed (the comment v1's own verbs had left), which v1 acts on with exactly the writes this projection keeps it from making. Where pwsh or the fake is not built the cases return at once (the
/// CI shards that run these tests have both).
/// </summary>
public sealed class StateProjectionRollbackTests : IDisposable
{
    static readonly RepoId Home = new(FakeGitHubRig.Owner, FakeGitHubRig.Repo);
    static readonly string[] Labels = ["factory worker", "Auto Merge"];
    readonly FakeGitHubRig? rig = FakeGitHubRig.Start();
    readonly string? pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, "pwsh")).FirstOrDefault(File.Exists);
    readonly string dir = Directory.CreateTempSubdirectory("state-projection-rollback-").FullName;
    bool Available => rig is not null && pwsh is not null;

    public void Dispose()
    {
        rig?.Dispose();
        try { Directory.Delete(dir, true); } catch { }
    }

    // ---- the two sides: the saga's projection, and v1's sweep ---------------------------------------------------------------------------------------------

    /// <summary>The projection of one decision of the saga, as the gateway makes it: the state comment of the item over the fake, the saga's state in its store.</summary>
    async Task Project(int item, WorkItemState saga, string action, DateTimeOffset at)
    {
        var http = rig!.Http;
        var client = await new GitHubClientFactory(http, new StaticCredentialSource([], new GitHubCredential("t", "test-token")), new Fworker.Integrations.GitHub.GitHubBudget()).For(Home);
        var versions = new InMemoryProjectionVersions();
        var comment = new StateCommentProjection(new GitHubJournalStore(client, new JournalIdentity("factory-worker", _ => Task.FromResult("factory-bot"), [])), versions, new ProjectionEcho(versions));
        var store = new InMemoryWorkItemStore();
        saga.Item = new ItemId(Home, item).ToString();
        await store.Save(saga.Item, saga, 0, default);
        var projection = new SagaStateProjection(() => comment, store, StateProjectionTests.Plan, StateProjectionTests.Settings);
        var decision = new WorkItemDecided(new ItemId(Home, item), saga.Generation, saga.Sequence, action, saga.Plan, saga.Status == WorkItemStatus.Completed ? null : saga.Step.ToString(), null, [saga.ParkReason], at);
        await projection.Project(decision, default);
    }

    async Task<List<string>> Log() => JsonNode.Parse(await rig!.Http.GetStringAsync("_fake/log"))!.AsArray().Select(x => x!.ToString()).ToList();

    sealed record Swept(JsonNode? Json, List<string> Calls)
    {
        /// <summary>What v1 wrote about the item or its pull request: everything but reads (the item's issue and its comments, its pull request). The card is read with <see cref="Card"/> (a GraphQL call is a read or a move).</summary>
        public List<string> Writes(int item, int pr) => Calls.Where(c => !c.StartsWith("GET ") && (c.Contains($"/issues/{item}") || c.Contains($"/pulls/{pr}") || c.Contains("/issues/comments/"))).ToList();
    }

    /// <summary>Where the item stands on the board and whether its issue is open.</summary>
    string Card(int item) { var i = rig!.State()["issues"]!.AsArray().First(x => x!["number"]!.GetValue<int>() == item)!; return $"{i["column"]} open={i["open"]}"; }

    async Task<Swept> V1Sweep(params int[] items)
    {
        var before = (await Log()).Count;
        var psi = new ProcessStartInfo(pwsh!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = FakeGitHubRig.Root() };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(FakeGitHubRig.Root(), "plugin", "scripts", "factory.ps1"), "--server", "sync-board" }) psi.ArgumentList.Add(a);
        RunnerEnv.Apply(psi);
        psi.Environment["GH_TOKEN"] = "test-token"; psi.Environment["GITHUB_API_URL"] = $"http://127.0.0.1:{rig!.Port}"; psi.Environment["FACTORY_FAKE_APPS"] = "1";
        psi.Environment["FACTORY_ENVIRONMENT"] = "tdd"; psi.Environment["FACTORY_DATA_DIR"] = Path.Combine(dir, "data");
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(new JsonObject { ["items"] = new JsonArray(items.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["low"] = true }.ToJsonString()); p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(); var output = (await outTask).Trim(); var error = await errTask;
        Assert.True(p.ExitCode == 0, $"sync-board exited {p.ExitCode}: {output} {error}");
        return new Swept(output.Length > 0 ? JsonNode.Parse(output) : null, (await Log()).Skip(before).ToList());
    }

    /// <summary>An item in Functional Testing with an open green pull request and a PASS that v1's own verb recorded a minute and a half ago (the gateway's merge grace is over).</summary>
    (int Item, int Pr, string Head) Passed()
    {
        var item = rig!.NewItem("Rollback trial", Labels, "Functional Testing");
        var (pr, head) = rig.NewPr(item);
        rig.SetState(item, s =>
        {
            s["pr"] = new JsonObject { ["number"] = pr, ["branch"] = $"factory-worker/{item}-development" };
            s["verdict"] = new JsonObject { ["passed"] = true, ["pr"] = pr, ["head_sha"] = head, ["summary"] = "criteria met", ["at"] = JournalJson.Iso(DateTime.UtcNow.AddMinutes(-90)), ["round"] = 0, ["processed"] = false };
        });
        return (item, pr, head);
    }

    static WorkItemState Saga(int item, int pr, string head, Action<WorkItemState> edit)
    {
        var s = new WorkItemState { Item = new ItemId(Home, item).ToString(), Generation = 1, Plan = "feature", Step = StepKind.Merge, PullRequest = pr, Head = head, VerdictHead = head, StartedAt = DateTimeOffset.UtcNow.AddHours(-2), Sequence = 9, ArmId = "1.4" };
        edit(s);
        return s;
    }

    // ---- a parked item stays parked -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnItemTheSagaParked_IsNotMergedOrOfferedByV1_WhileTheSameItemWithoutTheProjectionIsMerged()
    {
        if (!Available) return;
        // the control: what v1's own verbs left. The PASS is unprocessed and the grace is over, so the sweep merges the pull request
        var control = Passed();
        var swept = await V1Sweep(control.Item);
        Assert.Contains(swept.Writes(control.Item, control.Pr), w => w.StartsWith("PUT ") && w.EndsWith($"/pulls/{control.Pr}/merge"));

        // the item: the saga parked it after 60 merge calls without a merge (a fault the saga alone knows of); the projection writes the park and closes the PASS
        var item = Passed();
        await Project(item.Item, Saga(item.Item, item.Pr, item.Head, s => { s.Parked = true; s.ParkReason = "merge-blocked: 60 merge calls without a merge (hold)"; s.ParkUntil = DateTimeOffset.UtcNow.AddHours(24); }), WorkItemActions.Parked, DateTimeOffset.UtcNow);
        var state = rig!.StateComment(item.Item)!;
        Assert.Equal("merge-blocked", state["park"]!["reason"]!.ToString());
        Assert.True((bool)state["verdict"]!["processed"]!);
        var rev = ItemStateComment.RevOf(state); var card = Card(item.Item);

        swept = await V1Sweep(item.Item);
        Assert.Empty(swept.Writes(item.Item, item.Pr));
        Assert.Equal(card, Card(item.Item));
        Assert.False(MentionsItem(swept.Json, "offers", item.Item), "v1 offered the parked item");
        Assert.Equal(rev, ItemStateComment.RevOf(rig.StateComment(item.Item)!));   // and the comment is as the saga left it: no write of v1's on it
        Assert.False(rig.Api("GET", $"pulls/{item.Pr}")!["merged"]!.GetValue<bool>());
    }

    // ---- a merged item is settled by v1, not picked up as a person's merge --------------------------------------------------------------------------------

    int Merged((int Item, int Pr, string Head) x)
    {
        rig!.Api("PUT", $"pulls/{x.Pr}/merge", new { sha = x.Head, merge_method = "squash" });
        return 0;
    }

    [Fact]
    public async Task AnItemTheGatewayMerged_IsLeftAloneByV1_WhileTheSameItemWithoutTheProjectionIsPickedUpAsAPersonsMerge()
    {
        if (!Available) return;
        // the control: the pull request was merged, the card is not moved yet and the comment is v1's before the merge: v1 takes it for a person's merge and writes the record, the comment and the move
        var control = Passed(); Merged(control);
        var swept = await V1Sweep(control.Item);
        Assert.Contains(swept.Writes(control.Item, control.Pr), w => w.StartsWith("POST ") && w.EndsWith($"/issues/{control.Item}/comments"));
        Assert.Equal("person", rig!.StateComment(control.Item)!["merge"]!["by"]!.ToString());

        // the item: the saga merged it (the docs plan ends at the merge) and the projection wrote the merge record, done, and closed the PASS
        var item = Passed(); Merged(item);
        var merge = rig.Api("GET", $"pulls/{item.Pr}")!["merge_commit_sha"]!.ToString();
        await Project(item.Item, Saga(item.Item, item.Pr, item.Head, s => { s.Plan = "docs"; s.Status = WorkItemStatus.Completed; s.MergeCommit = merge; s.CompletedAt = DateTimeOffset.UtcNow; }), WorkItemActions.Merged, DateTimeOffset.UtcNow);
        var state = rig.StateComment(item.Item)!;
        Assert.Equal((merge, true), (state["merge"]!["sha"]!.ToString(), state["merge"]!["done_at"] is not null));
        var rev = ItemStateComment.RevOf(state); var card = Card(item.Item);

        swept = await V1Sweep(item.Item);
        Assert.Empty(swept.Writes(item.Item, item.Pr));
        Assert.Equal(card, Card(item.Item));
        Assert.Equal(rev, ItemStateComment.RevOf(rig.StateComment(item.Item)!));
        Assert.Null(rig.StateComment(item.Item)!["merge"]!["by"]);
    }

    [Fact]
    public async Task AnItemTheGatewayMergedThatAwaitsMastersCi_IsSettledByV1_WithoutBeingPickedUpAgain()
    {
        if (!Available) return;
        var item = Passed(); Merged(item);
        var merge = rig!.Api("GET", $"pulls/{item.Pr}")!["merge_commit_sha"]!.ToString();
        await Project(item.Item, Saga(item.Item, item.Pr, item.Head, s => { s.Step = StepKind.Deliver; s.MergeCommit = merge; s.MasterState = "waiting"; }), WorkItemActions.Merged, DateTimeOffset.UtcNow);
        var state = rig.StateComment(item.Item)!;
        Assert.Equal((merge, "master-green"), (state["merge"]!["sha"]!.ToString(), state["merge"]!["awaiting"]!.ToString()));

        var swept = await V1Sweep(item.Item);
        // v1 goes on with the settle (master's checks are read and, as they are green, the wait moves on): that is its step to take, and the only one. It does not record the merge as a person's, say that it saw the merge,
        // or merge again
        Assert.DoesNotContain(swept.Calls, c => c.StartsWith("PUT ") && c.EndsWith("/merge"));
        Assert.DoesNotContain(swept.Json!["actions"]?.AsArray() ?? new JsonArray(), a => a?["action"]?.ToString() == "merge-detected");
        var after = rig.StateComment(item.Item)!;
        Assert.Equal(merge, after["merge"]!["sha"]!.ToString());
        Assert.Null(after["merge"]!["by"]);
        Assert.DoesNotContain(rig.Api("GET", $"issues/{item.Item}/comments")!.AsArray().Select(c => c), c => c?["body"]?.ToString().Contains("was merged as") == true);
    }

    static bool MentionsItem(JsonNode? sweep, string list, int item) => (sweep?[list] as JsonArray ?? []).Any(o => o?["item"]?.GetValue<int>() == item);
}
