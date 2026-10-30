using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W3.1: the RuntimeManager under the v2 engine. A manager with DispatchStart/DispatchStop set hands the start and the stop to the outbox and settles on the
/// result message (CompleteStart); one without them calls the adapter itself, as v1 does. Pins that both settle the same way.
/// </summary>
public sealed class RuntimeCommandManagerTests
{
    sealed class Rt : IWorkerRuntime
    {
        public string Kind => "cursor-cloud"; public int MaxConcurrent => 5;
        public int Provisions; public List<(string, string)> Stops = new();
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) { Provisions++; return Task.FromResult(new ProvisionResult(true, "exec-v1", "", "http://v1")); }
        public Task Deprovision(string workerName, string reason, CancellationToken ct) { Stops.Add((workerName, reason)); return Task.CompletedTask; }
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>([]);
    }

    static RuntimeManager Manager(Rt rt)
    {
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.IsOn = () => true; limits.MaxConcurrent = 5; limits.MaxExecutionsPerDay = 100;
        return new RuntimeManager(cfg, rt, new RuntimeNonces(), limits) { MayAct = () => true };
    }

    [Fact]
    public async Task V1_WithoutDispatch_CallsTheAdapterItself()
    {
        var rt = new Rt(); var m = Manager(rt);
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        Assert.Equal(1, rt.Provisions); Assert.Equal("exec-v1", m.ExecutionOf(t.Name)); Assert.True(m.Alive(t.Name));
    }

    [Fact]
    public async Task V2_Provision_DispatchesTheCommand_AndDoesNotCallTheAdapter_UntilTheResultArrives()
    {
        var rt = new Rt(); var m = Manager(rt); RuntimeManager.Ticket? seen = null;
        m.DispatchStart = t => { seen = t; return Task.CompletedTask; };
        var t = m.Begin(7, "Development", "prof", "role");
        await m.Provision(t);
        Assert.Equal(0, rt.Provisions); Assert.Equal(t, seen);
        Assert.Equal("", m.ExecutionOf(t.Name));   // not yet: the result message has not come back
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec-v2", "", "http://v2"));
        Assert.Equal("exec-v2", m.ExecutionOf(t.Name)); Assert.Equal("http://v2", m.UrlOf(t.Name)); Assert.True(m.Alive(t.Name));
    }

    [Fact]
    public async Task V2_AStartThatFailedForGood_EndsTheExecution_AsAStartFailure()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(false, "", "runtime off: cursor-cloud"));
        Assert.False(m.Alive(t.Name));
    }

    [Fact]
    public async Task V2_NoCapacity_GivesTheSlotBack_LikeV1()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(false, "", "no capacity"));
        Assert.False(m.Alive(t.Name));
        Assert.False(m.Exhausted(7, "Development"));   // no launch and no failure counted
    }

    [Fact]
    public async Task V2_Reserved_ACopilotLaunchWhoseOutcomeIsUnknown_IsNoFailureOfTheItem()
    {
        // W9.10c: the launch or the resume may have taken effect and the reconciler decides; the start is not a failure and counts no launch, however often it is offered meanwhile
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        for (var i = 0; i < 6; i++)
        {
            var t = m.Begin(7, "Development", "prof");
            await m.Provision(t);
            await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(false, "", "reserved"));
            Assert.False(m.Alive(t.Name));
        }
        Assert.False(m.Exhausted(7, "Development"));
    }

    [Fact]
    public async Task V2_ADispatchThatThrows_IsAStartFailure_NotAnException()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => throw new InvalidOperationException("the Wolverine host is not up");
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        Assert.False(m.Alive(t.Name)); Assert.Equal(0, rt.Provisions);
    }

    [Fact]
    public async Task V2_AResultForAnEndedExecution_StopsWhatItStarted()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        await m.Deprovision(t.Name, "revoked");   // DispatchStop is unset: the adapter is told
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec-v2", "", ""));
        Assert.Contains(rt.Stops, s => s.Item1 == t.Name && s.Item2 == "ended-during-start");
    }

    [Fact]
    public async Task V2_Deprovision_DispatchesTheStop_AndLeavesTheAdapterAlone()
    {
        var rt = new Rt(); var m = Manager(rt); (string, string)? sent = null;
        m.DispatchStop = (n, r) => { sent = (n, r); return Task.CompletedTask; };
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        await m.Deprovision(t.Name, "done");
        Assert.Equal((t.Name, "done"), sent); Assert.Empty(rt.Stops);
    }

    // ---- #522: the start of v2 answers like v1's -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProvisionAndWait_WithoutDispatch_IsV1sCall_TheExecutionIsKnownWhenItAnswers()
    {
        var rt = new Rt(); var m = Manager(rt);
        var t = m.Begin(7, "Development", "prof");
        Assert.True(await m.ProvisionAndWait(t, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, rt.Provisions); Assert.Equal("exec-v1", m.ExecutionOf(t.Name)); Assert.Equal("http://v1", m.UrlOf(t.Name));
    }

    [Fact]
    public async Task V2_ProvisionAndWait_AnswersWhenTheResultHasSettledTheStart_NotBefore()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        var wait = m.ProvisionAndWait(t, TimeSpan.FromSeconds(30));
        await Task.Delay(150);
        Assert.False(wait.IsCompleted);   // dispatched, no result yet: v1 would not have answered either
        Assert.Equal("", m.ExecutionOf(t.Name));
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec-v2", "", "http://v2"));
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("exec-v2", m.ExecutionOf(t.Name)); Assert.Equal("http://v2", m.UrlOf(t.Name)); Assert.True(m.Alive(t.Name));
    }

    [Fact]
    public async Task V2_ProvisionAndWait_AResultThatComesBackBeforeTheDispatchReturns_IsNotMissed()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = async t => await m.CompleteStart(t.Name, t.Item, t.Phase, new ProvisionResult(true, "exec-fast", "", "http://fast"));
        var t = m.Begin(7, "Development", "prof");
        Assert.True(await m.ProvisionAndWait(t, TimeSpan.FromSeconds(10)));
        Assert.Equal("exec-fast", m.ExecutionOf(t.Name));
    }

    [Fact]
    public async Task V2_ProvisionAndWait_AStartThatFailed_AnswersToo_AndTheItemIsTakenBack()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        var wait = m.ProvisionAndWait(t, TimeSpan.FromSeconds(30));
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(false, "", "no capacity"));
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", m.ExecutionOf(t.Name)); Assert.False(m.Alive(t.Name)); Assert.False(m.Exhausted(7, "Development"));
    }

    [Fact]
    public async Task V2_ProvisionAndWait_AResultForAnExecutionThatWasStoppedMeanwhile_AnswersToo_AndStopsWhatItStarted()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        var wait = m.ProvisionAndWait(t, TimeSpan.FromSeconds(30));
        await m.Deprovision(t.Name, "revoked");
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec-v2", "", ""));
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(rt.Stops, s => s.Item1 == t.Name && s.Item2 == "ended-during-start");
    }

    [Fact]
    public async Task V2_ProvisionAndWait_ADispatchThatThrows_AnswersAtOnce_AsAStartFailure()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => throw new InvalidOperationException("the Wolverine host is not up");
        var t = m.Begin(7, "Development", "prof");
        Assert.True(await m.ProvisionAndWait(t, TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(m.Alive(t.Name));
    }

    [Fact]
    public async Task V2_ProvisionAndWait_AFencedProcess_AnswersAtOnce_NothingIsDispatched()
    {
        var rt = new Rt(); var m = Manager(rt); m.MayAct = () => false;
        var dispatched = false; m.DispatchStart = _ => { dispatched = true; return Task.CompletedTask; };
        var t = m.Begin(7, "Development", "prof");
        Assert.True(await m.ProvisionAndWait(t, TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(dispatched); Assert.False(m.Alive(t.Name));
    }

    [Fact]
    public async Task V2_ProvisionAndWait_AResultThatStaysOut_ReturnsFalseAtTheBound_AndALateResultStillSettlesTheStart()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        Assert.False(await m.ProvisionAndWait(t, TimeSpan.FromMilliseconds(100)));   // the bound is the answer, no exception; the command's own retries go on
        Assert.True(m.Alive(t.Name)); Assert.Equal("", m.ExecutionOf(t.Name));
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec-late", "", "http://late"));
        Assert.Equal("exec-late", m.ExecutionOf(t.Name)); Assert.True(m.Alive(t.Name));
    }

    [Fact]
    public async Task NoteStarted_GivesTheRecordsTheExecutionBeforeTheResultMessageArrives_AndTheResultSettlesItAsBefore()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        m.NoteStarted(t.Name, "exec-noted", "http://noted");
        Assert.Equal("exec-noted", m.ExecutionOf(t.Name)); Assert.Equal("http://noted", m.UrlOf(t.Name)); Assert.True(m.Alive(t.Name));
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec-noted", "", "http://noted"));
        Assert.Equal("exec-noted", m.ExecutionOf(t.Name)); Assert.True(m.Alive(t.Name));
    }

    [Fact]
    public async Task NoteStarted_TouchesNeitherAnEndedExecutionNorAnUnknownName_NorMovesASettledExecution()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        m.NoteStarted("never-begun", "x", "y");   // no record, no exception
        Assert.Equal("", m.ExecutionOf("never-begun"));
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec-1", "", "http://1"));
        m.NoteStarted(t.Name, "exec-2", "http://2");   // a replayed result must not move a settled execution
        Assert.Equal("exec-1", m.ExecutionOf(t.Name)); Assert.Equal("http://1", m.UrlOf(t.Name));
        var t2 = m.Begin(8, "Development", "prof");
        await m.Provision(t2);
        await m.CompleteStart(t2.Name, 8, "Development", new ProvisionResult(false, "", "runtime off"));   // ended as a failed start
        m.NoteStarted(t2.Name, "exec-3", "http://3");
        Assert.Equal("", m.ExecutionOf(t2.Name));
    }
}
