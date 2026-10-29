using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Intake;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W6.0: the intake rule of the v2 engine (Invoke-Intake of Verbs.Sweep.ps1 as a pure function, with a WIP limit per repository and the choice of the engine), its settings and its start of the saga.</summary>
public class IntakeRuleTests
{
    static ItemId Id(string repo, int n) => new("acme", repo, n);

    static IntakeSettings Settings(int wip = 6, Dictionary<string, int>? perRepo = null, string[]? v2Plans = null, bool enabled = true, string[]? hold = null) =>
        new(enabled, wip, "Todo", hold ?? ["Needs Human Review"], perRepo ?? [], v2Plans ?? [], "engine:v2", "factory worker", "AI Factory", "Demo");

    static IntakeCandidate Cand(string repo, int n, string[]? labels = null, bool open = true, bool card = true, bool deps = true, string? plan = null) =>
        new(Id(repo, n), open, card, labels ?? ["factory worker"], deps, plan);

    static readonly IReadOnlyDictionary<string, int> None = new Dictionary<string, int>();

    // ---- the rule of v1's Invoke-Intake ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFirstEligibleCandidateInBoardOrderIsPulled_AtMostOne()
    {
        var d = IntakeRule.Decide([Cand("orders", 5), Cand("orders", 6)], None, 1, false, Settings());
        Assert.Equal(Id("orders", 5), d.Pull!.Item);
        Assert.Equal((IntakeEngine.V1, IntakeRule.Pulled), (d.Pull.Engine, d.Reason));
    }

    [Theory]
    [InlineData(false, 1, false, 0, IntakeRule.Disabled)]
    [InlineData(true, 0, false, 0, IntakeRule.NoFreeSlot)]
    [InlineData(true, 1, true, 0, IntakeRule.DesignWaiting)]
    [InlineData(true, 1, false, 6, IntakeRule.WipLimit)]
    [InlineData(true, 1, false, 9, IntakeRule.WipLimit)]
    public void NothingIsPulled_WhenOffNoSlotIsFreeSomethingWaitsInDesignOrTheAiColumnsAreFull(bool enabled, int slots, bool waiting, int inFlight, string reason)
    {
        var counts = new Dictionary<string, int> { ["acme/orders"] = inFlight };
        var d = IntakeRule.Decide([Cand("orders", 5)], counts, slots, waiting, Settings(enabled: enabled));
        Assert.Null(d.Pull);
        Assert.Equal(reason, d.Reason);
    }

    [Fact]
    public void TheGlobalLimitCountsEveryRepository_AndOneBelowItStillPulls()
    {
        var counts = new Dictionary<string, int> { ["acme/orders"] = 3, ["acme/billing"] = 2 };
        Assert.NotNull(IntakeRule.Decide([Cand("orders", 5)], counts, 1, false, Settings(wip: 6)).Pull);
        Assert.Equal(IntakeRule.WipLimit, IntakeRule.Decide([Cand("orders", 5)], counts, 1, false, Settings(wip: 5)).Reason);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("no-factory-label")]
    [InlineData("rival")]
    [InlineData("demo")]
    [InlineData("hold:owner")]
    [InlineData("worker:alice")]
    [InlineData("needs human review")]
    [InlineData("no-card")]
    [InlineData("depends-on")]
    public void ACandidateThatIsClosedUnlabelledRivalDemoHeldPinnedCardlessOrWaitingForADependency_IsPassedOver(string why)
    {
        var bad = why switch
        {
            "closed" => Cand("orders", 5, open: false),
            "no-factory-label" => Cand("orders", 5, labels: ["bug"]),
            "rival" => Cand("orders", 5, labels: ["factory worker", "AI Factory"]),
            "demo" => Cand("orders", 5, labels: ["factory worker", "Demo"]),
            "hold:owner" => Cand("orders", 5, labels: ["factory worker", "hold:owner"]),
            "worker:alice" => Cand("orders", 5, labels: ["factory worker", "worker:alice"]),
            "needs human review" => Cand("orders", 5, labels: ["factory worker", "Needs Human Review"]),
            "no-card" => Cand("orders", 5, card: false),
            _ => Cand("orders", 5, deps: false),
        };
        var d = IntakeRule.Decide([bad, Cand("orders", 6)], None, 1, false, Settings());
        Assert.Equal(Id("orders", 6), d.Pull!.Item);
        Assert.Equal(Id("orders", 5), Assert.Single(d.Passed).Item);
        var only = IntakeRule.Decide([bad], None, 1, false, Settings());
        Assert.Null(only.Pull);
        Assert.Equal(IntakeRule.NoCandidate, only.Reason);
    }

