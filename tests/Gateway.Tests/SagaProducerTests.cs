using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10h (docs/design/fworker-wolverine-implementation.md W9.10h): the producers of the saga's inputs in the gateway. One test or more for each: the park and unpark of the admin endpoint, the runtime adapters and the credit
/// guard (SagaInputs), the budgets and the refused starts of the runtime manager, the events of the ingress inbox (SagaIngress), the merge facts after the merge (MergeFactsPort). The saga's side of the inputs is
/// tested in Fworker.Workflow.Tests (SagaProducerInputsTests).
/// </summary>
public sealed class SagaInputsTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly ItemId Item = new(Home, 7);
    static DocsPlanSettings Settings() => DocsPlanSettingsReader.From(JsonNode.Parse("""{"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","done":"Done"},"requiredBodySection":{"Technical Design":"## Technical Design"}}"""));

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly WorkItemService Svc;
        public readonly V2Ownership Own;
        public readonly SagaInputs Saga;
        public readonly List<object> Published = [];
        public readonly List<TimeSpan> Pauses = [];
        public bool Accept = true;
        public Exception? Fault;

        public Rig()
        {
            Svc = new WorkItemService(Store, new WorkItemSaga(new DocsPort()));
            Own = new V2Ownership(Store, Home, Settings().ColumnOf);
            Saga = new SagaInputs(Own, Store, Home, Publish, m => { Published.Add(m); return Task.FromResult(Accept); }, d => { Pauses.Add(d); return Task.CompletedTask; });
        }

        async Task<bool> Publish(WorkItemInput m)
        {
            if (Fault is not null) throw Fault;
            if (!Accept) return false;
            Published.Add(m);
            await Svc.Handle(m);   // (the host delivers it to the saga)
            return true;
        }

        public async Task Start() { await Svc.Handle(new StartWorkItem(Item, "intake", "docs")); await Own.RefreshAsync(); }
        public WorkItemState State => Store.States[Item.ToString()];
    }

    [Fact]
    public async Task APark_OfAnItemTheSagaOwns_IsTheSagasToo_AndTheItemIsNoLongerOffered()
    {
        var rig = new Rig(); await rig.Start();
        Assert.Equal([(7, "Technical Design")], rig.Own.Offers);
        Assert.True(await rig.Saga.Parked(7, "runtime-failed", "park|2026-10-30T10:00:00.000Z"));
        var park = Assert.IsType<ParkWorkItem>(Assert.Single(rig.Published));
        Assert.Equal((Item, 0, "park|2026-10-30T10:00:00.000Z", "runtime-failed"), (park.Item, park.Generation, park.Key, park.Reason));
        Assert.Equal((true, "runtime-failed"), (rig.State.Parked, rig.State.ParkReason));
        await rig.Own.RefreshAsync();
        Assert.Empty(rig.Own.Offers);   // (the saga keeps offering nothing it parked)
        Assert.True(rig.Own.Owns(7));
    }

    [Fact]
    public async Task AnUnparkThroughTheAdminEndpoint_ResumesTheSaga_AndTheItemIsOfferedAgain()
    {
        var rig = new Rig(); await rig.Start();
        await rig.Saga.Parked(7, "credit-guard", "park|a");
        await rig.Own.RefreshAsync();
        Assert.Empty(rig.Own.Offers);
        // the admin endpoint: unpark-item ran, and its answer names the park it lifted
        var key = SagaKeys.ResumeKey("""{"ok":true,"unparked":true,"park":{"reason":"credit-guard","at":"2026-10-30T10:00:00.000Z"}}""");
        Assert.Equal("resume|2026-10-30T10:00:00.000Z", key);
        Assert.True(await rig.Saga.Resumed(7, key));
        Assert.IsType<ResumeWorkItem>(rig.Published.Last());
        Assert.False(rig.State.Parked);
        await rig.Own.RefreshAsync();
        Assert.Equal([(7, "Technical Design")], rig.Own.Offers);   // the next read of the saga's table offers it again
    }

    [Fact]
    public async Task AnItemTheSagaDoesNotOwn_CostsOneLook_AndIsToldNothing()
    {
        var rig = new Rig(); await rig.Start();
        Assert.False(await rig.Saga.Parked(8, "runtime-failed", "park|x"));
        Assert.False(await rig.Saga.Resumed(8, "resume|x"));
        Assert.False(await rig.Saga.Budget(8, BudgetKind.Failure, "failure|x", "x"));
        Assert.False(await rig.Saga.StartRefused(8, "refused|x", "host-full"));
        Assert.Empty(rig.Published);
    }

    [Fact]
    public async Task ABudgetCount_GoesUnderTheArmOfTheStepTheItemIsOnNow_AndTheLimitParksTheItem()
    {
        var rig = new Rig(); await rig.Start();
        Assert.True(await rig.Saga.Budget(7, BudgetKind.Failure, "failure|oct-7-aa", "start-failed"));
        var spent = Assert.IsType<BudgetSpent>(Assert.Single(rig.Published));
        Assert.Equal((Item, 1, "1.1", BudgetKind.Failure, "failure|oct-7-aa"), (spent.Item, spent.Generation, spent.ArmId, spent.Kind, spent.Key));
        Assert.False(rig.State.Parked);
        Assert.True(await rig.Saga.Budget(7, BudgetKind.Failure, "failure|oct-7-bb", "start-failed"));
        Assert.True(rig.State.Parked);   // maxFailuresPerPhase 2: the second failure of the phase parks the item
        Assert.StartsWith("runtime-failed", rig.State.ParkReason);
        // a count with the same key is the saga's duplicate
        var before = rig.State.Sequence;
        await rig.Saga.Budget(7, BudgetKind.Failure, "failure|oct-7-aa", "again");
        Assert.Equal(before, rig.State.Sequence);
    }

    [Fact]
    public async Task ALaunch_AnIdleFollowUp_AndASessionThatConnected_AreCountedToo()
    {
        var rig = new Rig(); await rig.Start();
        await rig.Saga.Budget(7, BudgetKind.Launch, "launch|a", "octopus");
        Assert.Equal(1, rig.State.LaunchesOf(StepKind.Design));
        await rig.Saga.Budget(7, BudgetKind.IdleFollowUp, "idle|a", "idle");
        Assert.Equal(1, rig.State.IdleFollowUps);
        await rig.Saga.Budget(7, BudgetKind.Registered, "registered|a", "connected");
        Assert.Equal(0, rig.State.IdleFollowUps);
    }

    [Fact]
    public async Task ACountForAnItemWhoseSagaHasCompleted_IsNotSent()
    {
        var rig = new Rig(); await rig.Start();
        await rig.Svc.Handle(new ItemClosed(Item, "closed", "webhook"));
        await rig.Own.RefreshAsync();
        Assert.False(rig.Own.Owns(7));
        Assert.False(await rig.Saga.Budget(7, BudgetKind.Failure, "failure|x", "x"));
        Assert.Empty(rig.Published);
    }

    [Fact]
    public async Task ARefusedStart_GoesToTheScheduler_AsHostFullOrAsAnyOtherOutcome()
    {
        var rig = new Rig(); await rig.Start();
        Assert.True(await rig.Saga.StartRefused(7, "refused|oct-7-aa", AdmissionCodes.HostFull));
        var refused = Assert.IsType<StartRefused>(Assert.Single(rig.Published));
        Assert.Equal((Item, "refused|oct-7-aa", "", "host-full"), (refused.Item, refused.Key, refused.SlotId, refused.Outcome));   // (no slot named: the item's current one)
    }

    [Fact]
    public async Task AHostThatDoesNotTakeTheMessage_IsAskedTwiceMore_ThenTheMessageIsDropped_NeverAnExceptionToTheCaller()
    {
        var rig = new Rig(); await rig.Start(); rig.Accept = false;
        Assert.False(await rig.Saga.Parked(7, "runtime-failed", "park|a"));
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)], rig.Pauses);
        Assert.False(rig.State.Parked);
        rig.Accept = true; rig.Fault = new InvalidOperationException("the host is stopping"); rig.Pauses.Clear();
        Assert.False(await rig.Saga.Resumed(7, "resume|a"));
        Assert.False(await rig.Saga.Budget(7, BudgetKind.Failure, "failure|a", "x"));
        Assert.False(rig.State.Parked);
    }

    [Fact]
    public void TheKeysOfAParkAndItsUnpark_NameThePark_AsTheVerbFeedDoes_AndFallBackToTheMomentOfTheCall()
    {
        const string answer = """{"ok":true,"parked":true,"park":{"reason":"runtime-failed","at":"2026-10-29T12:00:00.000Z"}}""";
        Assert.Equal("park|2026-10-29T12:00:00.000Z", SagaKeys.ParkKey(answer, "runtime-failed", "Development"));
        Assert.StartsWith("park|runtime-failed|Development|", SagaKeys.ParkKey("""{"ok":true}""", "runtime-failed", "Development"));
        Assert.StartsWith("park|runtime-failed|Development|", SagaKeys.ParkKey("not json", "runtime-failed", "Development"));
        Assert.Matches(@"^resume\|\d+$", SagaKeys.ResumeKey(""));
    }
}

