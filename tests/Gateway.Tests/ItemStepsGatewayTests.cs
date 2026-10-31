using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10f: the gateway's side of the sweep's item steps: the saga's rules from the configuration (timing.parkHours, timing.masterGreenTimeoutMinutes, timing.autoUnparkMax, idleReleaseMinutes), the release of an owner that
/// waited out a hold, and the watcher's slot. The rules themselves are tested in Fworker.Workflow.Tests (SweepStepsTests, ItemStepsTests).
/// </summary>
public sealed class ItemStepsSettingsTests
{
    static JsonNode Source()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json")))!;
    }

    [Fact]
    public void TheSourceDefaults_GiveTheSagaTheSweepsOwnTimes()
    {
        var sweep = DocsPlanSettingsReader.From(Source()).Policy.Sweep!;
        Assert.Equal((TimeSpan.FromHours(24), TimeSpan.FromMinutes(30), 3, TimeSpan.FromMinutes(60)), (sweep.ParkTime, sweep.MasterTimeout, sweep.AutoUnparkMax, sweep.IdleReleaseAfter));
        // the master wait belongs to the code plan: where the engine carries no feature plan nothing watches master
        Assert.False(sweep.MasterWatch);
        Assert.False(DocsPlanSettingsReader.From(Source()).Policy.WatchesMaster("feature"));
    }

    [Fact]
    public void TheFeaturePlanNamedByIntake_WatchesMaster_AndTheTimesFollowTheConfiguration()
    {
        var cfg = Source();
        cfg["intake"]!["v2Plans"] = new JsonArray("docs", "feature");
        cfg["timing"]!["parkHours"] = 2; cfg["timing"]!["masterGreenTimeoutMinutes"] = 5; cfg["timing"]!["autoUnparkMax"] = 0; cfg["idleReleaseMinutes"] = 10;
        var policy = DocsPlanSettingsReader.From(cfg).Policy;
        Assert.True(policy.WatchesMaster("feature")); Assert.True(policy.WatchesMaster("hotfix")); Assert.False(policy.WatchesMaster("docs"));
        Assert.Equal((TimeSpan.FromHours(2), TimeSpan.FromMinutes(5), 0, TimeSpan.FromMinutes(10)), (policy.Sweep!.ParkTime, policy.Sweep.MasterTimeout, policy.Sweep.AutoUnparkMax, policy.Sweep.IdleReleaseAfter));
    }

    [Fact]
    public void AMalformedConfiguration_KeepsTheDefaults()
    {
        var sweep = DocsPlanSettingsReader.From(JsonNode.Parse("""{"timing":{"parkHours":"x","masterGreenTimeoutMinutes":-1,"autoUnparkMax":"many"},"idleReleaseMinutes":[1]}""")).Policy.Sweep!;
        Assert.Equal((SweepRules.Default.ParkTime, SweepRules.Default.MasterTimeout, 3, SweepRules.Default.IdleReleaseAfter), (sweep.ParkTime, sweep.MasterTimeout, sweep.AutoUnparkMax, sweep.IdleReleaseAfter));
    }

    [Fact]
    public void TheWatcherHasASlotThatRunsEveryMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), RecurringJobSlots.Default().Slots.Single(s => s.Name == RecurringJobSlots.ItemSteps).Every);
    }
}

public sealed class OwnerReleaseTests
{
    static readonly ItemId Item = new("acme", "orders", 7);
    static OwnerReleaseRequested Request() => new(Item, "release|1", "idle for more than 60 min on a hold (inspector-escalated)");

    sealed class Router(Func<string, JsonObject, VerbAnswer> answer) : IVerbRouter
    {
        public readonly List<(string Verb, JsonObject Args)> Calls = [];
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout)
        {
            var args = JsonNode.Parse(stdin)!.AsObject();
            Calls.Add((verb, args));
            return Task.FromResult(answer(verb, args));
        }
    }

    static VerbAnswer Ok(JsonObject o) => new(0, o.ToJsonString());
    static VerbAnswer Claimed(string holder) => Ok(new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["claim"] = new JsonObject { ["holder"] = holder } } });

    [Fact]
    public async Task TheClaimOfTheItem_IsGivenUp_ByItsHolder_AndThePlantForgetsTheOwner()
    {
        var router = new Router((verb, _) => verb == "get-item" ? Claimed("worker-1") : Ok(new JsonObject { ["ok"] = true, ["released"] = true }));
        var forgotten = new List<(int, string)>();
        var port = new OwnerRelease(() => router, (item, holder) => forgotten.Add((item, holder)));
        Assert.Equal("released", await port.Run(Request(), default));
        Assert.Equal(["get-item", "release"], router.Calls.Select(c => c.Verb));
        var release = router.Calls[1].Args;
        Assert.Equal((7, "worker-1", "idle for more than 60 min on a hold (inspector-escalated)"), (release["item"]!.GetValue<int>(), release["holder"]!.ToString(), release["reason"]!.ToString()));
        Assert.Equal([(7, "worker-1")], forgotten);
    }

    [Fact]
    public async Task AnItemWithNoClaim_OrNotThere_IsReleasedAlready()
    {
        var none = new Router((_, _) => Ok(new JsonObject { ["ok"] = true, ["state"] = new JsonObject() }));
        Assert.Equal("no-claim", await new OwnerRelease(() => none).Run(Request(), default));
        Assert.Single(none.Calls);
        var gone = new Router((_, _) => new VerbAnswer(12, "{}"));
        Assert.Equal("no-item", await new OwnerRelease(() => gone).Run(Request(), default));
    }

    [Fact]
    public async Task AnItemAnotherOwnerTookMeanwhile_IsLeftAlone_AndAFaultIsTriedAgainByTheHost()
    {
        var taken = new Router((verb, _) => verb == "get-item" ? Claimed("worker-1") : new VerbAnswer(40, "{}"));
        var forgotten = new List<(int, string)>();
        Assert.Equal("other-owner", await new OwnerRelease(() => taken, (i, h) => forgotten.Add((i, h))).Run(Request(), default));
        Assert.Empty(forgotten);
        var down = new Router((_, _) => new VerbAnswer(20, "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OwnerRelease(() => down).Run(Request(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OwnerRelease(() => null).Run(Request(), default));
    }

    [Fact]
    public async Task APlantThatCannotForget_DoesNotUndoTheRelease()
    {
        var router = new Router((verb, _) => verb == "get-item" ? Claimed("worker-1") : Ok(new JsonObject { ["ok"] = true }));
        Assert.Equal("released", await new OwnerRelease(() => router, (_, _) => throw new InvalidOperationException("no plant")).Run(Request(), default));
    }
}
