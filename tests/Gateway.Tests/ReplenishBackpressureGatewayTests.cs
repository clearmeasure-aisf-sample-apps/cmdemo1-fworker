using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W0.6 (delivery-speed.md 6): the input the gateway passes to sync-board for the replenisher. A full sweep carries <c>replenish_waiting</c>, the number of
/// work items the last plan left without an accepting session; the first sweep of a process, before any plan, carries none (Replenish.ps1 then holds
/// back). Through the stub rule scripts (StubPwsh), whose log of inputs is read.
/// </summary>
public sealed class ReplenishBackpressureGatewayTests
{
    static GatewayEnvironment Environment()
    {
        var env = new GatewayEnvironment();
        env.Set("FACTORY_SWEEP_SECONDS", "1");
        env.Set("FACTORY_SWEEP_MIN_SECONDS", "1");
        env.Apply();
        return env;
    }

    // the sync-board inputs so far, oldest first
    static List<JsonObject> SweepInputs(GatewayEnvironment env)
    {
        var log = env.StubLog + ".in";
        if (!File.Exists(log)) return new();
        using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var all = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return all.Select(l => l.Split('\t', 2)).Where(p => p.Length == 2 && p[0] == "sync-board" && !p[1].Contains("\"items\""))
            .Select(p => JsonNode.Parse(p[1]) as JsonObject ?? new JsonObject()).ToList();
    }

    static async Task<List<JsonObject>> InputsUntil(GatewayEnvironment env, int count)
    {
        var sw = Stopwatch.StartNew();
        var inputs = SweepInputs(env);
        while (inputs.Count < count && sw.Elapsed < TimeSpan.FromSeconds(40)) { await Task.Delay(200); inputs = SweepInputs(env); }
        return inputs;
    }

    static void Answer(GatewayEnvironment env, JsonArray offers) =>
        File.WriteAllText(Path.Combine(env.Dir, "answer-sync-board.json"), new JsonObject { ["ok"] = true, ["actions"] = new JsonArray(), ["offers"] = offers }.ToJsonString() + "\n");

    [Fact]
    public async Task FullSweeps_CarryNoCountBeforeTheFirstPlan_ThenTheLastPlansWaitingCount_Zero()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        Answer(env, new JsonArray());
        await using var host = new GatewayHost();
        _ = host.Server;
        var inputs = await InputsUntil(env, 2);
        Assert.True(inputs.Count >= 2, "two full sweeps expected, saw " + inputs.Count);
        Assert.False(inputs[0].ContainsKey("replenish_waiting"), "the first sweep, before any plan: " + inputs[0].ToJsonString());
        Assert.Equal(0, (int?)inputs[1]["replenish_waiting"]);
    }

    [Fact]
    public async Task FullSweeps_CarryTheItemsTheLastPlanLeftWaiting()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        // two items offered; no worker is connected, so both wait
        Answer(env, new JsonArray(
            new JsonObject { ["item"] = 701, ["column"] = "Technical Design", ["reason"] = "x" },
            new JsonObject { ["item"] = 702, ["column"] = "Technical Design", ["reason"] = "x" }));
        await using var host = new GatewayHost();
        _ = host.Server;
        var inputs = await InputsUntil(env, 2);
        Assert.True(inputs.Count >= 2, "two full sweeps expected, saw " + inputs.Count);
        Assert.False(inputs[0].ContainsKey("replenish_waiting"));
        Assert.Equal(2, (int?)inputs[1]["replenish_waiting"]);
    }
}
