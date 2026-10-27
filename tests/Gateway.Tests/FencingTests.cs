using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W0.1, fencing (docs/design/delivery-speed.md 18.1.1): the lease epoch goes into the rule scripts that act on the board, and a superseded gateway
/// waits for them at most 45 s and then ends them. The rule scripts' own refusal of a write once the lease is lost is covered by the Tier 1 scenarios
/// of the owner suite (tests/scenarios/leader.ps1); these tests pin what the gateway sends and does.
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

    [Fact]
    public void FenceLease_IsNullWhileTheLeaseIsOffOrNotTaken_AndKeepsTheEpochAfterSupersession()
    {
        var off = new GatewayLeader(NewRunner(null), new LeaderSettings { On = false }, "0.0.0");
        Assert.Null(off.FenceLease);
        var on = new GatewayLeader(NewRunner(null), new LeaderSettings { On = true }, "0.0.0");
        Assert.Null(on.FenceLease);   // no epoch before the first acquire
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

    // the stub of LeadershipGatewayTests, trimmed: logs verb, action and the whole input
    const string Stub = """
        #!/bin/sh
        verb="$6"
        input=$(cat)
        action=$(printf '%s' "$input" | grep -o '"action":"[^"]*"' | head -n 1 | sed 's/^"action":"//; s/"$//')
        printf '%s\t%s\t%s\n' "$verb" "$action" "$input" >> "$FWTEST_DIR/fence.log"
        if [ "$verb" = leader-lease ] && [ "$action" = check ] && [ -e "$FWTEST_DIR/supersede" ]; then
          echo '{"ok":true,"leader":false,"sha":"s2","current":{"epoch":2,"holder":"newer","until":"2099-01-01T00:00:00Z"}}'; exit 0
        fi
        if [ "$verb" = sync-board ] && [ -e "$FWTEST_DIR/slow" ]; then exec sleep 60; fi
        case "$verb" in
          revocations) echo '{"ok":true,"revoked":{}}' ;;
          leader-lease)
            if [ "$action" = "acquire" ]; then echo '{"ok":true,"epoch":7,"leader":true,"sha":"s7"}'; else echo '{"ok":true,"leader":true}'; fi ;;
          plant-snapshot-load|plant-snapshot-await) echo '{"ok":true,"found":false}' ;;
          plant-snapshot-save) echo '{"ok":true,"tree":"t1"}' ;;
          sync-board) echo '{"ok":true,"offers":[],"actions":[],"errors":[],"owners":{},"triggers":[]}' ;;
          *) echo '{"ok":true}' ;;
        esac
        exit 0
        """;

    [Fact]
    public async Task ActingGateway_RunsSyncBoardUnderItsEpoch_AndTheLeaseAndSnapshotVerbsWithout()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("FACTORY_LEADER_LEASE", null);
        env.Set("FACTORY_PLANT_SNAPSHOT", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Set("FACTORY_TIMING_leaderCheckSeconds", "3600");
        env.Set("FACTORY_TIMING_plantSnapshotDebounceSeconds", "1");
        env.Apply();
        File.WriteAllText(Path.Combine(env.Dir, "bin", "pwsh"), Stub.Replace("\r\n", "\n") + "\n");
        await using var host = new GatewayHost();
        _ = host.Server;
        var log = Path.Combine(env.Dir, "fence.log");
        List<string[]> Lines() => !File.Exists(log) ? new() : File.ReadAllLines(log).Select(l => l.Split('\t', 3)).Where(p => p.Length == 3).ToList();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(40) && !Lines().Any(p => p[0] == "sync-board")) await Task.Delay(100);
        var calls = Lines();
        var sync = calls.FirstOrDefault(p => p[0] == "sync-board");
        Assert.NotNull(sync);
        var status = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json;
        var input = JsonNode.Parse(sync![2])!.AsObject();
        Assert.Equal(7L, (long?)input["_lease"]?["epoch"]);
        Assert.Equal((string?)status?["leader"]?["holder"], (string?)input["_lease"]?["holder"]);
        // the lease's own verb and the snapshot's carry none: a superseded gateway must still be able to write its final snapshot
        foreach (var p in calls.Where(p => p[0] is "leader-lease" or "plant-snapshot-save" or "plant-snapshot-load" or "revocations"))
            Assert.DoesNotContain("_lease", p[2]);
    }

    [Fact]
    public async Task SupersededWhileAFencedScriptRuns_FinalSnapshotAndHandOverComeFirst_AndTheStopIsNotHeldUp()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("FACTORY_LEADER_LEASE", null);
        env.Set("FACTORY_PLANT_SNAPSHOT", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Set("FACTORY_TIMING_leaderCheckSeconds", "1");
        env.Set("FACTORY_TIMING_plantSnapshotDebounceSeconds", "1");
        env.Apply();
        File.WriteAllText(Path.Combine(env.Dir, "bin", "pwsh"), Stub.Replace("\r\n", "\n") + "\n");
        File.WriteAllText(Path.Combine(env.Dir, "slow"), "");   // the first sync-board runs for 60 s
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var log = Path.Combine(env.Dir, "fence.log");
            List<string[]> Lines() => !File.Exists(log) ? new() : File.ReadAllLines(log).Select(l => l.Split('\t', 3)).Where(p => p.Length == 3).ToList();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(40) && !Lines().Any(p => p[0] == "sync-board")) await Task.Delay(100);
            Assert.Contains(Lines(), p => p[0] == "sync-board");   // in flight now
            File.WriteAllText(Path.Combine(env.Dir, "supersede"), "");
            sw.Restart();
            while (sw.Elapsed < TimeSpan.FromSeconds(15) && !Lines().Any(p => p[0] == "plant-snapshot-save" && p[2].Contains("\"final\":true"))) await Task.Delay(100);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "the final snapshot waited for the running script");
            var s = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json;
            Assert.Equal("superseded", (string?)s?["leader"]?["role"]);
            var stop = Stopwatch.StartNew();
            await host.DisposeAsync();
            disposed = true;
            Assert.True(stop.Elapsed < TimeSpan.FromSeconds(30), $"the stop took {stop.Elapsed.TotalSeconds:0.0} s");
        }
        finally { if (!disposed) await host.DisposeAsync(); }
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