/// <summary>W9.10h: the runtime manager tells the saga of the sessions it launches and ends, and the host's refusal for room (exit 75) is a wait, not a failure.</summary>
public sealed class RuntimeSagaFeedTests
{
    sealed class Rt : IWorkerRuntime
    {
        public string Kind => "octopus"; public int MaxConcurrent => 5;
        public ProvisionResult Provisioned = new(true, "task-1", "", "http://octopus/task-1");
        public List<RuntimeInstance> Instances = [];
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) => Task.FromResult(Provisioned);
        public Task Deprovision(string workerName, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>(Instances.ToList());
    }

    sealed class Feed
    {
        public readonly List<(int Item, BudgetKind Kind, string Key)> Budgets = [];
        public readonly List<(int Item, string Key, string Outcome)> Refused = [];
        public Exception? Throw;
    }

    static (RuntimeManager Manager, Rt Rt, Feed Feed) Make()
    {
        var rt = new Rt(); var feed = new Feed();
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.IsOn = () => true; limits.MaxConcurrent = 5; limits.MaxExecutionsPerDay = 100; limits.MaxFailuresPerPhase = 2;
        var m = new RuntimeManager(cfg, rt, new RuntimeNonces(), limits) { MayAct = () => true };
        m.OnBudget = (item, kind, key, _) => { if (feed.Throw is not null) throw feed.Throw; feed.Budgets.Add((item, kind, key)); };
        m.OnStartRefused = (item, key, outcome) => feed.Refused.Add((item, key, outcome));
        return (m, rt, feed);
    }

