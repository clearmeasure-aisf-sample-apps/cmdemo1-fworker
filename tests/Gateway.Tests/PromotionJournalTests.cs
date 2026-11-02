using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Integrations.Commands;
using Fworker.Workflows.Delivery;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// V2.1: the journal lines of v1's AutoPromoter (Verbs.Promote.ps1) written by the acting train through the journal ledger: promoted, promotion-waiting (once per release, environment and reason, naming the item that
/// holds the others; the refusal of Octopus with the permission) and promotion-signoff. A shadow train writes none (v1 still writes its own there).
/// </summary>
public sealed class PromotionJournalTests
{
    static readonly RepoId Repo = new("o", "r");

    sealed class Store : IJournalStore
    {
        public readonly Dictionary<int, JsonObject> States = [];
        public readonly List<(int Item, string Body)> Posts = [];
        public Task<ItemState> Read(int item, CancellationToken ct)
        {
            var state = States.TryGetValue(item, out var s) ? (JsonObject)s.DeepClone() : new JsonObject { ["schema"] = 1, ["item"] = item };
            return Task.FromResult(new ItemState { CommentId = 1000 + item, State = state, Rev = ItemStateComment.RevOf(state) });
        }
        public Task Save(int item, ItemState holder, CancellationToken ct) { var s = (JsonObject)holder.State.DeepClone(); s["rev"] = (holder.Rev ?? 0) + 1; States[item] = s; return Task.CompletedTask; }
        public Task<long?> Post(int item, string body, CancellationToken ct) { Posts.Add((item, body)); return Task.FromResult<long?>(Posts.Count); }
        public Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct) => Task.FromResult(PatchOutcome.Ok);
    }

    sealed class Policies(bool shadow) : IDeliveryPolicies
    {
        DeliveryPolicy Make(string env) => new("ui", Repo, env, Promoter.Gateway, true, "orders-ui", new Dictionary<string, string> { ["uat"] = "UAT", ["prod"] = "Production" }, new Dictionary<string, string>(), "/health", new HealthWindow(5, 10), 2, false, Shadow: shadow);
        public DeliveryPolicy? For(string deployable, string environment) => environment == "tdd" ? null : Make(environment);
        public IReadOnlyList<DeliveryPolicy> All() => [Make("uat"), Make("prod")];
        public IReadOnlyList<DeliveryPolicy> ForOctopusEvents() => All();
        public IReadOnlyList<DeliveryPolicy> ForRepository(RepoId repository) => All();
    }

    static (PromotionJournal Journal, Store Store, InMemoryTrainStore Trains) Rig(bool shadow = false)
    {
        var store = new Store();
        var ledger = new JournalLedger(store, new JournalSettings(), log: _ => { });
        var trains = new InMemoryTrainStore();
        return (new PromotionJournal(() => ledger, Repo, new Policies(shadow), trains, _ => { }), store, trains);
    }

    static readonly PromoteRelease Cmd = new("PromoteRelease|ui|1.0.5|uat|1", "ui", "orders-ui", "1.0.5", "uat", ["o/r#7", "o/r#8", "x/other#9"]);

    [Fact]
    public async Task APromotion_IsJournaledOnEachItemOfTheHomeRepository_Once()
    {
        var (journal, store, _) = Rig();
        await journal.Promoted(Cmd, "ServerTasks-41", default);
        await journal.Promoted(Cmd, "ServerTasks-41", default);   // the command runs again: the key is known
        Assert.Equal([7, 8], store.Posts.Select(p => p.Item).Order());
        Assert.Contains("promoted release 1.0.5 to **UAT** (Octopus task `ServerTasks-41`)", store.Posts[0].Body);
        Assert.Contains("event=promoted key=1.0.5|uat", store.Posts[0].Body);
    }

    [Fact]
    public async Task ARefusalOfOctopus_IsAWaitingReasonOnTheItems_NamingThePermission_Once()
    {
        var (journal, store, _) = Rig();
        var error = "Octopus refused to create the deployment (403, missing permission DeploymentCreate); the owner grants it to the gateway's Octopus account";
        await journal.PromoteRefused(Cmd, error, default);
        await journal.PromoteRefused(Cmd, error, default);
        Assert.Equal(2, store.Posts.Count);
        Assert.All(store.Posts, p => Assert.Contains("missing permission DeploymentCreate", p.Body));
        Assert.All(store.Posts, p => Assert.Contains("waits for the automatic promotion to **UAT**", p.Body));
    }

    [Fact]
    public async Task AWaitingRelease_NamesTheItemThatHoldsTheOthers_OncePerReason()
    {
        var (journal, store, _) = Rig();
        var d = new TrainDecided("ui/uat", "waiting", "1.0.5", ["o/r#7", "o/r#8"], ["#7 has no 'Auto Merge' label", "the uat app answered Unhealthy on /health"], DateTimeOffset.UnixEpoch);
        await journal.Decided(d, default);
        await journal.Decided(d, default);
        Assert.Equal(2, store.Posts.Count);
        var on7 = store.Posts.Single(p => p.Item == 7).Body; var on8 = store.Posts.Single(p => p.Item == 8).Body;
        Assert.Contains("the uat app answered Unhealthy on /health; #7 has no 'Auto Merge' label", on7);
        Assert.DoesNotContain("carried by this release", on7);
        Assert.Contains("#7, carried by this release, does not qualify", on8);
        Assert.DoesNotContain("has no 'Auto Merge' label", on8);
        // another reason is another line
        await journal.Decided(d with { Reasons = ["the uat app answered Unhealthy on /health"] }, default);
        Assert.Equal(4, store.Posts.Count);
    }

    [Fact]
    public async Task ASignOffThatWaits_AndOneThatIsAnswered_AreJournaled_TheAnswerWithTheTaskOfTheTrain()
    {
        var (journal, store, trains) = Rig();
        var state = new TrainState { Key = "ui/uat", Deployable = "ui", Environment = "uat" };
        state.SignOffs.Add(new TrainSignOff { InterruptionId = "I-1", TaskId = "ServerTasks-9", Version = "1.0.5", Since = DateTimeOffset.UnixEpoch });
        Assert.True(await trains.Save("ui/uat", state, 0, default));
        await journal.Decided(new TrainDecided("ui/uat", "signoff-waiting", "1.0.5", ["o/r#7"], ["the Sign-off of task ServerTasks-9 waits: a merge is not settled"], DateTimeOffset.UnixEpoch), default);
        await journal.Decided(new TrainDecided("ui/uat", "signoff-proceed", "1.0.5", ["o/r#7"], [], DateTimeOffset.UnixEpoch), default);
        await journal.Decided(new TrainDecided("ui/uat", "signoff-proceed", "1.0.5", ["o/r#7"], [], DateTimeOffset.UnixEpoch), default);
        Assert.Equal(2, store.Posts.Count);
        Assert.Contains("the Sign-off of task ServerTasks-9 waits: a merge is not settled", store.Posts[0].Body);
        Assert.Contains("answered the Sign-off of the deployment of release 1.0.5 to **UAT** (Octopus task `ServerTasks-9`): **Proceed**", store.Posts[1].Body);
    }

    [Fact]
    public async Task AnAnswerOfTheSignOffThatIsRefused_IsAWaitingReasonOnTheItemsOfItsRelease()
    {
        var (journal, store, trains) = Rig();
        var state = new TrainState { Key = "ui/uat", Deployable = "ui", Environment = "uat" };
        state.SignOffs.Add(new TrainSignOff { InterruptionId = "I-1", TaskId = "ServerTasks-9", Version = "1.0.5", Since = DateTimeOffset.UnixEpoch });
        state.RequestItems["1.0.5"] = ["o/r#7"];
        await trains.Save("ui/uat", state, 0, default);
        await journal.AnswerRefused(new AnswerSignOff("AnswerSignOff|ui|I-1", "ui", "I-1", "ServerTasks-9", true, "n"), "Octopus refused to answer (403, missing permission InterruptionSubmit)", default);
        Assert.Equal(7, Assert.Single(store.Posts).Item);
        Assert.Contains("missing permission InterruptionSubmit", store.Posts[0].Body);
    }

    [Fact]
    public async Task AShadowTrain_WritesNoLines()
    {
        var (journal, store, _) = Rig(shadow: true);
        await journal.Decided(new TrainDecided("ui/uat", "waiting", "1.0.5", ["o/r#7"], ["#7 has no 'Auto Merge' label"], DateTimeOffset.UnixEpoch), default);
        Assert.Empty(store.Posts);
    }

    [Fact]
    public async Task AReplayedDecision_AndADecisionWithNoItems_WriteNoLines()
    {
        var (journal, store, _) = Rig();
        await journal.Decided(new TrainDecided("ui/uat", "waiting", "1.0.5", ["o/r#7"], ["x"], DateTimeOffset.UnixEpoch, Replay: true), default);
        await journal.Decided(new TrainDecided("ui/uat", "waiting", "1.0.5", [], ["it has no deployment to UAT"], DateTimeOffset.UnixEpoch), default);
        Assert.Empty(store.Posts);
    }

    [Fact]
    public void ThePromotionLinesAreLinesOnlyThePlantWrites_TheVerbStillRefusesThem()
    {
        foreach (var ev in new[] { "promoted", "promotion-waiting", "promotion-signoff" })
        {
            Assert.Contains(ev, JournalEvents.Promotion);
            Assert.DoesNotContain(ev, JournalEvents.Writable);   // the journal verb and its command refuse them, as before
            Assert.DoesNotContain(ev, JournalEvents.Supported);
        }
    }

    [Fact]
    public void AnOctopusDeploymentThatSucceededWithWarnings_IsASuccessTheWayV1ReadsIt()
    {
        // Octopus lists such a deployment (Deployments-57454, 2026-10-27) as SuccessWithWarning in its UI; the task of the API has State "Success" and HasWarningsOrErrors true. Verbs.Promote.ps1 (Get-ReleaseDeployments,
        // `$d.state -eq 'Success'`) and the watcher read the task's State alone, so the deployment is delivered and the next environment's gate is met in both.
        Assert.Equal(Fworker.Contracts.Seams.OctopusDeploymentPhase.Succeeded, Fworker.Integrations.Octopus.OctopusWatcher.PhaseOf("Success"));
    }
}
