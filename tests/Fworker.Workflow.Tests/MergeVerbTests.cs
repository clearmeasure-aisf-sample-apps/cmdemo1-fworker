using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Fworker.Workflows.WorkItem.Verbs;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.3 (#355): `merge`, `approve-pr` and `clear-hold` on v2. The checks are those of Verbs.Merge.ps1 and Verbs.Inspect.ps1, with their codes, exits and fields, applied to the saga's state; the merge itself is the saga's
/// (its retry timer is delivered at once and its answer reported). The saga here is the real one on an in-memory store; the test plays the part of the merge port.
/// </summary>
public class MergeVerbTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
    const string Commit = "bbb2222222222222222222222222222222222222";
    static readonly RepoId Home = Item.Repository;

    static MergeVerbSettings Settings(string policy = "auto", params string[] holds) => new(policy, "Auto Merge", holds.Length == 0 ? ["hold:owner"] : holds, "Functional Testing",
        step => step switch { StepKind.Design => "Technical Design", StepKind.Develop => "Development", StepKind.Verify => "Functional Testing", _ => null });

    sealed class Labels(params string[] names) : IItemLabels
    {
        public IReadOnlyList<string>? Value = names;
        public Task<IReadOnlyList<string>?> LabelsOf(ItemId item, CancellationToken ct) => Task.FromResult(Value);
    }

    // the item on its Merge step with the PASS head, the merge call out (the saga asked the port)
    static async Task<SagaFixture> AtMerge()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        await fx.Verdict(true, Head);
        Assert.Equal(StepKind.Merge, fx.State.Step);
        return fx;
    }

    // the saga has been told the merge is blocked: its retry timer is set
    static async Task<SagaFixture> Waiting(string reason = "checks-pending")
    {
        var fx = await AtMerge();
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, reason, "", Transient: true));
        Assert.NotNull(fx.State.MergeRetryAt);
        return fx;
    }

    sealed class Harness
    {
        public readonly SagaFixture Fx;
        public readonly List<MergeRetryDue> Published = [];
        public Func<Task>? OnPause;
        public bool Accepts = true;
        public int Pauses;
        public readonly MergeVerbService Service;

        public Harness(SagaFixture fx, Labels? labels = null, string policy = "auto", int looks = 3, IMergeMigrations? migrations = null, IItemRefusals? refusals = null)
        {
            Fx = fx;
            Service = new MergeVerbService(Home, fx.Store, labels ?? new Labels("Auto Merge"), async m => { Published.Add(m); if (Accepts) await fx.Send(m); return Accepts; },
                Settings(policy), new MergeVerbWait(TimeSpan.Zero, looks), async _ => { Pauses++; if (OnPause is not null) await OnPause(); }, () => T0, migrations, refusals);
        }

        public async Task<(int Code, JsonObject Body)> Merge(string json)
        {
            var a = await Service.Merge((JsonObject)JsonNode.Parse(json)!, CancellationToken.None);
            Assert.NotNull(a);
            return (a!.Value.Code, (JsonObject)JsonNode.Parse(a.Value.Output)!);
        }
    }

    static string Item7 => "{\"item\":7}";

    sealed class Migrations(MigrationCollision? answer) : IMergeMigrations
    {
        public readonly List<int> Asked = [];
        public Task<MigrationCollision?> CollisionOf(int pullRequest, CancellationToken ct) { Asked.Add(pullRequest); return Task.FromResult(answer); }
    }

    sealed class Refusals : IItemRefusals
    {
        public readonly List<int> Added = [];
        public bool Fails;
        public Task Add(int item, CancellationToken ct) { if (Fails) throw new IOException("the state directory is read only"); Added.Add(item); return Task.CompletedTask; }
        public Task Reset(int item, CancellationToken ct) => Task.CompletedTask;
        public Task<int> Count(int item, CancellationToken ct) => Task.FromResult(Added.Count(x => x == item));
    }

    // ---- W9.10q: the migration number collision and the model ladder's count of refusals ---------------------------------------------------------

    [Fact]
    public async Task ADbUpNumberAnotherPullRequestTook_IsRefusedAsTheScriptDoes_AndTheSagaIsNotAsked()
    {
        var fx = await AtMerge();
        var pr = fx.State.PullRequest;
        var migrations = new Migrations(new MigrationCollision("src/Database/scripts/Update/040_AddOrders.sql", "src/Database/scripts/Update/040_TakenFirst.sql", "041"));
        var h = new Harness(fx, migrations: migrations);
        var (code, body) = await h.Merge(Item7);
        Assert.Equal((10, "migration-number-taken", false), (code, body["code"]!.ToString(), body["ok"]!.GetValue<bool>()));
        Assert.Equal($"merge refused: PR #{pr} adds src/Database/scripts/Update/040_AddOrders.sql, but src/Database/scripts/Update/040_TakenFirst.sql on master already has that number; the branch's script must be renumbered to 041 (never the one on master: DbUp runs a renamed script again)", body["reason"]!.ToString());
        Assert.Equal((pr, "src/Database/scripts/Update/040_AddOrders.sql", "src/Database/scripts/Update/040_TakenFirst.sql", "041", "merge", 10),
            (body["prNumber"]!.GetValue<int>(), body["script"]!.ToString(), body["taken_by"]!.ToString(), body["next"]!.ToString(), body["verb"]!.ToString(), body["exit"]!.GetValue<int>()));
        Assert.Equal([pr], migrations.Asked); Assert.Empty(h.Published); Assert.Equal(StepKind.Merge, fx.State.Step);
        // no collision: the saga is asked as before
        var free = new Migrations(null);
        var (code2, body2) = await new Harness(await Waiting(), migrations: free).Merge(Item7);
        Assert.Equal(0, code2); Assert.Equal("waiting", body2["outcome"]!.ToString()); Assert.Single(free.Asked);
    }

    [Fact]
    public async Task ARefusal10_OfMergeOrApprovePr_IsOneDataPointOfTheModelLadder()
    {
        var refusals = new Refusals();
        var h = new Harness(await AtMerge(), policy: "auto", refusals: refusals);
        var refused = new VerbAnswer(10, "{\"ok\":false}");
        await h.Service.CountRefusal((JsonObject)JsonNode.Parse(Item7)!, refused);
        await h.Service.CountRefusal((JsonObject)JsonNode.Parse("{\"item\":\"7\"}")!, refused);   // the item as text counts as the script reads it
        Assert.Equal([7, 7], refusals.Added);
        // only a refusal 10 on an item: a wait (21), a fault (20), a usage error (2), a success and a call without an item are none
        foreach (var code in new[] { 0, 2, 11, 20, 21, 30 }) await h.Service.CountRefusal((JsonObject)JsonNode.Parse(Item7)!, new VerbAnswer(code, "{}"));
        await h.Service.CountRefusal((JsonObject)JsonNode.Parse("{}")!, refused);
        Assert.Equal([7, 7], refusals.Added);
        // approve-pr under auto is a refusal 10 ("not-required"), counted with the answer it gave
        var a = h.Service.ApprovePr((JsonObject)JsonNode.Parse("{\"item\":7,\"text\":\"fine\"}")!)!.Value;
        Assert.Equal(10, a.Code);
        await h.Service.CountRefusal((JsonObject)JsonNode.Parse("{\"item\":7,\"text\":\"fine\"}")!, a);
        Assert.Equal([7, 7, 7], refusals.Added);
        // a file of the state directory that cannot be written is not the verb's fault
        refusals.Fails = true;
        await h.Service.CountRefusal((JsonObject)JsonNode.Parse(Item7)!, refused);
        // and a service made without a counter counts nothing
        await new Harness(new SagaFixture()).Service.CountRefusal((JsonObject)JsonNode.Parse(Item7)!, refused);
    }

    // ---- the script's checks, in its order --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnItemTheSagaDoesNotKnow_IsTheScripts()
    {
        var h = new Harness(new SagaFixture());
        Assert.Null(await h.Service.Merge((JsonObject)JsonNode.Parse(Item7)!, CancellationToken.None));
        Assert.Null(await h.Service.ClearHold((JsonObject)JsonNode.Parse(Item7)!, CancellationToken.None));
    }

    [Fact]
    public async Task MergePolicyPeerApproved_IsTheScripts_AnItemTheSagaOwnsIncluded()
    {
        var h = new Harness(await AtMerge(), policy: "peer-approved");
        Assert.Null(await h.Service.Merge((JsonObject)JsonNode.Parse(Item7)!, CancellationToken.None));
        Assert.Null(await h.Service.ClearHold((JsonObject)JsonNode.Parse(Item7)!, CancellationToken.None));
        Assert.Null(h.Service.ApprovePr((JsonObject)JsonNode.Parse("{\"item\":7,\"text\":\"fine\\nINSPECTOR: APPROVE\"}")!));
        Assert.Empty(h.Published);
    }

    [Fact]
    public async Task TheItemIsRequired_AndMustBeANumber()
    {
        var h = new Harness(new SagaFixture());
        var (code, body) = await h.Merge("{}");
        Assert.Equal(2, code);
        Assert.Equal(("usage", "missing argument: item", false, "merge", 2), (body["code"]!.ToString(), body["reason"]!.ToString(), body["ok"]!.GetValue<bool>(), body["verb"]!.ToString(), body["exit"]!.GetValue<int>()));
        (code, body) = await h.Merge("{\"item\":\"abc\"}");
        Assert.Equal(20, code);
        Assert.Equal(("internal-error", 30), (body["code"]!.ToString(), body["retryAfterSeconds"]!.GetValue<int>()));
    }

    [Fact]
    public async Task AnItemOutsideTheValidationColumn_IsRefusedWithWrongColumn()
    {
        var fx = new SagaFixture(); await fx.Start(plan: "docs");
        var (code, body) = await new Harness(fx).Merge(Item7);
        Assert.Equal(10, code);
        Assert.Equal("wrong-column", body["code"]!.ToString());
        Assert.Equal("merging happens only from 'Functional Testing'; the item is in 'Technical Design'", body["reason"]!.ToString());
    }

    [Fact]
    public async Task WithoutAPass_TheMergeIsRefused()
    {
        var fx = new SagaFixture(); await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done(); await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        Assert.Equal(StepKind.Verify, fx.State.Step);
        var (code, body) = await new Harness(fx).Merge(Item7);
        Assert.Equal((10, "no-pass-verdict"), (code, body["code"]!.ToString()));
    }

    [Fact]
    public async Task AHoldLabel_RefusesTheMerge_BeforeThePullRequestAndTheAutoMergeLabelAreLookedAt()
    {
        var h = new Harness(await Waiting(), new Labels("hold:owner"));   // (no Auto Merge label either: the hold is the first answer)
        var (code, body) = await h.Merge("{\"item\":7,\"pr\":99}");
        Assert.Equal((10, "held-by-label", "hold:owner"), (code, body["code"]!.ToString(), body["label"]!.ToString()));
        Assert.Empty(h.Published);
    }

    [Fact]
    public async Task AnotherPullRequestThanTheVerdicts_IsAMismatch_AndNoAutoMergeLabelIsRefused()
    {
        var (code, body) = await new Harness(await Waiting()).Merge("{\"item\":7,\"pr\":99}");
        Assert.Equal((10, "pr-mismatch", "the verdict names PR #11, not #99"), (code, body["code"]!.ToString(), body["reason"]!.ToString()));
        (code, body) = await new Harness(await Waiting(), new Labels("docs")).Merge(Item7);
        Assert.Equal((10, "no-auto-merge-label", "item does not carry 'Auto Merge'"), (code, body["code"]!.ToString(), body["reason"]!.ToString()));
    }

    [Fact]
    public async Task AnIssueThatDoesNotExist_IsNotFound()
    {
        var (code, body) = await new Harness(await Waiting(), new Labels { Value = null }).Merge(Item7);
        Assert.Equal((12, "item-not-found"), (code, body["code"]!.ToString()));
    }

    [Fact]
    public async Task AParkedSaga_MergesNothing()
    {
        var fx = await Waiting();
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "merge-blocked"));
        var (code, body) = await new Harness(fx).Merge(Item7);
        Assert.Equal((10, "held", "item is held: merge-blocked"), (code, body["code"]!.ToString(), body["reason"]!.ToString()));
    }

    [Fact]
    public async Task AMergedItem_AnswersAlreadyMerged_WhateverTheColumn_AndAClosedOneIsRefused()
    {
        var fx = await AtMerge();
        await fx.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: Commit));
        var h = new Harness(fx);
        var (code, body) = await h.Merge("{\"item\":7,\"pr\":11}");
        Assert.Equal((0, "already-merged", Commit, true, "merge"), (code, body["outcome"]!.ToString(), body["sha"]!.ToString(), body["ok"]!.GetValue<bool>(), body["verb"]!.ToString()));
        Assert.Equal(7, body["id"]!.GetValue<int>());
        // another pull request than the merged one is not the merged one: the item has left its column
        (code, body) = await h.Merge("{\"item\":7,\"pr\":12}");
        Assert.Equal((11, "item-closed"), (code, body["code"]!.ToString()));
    }

    // ---- the merge: the saga's, asked now ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSagasRetryTimerIsDeliveredAtOnce_AndAMergeIsReported()
    {
        var fx = await Waiting();
        var due = fx.State.MergeRetryAt!.Value;
        var h = new Harness(fx);
        // the port answers the call the saga makes when the timer comes
        h.OnPause = async () => { if (fx.State.MergeRequest is not null) await fx.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: Commit)); };
        var (code, body) = await h.Merge(Item7);
        Assert.Equal(0, code);
        Assert.Equal(("merged", Commit, "gateway", true), (body["outcome"]!.ToString(), body["sha"]!.ToString(), body["by"]!.ToString(), body["ok"]!.GetValue<bool>()));
        var asked = Assert.Single(h.Published);
        Assert.Equal((Item, fx.State.Generation, due), (asked.Item, asked.Generation, asked.Due));
        Assert.Equal(1, h.Pauses);   // (it stops looking at the first look that shows the merge)
    }

    [Fact]
    public async Task AMergeCallThatIsOut_IsNotAskedAgain()
    {
        var fx = await AtMerge();
        Assert.NotNull(fx.State.MergeRequest);
        var h = new Harness(fx);
        var (_, body) = await h.Merge(Item7);
        Assert.Empty(h.Published);
        Assert.Equal("waiting", body["outcome"]!.ToString());
    }

    [Fact]
    public async Task WhileTheChecksAreRunning_TheAnswerIsWaitingWithTheSagasReason()
    {
        var fx = await Waiting("checks-pending");
        var h = new Harness(fx, looks: 4);
        h.OnPause = async () => { if (fx.State.MergeRequest is not null) await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "mergeability-unknown", "", Transient: true)); };
        var (code, body) = await h.Merge(Item7);
        Assert.Equal(0, code);
        Assert.Equal(("waiting", "mergeability-unknown", true), (body["outcome"]!.ToString(), body["reason"]!.ToString(), body["ok"]!.GetValue<bool>()));
        Assert.NotNull(body["since"]);
        Assert.Equal(4, h.Pauses);   // (the looks ran out)
    }

    [Fact]
    public async Task AHeadThatMoved_SendsTheItemBackToItsVerdict_AsADemotion()
    {
        var fx = await Waiting();
        var h = new Harness(fx);
        h.OnPause = async () => { if (fx.State.MergeRequest is not null) await fx.Attempted(new MergeResult(MergeResultKind.HeadMoved, "stale-head", "the branch changed")); };
        var (code, body) = await h.Merge(Item7);
        Assert.Equal(0, code);
        Assert.Equal(("demoted", "stale-head"), (body["outcome"]!.ToString(), body["reason"]!.ToString()));
        Assert.Equal("demoted", body["detail"]!["outcome"]!.ToString());
        Assert.Equal(StepKind.Verify, fx.State.Step);
    }

    [Fact]
    public async Task ARefusalForGood_ParksTheItem_AndTheAnswerSaysSo()
    {
        var fx = await Waiting();
        var h = new Harness(fx);
        h.OnPause = async () => { if (fx.State.MergeRequest is not null) await fx.Attempted(new MergeResult(MergeResultKind.Failed, "not-mergeable", "GitHub reports it is NOT mergeable")); };
        var (code, body) = await h.Merge(Item7);
        Assert.Equal(0, code);
        Assert.Equal(("parked", "merge-failed"), (body["outcome"]!.ToString(), body["reason"]!.ToString()));
    }

    [Fact]
    public async Task AnOutOfDateBranch_IsADemotionToDevelopment()
    {
        var fx = await Waiting();
        var h = new Harness(fx);
        h.OnPause = async () => { if (fx.State.MergeRequest is not null) await fx.Attempted(new MergeResult(MergeResultKind.Failed, "merge-out-of-date", "kept falling behind")); };
        var (_, body) = await h.Merge(Item7);
        Assert.Equal(("demoted", "merge-out-of-date"), (body["outcome"]!.ToString(), body["reason"]!.ToString()));
        Assert.Equal(StepKind.Develop, fx.State.Step);
    }

    [Fact]
    public async Task AHostThatTakesNothing_IsAnAnswerToAskAgain()
    {
        var h = new Harness(await Waiting()) { Accepts = false };
        var (code, body) = await h.Merge(Item7);
        Assert.Equal((20, "merge-unavailable", 30), (code, body["code"]!.ToString(), body["retryAfterSeconds"]!.GetValue<int>()));
        Assert.Equal(0, h.Pauses);
    }

    // ---- approve-pr and clear-hold ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ApprovePr_ChecksItsArgumentsFirst_ThenSaysNoInspectionIsNeededUnderAuto()
    {
        var service = new MergeVerbService(Home, new InMemoryWorkItemStore(), new Labels(), _ => Task.FromResult(true), Settings());
        static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;
        var a = service.ApprovePr(J("{\"text\":\"x\"}"))!.Value;
        Assert.Equal((2, "usage", "missing argument: item"), (a.Code, J(a.Output)["code"]!.ToString(), J(a.Output)["reason"]!.ToString()));
        a = service.ApprovePr(J("{\"item\":7}"))!.Value;
        Assert.Equal((2, "missing argument: text"), (a.Code, J(a.Output)["reason"]!.ToString()));
        a = service.ApprovePr(J("{\"item\":7,\"text\":\"fine\\nINSPECTOR: APPROVE\"}"))!.Value;
        var o = J(a.Output);
        Assert.Equal((10, "not-required", "merge policy is 'auto'; no inspection is needed", false, "approve-pr", 10), (a.Code, o["code"]!.ToString(), o["reason"]!.ToString(), o["ok"]!.GetValue<bool>(), o["verb"]!.ToString(), o["exit"]!.GetValue<int>()));
    }

    [Fact]
    public async Task ClearHold_UnderAuto_FindsNoHoldOfTheInspector_AndRefusesAClosedItem()
    {
        var fx = await AtMerge();
        var h = new Harness(fx);
        var a = (await h.Service.ClearHold((JsonObject)JsonNode.Parse(Item7)!, CancellationToken.None))!.Value;
        var o = (JsonObject)JsonNode.Parse(a.Output)!;
        Assert.Equal((10, "not-held", "the item is not held by the inspector", "clear-hold"), (a.Code, o["code"]!.ToString(), o["reason"]!.ToString(), o["verb"]!.ToString()));
        await fx.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: Commit));   // the docs plan ends at the merge
        Assert.False(fx.State.IsOpen);
        a = (await h.Service.ClearHold((JsonObject)JsonNode.Parse(Item7)!, CancellationToken.None))!.Value;
        Assert.Equal((11, "item-closed"), (a.Code, ((JsonObject)JsonNode.Parse(a.Output)!)["code"]!.ToString()));
    }
}
