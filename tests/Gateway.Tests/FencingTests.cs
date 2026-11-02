using Fworker.Contracts.Seams;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W0.1, fencing (docs/design/delivery-speed.md 18.1.1): the Runner puts a lease into the rule scripts that act on the board when it is given one (the gateway gives none since V3.2: the GitHub ref the scripts checked
/// is gone, and the SQL lock is checked by the gateway itself, GatewayLeader.MayAct, which refuses the routes of a fenced verb), and a superseded gateway waits for the running ones at most 45 s and then ends them.
/// </summary>
public sealed class FencingTests
{
    static Runner NewRunner(Func<(long Epoch, string Holder)?>? lease) => new(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "factory.ps1")) { Lease = lease };

    [Fact]
    public void InjectLease_FencedVerb_AddsEpochAndHolder_KeepsTheRestOfTheInput()
    {
        var r = NewRunner(() => (7, "gw:1:abc"));
        var o = JsonNode.Parse(r.InjectLease("sync-board", """{"items":[3,4],"low":true}"""))!.AsObject();
        Assert.Equal(7L, (long?)o["_lease"]?["epoch"]);
        Assert.Equal("gw:1:abc", (string?)o["_lease"]?["holder"]);
        Assert.Equal(2, o["items"]!.AsArray().Count);
        Assert.Equal(true, (bool?)o["low"]);
    }

    [Theory]
    [InlineData("sync-board")]
    [InlineData("auto-promote")]
    [InlineData("merge")]
    [InlineData("settle")]
    [InlineData("resettle-item")]
    [InlineData("copilot-settle")]
    [InlineData("base-update-branch")]
    [InlineData("qc-sync")]
    public void InjectLease_EveryFencedVerb_Gets_TheLease_EvenOnEmptyInput(string verb)
    {
        var r = NewRunner(() => (3, "h"));
        Assert.Contains(verb, Runner.FencedVerbs);
        var o = JsonNode.Parse(r.InjectLease(verb, ""))!.AsObject();
        Assert.Equal(3L, (long?)o["_lease"]?["epoch"]);
    }

    [Theory]
    [InlineData("leader-lease")]
    [InlineData("plant-snapshot-save")]
    [InlineData("plant-snapshot-load")]
    [InlineData("revocations")]
    [InlineData("get-item")]
    public void InjectLease_UnfencedVerbs_AreLeftAlone(string verb)
    {
        // the lease's own verb and the snapshot's must work after a hand-over (the final snapshot of a superseded gateway), and reads need no fence
        var r = NewRunner(() => (3, "h"));
        Assert.DoesNotContain(verb, Runner.FencedVerbs);
        Assert.Equal("""{"a":1}""", r.InjectLease(verb, """{"a":1}"""));
    }

    [Fact]
    public void InjectLease_LeaseOff_OrInputNotAnObject_ChangesNothing()
    {
        Assert.Equal("""{"a":1}""", NewRunner(null).InjectLease("sync-board", """{"a":1}"""));
        Assert.Equal("""{"a":1}""", NewRunner(() => null).InjectLease("sync-board", """{"a":1}"""));
        Assert.Equal("[1,2]", NewRunner(() => (1, "h")).InjectLease("sync-board", "[1,2]"));
        Assert.Equal("not json", NewRunner(() => (1, "h")).InjectLease("sync-board", "not json"));
    }

    // a stand-in `pwsh` that runs for 30 s whatever it is asked (the rule script that is still running when the lease is lost)
    static GatewayEnvironment SlowPwsh()
    {
        var env = new GatewayEnvironment();
        env.Apply();
        File.WriteAllText(Path.Combine(env.Dir, "bin", "pwsh"), "#!/bin/sh\ncat > /dev/null\nexec sleep 30\n");
        return env;
    }

    [Fact]
    public async Task Drain_NothingRunning_ReturnsAtOnce()
    {
        var r = NewRunner(() => (1, "h"));
        var sw = Stopwatch.StartNew();
        var (waited, killed) = await r.DrainFencedAsync(TimeSpan.FromSeconds(45));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(0, killed);
        Assert.True(waited < 2000);
    }

    [Fact]
    public async Task Drain_ARunningFencedScript_IsAwaitedForTheTimeGiven_ThenEnded()
    {
        if (!StubPwsh.Supported) return;
        using var env = SlowPwsh();
        var r = NewRunner(() => (1, "h"));
        var call = r.RunAsync("sync-board", "{}", TimeSpan.FromSeconds(120));
        var sw = Stopwatch.StartNew();
        while (r.FencedInFlight == 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.Equal(1, r.FencedInFlight);
        await Task.Delay(300);   // the process has started
        var (waited, killed) = await r.DrainFencedAsync(TimeSpan.FromSeconds(1));
        Assert.True(waited >= 1000, $"waited {waited} ms");
        Assert.Equal(1, killed);
        var (code, _) = await call.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, code);
        Assert.Equal(0, r.FencedInFlight);
    }

    [Fact]
    public async Task Drain_AScriptThatFinishesInTime_IsNotEnded()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Apply();   // the ordinary stub answers {"ok":true} at once
        var r = NewRunner(() => (1, "h"));
        var (code, _) = await r.RunAsync("sync-board", "{}", TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        var (_, killed) = await r.DrainFencedAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, killed);
    }

    [Fact]
    public void InjectLease_AClientsOwnLease_IsTakenOut_WhateverTheVerbAndTheLease()
    {
        var on = NewRunner(() => (7, "gw"));
        var forged = """{"item":3,"_lease":{"epoch":999,"holder":"x"}}""";
        Assert.Null(JsonNode.Parse(on.InjectLease("comment", forged))!["_lease"]);   // not a fenced verb
        Assert.Equal(7L, (long?)JsonNode.Parse(on.InjectLease("merge", forged))!["_lease"]?["epoch"]);   // a fenced verb: the gateway's, not the client's
        Assert.Null(JsonNode.Parse(NewRunner(null).InjectLease("merge", forged))!["_lease"]);   // the lease is off
        Assert.Equal(3, (int?)JsonNode.Parse(on.InjectLease("comment", forged))!["item"]);
    }

    // ---- W9.9a: the routes of a fenced verb are fenced like the script ----

    static (Runner Runner, V2VerbRoutes Routes, List<string> Writes) RoutedRunner(Func<bool>? mayAct)
    {
        var writes = new List<string>();
        var routes = new V2VerbRoutes();
        routes.Add(ItemRoutes.Verbs, "resettle-item", (stdin, _) => { writes.Add("resettle-item " + stdin); return Task.FromResult(new VerbAnswer(0, "{\"ok\":true}")); });
        routes.Add(ItemRoutes.Verbs, "claim-item", (stdin, _) => { writes.Add("claim-item " + stdin); return Task.FromResult(new VerbAnswer(0, "{\"ok\":true}")); });
        var runner = NewRunner(() => (7, "gw"));
        runner.Routes = routes; runner.MayAct = mayAct;
        return (runner, routes, writes);
    }

    [Fact]
    public async Task ARoutedResettleItem_AfterTheLeaseIsLost_AnswersLeaseLost20_AndWritesNothing()
    {
        var held = true;
        var (r, _, writes) = RoutedRunner(() => held);
        Assert.Contains("resettle-item", Runner.FencedVerbs);
        Assert.Equal(0, (await r.RunAsync("resettle-item", """{"item":4,"reason":"x"}""", TimeSpan.FromSeconds(5))).Code);   // the lease is held: the route answers
        Assert.Single(writes);
        held = false;   // a newer process took the lease
        var (code, output) = await r.RunAsync("resettle-item", """{"item":4,"reason":"x"}""", TimeSpan.FromSeconds(5));
        Assert.Equal(20, code);
        var o = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("lease-lost", (string?)o["code"]);
        Assert.Equal(false, (bool?)o["ok"]);
        Assert.Equal(20, (int?)o["exit"]);
        Assert.Single(writes);   // the route was not asked the second time
        Assert.Equal(0, r.FencedInFlight);
    }

    [Fact]
    public async Task AVerbThatIsNotFenced_IsStillAnsweredByItsRoute_WhenTheLeaseIsLost()
    {
        var (r, _, writes) = RoutedRunner(() => false);
        Assert.DoesNotContain("claim-item", Runner.FencedVerbs);
        Assert.Equal(0, (await r.RunAsync("claim-item", "{}", TimeSpan.FromSeconds(5))).Code);   // like the script path: only the fenced verbs stop
        Assert.Single(writes);
    }

    [Fact]
    public async Task ARoutedFencedVerb_IsCountedWhileItRuns_SoTheHandOverWaitsForIt()
    {
        var gate = new TaskCompletionSource();
        var routes = new V2VerbRoutes();
        routes.Add(ItemRoutes.Verbs, "resettle-item", async (_, _) => { await gate.Task; return new VerbAnswer(0, "{\"ok\":true}"); });
        var r = NewRunner(() => (7, "gw"));
        r.Routes = routes; r.MayAct = () => true;
        var call = r.RunAsync("resettle-item", "{}", TimeSpan.FromSeconds(30));
        Assert.Equal(1, r.FencedInFlight);
        var (_, killed) = await r.DrainFencedAsync(TimeSpan.FromMilliseconds(300));   // nothing to end for a route: it is waited for, then the count is still 1
        Assert.Equal(0, killed);
        Assert.Equal(1, r.FencedInFlight);
        gate.SetResult();
        Assert.Equal(0, (await call.WaitAsync(TimeSpan.FromSeconds(5))).Code);
        Assert.Equal(0, r.FencedInFlight);
    }

    [Fact]
    public async Task WhereTheEngineDoesNotAct_ThereAreNoRoutes_AndNothingIsAskedOfTheLeaseHere()
    {
        var asked = 0;
        var r = NewRunner(() => (7, "gw"));
        r.MayAct = () => { asked++; return false; };
        Assert.Null(r.Routes);
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Apply();   // the ordinary stub answers {"ok":true}: the script path is v1's, fenced by the lease in its input
        Assert.Equal(0, (await r.RunAsync("resettle-item", "{}", TimeSpan.FromSeconds(30))).Code);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Provision_WithoutTheLease_StartsNothing_AndItsEndIsNotSettledClaimedOrRevoked()
    {
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var cae = new NeverStarts();
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.RevokeOnEnd = true;
        var m = new RuntimeManager(cfg, cae, new RuntimeNonces(), limits) { MayAct = () => false };
        var ending = 0; var revoked = 0; var claimed = 0;
        m.Ending = _ => { ending++; return Task.FromResult(false); };
        m.Revoke = _ => { revoked++; return Task.CompletedTask; };
        m.ReleaseClaim = (_, _) => { claimed++; return Task.CompletedTask; };
        var t = m.Begin(5, "Development", "");
        await m.Provision(t);
        Assert.Equal(0, cae.Provisions);
        Assert.Equal((0, 0, 0), (ending, revoked, claimed));
    }

    sealed class NeverStarts : IWorkerRuntime
    {
        public int Provisions;
        public string Kind => "cae-job";
        public int MaxConcurrent => 1;
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) { Provisions++; return Task.FromResult(new ProvisionResult(true, "x", "")); }
        public Task Deprovision(string workerName, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>(Array.Empty<RuntimeInstance>());
    }
}
