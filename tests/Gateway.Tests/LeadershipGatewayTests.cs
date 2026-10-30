using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The leader lease's start, the hand-over and the plant snapshot as the gateway does them (C4d): which rule scripts it calls, in which order and
/// with which arguments, and what /api/v1/status says about them. Written against the gateway before LeadershipService and SnapshotCoordinator
/// existed, so they pin today's behaviour, not the new classes'. The whole-gateway stop order is pinned by StopOrderTests.
/// </summary>
public sealed class LeadershipGatewayTests
{
    // the rule scripts for these tests: the answer of a verb is the file answers/<verb>.<action> (the input's first "action"), else answers/<verb>,
    // else a default; a test changes the files while the gateway runs. Every call is logged with its action and input.
    internal const string Stub = """
        #!/bin/sh
        verb="$6"
        input=$(cat)
        action=$(printf '%s' "$input" | grep -o '"action":"[^"]*"' | head -n 1 | sed 's/^"action":"//; s/"$//')
        printf '%s\t%s\t%s\n' "$verb" "$action" "$input" >> "$FWTEST_DIR/lead.log"
        a="$FWTEST_DIR/answers"
        if [ -n "$action" ] && [ -e "$a/$verb.$action" ]; then cat "$a/$verb.$action"; exit 0; fi
        if [ -e "$a/$verb" ]; then cat "$a/$verb"; exit 0; fi
        case "$verb" in
          revocations) echo '{"ok":true,"revoked":{}}' ;;
          leader-lease)
            if [ "$action" = "acquire" ]; then echo '{"ok":true,"epoch":1,"leader":true,"sha":"s1"}'; else echo '{"ok":true,"leader":true}'; fi ;;
          plant-snapshot-load|plant-snapshot-await) echo '{"ok":true,"found":false}' ;;
          plant-snapshot-save) echo '{"ok":true,"tree":"t1"}' ;;
          *) echo '{"ok":true}' ;;
        esac
        exit 0
        """;

    sealed record Call(string Verb, string Action, JsonObject Input);

