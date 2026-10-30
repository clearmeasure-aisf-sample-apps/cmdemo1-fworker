using System.Text.Json.Nodes;
using Fworker.Workflows.Keeper;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.6: the PowerShell behaviour the keeper and manager verbs lean on (VerbSupport.cs, FactoryRules.cs), pinned to what the rule scripts do with the same input.</summary>
public sealed class VerbSupportTests
{
    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("7", "7")]
    [InlineData("1.5", "1.5")]
    [InlineData("true", "True")]
    [InlineData("false", "False")]
    [InlineData("null", "")]
    [InlineData("[\"a\",\"b\"]", "a b")]
    public void AValueIsStringifiedAsTheScriptsDo(string json, string expected) => Assert.Equal(expected, Ps.Str(JsonNode.Parse(json)));

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"false\"", true)]    // a text is true when it is not empty, whatever it says
    [InlineData("\"\"", false)]
    [InlineData("0", false)]
    [InlineData("2", true)]
    [InlineData("[]", false)]
    [InlineData("[1]", true)]
    [InlineData("null", false)]
    public void TruthIsPowerShellsTruth(string json, bool expected) => Assert.Equal(expected, Ps.Truthy(JsonNode.Parse(json)));

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\"yes\"", false)]
    [InlineData("1", true)]
    [InlineData("2", false)]
    [InlineData("null", false)]
    public void EqualToTrueIsPowerShellsEq(string json, bool expected) => Assert.Equal(expected, Ps.EqTrue(JsonNode.Parse(json)));

    [Fact]
    public void TheArgumentsAreReadWithoutRegardToTheCaseOfTheirNames_AndNeedRefusesNothingAndEmpty()
    {
        var a = new VerbArgs(JsonNode.Parse("""{ "Item": 5, "reason": "", "gone": null, "text": "x" }""")!.AsObject());
        Assert.True(a.Has("item")); Assert.True(a.Has("reason")); Assert.False(a.Has("gone")); Assert.False(a.Has("none"));
        Assert.Equal(5, a.ItemNumber());
        Assert.Equal("x", a.Need("TEXT"));
        Assert.Equal("usage", Assert.Throws<VerbRejection>(() => a.Need("reason")).Code);
        Assert.Equal("missing argument: gone", Assert.Throws<VerbRejection>(() => a.Need("gone")).Message);
        Assert.Equal(2, Assert.Throws<VerbRejection>(() => a.Need("none")).Exit);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" 12 ", 12)]
    [InlineData("12.4", 12)]
    [InlineData("12.5", 12)]
    [InlineData("13.5", 14)]
    public void AnIntIsPowerShellsInt(string text, int expected) => Assert.Equal(expected, VerbArgs.ToInt(text));

    [Fact]
    public void ATextThatIsNoNumberIsAnErrorOfTheScript_NotARefusal() => Assert.Throws<FormatException>(() => VerbArgs.ToInt("five"));

    [Fact]
    public void TheDefaultsAreTheScriptsDefaults_AndTheConfigurationOverridesThem()
    {
        var d = FactoryRules.From(JsonNode.Parse("{}"), _ => null);
        Assert.Equal("hold:owner", d.OwnerHoldLabel); Assert.Equal("priority:top", d.PriorityTop); Assert.Equal("priority:high", d.PriorityHigh);
        Assert.Equal("Technical Design", d.KeeperColumn); Assert.Equal(3, d.ManagerPerItemPerDay);
        Assert.Equal(FactoryRules.DefaultKeeperPolicies, d.KeeperPolicies); Assert.Equal(FactoryRules.DefaultManagerPolicies, d.ManagerPolicies);
        Assert.Equal(FactoryRules.DefaultProtectedCriteria, d.ProtectedCriteria);
        Assert.Equal("", d.EnvLabel);

        var c = FactoryRules.From(JsonNode.Parse("""
            { "ownerHoldLabel": "stop", "priorityLabels": { "top": "p0" }, "keeper": { "newItemColumn": "Todo", "policies": ["x"], "protectedCriteria": ["abc"] },
              "manager": { "actions": { "perItemPerDay": 5, "policies": ["y"] } }, "columns": { "ai": ["A", "B"] }, "requiredBodySection": { "A": "## A", "B": "## B" } }
            """), _ => null);
        Assert.Equal("stop", c.OwnerHoldLabel); Assert.Equal("p0", c.PriorityTop); Assert.Equal("priority:high", c.PriorityHigh);
        Assert.Equal("Todo", c.KeeperColumn); Assert.Equal(["x"], c.KeeperPolicies); Assert.Equal(["abc"], c.ProtectedCriteria);
        Assert.Equal(5, c.ManagerPerItemPerDay); Assert.Equal(["y"], c.ManagerPolicies);
        Assert.Equal(["A", "B"], c.AiColumns); Assert.Equal(["## A", "## B"], c.BodySections);
        Assert.Equal("top", c.PriorityOf(["p0"])); Assert.Equal("high", c.PriorityOf(["Priority:High"])); Assert.Equal("", c.PriorityOf(["x"]));
    }

    [Theory]
    [InlineData("tdd", null, "[\"tdd\"]", "env:tdd")]
    [InlineData("tdd", null, "[\"uat\"]", "")]
    [InlineData("tdd", "0", "[\"tdd\"]", "")]
    [InlineData("tdd", "1", "[]", "env:tdd")]
    [InlineData("", null, "[\"tdd\"]", "env:tdd")]      // no environment named is tdd
    [InlineData("UAT", "1", "[]", "env:uat")]
    public void TheEnvironmentLabelFollowsTheScopeOfTheEnvironment(string environment, string? scope, string envScope, string expected)
    {
        Func<string, string?> env = n => n == "FACTORY_ENVIRONMENT" ? environment : n == "FACTORY_ENV_SCOPE" ? scope : null;
        Assert.Equal(expected, FactoryRules.EnvLabelOf(JsonNode.Parse($"{{ \"envScope\": {envScope} }}"), env));
    }

    [Fact]
    public void WithScopingOnAnItemIsInScopeOnlyWithExactlyItsOwnEnvLabel()
    {
        var r = FactoryRules.From(JsonNode.Parse("""{ "envScope": ["tdd"] }"""), n => n == "FACTORY_ENVIRONMENT" ? "tdd" : null);
        Assert.True(r.InScope(["factory", "ENV:TDD"]));
        Assert.False(r.InScope(["factory"]));
        Assert.False(r.InScope(["env:tdd", "env:uat"]));
        Assert.False(r.InScope(["env:uat"]));
        Assert.True(FactoryRules.From(JsonNode.Parse("{}"), _ => null).InScope(["env:uat"]));   // scoping off: everything is in
    }

    [Fact]
    public void TheSessionFooterIsTheNameWithItsPage_OrTheNameAlone_OrNothing()
    {
        Assert.Equal("AI session: [k1](https://gw.example/sessions/k1)", VerbContext.SessionFooter(JsonNode.Parse("""{ "name": "k1", "url": "https://gw.example/sessions/k1" }""")!.AsObject()));
        Assert.Equal("AI session: k1", VerbContext.SessionFooter(JsonNode.Parse("""{ "name": "k1", "url": "" }""")!.AsObject()));
        Assert.Equal("AI session: k1", VerbContext.SessionFooter(JsonNode.Parse("""{ "name": "k1", "url": "https://x/y?token=abc" }""")!.AsObject()));   // a token never travels in a link
        Assert.Equal("", VerbContext.SessionFooter(JsonNode.Parse("""{ "name": "bad name" }""")!.AsObject()));
        Assert.Equal("", VerbContext.SessionFooter(null));
    }
}
