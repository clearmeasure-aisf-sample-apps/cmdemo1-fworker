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

    EngineOptions Engine(string environment, string v2, string shadow = "", string? off = null)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, EngineOptionsTests.BlockFor(v2, shadow));
        Func<string, string?> env = n => n == "FACTORY_V2_OFF" ? off : null;
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
    [InlineData("tdd", "\"prod\"", "", null)]          // v1 acts
    [InlineData("tdd", "", "\"tdd\"", null)]           // the shadow run: v1 acts
    [InlineData("prod", "\"prod\"", "", "1")]          // the kill switch: v1 again
    public void NothingIsRegistered_WhereTheEngineDoesNotAct(string environment, string v2, string shadow, string? off)
    {
        var routes = V2VerbRoutes.Build(Engine(environment, v2, shadow, off), new ServiceCollection().BuildServiceProvider());
        Assert.Equal(0, routes.Count);
    }

    [Fact]
    public void WhereTheEngineActs_EachGroupRegistersItsOwnVerbs_AndTheDeliveryGroupItsSix()
    {
        var routes = V2VerbRoutes.Build(Engine("prod", "\"prod\""), new ServiceCollection().BuildServiceProvider());
        var all = V2VerbRoutes.Groups.SelectMany(g => g.Verbs).ToList();
        Assert.All(routes.Verbs, v => Assert.Contains(v, all));
        // W9.4: the delivery train's verbs; auto-promote is in the group but is the train's own executor, not a route
        Assert.Equal(["ci-release", "copilot-owner", "copilot-settle", "private-build", "publish-demo", "settle"], DeliveryRoutes.Verbs.Where(routes.Verbs.Contains).Order().ToArray());
        Assert.DoesNotContain("auto-promote", routes.Verbs);
    }

    [Fact]
    public void WhereTheEngineDoesNotAct_RegisterIsNotCalled()
    {
        // the table is the shared empty one, so nothing a route file does could be added to it
        Assert.Same(V2VerbRoutes.None, V2VerbRoutes.Build(Engine("tdd", "\"prod\""), new ServiceCollection().BuildServiceProvider()));
        Assert.Same(V2VerbRoutes.None, V2VerbRoutes.Build(Engine("prod", "\"prod\"", off: "1"), new ServiceCollection().BuildServiceProvider()));
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
        // EndpointModules wraps the router only when a route exists (W9.0: none, W9.4: the delivery group's), and a router with no route is the one it was
        var source = File.ReadAllText(Path.Combine(Root(), "gateway", "Endpoints", "EndpointModules.cs"));
        Assert.Contains("is { Count: > 0 } routes ? new V2VerbRouter(router, routes) : router", source);
    }
}
