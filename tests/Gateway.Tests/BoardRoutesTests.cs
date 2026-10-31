using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.8 (gateway/Verbs/Routes/Board.cs, gateway/Ports/BoardPort.cs): the board group registers its six verbs where the engine acts and its parts are there, the Runner asks them for the gateway's own calls (W9.9a; the plant snapshot and the leader lease have no route and stay the rule scripts'), and sync-board's route is the script's sweep with one pass of the reconciler.
/// </summary>
public sealed class BoardRoutesTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("board-routes-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    EngineOptions Engine(string v2, string? off = null)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, EngineOptionsTests.BlockFor(v2, ""));
        Func<string, string?> env = n => n == "FACTORY_V2_OFF" ? off : null;
        return EngineOptions.From(InstanceConfig.Load(path, env), "prod", env);
    }

    static IServiceProvider Services(Runner runner, BoardVerbParts? parts)
    {
        var s = new ServiceCollection();
        s.AddSingleton(runner);
        if (parts != null) s.AddSingleton(parts);
        return s.BuildServiceProvider();
    }

    static BoardVerbParts Parts(Func<string, string, TimeSpan, Task<(int Code, string Output)>>? ruleScript = null) => new()
    {
        RuleScript = ruleScript ?? ((verb, _, _) => Task.FromResult((0, "{\"ok\":true,\"swept\":true}"))),
    };

    [Fact]
    public void WhereTheEngineActs_AndThePartsAreThere_AllSixVerbsAreRegistered()
    {
        var routes = V2VerbRoutes.Build(Engine("\"prod\""), Services(new Runner("/nonexistent/factory.ps1"), Parts()));
        // the other groups register what they register (their own tests say what): the board group is all of its six verbs
        Assert.Equal(BoardRoutes.Verbs.OrderBy(x => x), routes.Verbs.Where(BoardRoutes.Verbs.Contains).OrderBy(x => x));
        Assert.Equal(["sync-board", "ingest-event", "journal", "transcript-save", "transcript-load", "transcript-prune"], BoardRoutes.Verbs);
    }

    [Fact]
    public void WithoutTheParts_NothingIsRegistered_AndTheRunnerIsLeftAlone()
    {
        var runner = new Runner("/nonexistent/factory.ps1");
        Assert.DoesNotContain(V2VerbRoutes.Build(Engine("\"prod\""), Services(runner, null)).Verbs, BoardRoutes.Verbs.Contains);
    }

    [Fact]
    public void WhereTheEngineDoesNotAct_NothingIsRegistered_AndTheRunnerIsLeftAlone()
    {
        var runner = new Runner("/nonexistent/factory.ps1");
        Assert.Same(V2VerbRoutes.None, V2VerbRoutes.Build(Engine("\"tdd\""), Services(runner, Parts())));          // another environment: v1
        Assert.Same(V2VerbRoutes.None, V2VerbRoutes.Build(Engine("\"prod\"", off: "1"), Services(runner, Parts())));   // the kill switch
        Assert.Null(runner.Routes);   // (W9.9a: every call of the gateway itself is then the rule script)
    }

    [Fact]
    public async Task TheGatewaysOwnCalls_AreAnsweredByTheRoutes_WhereTheEngineActs_AVerbWithNoRouteIsTheScripts()
    {
        var runner = new Runner("/nonexistent/factory.ps1");
        var routes = V2VerbRoutes.Build(Engine("\"prod\""), Services(runner, Parts()));
        Assert.Same(routes, runner.Routes);
        // ingest-event without an event: v2's refusal (a usage error) and no rule script
        var mine = await runner.RunAsync("ingest-event", "{}", TimeSpan.FromSeconds(5));
        Assert.Equal(2, mine.Code);
        Assert.Equal("usage", JsonNode.Parse(mine.Output)!["code"]!.ToString());
        Assert.Equal(2, (await runner.RunAsync("journal", "{}", TimeSpan.FromSeconds(5))).Code);
        // a verb with no route (the plant snapshot) is the rule script's still; the Runner of this test points at no script, so the script's run is a failure and not v2's answer
        Assert.False(routes.TryGet("plant-snapshot-save", out _));
        Assert.NotEqual(0, (await runner.RunAsync("plant-snapshot-save", "{}", TimeSpan.FromSeconds(5))).Code);
    }

    [Fact]
    public async Task ThroughTheRouter_SyncBoardIsTheScriptsSweep_AndAJournalCallIsV2s()
    {
        var swept = new List<string>();
        var routes = V2VerbRoutes.Build(Engine("\"prod\""), Services(new Runner("/nonexistent/factory.ps1"), Parts((verb, stdin, _) => { swept.Add(verb); return Task.FromResult((0, "{\"ok\":true,\"offers\":[]}")); })));
        var router = new V2VerbRouter(new ThrowingBelow(), routes);
        var a = await router.RunAsync("sync-board", "{}", TimeSpan.FromSeconds(5));
        Assert.Equal(0, a.Code); Assert.Equal(["sync-board"], swept);
        Assert.Equal(2, (await router.RunAsync("journal", """{"item":3}""", TimeSpan.FromSeconds(5))).Code);   // v2's usage error: missing event; v1 was never asked
    }

    sealed class ThrowingBelow : IVerbRouter
    {
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) => throw new InvalidOperationException("v1 was asked for " + verb);
    }

    [Fact]
    public void TheEnvLabel_IsGetEnvLabelOfTheScript()
    {
        Func<string, string?> Env(params (string, string)[] kv) => n => kv.Where(p => p.Item1 == n).Select(p => p.Item2).FirstOrDefault();
        var scoped = JsonNode.Parse("""{"envScope":["TDD","uat"]}""");
        Assert.Equal("env:tdd", BoardWiring.EnvLabel(scoped, Env()));                                                        // the default environment is tdd
        Assert.Equal("env:uat", BoardWiring.EnvLabel(scoped, Env(("FACTORY_ENVIRONMENT", " UAT "))));
        Assert.Equal("", BoardWiring.EnvLabel(scoped, Env(("FACTORY_ENVIRONMENT", "prod"))));                                // not in the list: not scoped
        Assert.Equal("env:prod", BoardWiring.EnvLabel(scoped, Env(("FACTORY_ENVIRONMENT", "prod"), ("FACTORY_ENV_SCOPE", "1"))));   // the override wins
        Assert.Equal("", BoardWiring.EnvLabel(scoped, Env(("FACTORY_ENV_SCOPE", "0"))));
        Assert.Equal("", BoardWiring.EnvLabel(JsonNode.Parse("""{"envScope":[]}"""), Env()));
        Assert.Equal("", BoardWiring.EnvLabel(null, Env()));
    }
}