    // ---- the limit of a repository -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ARepositoryAtItsOwnLimit_IsPassedOver_AndTheNextRepositoryIsPulledInstead()
    {
        var s = Settings(perRepo: new() { ["acme/orders"] = 2 });
        var counts = new Dictionary<string, int> { ["acme/orders"] = 2, ["acme/billing"] = 1 };
        var d = IntakeRule.Decide([Cand("orders", 5), Cand("orders", 6), Cand("billing", 3)], counts, 1, false, s);
        Assert.Equal(Id("billing", 3), d.Pull!.Item);
        Assert.Equal(2, d.Passed.Count);
        Assert.All(d.Passed, p => Assert.StartsWith("repository-wip: acme/orders holds 2 or more", p.Reason));
    }

    [Fact]
    public void ARepositoryWithoutALimitOfItsOwn_IsLimitedOnlyByTheGlobalOne_AndOneBelowItsLimitIsPulled()
    {
        var s = Settings(perRepo: new() { ["acme/orders"] = 2 });
        Assert.NotNull(IntakeRule.Decide([Cand("billing", 3)], new Dictionary<string, int> { ["acme/billing"] = 5 }, 1, false, s).Pull);   // (no entry: global limit 6 only)
        Assert.NotNull(IntakeRule.Decide([Cand("orders", 5)], new Dictionary<string, int> { ["acme/orders"] = 1 }, 1, false, s).Pull);
        var full = IntakeRule.Decide([Cand("orders", 5)], new Dictionary<string, int> { ["acme/orders"] = 2 }, 1, false, s);
        Assert.Null(full.Pull);
        Assert.Equal(IntakeRule.NoCandidate, full.Reason);
    }

    [Fact]
    public void ALimitOfZero_StopsTheRepositoryAltogether()
    {
        var d = IntakeRule.Decide([Cand("orders", 5)], None, 1, false, Settings(perRepo: new() { ["acme/orders"] = 0 }));
        Assert.Null(d.Pull);
    }

    // ---- the engine ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnItemOnAPlanOfV2Plans_IsTheV2EnginesOwn_AnyOtherStaysWithV1()
    {
        var s = Settings(v2Plans: ["docs"]);
        var docs = IntakeRule.Decide([Cand("orders", 5, plan: "docs")], None, 1, false, s).Pull!;
        Assert.Equal((IntakeEngine.V2, "docs"), (docs.Engine, docs.Plan));
        Assert.Equal(IntakeEngine.V1, IntakeRule.Decide([Cand("orders", 5, plan: "feature")], None, 1, false, s).Pull!.Engine);
        Assert.Equal(IntakeEngine.V1, IntakeRule.Decide([Cand("orders", 5)], None, 1, false, s).Pull!.Engine);
        Assert.Equal(IntakeEngine.V1, IntakeRule.Decide([Cand("orders", 5, plan: "docs")], None, 1, false, Settings()).Pull!.Engine);   // (none by default)
    }

