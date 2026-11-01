using System.Text.Json;
using Fworker.Contracts.Seams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.0 (gateway/Verbs/V2VerbRoutes.cs and gateway/Verbs/Routes/*): one route file per verb group, stubs until W9.2 to W9.8 fill them. The groups are the verbs of docs/design/verb-inventory.json, each in exactly
/// one group; the QC verbs (W9.1) and the retired ones are the only verbs in none. The table is empty unless the engine acts, a stub registers nothing, and a router with nothing registered is the one it was.
/// </summary>
public sealed class V2VerbRoutesTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("v2-routes-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    EngineOptions Engine(string environment, string v2)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, EngineOptionsTests.BlockFor(v2));
        Func<string, string?> env = _ => null;
        return EngineOptions.From(InstanceConfig.Load(path, env), environment, env);
    }

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "docs", "design", "verb-inventory.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    static List<(string Verb, string Destination)> Inventory()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs", "design", "verb-inventory.json")));
        return doc.RootElement.GetProperty("verbs").EnumerateArray().Select(e => (e.GetProperty("verb").GetString()!, e.GetProperty("destination").GetString()!)).ToList();
    }

    sealed class Below(string name) : IVerbRouter
    {
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) => Task.FromResult(new VerbAnswer(0, name + ":" + verb));
    }

    [Fact]
    public void TheGroupsAreTheSevenRouteFiles_EachWithItsVerbs()
    {
        Assert.Equal(["item", "merge", "delivery", "read-models", "keeper", "services", "board"], V2VerbRoutes.Groups.Select(g => g.Group));
        Assert.All(V2VerbRoutes.Groups, g => Assert.NotEmpty(g.Verbs));
        foreach (var name in new[] { "Item", "Merge", "Delivery", "ReadModels", "Keeper", "Services", "Board" })
            Assert.True(File.Exists(Path.Combine(Root(), "gateway", "Verbs", "Routes", name + ".cs")), name + ".cs");
    }

    [Fact]
    public void EveryVerbIsInExactlyOneGroup_ExceptTheQcVerbsAndTheRetiredOnes()
    {
        var all = V2VerbRoutes.Groups.SelectMany(g => g.Verbs).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());   // a verb is in one group only
        var inventory = Inventory();
        Assert.Empty(all.Except(inventory.Select(v => v.Verb)).OrderBy(x => x));   // a group names no verb the inventory does not know
        // the verbs in no group: QC's (W9.1 registers them with QC itself, no flag moves them to v2) and the five that Wolverine's leader election and durable state retire
        var outside = inventory.Select(v => v.Verb).Except(all).OrderBy(x => x).ToList();
        var expected = inventory.Where(v => v.Verb.StartsWith("qc-", StringComparison.Ordinal) || v.Destination == "retired").Select(v => v.Verb).OrderBy(x => x).ToList();
        Assert.Equal(expected, outside);
        Assert.Equal(14, outside.Count(v => v.StartsWith("qc-", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheGroupsFollowTheInventoryOwners()
    {
        var owner = Inventory().ToDictionary(v => v.Verb, v => v.Destination);
        // the item group is the WorkItemSaga's 18 verbs, the merge group the MergeTrainSaga's 5
        Assert.Equal(18, V2VerbRoutes.Groups.Single(g => g.Group == "item").Verbs.Count);
        Assert.Equal(5, V2VerbRoutes.Groups.Single(g => g.Group == "merge").Verbs.Count);
        Assert.All(V2VerbRoutes.Groups.Where(g => g.Group is "item" or "merge" or "delivery").SelectMany(g => g.Verbs), v => Assert.Equal("saga", owner[v]));
        Assert.All(V2VerbRoutes.Groups.Where(g => g.Group is "read-models" or "keeper" or "services").SelectMany(g => g.Verbs), v => Assert.Equal("handler", owner[v]));
    }

    [Theory]
    [InlineData("tdd", "\"prod\"")]          // v1 acts
    public void NothingIsRegistered_WhereV1Acts(string environment, string v2)
    {
        var routes = V2VerbRoutes.Build(Engine(environment, v2), new ServiceCollection().BuildServiceProvider());
        Assert.Equal(0, routes.Count);
    }

    [Fact]
    public void WhereV2Acts_OnlyTheVerbsOfTheGroupsAreRegistered_AndNoneTwice()
    {
        // each group's item fills its own route file (W9.2 to W9.8): what is registered is always some group's verb, once; the QC verbs are in no group
        var routes = V2VerbRoutes.Build(Engine("prod", "\"prod\""), new ServiceCollection().BuildServiceProvider());
        var all = V2VerbRoutes.Groups.SelectMany(g => g.Verbs).ToHashSet();
        Assert.All(routes.Verbs, v => Assert.Contains(v, all));
        Assert.Equal(routes.Count, routes.Verbs.Distinct().Count());
        // W9.3 (#355): the five merge train verbs are registered
        Assert.All(MergeRoutes.Verbs, v => Assert.Contains(v, routes.Verbs));
        // W9.4: the delivery train's verbs; auto-promote is in the group but is the train's own executor, not a route
        Assert.Equal(["ci-release", "copilot-owner", "copilot-settle", "private-build", "publish-demo", "settle"], DeliveryRoutes.Verbs.Where(routes.Verbs.Contains).Order().ToArray());
        Assert.DoesNotContain("auto-promote", routes.Verbs);
    }

    [Fact]
    public void WhereTheEngineDoesNotAct_RegisterIsNotCalled()
    {
        // the table is the shared empty one, so nothing a route file does could be added to it
        Assert.Same(V2VerbRoutes.None, V2VerbRoutes.Build(Engine("tdd", "\"prod\""), new ServiceCollection().BuildServiceProvider()));
        Assert.NotSame(V2VerbRoutes.None, V2VerbRoutes.Build(Engine("prod", "\"prod\""), new ServiceCollection().BuildServiceProvider()));
    }

    [Fact]
    public void AGroupRegistersOnlyItsOwnVerbs_AndOnlyOnce()
    {
        var routes = new V2VerbRoutes();
        V2VerbHandler handler = (_, _) => Task.FromResult(new VerbAnswer(0, "{}"));
        routes.Add(MergeRoutes.Verbs, "merge", handler);
        Assert.Throws<InvalidOperationException>(() => routes.Add(MergeRoutes.Verbs, "merge", handler));       // twice
        Assert.Throws<InvalidOperationException>(() => routes.Add(MergeRoutes.Verbs, "get-item", handler));    // another group's verb
        Assert.Throws<InvalidOperationException>(() => routes.Add(ItemRoutes.Verbs, "no-such-verb", handler));
        Assert.Equal(["merge"], routes.Verbs);
    }

    [Fact]
    public async Task ARegisteredVerbGoesToV2_EveryOtherVerbToTheRouterBelow()
    {
        var routes = new V2VerbRoutes();
        routes.Add(ReadModelRoutes.Verbs, "pr-checks", (stdin, _) => Task.FromResult(new VerbAnswer(0, "v2:" + stdin)));
        var router = new V2VerbRouter(new Below("v1"), routes);
        Assert.Equal(new VerbAnswer(0, "v2:{\"pr\":7}"), await router.RunAsync("pr-checks", "{\"pr\":7}", TimeSpan.FromSeconds(1)));
        Assert.Equal(new VerbAnswer(0, "v1:wait-for-checks"), await router.RunAsync("wait-for-checks", "{}", TimeSpan.FromSeconds(1)));
        Assert.Equal(new VerbAnswer(0, "v1:qc-job"), await router.RunAsync("qc-job", "{}", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TheRouterIsWrappedOnlyWhenARouteExists()
    {
        // EndpointModules wraps the router only when a route exists (a table with none is the stub state of W9.0; the engine that does not act has the shared empty table)
        Assert.Equal(0, V2VerbRoutes.None.Count);
        var source = File.ReadAllText(Path.Combine(Root(), "gateway", "Endpoints", "EndpointModules.cs"));
        Assert.Contains("is { Count: > 0 } routes ? new V2VerbRouter(router, routes) : router", source);
    }

    // ---- W9.9a: the Runner asks the routes, whoever calls --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRunnerAsksTheRoutesFirst_AVerbWithARouteIsV2s_AnyOtherVerbIsTheScripts()
    {
        var routes = new V2VerbRoutes();
        routes.Add(ItemRoutes.Verbs, "claim-item", (stdin, _) => Task.FromResult(new VerbAnswer(0, "v2:" + stdin)));
        routes.Add(BoardRoutes.Verbs, "journal", (stdin, _) => Task.FromResult(new VerbAnswer(0, "v2:journal")));
        var runner = new Runner(Path.Combine(dir, "no-such-factory.ps1")) { Routes = routes };
        Assert.Equal((0, "v2:{\"item\":3}"), await runner.RunAsync("claim-item", "{\"item\":3}", TimeSpan.FromSeconds(5)));
        Assert.Equal((0, "v2:journal"), await runner.RunAsync("journal", "{}", TimeSpan.FromSeconds(5)));   // BaseUpdate and PlantTicker (#477)
        // no route: the rule script, which this test does not have; a failed start is not a v2 answer
        Assert.NotEqual(0, (await runner.RunAsync("auto-promote", "{}", TimeSpan.FromSeconds(5))).Code);
        Assert.Null(new Runner(Path.Combine(dir, "no-such-factory.ps1")).Routes);   // where the engine does not act the table is never set
    }

    [Fact]
    public void ThePerPullRequestBridgesAreGone_AndNoRouteFallsBackToTheRunnerThatWouldAskItAgain()
    {
        var gateway = Directory.EnumerateFiles(Path.Combine(Root(), "gateway"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).ToList();
        Assert.NotEmpty(gateway);
        foreach (var file in gateway)
        {
            var text = ArchitectureTests.Code(File.ReadAllText(file));
            foreach (var gone in new[] { "DeliveryVerbBridge", "ServiceVerbCalls", "V2Own ", "V2Own=", "BoardWiring.OwnCalls" })
                Assert.False(text.Contains(gone, StringComparison.Ordinal), $"{Path.GetRelativePath(Root(), file)} still uses {gone}, which W9.9a removed");
            // a route's own fallback is Runner.RunScriptAsync: Runner.RunAsync would ask the same route again
            var rel = Path.GetRelativePath(Root(), file).Replace('\\', '/');
            if (rel != "gateway/Verbs/VerbRouters.cs" && (rel.StartsWith("gateway/Verbs/", StringComparison.Ordinal) || rel.StartsWith("gateway/Ports/", StringComparison.Ordinal) || rel.StartsWith("gateway/Identity/", StringComparison.Ordinal)))   // (V1VerbRouter is the router below the routes: the script)
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, @"Runner>\(\)\.RunAsync|\brunner\.RunAsync"), $"{rel} calls Runner.RunAsync from a route; call RunScriptAsync");
        }
    }

    [Fact]
    public void TheTableIsBuiltAtStart_BeforeTheFirstCallOfTheGatewayItself()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "gateway", "Composition", "GatewayHostingExtensions.cs"));
        var built = text.IndexOf("GetRequiredService<V2VerbRoutes>()", StringComparison.Ordinal);
        Assert.True(built > 0, "UseGatewayLifetime must build the table of routes");
        Assert.True(built < text.IndexOf("GetRequiredService<BaseWatch>()", StringComparison.Ordinal));
        Assert.True(built < text.IndexOf("GetRequiredService<RevocationService>()", StringComparison.Ordinal), "the read of the revocations at start is a call of the gateway itself");
    }
}