    static int Today(RuntimeManager m) => (int)typeof(RuntimeManager).GetField("today", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(m)!;

    static async Task<RuntimeManager.Ticket> Launch(RuntimeManager m, Rt rt, int item = 7, string phase = "Development")
    {
        await m.Poll(CancellationToken.None);   // (the first reading: the manager may start)
        var t = m.Begin(item, phase, "");
        await m.Provision(t);
        rt.Instances.Add(new RuntimeInstance("task-1", t.Name, item, phase, "Running", DateTime.UtcNow, null));
        return t;
    }

    [Fact]
    public async Task ALaunch_IsCountedOnceTheExecutionExists_UnderTheKeyOfTheWorker()
    {
        var (m, rt, feed) = Make();
        var t = await Launch(m, rt);
        Assert.Equal([(7, BudgetKind.Launch, $"launch|{t.Name}")], feed.Budgets);
    }

    [Fact]
    public async Task AFailedStart_IsAFailureOfTheStep_AndFreesTheSchedulersSlot()
    {
        var (m, rt, feed) = Make();
        rt.Provisioned = new ProvisionResult(false, "", "start-error: HttpRequestException");
        await m.Poll(CancellationToken.None);
        var t = m.Begin(7, "Development", "");
        await m.Provision(t);
        Assert.Equal([(7, BudgetKind.Failure, $"failure|{t.Name}")], feed.Budgets);
        Assert.Equal([(7, $"refused|{t.Name}", "start-failed")], feed.Refused);
    }

    [Fact]
    public async Task NoCapacity_IsAWait_TheSlotGoesBackAsHostFull_AndNothingIsCountedAgainstTheStep()
    {
        var (m, rt, feed) = Make();
        rt.Provisioned = new ProvisionResult(false, "", "no capacity");
        await m.Poll(CancellationToken.None);
        var t = m.Begin(7, "Development", "");
        await m.Provision(t);
        Assert.Empty(feed.Budgets);
        Assert.Equal([(7, $"refused|{t.Name}", "host-full")], feed.Refused);
        Assert.Equal(0, Today(m));
    }

    [Fact]
    public async Task AStartTheHostRefusedForRoom_Exit75_IsNotAFailure_ItsExecutionIsRefunded_AndTheSlotGoesBackAsHostFull()
    {
        var (m, rt, feed) = Make();
        var t = await Launch(m, rt);
        Assert.Equal(1, Today(m));
        // the start task ended Failed with the host's refusal: the runtime says HostFull
        rt.Instances[0] = rt.Instances[0] with { Status = "HostFull", Ended = DateTime.UtcNow };
        await m.Poll(CancellationToken.None);
        Assert.False(m.Alive(t.Name));
        Assert.Equal(0, Today(m));   // (the execution is refunded)
        Assert.False(m.Exhausted(7, "Development"));
        Assert.DoesNotContain(feed.Budgets, b => b.Kind == BudgetKind.Failure);
        Assert.Equal([(7, $"refused|{t.Name}", "host-full")], feed.Refused);
        var status = m.Status()["octopus"]!;
        Assert.Equal((true, 0), ((bool)status["healthy"]!, (int)status["startFailuresInRow"]!));   // (the runtime stays healthy)
        Assert.Empty(status["failures"]!.AsArray());
        // and the launch is given back: the same step can start as often as before
        Assert.Empty(status["launches"]!.AsArray());
    }

    [Fact]
    public async Task ALostExecutionWithAFailedStatus_IsStillAFailure_OfTheStepAndOfTheRuntimesHealth()
    {
        var (m, rt, feed) = Make();
        var t = await Launch(m, rt);
        rt.Instances[0] = rt.Instances[0] with { Status = "Failed", Ended = DateTime.UtcNow };
        await m.Poll(CancellationToken.None);
        Assert.Contains((7, BudgetKind.Failure, $"failure|{t.Name}"), feed.Budgets);
        Assert.Empty(feed.Refused);
        Assert.Equal(1, Today(m));
    }

    [Fact]
    public async Task ASessionThatRegistered_IsCountedAsConnected_OnceNotTwice()
    {
        var (m, rt, feed) = Make();
        var t = await Launch(m, rt);
        m.Registered(t.Name); m.Registered(t.Name);
        Assert.Single(feed.Budgets, b => b.Kind == BudgetKind.Registered);
    }

    [Fact]
    public async Task AFaultOfTheFeed_NeverReachesTheStart()
    {
        var (m, rt, feed) = Make();
        feed.Throw = new InvalidOperationException("boom");
        var t = await Launch(m, rt);
        Assert.True(m.Alive(t.Name));
    }
}

/// <summary>W9.10h: the Octopus runtime tells a start task the host refused for room from any other failure.</summary>
public sealed class HostFullTaskTests
{
    [Theory]
    [InlineData("The remote script failed with exit code 75", null, true)]
    [InlineData("The remote script failed with exit code 75.", null, true)]
    [InlineData("The remote script failed with exit code 1", null, false)]
    [InlineData("The remote script failed with exit code 750", null, false)]
    [InlineData(null, "INFO 12:00 starting\nhost-full: 2 running fw-* container(s) on this host, the limit is 2\nERROR exit 75", true)]
    [InlineData(null, "docker: Error response from daemon: name conflict", false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void TheErrorMessageOrTheLog_SaysWhetherTheHostRefusedTheStartForRoom(string? error, string? log, bool hostFull) =>
        Assert.Equal(hostFull, OctopusRunbookRuntime.IsHostFull(error, log));
}