    // a gateway with the leader lease and the plant snapshot on (the revocations are read, through the stub), quick snapshot cadence, rare lease checks
    internal static GatewayEnvironment Environment(Action<GatewayEnvironment>? change = null)
    {
        var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("FACTORY_LEADER_LEASE", null);
        env.Set("FACTORY_PLANT_SNAPSHOT", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Set("FACTORY_TIMING_leaderCheckSeconds", "3600");
        env.Set("FACTORY_TIMING_plantSnapshotDebounceSeconds", "1");
        change?.Invoke(env);
        env.Apply();
        File.WriteAllText(Path.Combine(env.Dir, "bin", "pwsh"), Stub.Replace("\r\n", "\n") + "\n");
        Directory.CreateDirectory(Path.Combine(env.Dir, "answers"));
        return env;
    }

    static void Answer(GatewayEnvironment env, string name, JsonNode answer) => File.WriteAllText(Path.Combine(env.Dir, "answers", name), answer.ToJsonString() + "\n");

    static List<Call> Calls(GatewayEnvironment env)
    {
        var log = Path.Combine(env.Dir, "lead.log");
        if (!File.Exists(log)) return new();
        return File.ReadAllLines(log).Select(l => l.Split('\t', 3)).Where(p => p.Length == 3)
            .Select(p => new Call(p[0], p[1], JsonNode.Parse(p[2]) as JsonObject ?? new JsonObject())).ToList();
    }

    static string Iso(DateTime d) => d.ToString("o", CultureInfo.InvariantCulture);

    // a plant snapshot the restore accepts: one owner (#12, alice), written now, tagged with the epoch
    static JsonObject Snapshot(long? epoch, bool? final = null)
    {
        var s = new JsonObject { ["v"] = 1, ["at"] = Iso(DateTime.UtcNow), ["assignments"] = new JsonArray(), ["owners"] = new JsonObject { ["12"] = "alice" } };
        if (epoch != null) s["epoch"] = epoch;
        if (final != null) s["final"] = final;
        return s;
    }

    static async Task<JsonNode?> Status(GatewayHost host) =>
        (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json;

    internal static async Task<JsonNode?> StatusUntil(GatewayHost host, Func<JsonNode?, bool> cond, int seconds = 30)
    {
        var sw = Stopwatch.StartNew();
        JsonNode? s = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            s = await Status(host);
            if (cond(s)) return s;
            await Task.Delay(100);
        }
        return s;
    }

    internal static string Role(JsonNode? s) => (string?)s?["leader"]?["role"] ?? "";
    static JsonNode? Restore(JsonNode? s) => s?["plantSnapshot"]?["restore"];
    static long Saves(JsonNode? s) => (long?)s?["plantSnapshot"]?["saves"] ?? 0;
    static string Trail(IEnumerable<Call> calls) => string.Join(", ", calls.Select(c => c.Action == "" ? c.Verb : c.Verb + ":" + c.Action));

    [Fact]
    public async Task Start_NoPredecessor_ReadsRevocationsThenAcquiresRestoresConfirms_ThenFirstSnapshotWithItsEpoch()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "acting" && Saves(x) >= 1);
        Assert.Equal("acting", Role(s));
        Assert.Equal(1L, (long?)s?["leader"]?["epoch"]);
        Assert.Null(s?["leader"]?["sql"]);   // W9.10b: no database, no SQL lease: the ref alone arbitrates, as before
        var ps = s?["plantSnapshot"];
        Assert.Equal(true, (bool?)ps?["enabled"]);
        Assert.Equal(true, (bool?)ps?["leader"]);
        Assert.Equal(30.0, (double?)ps?["maxAgeMinutes"]);
        Assert.Equal(1L, (long?)ps?["saves"]);
        Assert.Equal(0L, (long?)ps?["saveFailures"]);
        Assert.Equal(1L, (long?)ps?["heldEpoch"]);
        Assert.True(DateTime.TryParse((string?)ps?["lastSave"], out _));
        var r = Restore(s);
        Assert.Equal("no-predecessor", (string?)r?["how"]);
        Assert.Equal("start", (string?)r?["via"]);
        Assert.Equal("none", (string?)r?["skipped"]);
        Assert.Equal(0, (int?)r?["assignments"]);
        Assert.Equal(0, (int?)r?["owners"]);
        Assert.Equal(false, (bool?)r?["final"]);
        Assert.Null(r?["predecessorEpoch"]);
        Assert.Null(r?["snapshotEpoch"]);
        Assert.Null(r?["gapSince"]);
        // the dashboard carries the same block
        var dash = (await host.SendAsync("GET", "/api/v1/dashboard", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json;
        Assert.Equal("no-predecessor", (string?)dash?["plantSnapshot"]?["restore"]?["how"]);
        Assert.Equal("acting", (string?)dash?["leader"]?["role"]);

        // the order of the start: the revoked credentials, the lease, the snapshot read, the confirmation, then the first periodic write
        var calls = Calls(env);
        var trail = Trail(calls);
        var rev = calls.FindIndex(c => c.Verb == "revocations");
        var acquire = calls.FindIndex(c => c.Verb == "leader-lease" && c.Action == "acquire");
        var load = calls.FindIndex(c => c.Verb == "plant-snapshot-load");
        var confirm = calls.FindIndex(c => c.Verb == "leader-lease" && c.Action == "check");
        var save = calls.FindIndex(c => c.Verb == "plant-snapshot-save");
        Assert.True(rev >= 0 && acquire > rev && load > acquire && confirm > load && save > confirm, trail);
        Assert.DoesNotContain(calls, c => c.Verb is "plant-snapshot-await" or "plant-snapshot-clear");
        Assert.Equal("{}", calls[load].Input.ToJsonString());
        var first = calls[save].Input;
        Assert.Equal(1L, (long?)first["guard_epoch"]);
        Assert.Null(first["tree"]);
        var snap = first["snapshot"] as JsonObject;
        Assert.NotNull(snap);
        Assert.Equal(1L, (long?)snap!["epoch"]);
        Assert.Equal(false, (bool?)snap["final"]);
        Assert.Equal((string?)s?["leader"]?["holder"], (string?)snap["holder"]);
        Assert.Equal(1, (int?)snap["v"]);
        // nothing changed in the plant: no second periodic write
        await Task.Delay(2500);
        Assert.Single(Calls(env), c => c.Verb == "plant-snapshot-save");
    }

    [Theory]
    [InlineData(3, "", 1)]
    [InlineData(2, "other-epoch", 0)]
    public async Task Start_ReleasedPredecessor_AcceptsOnlyItsPlantEpoch_AndHandsTheGapToTheSweep(long snapEpoch, string skipped, int owners)
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        var releasedAt = DateTime.UtcNow.AddMinutes(-1);
        var pred = new JsonObject { ["epoch"] = 4, ["holder"] = "old", ["released"] = true, ["plant_epoch"] = 3, ["released_at"] = Iso(releasedAt), ["renewed_at"] = Iso(releasedAt.AddMinutes(-1)), ["until"] = Iso(DateTime.UtcNow.AddMinutes(5)) };
        Answer(env, "leader-lease.acquire", new JsonObject { ["ok"] = true, ["epoch"] = 5, ["leader"] = true, ["sha"] = "s5", ["predecessor"] = pred });
        Answer(env, "plant-snapshot-load", new JsonObject { ["ok"] = true, ["found"] = true, ["snapshot"] = Snapshot(snapEpoch) });
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "acting" && Saves(x) >= 1);
        var r = Restore(s);
        Assert.Equal("released", (string?)r?["how"]);
        Assert.Equal(skipped, (string?)r?["skipped"]);
        Assert.Equal(owners, (int?)r?["owners"]);
        Assert.Equal(snapEpoch, (long?)r?["snapshotEpoch"]);
        Assert.Equal(4L, (long?)r?["predecessorEpoch"]);
        Assert.Equal(false, (bool?)r?["final"]);
        Assert.Equal(Iso(releasedAt), (string?)r?["gapSince"]);
        Assert.Equal(5L, (long?)s?["plantSnapshot"]?["heldEpoch"]);   // after its own first write
        var calls = Calls(env);
        Assert.Contains(calls, c => c.Verb == "plant-snapshot-load");
        Assert.DoesNotContain(calls, c => c.Verb is "plant-snapshot-await" or "plant-snapshot-clear");
        Assert.Equal(5L, (long?)calls.First(c => c.Verb == "plant-snapshot-save").Input["guard_epoch"]);
    }

    [Fact]
    public async Task Start_LapsedPredecessor_ReadsOnce_GapFromItsLastRenewal()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        var renewedAt = DateTime.UtcNow.AddMinutes(-7);
        var pred = new JsonObject { ["epoch"] = 4, ["holder"] = "old", ["renewed_at"] = Iso(renewedAt), ["until"] = Iso(DateTime.UtcNow.AddMinutes(-1)) };
        Answer(env, "leader-lease.acquire", new JsonObject { ["ok"] = true, ["epoch"] = 5, ["leader"] = true, ["predecessor"] = pred });
        Answer(env, "plant-snapshot-load", new JsonObject { ["ok"] = true, ["found"] = true, ["snapshot"] = Snapshot(4) });
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "acting" && Restore(x) != null);
        var r = Restore(s);
        Assert.Equal("lapsed", (string?)r?["how"]);
        Assert.Equal("", (string?)r?["skipped"]);
        Assert.Equal(1, (int?)r?["owners"]);
        Assert.Equal(Iso(renewedAt), (string?)r?["gapSince"]);
        Assert.DoesNotContain(Calls(env), c => c.Verb == "plant-snapshot-await");
    }

    [Theory]
    [InlineData(true, "handover")]
    [InlineData(false, "handover-timeout")]
    public async Task Start_HoldingPredecessor_AwaitsItsFinalSnapshot(bool final, string how)
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment(e => e.Set("FACTORY_TIMING_leaderHandoverSeconds", "7"));
        var renewedAt = DateTime.UtcNow.AddSeconds(-20);
        var until = DateTime.UtcNow.AddMinutes(5);
        var pred = new JsonObject { ["epoch"] = 4, ["holder"] = "old", ["renewed_at"] = Iso(renewedAt), ["until"] = Iso(until) };
        Answer(env, "leader-lease.acquire", new JsonObject { ["ok"] = true, ["epoch"] = 5, ["leader"] = true, ["predecessor"] = pred });
        Answer(env, "plant-snapshot-await", new JsonObject { ["ok"] = true, ["found"] = true, ["final"] = final, ["snapshot"] = Snapshot(4, final) });
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "acting" && Restore(x) != null);
        var r = Restore(s);
        Assert.Equal(how, (string?)r?["how"]);
        Assert.Equal(final, (bool?)r?["final"]);
        Assert.Equal("", (string?)r?["skipped"]);
        Assert.Equal(1, (int?)r?["owners"]);
        Assert.Equal(4L, (long?)r?["snapshotEpoch"]);
        Assert.Equal(final ? null : Iso(renewedAt), (string?)r?["gapSince"]);
        var calls = Calls(env);
        Assert.DoesNotContain(calls, c => c.Verb == "plant-snapshot-load");
        var wait = Assert.Single(calls, c => c.Verb == "plant-snapshot-await").Input;
        Assert.Equal(4L, (long?)wait["epoch"]);
        Assert.Equal(7.0, (double?)wait["wait_seconds"]);
        Assert.Equal(Iso(until), (string?)wait["until"]);
    }

    [Fact]
    public async Task SupersededWhileActing_FinalSnapshot_Ready503_NoReleaseAtStop()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment(e => e.Set("FACTORY_TIMING_leaderCheckSeconds", "1"));
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var s = await StatusUntil(host, x => Role(x) == "acting" && Saves(x) >= 1);
            Assert.Equal("acting", Role(s));
            Assert.Equal(200, (await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
            Answer(env, "leader-lease.check", new JsonObject { ["ok"] = true, ["leader"] = false, ["sha"] = "s2", ["current"] = new JsonObject { ["epoch"] = 2, ["holder"] = "newer", ["until"] = Iso(DateTime.UtcNow.AddMinutes(10)) } });
            s = await StatusUntil(host, x => Role(x) == "superseded" && Calls(env).Any(c => c.Verb == "plant-snapshot-save" && (bool?)c.Input["snapshot"]?["final"] == true));
            Assert.Equal("superseded", Role(s));
            Assert.Equal("newer", (string?)s?["leader"]?["supersededBy"]?["holder"]);
            var fin = Calls(env).Last(c => c.Verb == "plant-snapshot-save").Input;
            Assert.Equal(true, (bool?)fin["snapshot"]?["final"]);
            Assert.Equal(1L, (long?)fin["guard_epoch"]);
            Assert.Equal("t1", (string?)fin["tree"]);   // the tree its first write answered
            var ready = await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort);
            Assert.Equal(503, ready.Status);
            Assert.Equal(true, (bool?)ready.Json?["replaced"]);
            // superseded: no periodic write any more
            var saves = Calls(env).Count(c => c.Verb == "plant-snapshot-save");
            await Task.Delay(2500);
            Assert.Equal(saves, Calls(env).Count(c => c.Verb == "plant-snapshot-save"));
            var before = Calls(env).Count;
            await host.DisposeAsync();
            disposed = true;
            var after = Calls(env).Skip(before).ToList();
            Assert.DoesNotContain(after, c => c.Verb == "plant-snapshot-save");
            Assert.DoesNotContain(after, c => c.Verb == "leader-lease" && c.Action == "release");
        }
        finally { if (!disposed) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task SupersededBeforeActing_StandsBy_WithItsFinalSnapshot()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        Answer(env, "leader-lease.check", new JsonObject { ["ok"] = true, ["leader"] = false, ["current"] = new JsonObject { ["epoch"] = 2, ["holder"] = "newer", ["until"] = Iso(DateTime.UtcNow.AddMinutes(10)) } });
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "superseded" && Calls(env).Any(c => c.Verb == "plant-snapshot-save"));
        Assert.Equal("superseded", Role(s));
        Assert.Equal("no-predecessor", (string?)Restore(s)?["how"]);
        await Task.Delay(2500);
        var save = Assert.Single(Calls(env), c => c.Verb == "plant-snapshot-save").Input;
        Assert.Equal(true, (bool?)save["snapshot"]?["final"]);
        Assert.Equal(1L, (long?)save["guard_epoch"]);
    }

    [Fact]
    public async Task StopWhileActing_FinalSnapshotThenReleaseNamingTheHeldEpoch()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        Answer(env, "leader-lease.acquire", new JsonObject { ["ok"] = true, ["epoch"] = 7, ["leader"] = true, ["sha"] = "s7" });
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var s = await StatusUntil(host, x => Role(x) == "acting" && Saves(x) >= 1);
            Assert.Equal(7L, (long?)s?["plantSnapshot"]?["heldEpoch"]);
            var before = Calls(env).Count;
            await host.DisposeAsync();
            disposed = true;
            var after = Calls(env).Skip(before).ToList();
            var trail = Trail(after);
            var fin = after.FindIndex(c => c.Verb == "plant-snapshot-save");
            var release = after.FindIndex(c => c.Verb == "leader-lease" && c.Action == "release");
            Assert.True(fin >= 0 && release > fin, trail);
            Assert.Equal(true, (bool?)after[fin].Input["snapshot"]?["final"]);
            Assert.Equal(7L, (long?)after[fin].Input["guard_epoch"]);
            Assert.Equal("t1", (string?)after[fin].Input["tree"]);
            Assert.Equal(7L, (long?)after[release].Input["plant_epoch"]);
            Assert.Equal(7L, (long?)after[release].Input["epoch"]);
            Assert.Single(after, c => c.Verb == "plant-snapshot-save");
        }
        finally { if (!disposed) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task LeaseOff_SnapshotOn_ReadsClearsOnce_WritesUnguardedAtStop()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment(e => { e.Set("FACTORY_LEADER_LEASE", "0"); e.Set("GH_TOKEN", "unset"); });
        Answer(env, "plant-snapshot-load", new JsonObject { ["ok"] = true, ["found"] = true, ["snapshot"] = Snapshot(null) });
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var s = await StatusUntil(host, x => Restore(x) != null && Calls(env).Any(c => c.Verb == "plant-snapshot-clear"));
            Assert.Equal("off", Role(s));
            var ps = s?["plantSnapshot"];
            Assert.Equal(true, (bool?)ps?["enabled"]);
            Assert.Null(ps?["leader"]);
            var r = Restore(s);
            Assert.Equal("", (string?)r?["skipped"]);
            Assert.Equal(1, (int?)r?["owners"]);
            Assert.Null(r?["how"]);
            Assert.NotNull(r?["snapshotAt"]);
            Assert.Equal(new[] { "assignments", "at", "owners", "skipped", "snapshotAt" }, ((JsonObject)r!).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
            await Task.Delay(1500);
            var calls = Calls(env);
            Assert.DoesNotContain(calls, c => c.Verb is "leader-lease" or "plant-snapshot-save" or "plant-snapshot-await");
            Assert.Single(calls, c => c.Verb == "plant-snapshot-load");
            Assert.Single(calls, c => c.Verb == "plant-snapshot-clear");
            var before = calls.Count;
            await host.DisposeAsync();
            disposed = true;
            var save = Assert.Single(Calls(env).Skip(before), c => c.Verb == "plant-snapshot-save").Input;
            Assert.Equal(new[] { "snapshot" }, save.Select(kv => kv.Key));
            Assert.Null(save["snapshot"]?["epoch"]);
            Assert.Null(save["snapshot"]?["final"]);
        }
        finally { if (!disposed) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task LeaseOn_SnapshotOff_NoSnapshotCalls_ReleaseNamesNoPlant()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment(e => e.Set("FACTORY_PLANT_SNAPSHOT", "0"));
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var s = await StatusUntil(host, x => Role(x) == "acting" && Restore(x) != null);
            Assert.Equal("acting", Role(s));
            Assert.Equal(false, (bool?)s?["plantSnapshot"]?["enabled"]);
            Assert.Equal(true, (bool?)s?["plantSnapshot"]?["leader"]);
            Assert.Equal("snapshot-off", (string?)Restore(s)?["how"]);
            var before = Calls(env).Count;
            await host.DisposeAsync();
            disposed = true;
            var calls = Calls(env);
            Assert.DoesNotContain(calls, c => c.Verb.StartsWith("plant-snapshot", StringComparison.Ordinal));
            var release = Assert.Single(calls.Skip(before), c => c.Verb == "leader-lease" && c.Action == "release").Input;
            Assert.Equal(-1L, (long?)release["plant_epoch"]);
        }
        finally { if (!disposed) await host.DisposeAsync(); }
    }
}
