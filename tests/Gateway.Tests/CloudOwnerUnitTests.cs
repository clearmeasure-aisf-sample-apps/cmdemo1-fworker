using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>The classes C5c made of Program.cs's owner code, alone: CopilotOwnerVerbs, CopilotSettleClient and the master-fix text.</summary>
public sealed class CloudOwnerUnitTests
{
    // a rule-script runner that answers by verb and keeps every input
    sealed class FakeRun(Func<string, string, (int, string)> answer)
    {
        public readonly List<(string Verb, string Input, TimeSpan Timeout)> Calls = new();
        public Task<(int Code, string Output)> Run(string verb, string input, TimeSpan timeout) { Calls.Add((verb, input, timeout)); return Task.FromResult(answer(verb, input)); }
    }

    [Fact]
    public async Task OwnerVerbs_Inputs_AndTheirTimeout()
    {
        var f = new FakeRun((_, _) => (0, """{"ok":true,"rework":"red"}"""));
        var v = new CopilotOwnerVerbs(f.Run);
        await v.Status(7); await v.Close(7); await v.Close(7, unmerged: true); Assert.Equal("red", await v.WhyResume(7));
        await v.Save(new CopilotOwner { Item = 7, TaskId = "t", Url = "u", Branch = "b", Pr = 2, Phase = "p" }, "copilot-cloud");
        Assert.All(f.Calls, c => Assert.Equal(("copilot-owner", TimeSpan.FromSeconds(120)), (c.Verb, c.Timeout)));
        Assert.Equal(new[]
        {
            """{"item":7,"action":"status"}""", """{"item":7,"action":"close"}""", """{"item":7,"action":"close","unmerged":true}""", """{"item":7,"action":"status"}""",
            """{"item":7,"action":"set","task":{"id":"t","url":"u","branch":"b","pr":2,"phase":"p","runtime":"copilot-cloud"}}""",
        }, f.Calls.Select(c => c.Input));
    }

    [Theory]
    [InlineData("\"copilot-cloud\"", false, true)]
    [InlineData("\"copilot-cloud\"", true, false)]
    [InlineData("\"cursor-cloud\"", false, false)]
    [InlineData("\"cursor-cloud\"", true, true)]
    [InlineData("null", false, true)]
    [InlineData("null", true, false)]
    public async Task OwnerVerbs_Load_TakesOnlyTheRuntimesOwnRecords(string runtime, bool cursor, bool taken)
    {
        var f = new FakeRun((_, _) => (0, "{\"task\":{\"id\":\"x-1\",\"pr\":\"nope\",\"runtime\":" + runtime + "}}"));
        var o = await new CopilotOwnerVerbs(f.Run).Load(5, cursor);
        Assert.Equal(taken, o != null);
        if (o != null) Assert.Equal((5, "x-1", 0, "restored", "", ""), (o.Item, o.TaskId, o.Pr, o.WaitingOn, o.Url, o.Branch));
    }

    [Fact]
    public async Task OwnerVerbs_ExitNotZero_Throws_NamingTheAction()
    {
        var v = new CopilotOwnerVerbs(new FakeRun((_, _) => (4, "")).Run);
        Assert.Equal("copilot-owner close answered exit 4", (await Assert.ThrowsAsync<InvalidOperationException>(() => v.Close(1))).Message);
    }

    [Fact]
    public async Task Settle_HeadIsReadOnlyForDevelopment()
    {
        var f = new FakeRun((_, _) => (0, """{"action":"awaiting"}"""));
        var s = new CopilotSettleClient(f.Run);
        var reads = 0;
        string? Head(int item) { reads++; return "copilot/h" + item; }
        Assert.False(await s.Ending(new RuntimeManager.Exec { Item = 3, Phase = "Functional Testing" }, Head));
        Assert.Equal(0, reads);
        Assert.Empty(f.Calls);
        Assert.True(await s.Ending(new RuntimeManager.Exec { Item = 3, Phase = "DEVELOPMENT" }, Head));
        Assert.True(await s.Ending(new RuntimeManager.Exec { Item = 4, Phase = "Development" }, null));
        Assert.Equal(1, reads);
        Assert.Equal(new[] { ("copilot-settle", """{"item":3,"stage":"end","head":"copilot/h3"}"""), ("copilot-settle", """{"item":4,"stage":"end"}""") }, f.Calls.Select(c => (c.Verb, c.Input)));
        Assert.All(f.Calls, c => Assert.Equal(TimeSpan.FromSeconds(120), c.Timeout));
    }

