using System.Reflection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W0.2, the day budget counted from the runtime's task history (docs/design/delivery-speed.md 18.1.4): a restart or a UTC midnight does not
/// give the day back. The Octopus runtime's reading of its server tasks is covered by the Tier 1 scenarios (tests/scenarios/octopus.ps1);
/// these tests pin what the manager does with a history.
/// </summary>
public sealed class DayBudgetTests
{
    sealed class HistoryRuntime : IWorkerRuntime, IRunHistory
    {
        public int Count; public bool Fail; public readonly List<DateTime> Asked = new();
        public string Kind => "octopus";
        public int MaxConcurrent => 5;
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) => Task.FromResult(new ProvisionResult(true, "x", ""));
        public Task Deprovision(string workerName, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>(Array.Empty<RuntimeInstance>());
        public Task<int> LaunchesSince(DateTime sinceUtc, CancellationToken ct) { Asked.Add(sinceUtc); return Fail ? throw new HttpRequestException("octopus down") : Task.FromResult(Count); }
    }

    sealed class PlainRuntime : IWorkerRuntime
    {
        public string Kind => "cursor-cloud";
        public int MaxConcurrent => 5;
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) => Task.FromResult(new ProvisionResult(true, "x", ""));
        public Task Deprovision(string workerName, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>(Array.Empty<RuntimeInstance>());
    }

    static RuntimeManager Manager(IWorkerRuntime rt, int perDay)
    {
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.IsOn = () => true; limits.MaxConcurrent = 5; limits.MaxExecutionsPerDay = perDay;
        return new RuntimeManager(cfg, rt, new RuntimeNonces(), limits);
    }

    static void SetField(RuntimeManager m, string name, object v) => typeof(RuntimeManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(m, v);
    static int Today(RuntimeManager m) => (int)typeof(RuntimeManager).GetField("today", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!;

    [Fact]
    public async Task AfterARestart_TheBudgetIsTheRunsOfTheDay_NotWhatTheProcessLaunched()
    {
        var rt = new HistoryRuntime { Count = 3 };
        var m = Manager(rt, 3);
        Assert.Equal((false, "runtime-relisting"), m.CanStart());   // nothing starts before the day is counted
        await m.Poll(CancellationToken.None);
        Assert.Equal(3, Today(m));
        Assert.Equal((false, "runtime-budget"), m.CanStart());      // three runs since 00:00 UTC used the budget of 3, though this process launched none
        Assert.Equal(DateTime.UtcNow.Date, Assert.Single(rt.Asked));
    }

    [Fact]
    public async Task WithRoomLeft_TheCountIsACache_LaunchesAddToIt_AndTheHistoryIsNotReadAgainTheSameDay()
    {
        var rt = new HistoryRuntime { Count = 1 };
        var m = Manager(rt, 3);
        await m.Poll(CancellationToken.None);
        Assert.True(m.CanStart().Ok);
        m.Begin(5, "Development", ""); m.Begin(6, "Development", "");
        Assert.Equal(3, Today(m));
        await m.Poll(CancellationToken.None);
        Assert.Single(rt.Asked);
        Assert.Equal(3, Today(m));
        Assert.Equal((false, "runtime-budget"), m.CanStart());
    }

    [Fact]
    public async Task AHistoryThatCannotBeRead_KeepsTheStartClosed_UntilItCan()
    {
        var rt = new HistoryRuntime { Count = 2, Fail = true };
        var m = Manager(rt, 30);
        await m.Poll(CancellationToken.None);
        Assert.Equal((false, "runtime-relisting"), m.CanStart());
        rt.Fail = false;
        await m.Poll(CancellationToken.None);
        Assert.True(m.CanStart().Ok);
        Assert.Equal(2, Today(m));
    }

    [Fact]
    public async Task AcrossTheUtcMidnight_TheNewDayIsCountedFromTheHistory_ThePreviousDayIsDropped()
    {
        var rt = new HistoryRuntime { Count = 4 };
        var m = Manager(rt, 30);
        await m.Poll(CancellationToken.None);
        Assert.Equal(4, Today(m));
        // the clock passes 00:00 UTC: the process was counted for yesterday
        SetField(m, "day", DateTime.UtcNow.Date.AddDays(-1)); SetField(m, "countedDay", DateTime.UtcNow.Date.AddDays(-1));
        rt.Count = 1;   // one run was queued since the midnight; yesterday's four are not part of the new day
        Assert.Equal((false, "runtime-relisting"), m.CanStart());   // closed until the new day is counted (CanStart rolls the day first)
        Assert.Equal(0, Today(m));
        await m.Poll(CancellationToken.None);
        Assert.Equal(1, Today(m));
        Assert.True(m.CanStart().Ok);
        Assert.Equal(new[] { DateTime.UtcNow.Date, DateTime.UtcNow.Date }, rt.Asked);
    }

    [Fact]
    public async Task ARuntimeWithoutAHistory_CountsInMemoryOnly_AsBefore()
    {
        var m = Manager(new PlainRuntime(), 2);
        await m.Poll(CancellationToken.None);
        Assert.True(m.CanStart().Ok);
        m.Begin(1, "Development", ""); m.Begin(2, "Development", "");
        Assert.Equal((false, "runtime-budget"), m.CanStart());
    }
}