    // ---- the settings ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSettings_ComeFromTheIntakeBlock_WithTheDefaultsOfV1_AndNothingForV2()
    {
        var empty = IntakeSettings.From(JsonNode.Parse("{}"));
        Assert.Equal((false, 6, "Todo", "engine:v2"), (empty.Enabled, empty.WipLimit, empty.FromColumn, empty.V2Label));
        Assert.Empty(empty.V2Plans); Assert.Empty(empty.RepositoryWipLimits);
        var s = IntakeSettings.From(JsonNode.Parse("""
            {"label":"factory worker","rivalLabel":"AI Factory","demoLabel":"Demo",
             "intake":{"enabled":true,"fromColumn":"Backlog","wipLimit":4,"holdLabels":["Needs Human Review"],"repositoryWipLimits":{"Acme/Orders":2,"acme/billing":1,"bad":3,"acme/neg":-1,"acme/text":"x"},"v2Plans":["Docs"," "],"v2Label":"engine:two"}}
            """));
        Assert.Equal((true, 4, "Backlog", "engine:two"), (s.Enabled, s.WipLimit, s.FromColumn, s.V2Label));
        Assert.Equal(["Needs Human Review"], s.HoldLabels);
        Assert.Equal(new Dictionary<string, int> { ["acme/orders"] = 2, ["acme/billing"] = 1 }, s.RepositoryWipLimits.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value));
        Assert.Equal(2, s.LimitOf(RepoId.Parse("ACME/orders")));
        Assert.Null(s.LimitOf(RepoId.Parse("acme/other")));
        Assert.Equal(["docs"], s.V2Plans);
        Assert.True(s.IsV2Plan("DOCS")); Assert.False(s.IsV2Plan("feature")); Assert.False(s.IsV2Plan(null));
        Assert.Equal(("factory worker", "AI Factory", "Demo"), (s.FactoryLabel, s.RivalLabel, s.DemoLabel));
    }

    [Fact]
    public void TheEnvironment_OverridesTheSwitchAndTheGlobalLimit_AsInV1()
    {
        var root = JsonNode.Parse("""{"intake":{"enabled":false,"wipLimit":6}}""");
        var on = IntakeSettings.From(root, n => n switch { "FACTORY_INTAKE" => "1", "FACTORY_INTAKE_WIP" => "2", _ => null });
        Assert.Equal((true, 2), (on.Enabled, on.WipLimit));
        var off = IntakeSettings.From(JsonNode.Parse("""{"intake":{"enabled":true}}"""), n => n == "FACTORY_INTAKE" ? "0" : n == "FACTORY_INTAKE_WIP" ? "x" : null);
        Assert.Equal((false, 6), (off.Enabled, off.WipLimit));   // (the limit that is no number is ignored)
    }

    [Fact]
    public void AMalformedBlock_FallsBackToTheDefaults_FieldByField()
    {
        var s = IntakeSettings.From(JsonNode.Parse("""{"intake":{"enabled":"yes","wipLimit":"six","holdLabels":"x","repositoryWipLimits":[1],"v2Plans":{"a":1}}}"""));
        Assert.Equal((false, 6), (s.Enabled, s.WipLimit));
        Assert.Empty(s.HoldLabels); Assert.Empty(s.RepositoryWipLimits); Assert.Empty(s.V2Plans);
        Assert.False(IntakeSettings.From(null).Enabled);
        Assert.False(IntakeSettings.From(JsonNode.Parse("[1]")).Enabled);
    }

    [Fact]
    public void TheSourceConfiguration_SwitchesNothingOnForV2()
    {
        // the file the tests run on: intake is off, no repository is limited and no plan is the v2 engine's
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var s = IntakeSettings.From(JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json"))));
        Assert.False(s.Enabled);
        Assert.Empty(s.V2Plans);
        Assert.Empty(s.RepositoryWipLimits);
        Assert.Equal("engine:v2", s.V2Label);
        Assert.Equal(6, s.WipLimit);
    }

    // ---- the intake and the saga -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APullForV2_StartsTheSaga_AndAPullForV1_StartsNothing()
    {
        var fx = new SagaFixture();
        var intake = new WorkItemIntake(fx.Store, Settings(v2Plans: ["docs"]), async (m, ct) => await fx.Svc.Handle(m, ct));
        var v1 = await intake.Run([Cand("orders", 5, plan: "feature")], 1, false);
        Assert.Equal(IntakeEngine.V1, v1.Pull!.Engine);
        Assert.Empty(fx.Store.States);
        var v2 = await intake.Run([Cand("orders", 7, plan: "docs")], 1, false);
        Assert.Equal(IntakeEngine.V2, v2.Pull!.Engine);
        Assert.Equal(("docs", StepKind.Design, "intake"), (fx.State.Plan, fx.State.Step, fx.State.Source));
    }

    [Fact]
    public async Task WithoutCountsFromTheBoard_TheIntakeCountsTheItemsOfTheSagaTable()
    {
        var fx = new SagaFixture();
        var intake = new WorkItemIntake(fx.Store, Settings(v2Plans: ["docs"], perRepo: new() { ["acme/orders"] = 1 }), async (m, ct) => await fx.Svc.Handle(m, ct));
        await intake.Run([Cand("orders", 7, plan: "docs")], 1, false);
        var second = await intake.Run([Cand("orders", 8, plan: "docs"), Cand("billing", 2, plan: "docs")], 1, false);
        Assert.Equal(Id("billing", 2), second.Pull!.Item);   // (orders already has the one it may)
        Assert.Equal(2, fx.Store.States.Count);
        // counts the caller read from the board win over the table
        var third = await intake.Run([Cand("orders", 9, plan: "docs")], 1, false, new Dictionary<string, int>());
        Assert.Equal(Id("orders", 9), third.Pull!.Item);
    }
}
