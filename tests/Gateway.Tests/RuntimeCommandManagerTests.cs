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
}