    [Fact]
    public void MasterFixText_IsTodaysText()
    {
        Assert.Equal("Follow-up session of the task that owns #12: master is red after PR #34 was merged as abcdef1 (failed: build, tests). Fix forward: make a new branch from master, fix the failing checks, run the tests, push and open ONE pull request against master titled \"Fix master after #34 (#12)\". Never wait for CI; end the session after opening it.",
            CloudOwnerWatch.MasterFixText(12, 34, "abcdef1234567", "build, tests"));
        // no failed checks named, a short merge commit, and a value that holds a placeholder is left as it is
        Assert.Equal("Follow-up session of the task that owns #1: master is red after PR #0 was merged as {pr}. Fix forward: make a new branch from master, fix the failing checks, run the tests, push and open ONE pull request against master titled \"Fix master after #0 (#1)\". Never wait for CI; end the session after opening it.",
            CloudOwnerWatch.MasterFixText(1, 0, "{pr}", ""));
    }
}

/// <summary>The two hosted services C5c registered, as the Cursor-and-Copilot fixture builds them.</summary>
public sealed class CloudOwnerCompositionTests(RuntimesOnGateway gw) : IClassFixture<RuntimesOnGateway>
{
    [Fact]
    public void OwnerWatches_OnePerCloudRuntime_WithTheirOptions()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services; var r = sp.GetRequiredService<RuntimeRegistry>();
        var watches = sp.GetServices<IHostedService>().OfType<CloudOwnerWatch>().ToList();
        Assert.Equal(new[] { "copilot-cloud", "cursor-cloud" }, watches.Select(w => w.Kind));
        var cop = watches[0]; var cur = watches[1];
        Assert.Equal(sp.GetRequiredService<CopilotCloudSettings>().CiReleaseSeconds, cop.Options.IntervalSeconds);
        Assert.Same(r.CopilotRuntime, cop.Options.MasterFixer);
        Assert.Equal(sp.GetRequiredService<CursorCloudSettings>().OwnerWatchSeconds, cur.Options.IntervalSeconds);
        Assert.Null(cur.Options.MasterFixer);
        // the credit guard is set with the Copilot watch's registration
        Assert.NotNull(r.CopilotRuntime!.OverBudget);
    }

    [Fact]
    public void CiRelease_AtItsInterval_AndTheOwnerCollaboratorsAreSingletons()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        var ci = sp.GetServices<IHostedService>().OfType<CopilotCiReleaseService>().Single();
        Assert.Equal(TimeSpan.FromSeconds(sp.GetRequiredService<CopilotCloudSettings>().CiReleaseSeconds), ci.Interval);
        foreach (var t in new[] { typeof(CopilotOwnerVerbs), typeof(CopilotSettleClient) })
        {
            var ds = gw.Host.Captured!.Where(d => d.ServiceType == t).ToList();
            Assert.True(ds.Count == 1 && ds[0].Lifetime == ServiceLifetime.Singleton, $"{t.Name}: {ds.Count} registrations");
        }
    }

    [Fact]
    public async Task Services_StopAtOnce_DuringTheirWait()
    {
        if (!StubPwsh.Supported) return;
        // made as the registration makes them, with today's intervals (120 s): a stop does not wait for the interval to pass
        var sp = gw.Host.Services; var r = sp.GetRequiredService<RuntimeRegistry>();
        var services = new BackgroundService[]
        {
            new CopilotCiReleaseService(r.CopilotCiRelease, r.Copilot!, r.CopilotRuntime, sp.GetRequiredService<Plant>(), sp.GetRequiredService<Runner>(), sp.GetRequiredService<ILeadership>(), CancellationToken.None, TimeSpan.FromSeconds(120)),
            new CloudOwnerWatch(r.CursorRuntime!, r.Cursor!, sp.GetRequiredService<CopilotOwnerVerbs>(), sp.GetRequiredService<ILeadership>(), CancellationToken.None, new CloudOwnerWatchOptions { IntervalSeconds = 120 }),
        };
        foreach (var s in services)
        {
            await s.StartAsync(CancellationToken.None);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await s.StopAsync(CancellationToken.None);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{s.GetType().Name}: the stop took {sw.Elapsed.TotalSeconds:0.0} s");
            Assert.True(s.ExecuteTask!.IsCompleted);
        }
    }
}
