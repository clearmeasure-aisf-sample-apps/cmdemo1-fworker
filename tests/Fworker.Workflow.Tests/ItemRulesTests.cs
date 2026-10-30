using System.Text.Json.Nodes;
using Fworker.Workflows.WorkItem.Verbs;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.2 (#354): the small rules the item verbs share, and the PowerShell semantics they keep (Ps): what `[int]`, `"$x"`, `if ($x)` and a hashtable key make of a value, and the rules of the item state (the park, the claim's
/// lease, the settle step, the docs-only test, the criteria a review cites) against the scripts' own functions.
/// </summary>
public class ItemRulesTests
{
    static readonly DateTime T = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    static ItemVerbConfig Cfg(Func<string, string?>? env = null) =>
        ItemVerbConfig.From(JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "config", "instance.json"))), env ?? (_ => null));

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json")) && Directory.Exists(Path.Combine(d.FullName, "gateway"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json, Ps.CaseBlind)!;

    // ---- PowerShell semantics ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("12", 12)]
    [InlineData("12.5", 12)]      // [int]'12.5' rounds to even
    [InlineData("13.5", 14)]
    [InlineData(" 7 ", 7)]
    [InlineData("", 0)]
    public void IntOfAText_IsTheCastOfTheScript(string text, int expected) => Assert.Equal(expected, Ps.Int(JsonValue.Create(text)));

    [Fact]
    public void IntOfAWordIsTheErrorTheScriptDiesOf_AndNumbersAreTheirOwn()
    {
        Assert.Throws<FormatException>(() => Ps.Int(JsonValue.Create("abc")));
        Assert.Equal(5, Ps.Int(JsonNode.Parse("5")));
        Assert.Equal(5, Ps.Int(JsonNode.Parse("5.4")));
        Assert.Equal(5, Ps.Int(JsonValue.Create(5)));
        Assert.Equal(1, Ps.Int(JsonValue.Create(true)));
        Assert.Equal(0, Ps.Int(null));
    }

    [Fact]
    public void TheTextOfAValue_IsWhatDoubleQuotesMakeOfIt()
    {
        Assert.Equal("", Ps.Text(null));
        Assert.Equal("True", Ps.Text(JsonValue.Create(true))); Assert.Equal("False", Ps.Text(JsonValue.Create(false)));
        Assert.Equal("3", Ps.Text(JsonValue.Create(3))); Assert.Equal("3", Ps.Text(JsonNode.Parse("3"))); Assert.Equal("1.5", Ps.Text(JsonNode.Parse("1.5")));
        Assert.Equal("x", Ps.Text(JsonValue.Create("x")));
        Assert.Equal("""{"a":1}""", Ps.Text(JsonNode.Parse("""{"a":1}""")));
    }

    [Fact]
    public void Truthiness_IsThatOfAnIf_ATextThatSaysFalseIsTrue()
    {
        Assert.False(Ps.Truthy(null)); Assert.False(Ps.Truthy(JsonValue.Create(false))); Assert.False(Ps.Truthy(JsonValue.Create(0))); Assert.False(Ps.Truthy(JsonNode.Parse("0"))); Assert.False(Ps.Truthy(JsonValue.Create("")));
        Assert.True(Ps.Truthy(JsonValue.Create(true))); Assert.True(Ps.Truthy(JsonValue.Create("false"))); Assert.True(Ps.Truthy(JsonValue.Create(2))); Assert.True(Ps.Truthy(JsonNode.Parse("[1,2]")));
        Assert.False(Ps.Truthy(JsonNode.Parse("[]"))); Assert.True(Ps.Truthy(JsonNode.Parse("""{"a":1}""")));
    }

    [Fact]
    public void TestHas_IsTheKeyBeingThereAndNotNull_WithoutRegardToCase()
    {
        var o = O("""{"Item":1,"gone":null,"column":"Development"}""");
        Assert.True(Ps.Has(o, "item")); Assert.True(Ps.Has(o, "COLUMN")); Assert.False(Ps.Has(o, "gone")); Assert.False(Ps.Has(o, "missing")); Assert.False(Ps.Has(null, "x"));
        Assert.True(Ps.Eq("Development", "development")); Assert.False(Ps.Eq("Development", "Develop")); Assert.True(Ps.Eq(null, ""));
        Assert.True(Ps.NameIn("technical design", ["Technical Design", "Development"]));
    }

    [Fact]
    public void AColumnKeyedTable_IsReadAndWrittenWithoutRegardToCase()
    {
        var t = new JsonObject { ["Technical Design"] = "a" };
        Assert.Equal("a", Ps.CiGet(t, "technical design")!.ToString());
        Ps.CiSet(t, "TECHNICAL DESIGN", "b");
        Assert.Single(t); Assert.Equal("b", Ps.CiGet(t, "Technical Design")!.ToString());
        Ps.CiRemove(t, "technical design");
        Assert.Empty(t); Assert.Null(Ps.CiGet(t, "x")); Assert.Null(Ps.CiGet(null, "x"));
    }

    [Fact]
    public void TheIsoTime_IsTheOneOfTheScripts()
    {
        Assert.Equal("2026-10-29T12:00:00.000Z", Ps.Iso(T));
        Assert.Equal(T.AddMilliseconds(250), Ps.FromIso(JsonValue.Create("2026-10-29T12:00:00.250Z")));
        Assert.Equal(T, Ps.FromIso(JsonValue.Create("2026-10-29T12:00:00")));      // no zone: read as UTC
        Assert.Null(Ps.FromIso(null)); Assert.Null(Ps.FromIso(JsonValue.Create("")));
    }

    // ---- the park, the lease, the flow ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AParkEnds_WhenItsUntilPasses()
    {
        var s = new JsonObject();
        Assert.Null(ItemRules.GetPark(s, T));
        ItemRules.SetPark(s, "x", 60, T);
        Assert.Equal("x", ItemRules.GetPark(s, T.AddMinutes(59))!["reason"]!.ToString());
        Assert.Null(ItemRules.GetPark(s, T.AddMinutes(61)));
        Assert.Equal("2026-10-29T13:00:00.000Z", s["park"]!["until"]!.ToString());
        s["park"]!["until"] = null;   // no end: the park stands
        Assert.NotNull(ItemRules.GetPark(s, T.AddDays(9)));
    }

    [Fact]
    public void AClaimIsLive_UntilItsLeaseEnds_AndIsOwnedWhenTheOwnerFlagIsTrue()
    {
        var s = O("""{"claim":{"holder":"a","lease_until":"2026-10-29T12:30:00.000Z"}}""");
        Assert.True(ItemRules.ClaimLive(s, T)); Assert.False(ItemRules.ClaimLive(s, T.AddMinutes(31))); Assert.False(ItemRules.OwnedClaim(s));
        s["claim"]!["owner"] = true;
        Assert.True(ItemRules.OwnedClaim(s));
        Assert.False(ItemRules.ClaimLive(new JsonObject(), T));
        var bot = O("""{"bot":{"lease_until":"2026-10-29T12:30:00.000Z"}}""");
        Assert.True(ItemRules.BotLive(bot, T)); Assert.False(ItemRules.BotLive(bot, T.AddHours(1)));
    }

    [Fact]
    public void TheFlowKeepsTheLastFifteenStages_AndTheStageTheItemIsInIsNotAddedAgain()
    {
        var s = new JsonObject();
        ItemRules.AddFlowStamp(s, "Development", T); ItemRules.AddFlowStamp(s, "Development", T.AddMinutes(1));
        Assert.Single(s["flow"]!.AsArray());
        for (var i = 0; i < 20; i++) ItemRules.AddFlowStamp(s, i % 2 == 0 ? "Technical Design" : "Development", T.AddMinutes(i + 2));
        Assert.Equal(ItemRules.FlowKeep, s["flow"]!.AsArray().Count);
        Assert.Equal("Development", s["flow"]![14]!["c"]!.ToString());
    }

    [Fact]
    public void MoveOwnerTo_GivesTheOwnerTheNextColumnWithAFreshLease_AndForgetsTheTakeBackMarks()
    {
        var s = O("""{"claim":{"holder":"a","owner":true,"column":"Technical Design","session_launches":2,"taken_back":"x"},"reviews":{"Development":{"round":2}}}""");
        ItemRules.MoveOwnerTo(s, "Development", Cfg(), T);
        var c = s["claim"]!;
        Assert.Equal("Development", c["column"]!.ToString()); Assert.Equal("2026-10-29T12:30:00.000Z", c["lease_until"]!.ToString()); Assert.Equal("2026-10-29T12:00:00.000Z", c["heartbeat"]!.ToString());
        Assert.Null(c["session_launches"]); Assert.Null(c["taken_back"]);
        Assert.Equal("2026-10-29T12:00:00.000Z", s["dispatched_at"]!["Development"]!.ToString()); Assert.Null(s["reviews"]!["Development"]);
    }

    // ---- the settle step -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RestoreSettle_ArmsTheStepTheRecordSaysTheItemWasIn()
    {
        var cfg = Cfg();
        Assert.Equal("", ItemRules.RestoreSettle(new JsonObject(), cfg, T));                                                         // no merge record
        Assert.Equal("release-queue", ItemRules.RestoreSettle(O("""{"merge":{"sha":"a","release":"1.0.1"}}"""), cfg, T));
        var green = O("""{"merge":{"sha":"a","green_at":"2026-10-29T11:00:00.000Z"}}""");
        Assert.Equal("release-candidate", ItemRules.RestoreSettle(green, cfg, T)); Assert.Equal("2026-10-29T12:20:00.000Z", green["merge"]!["rc_deadline"]!.ToString());
        var timeout = O("""{"merge":{"sha":"a"},"park":{"reason":"master-green-timeout","until":"2026-10-30T12:00:00.000Z"}}""");
        Assert.Equal("master-timeout", ItemRules.RestoreSettle(timeout, cfg, T));
        var plain = O("""{"merge":{"sha":"a"}}""");
        Assert.Equal("master-broken", ItemRules.RestoreSettle(plain, cfg, T)); Assert.Equal("2026-10-29T12:00:00.000Z", plain["merge"]!["rearmed_at"]!.ToString());
        Assert.Equal("", ItemRules.RestoreSettle(plain, cfg, T));                                                                      // already waiting
        var done = O("""{"merge":{"sha":"a","done_at":"2026-10-29T11:00:00.000Z"}}""");
        Assert.Equal("", ItemRules.RestoreSettle(done, cfg, T));                                                                       // done: only with force
        Assert.Equal("master-broken", ItemRules.RestoreSettle(done, cfg, T, force: true)); Assert.Null(done["merge"]!["done_at"]);
    }

    // ---- docs-only, criteria ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("docs/design/x.md", true)]
    [InlineData("docs/img/a.png", true)]
    [InlineData("README.md", true)]
    [InlineData("guide/x.md", true)]
    [InlineData("src/App.cs", false)]
    [InlineData("src/notes.md", false)]          // markdown under src is code's
    [InlineData("CLAUDE.md", false)]
    [InlineData("docs/CLAUDE.md", true)]         // the first pattern is docs/*.md
    [InlineData("plugin/skills/x.md", false)]
    public void DocsOnlyFiles_AreTheConfiguredPatterns(string file, bool docs) => Assert.Equal(docs, ItemRules.DocsOnlyFiles([file], Cfg()));

    [Fact]
    public void DocsOnlyFiles_NeedAllFilesToMatch_AndAtLeastOne()
    {
        var cfg = Cfg();
        Assert.True(ItemRules.DocsOnlyFiles(["docs/a.md", "README.md"], cfg)); Assert.False(ItemRules.DocsOnlyFiles(["docs/a.md", "src/a.cs"], cfg)); Assert.False(ItemRules.DocsOnlyFiles([], cfg));
    }

    [Fact]
    public void AnItemIsDocsOnly_ByWhatTheDesignRecorded_TheBodyMarkerCountsOnlyForAnOlderItemPastItsDesign()
    {
        var cfg = Cfg(); const string marker = "<!-- aisf:docs-only -->";
        ItemInfo Info(string column, string created) => new(1, "I_1", true, "t", $"text {marker}", "u", created, [], null, column);
        Assert.True(ItemRules.ItemDocsOnly(O("""{"design":{"docs_only":true}}"""), Info("Development", "2026-10-28T00:00:00Z"), cfg));
        Assert.False(ItemRules.ItemDocsOnly(O("""{"design":{"docs_only":false}}"""), Info("Development", "2019-01-01T00:00:00Z"), cfg));   // once recorded, the body is ignored
        Assert.False(ItemRules.ItemDocsOnly(new JsonObject(), null, cfg));
        Assert.False(ItemRules.ItemDocsOnly(new JsonObject(), Info("Technical Design", "2019-01-01T00:00:00Z"), cfg));                       // not past the design column
        Assert.False(ItemRules.ItemDocsOnly(new JsonObject(), Info("Development", "2026-10-28T00:00:00Z"), cfg));                           // newer than the cutoff
        Assert.True(ItemRules.ItemDocsOnly(new JsonObject(), Info("Development", "2019-01-01T00:00:00Z"), cfg));                             // an older item: the body marker
    }

    [Fact]
    public void TimingCriteria_AreTheBulletsAboveTheDesignWorded_InTimeOrStatistics()
    {
        var info = new ItemInfo(1, "I", true, "t", "- the list loads within 2 seconds\n- the p95 is under load\n* 99.9 % of calls succeed\n1. an average of five\n2) a plain criterion\n- [ ] checkbox with 3 ms\n\nnot a bullet: 5 seconds\n\n## Technical Design\n- 7 seconds below the heading", "u", "", [], null, "Technical Design");
        Assert.Equal(["the list loads within 2 seconds", "the p95 is under load", "99.9 % of calls succeed", "an average of five", "checkbox with 3 ms"], ItemRules.TimingCriteria(info, Cfg()));
    }

    [Fact]
    public void CitedCriteria_AreTheQuotesTheItemHoldsWordForWord_AtLeastEightCharacters()
    {
        var info = new ItemInfo(1, "I", true, "t", "- The work order list shows the room number.\n- short\n\n## Technical Design\n- design says: the list loads", "u", "", [], null, "Technical Design");
        var cfg = Cfg();
        Assert.Equal(["The work order list shows the room number."], ItemRules.CitedCriteria(info, "FINDING: criterion \"The work order list   shows the room number.\" - gap; criterion \"The work order list shows the room number.\" again", cfg));
        Assert.Empty(ItemRules.CitedCriteria(info, "criterion \"short\" - too short; criterion \"the list loads\" - only in the design; criterion \"not in the item at all\"", cfg));
        // a criterion that itself holds quotes: every closing quote is tried, shortest first, and the first one the item holds is the criterion
        var q = new ItemInfo(1, "I", true, "t", "- The button reads \"Save order\" in bold.", "u", "", [], null, "Technical Design");
        Assert.Equal(["The button reads"], ItemRules.CitedCriteria(q, "criterion \"The button reads \"Save order\" in bold.\" - no bold", cfg));
    }

    [Fact]
    public void RepeatedCriteria_AreTheOnesTwoConsecutiveRoundsCite_APassingRoundClearsTheSource()
    {
        var s = new JsonObject();
        Assert.Empty(ItemRules.RepeatedCriteria(s, "review|Technical Design", ["a long enough criterion"]));
        Assert.Equal(["A LONG ENOUGH CRITERION"], ItemRules.RepeatedCriteria(s, "review|technical design", ["A LONG ENOUGH CRITERION", "another one"]));   // (the source and the text are compared without case; the answer is the round's own text)
        Assert.Empty(ItemRules.RepeatedCriteria(s, "review|Technical Design", [], clear: true));
        Assert.Empty(ItemRules.RepeatedCriteria(s, "review|Technical Design", ["a long enough criterion"]));
    }

    [Fact]
    public void TheConfiguration_IsReadAsTheScriptsReadIt_TheTimingOverrideOnlyForAKeyItHas()
    {
        var plain = Cfg();
        Assert.Equal(30, plain.LeaseMinutes); Assert.Equal(1440, plain.ParkMinutes); Assert.Equal(3, plain.MaxRelaunches); Assert.Equal(10, plain.MaxSessionRelaunches); Assert.Equal(2, plain.MaxDemotions);
        Assert.Equal("factory-worker", plain.Name); Assert.Equal("master", plain.DefaultBranch); Assert.Equal(["Technical Design", "Development", "Functional Testing"], plain.ColumnsAi); Assert.Equal("## Technical Design", plain.SectionOf("technical design"));
        Assert.Null(plain.SectionOf("Development")); Assert.Equal(["hold:owner"], plain.HoldLabels); Assert.Equal("hold:owner", plain.HoldLabelOf(["Auto Merge", "HOLD:OWNER"]));
        var env = new Dictionary<string, string?> { ["FACTORY_TIMING_leaseMinutes"] = "0.5", ["FACTORY_TIMING_notInTheFile"] = "7" };
        var t = Cfg(n => env.GetValueOrDefault(n));
        Assert.Equal(0.5, t.LeaseMinutes); Assert.Null(t.TimingOrNull("notInTheFile"));
        Assert.True(plain.InScope(["a"]));
        var scoped = ItemVerbConfig.From(plain.Root, _ => null, "env:tdd");
        Assert.True(scoped.InScope(["env:tdd", "x"])); Assert.False(scoped.InScope(["x"])); Assert.False(scoped.InScope(["env:tdd", "env:uat"])); Assert.False(scoped.InScope(["env:uat"]));
    }

    [Fact]
    public void ThePullRequestBody_EndsWithTheReferenceToTheItemOnce()
    {
        Assert.Equal("text\n\nRefs #5", ItemVerbs.PrBodyText("text", 5));
        Assert.Equal("text\n\nRefs #5", ItemVerbs.PrBodyText("text\n\nRefs #5\n", 5));
        Assert.Equal("Refs #5\n\nRefs #6", ItemVerbs.PrBodyText("Refs #5", 6));
        Assert.Equal("\n\nRefs #5", ItemVerbs.PrBodyText("", 5));
    }
}
