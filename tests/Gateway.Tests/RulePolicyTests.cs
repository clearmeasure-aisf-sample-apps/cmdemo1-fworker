using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// MetricsAccessPolicy, PresencePolicy, ReleaseRecordPolicy and DerivedTextRule (task C8): the rules that were bodies of endpoint filters and handlers,
/// now pure and tested without a host. The texts and statuses are the ones the endpoints answered before C8 (the endpoint pins of C7a to C7d stay the
/// proof that the endpoints still answer them).
/// </summary>
public class MetricsAccessPolicyTests
{
    static PolicyRefusal? Check(string kind, string name = "x", bool owner = true, bool managerOn = true, string supervisor = "pm", string keeper = "kay", string fresh = "", string max = "", string since = "") =>
        MetricsAccessPolicy.Check(kind, name, owner, managerOn, () => supervisor, () => keeper, fresh, max, since);

    [Fact]
    public void Admin_ReadsAnyQuery() => Assert.Null(Check("admin", fresh: "1", max: "300", since: "2026-01-01"));

    [Fact]
    public void AnyOtherKind_GetsABare403()
    {
        foreach (var kind in new[] { "worker", "item", "role", "qc-click", "operator" })
        {
            var r = Check(kind)!;
            Assert.True(r.IsBare); Assert.Equal(403, r.Status);
        }
    }

    [Fact]
    public void ActiveSupervisor_ReadsTheTwoWindowsOnly()
    {
        Assert.Null(Check("manager", "pm"));
        Assert.Null(Check("manager", "pm", since: "24h"));
        Assert.Null(Check("manager", "pm", since: "7d"));
        foreach (var (f, m, s) in new[] { ("1", "", ""), ("", "50", ""), ("", "", "30m"), ("", "", "2026-01-01") })
        {
            var r = Check("manager", "pm", fresh: f, max: m, since: s)!;
            Assert.Equal((400, "usage", 2), (r.Status, r.Code, r.Exit));
            Assert.Equal("the supervisor reads since=24h or since=7d (no fresh, no max)", r.Reason);
        }
        // fresh=0 is not fresh
        Assert.Null(Check("manager", "pm", fresh: "0"));
    }

    [Fact]
    public void ASupervisorThatIsNotActive_OrWithoutOwnerModeOrTheManager_IsRefused()
    {
        var expected = (403, "not-supervisor", 30);
        foreach (var r in new[] { Check("manager", "someone"), Check("manager", "pm", owner: false), Check("manager", "pm", managerOn: false) })
            Assert.Equal(expected, (r!.Status, r.Code, r.Exit));
        Assert.Equal("this credential is not the active supervisor: register with subscribe --own --supervisor first", Check("manager", "someone")!.Reason);
    }

    [Fact]
    public void TheKeeper_ReadsTheTwoWindowsOnly_AndOnlyWhenActive()
    {
        Assert.Null(Check("keeper", "kay", since: "7d"));
        var other = Check("keeper", "nobody")!;
        Assert.Equal((403, "not-keeper", 30), (other.Status, other.Code, other.Exit));
        Assert.Equal((403, "not-keeper"), (Check("keeper", "kay", owner: false)!.Status, Check("keeper", "kay", owner: false)!.Code));
        var wide = Check("keeper", "kay", max: "5")!;
        Assert.Equal((400, "usage", "the backlog keeper reads since=24h or since=7d (no fresh, no max)"), (wide.Status, wide.Code, wide.Reason));
    }

    [Fact]
    public void ThePlantsSlotsAreReadOnlyForTheCredentialThatNeedsThem()
    {
        var called = new List<string>();
        MetricsAccessPolicy.Check("admin", "a", true, true, () => { called.Add("sup"); return ""; }, () => { called.Add("keeper"); return ""; }, "", "", "");
        MetricsAccessPolicy.Check("worker", "a", true, true, () => { called.Add("sup"); return ""; }, () => { called.Add("keeper"); return ""; }, "", "", "");
        Assert.Empty(called);
    }

    [Theory]
    [InlineData("", "", false, null)]
    [InlineData("24h", "", false, null)]
    [InlineData("7d", "", false, null)]
    [InlineData("30m", "", false, "x")]
    [InlineData("", "10", false, "x")]
    [InlineData("", "", true, "x")]
    public void OperatorQueryError_AllowsTheTwoWindowsWithNoMaxAndNoFresh(string since, string max, bool freshPresent, string? error) =>
        Assert.Equal(error != null, MetricsAccessPolicy.OperatorQueryError(since, max, freshPresent) != null);

    [Fact]
    public void OperatorQueryError_IsTheTextTheEndpointAnswered() =>
        Assert.Equal("the operator session reads the metrics with since=24h or 7d (or none), no max and no fresh: answers come from a 120 s cache and a miss computes them", MetricsAccessPolicy.OperatorQueryError("1d", "", false));
}

public class PresencePolicyTests
{
    static (PresencePolicy.Verdict Verdict, PolicyRefusal? Refusal) Caller(string kind, string name = "olga", string role = "", bool operatorActive = false, string keeper = "kay", string supervisor = "pm", bool allowed = true) =>
        PresencePolicy.CheckCaller(kind, name, role, operatorActive, () => keeper, () => supervisor, _ => allowed);

    [Fact]
    public void TheActiveKeeperAndOperatorSession_AreOnline_WithoutABody()
    {
        Assert.Equal(PresencePolicy.Verdict.Online, Caller("keeper", "kay").Verdict);
        Assert.Equal(PresencePolicy.Verdict.Online, Caller("operator", "ops", operatorActive: true).Verdict);
    }

    [Fact]
    public void AKeeperOrOperatorThatIsNotActive_IsRefused()
    {
        var k = Caller("keeper", "other");
        Assert.Equal(PresencePolicy.Verdict.Refused, k.Verdict); Assert.Equal((403, "not-keeper"), (k.Refusal!.Status, k.Refusal.Code));
        var o = Caller("operator", "ops");
        Assert.Equal((403, "not-operator", 30), (o.Refusal!.Status, o.Refusal.Code, o.Refusal.Exit));
    }

    [Theory]
    [InlineData("admin")] [InlineData("role")] [InlineData("qc-click")]
    public void OtherKinds_SendNoPresence(string kind)
    {
        var r = Caller(kind);
        Assert.Equal((PresencePolicy.Verdict.Refused, 403, "forbidden"), (r.Verdict, r.Refusal!.Status, r.Refusal.Code));
    }

    [Theory]
    [InlineData("owner", true)] [InlineData("inspector", true)] [InlineData("qc", false)] [InlineData("", false)]
    public void AnItemToken_NeedsTheRoleOfAnOwnerOrAnInspector(string role, bool ok)
    {
        var r = Caller("item", role: role);
        Assert.Equal(ok ? PresencePolicy.Verdict.Continue : PresencePolicy.Verdict.Refused, r.Verdict);
        if (!ok) Assert.Equal("role-cannot-send", r.Refusal!.Code);
    }

    [Fact]
    public void AWorkerOrItemName_MustBeInAllowedWorkers_ButTheSupervisorIsNot()
    {
        Assert.Equal("not-allowed", Caller("worker", allowed: false).Refusal!.Code);
        Assert.Equal("not-allowed", Caller("item", role: "owner", allowed: false).Refusal!.Code);
        Assert.Equal(PresencePolicy.Verdict.Continue, Caller("manager", "pm", allowed: false).Verdict);
        Assert.Equal(PresencePolicy.Verdict.Continue, Caller("worker").Verdict);
    }

    [Fact]
    public void TheSupervisor_MustBeTheActiveOne()
    {
        Assert.Equal("not-supervisor", Caller("manager", "second").Refusal!.Code);
        Assert.Equal(PresencePolicy.Verdict.Continue, Caller("manager", "pm").Verdict);
    }

    [Fact]
    public void CheckFields_RefusesAForgedSenderAndUnknownFields()
    {
        Assert.Null(PresencePolicy.CheckFields((JsonObject)JsonNode.Parse("""{"summary":"x","progress":[]}""")!));
        Assert.Null(PresencePolicy.CheckFields(new JsonObject()));
        foreach (var f in new[] { "from", "sender", "holder", "fromKind", "role" })
        {
            var r = PresencePolicy.CheckFields(new JsonObject { [f] = "x" })!;
            Assert.Equal((403, "forged-sender", 30), (r.Status, r.Code, r.Exit));
        }
        var u = PresencePolicy.CheckFields(new JsonObject { ["mood"] = "x" })!;
        Assert.Equal((400, "usage", "unknown field 'mood'"), (u.Status, u.Code, u.Reason));
    }

    static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ParseProgress_ReadsTheEntriesWithTheirAges()
    {
        var (list, bad) = PresencePolicy.ParseProgress(JsonNode.Parse("""[{"item":7,"ageSeconds":30,"source":"commit"},{"item":9,"ageSeconds":0.5,"source":"work"}]""")!, false, Now);
        Assert.Null(bad);
        Assert.Equal([(7, Now.AddSeconds(-30), "commit"), (9, Now.AddSeconds(-0.5), "work")], list);
    }

    [Theory]
    [InlineData("""{"item":7}""")]
    [InlineData("""[{"item":0,"ageSeconds":1,"source":"commit"}]""")]
    [InlineData("""[{"item":7,"ageSeconds":-1,"source":"commit"}]""")]
    [InlineData("""[{"item":7,"ageSeconds":86401,"source":"commit"}]""")]
    [InlineData("""[{"item":7,"ageSeconds":"x","source":"commit"}]""")]
    [InlineData("""[{"item":7,"ageSeconds":1,"source":"telepathy"}]""")]
    [InlineData("""[5]""")]
    public void ParseProgress_RefusesABadEntry(string json)
    {
        var (list, bad) = PresencePolicy.ParseProgress(JsonNode.Parse(json)!, false, Now);
        Assert.Null(list); Assert.Equal((400, "usage"), (bad!.Status, bad.Code));
    }

    [Fact]
    public void ParseProgress_RefusesMoreThanTwentyEntries_AndAnyForTheSupervisor()
    {
        var many = new JsonArray(Enumerable.Range(1, 21).Select(i => (JsonNode?)new JsonObject { ["item"] = i, ["ageSeconds"] = 1, ["source"] = "work" }).ToArray());
        Assert.Equal("progress is an array of at most 20 { item, ageSeconds, source }", PresencePolicy.ParseProgress(many, false, Now).Refusal!.Reason);
        var twenty = new JsonArray(Enumerable.Range(1, 20).Select(i => (JsonNode?)new JsonObject { ["item"] = i, ["ageSeconds"] = 1, ["source"] = "work" }).ToArray());
        Assert.Equal(20, PresencePolicy.ParseProgress(twenty, false, Now).Progress!.Count);
        Assert.Equal("the supervisor owns no item: progress is sent by an owner's client", PresencePolicy.ParseProgress(twenty, true, Now).Refusal!.Reason);
    }

    [Fact]
    public void CheckSummary_AcceptsTextOfAtMost140Characters()
    {
        Assert.Null(PresencePolicy.CheckSummary(JsonValue.Create(new string('x', 140))!, out var t)); Assert.Equal(140, t.Length);
        var long141 = PresencePolicy.CheckSummary(JsonValue.Create(new string('x', 141))!, out _)!;
        Assert.Equal((413, "too-large", "summary is 140 characters at most"), (long141.Status, long141.Code, long141.Reason));
        var notText = PresencePolicy.CheckSummary(JsonValue.Create(5)!, out _)!;
        Assert.Equal((400, "usage", "summary is text"), (notText.Status, notText.Code, notText.Reason));
    }
}

public class ReleaseRecordPolicyTests
{
    [Theory]
    [InlineData("1.0.31", true)] [InlineData("123456.123456789.1.2", true)] [InlineData("1.0.31.4.5", false)] [InlineData("1234567.0", false)]
    [InlineData("1", false)] [InlineData("v1.0.1", false)] [InlineData("1.0.1\n", false)] [InlineData("", false)] [InlineData("1..2", false)]
    public void ValidVersion_IsDigitsAndDots(string v, bool ok)
    {
        Assert.Equal(ok, ReleaseRecordPolicy.ValidVersion(v));
        Assert.Equal(ok, Plant.ValidVersion(v));   // the plant's own check is this one
    }

    [Fact]
    public void ParseBody_IsExactlyPrAndVersion()
    {
        Assert.Equal((422, "1.0.31"), ReleaseRecordPolicy.ParseBody((JsonObject)JsonNode.Parse("""{"pr":422,"version":"1.0.31"}""")!));
        foreach (var bad in new[]
        {
            """{"pr":422}""", """{"version":"1.0.31"}""", """{"pr":0,"version":"1.0.31"}""", """{"pr":1000000000,"version":"1.0.31"}""",
            """{"pr":"422","version":"1.0.31"}""", """{"pr":422,"version":"latest"}""", """{"pr":422,"version":"1.0.31","extra":1}""", """{"pr":4.5,"version":"1.0.31"}""", "{}"
        })
            Assert.Null(ReleaseRecordPolicy.ParseBody((JsonObject)JsonNode.Parse(bad)!));
        Assert.Equal((1, "1.0"), ReleaseRecordPolicy.ParseBody((JsonObject)JsonNode.Parse("""{"pr":1,"version":"1.0"}""")!));
        Assert.Equal((999_999_999, "1.0"), ReleaseRecordPolicy.ParseBody((JsonObject)JsonNode.Parse("""{"pr":999999999,"version":"1.0"}""")!));
    }

    [Fact]
    public void CheckOn_RefusesWhenRevocationsAreOff()
    {
        Assert.Null(ReleaseRecordPolicy.CheckOn(false));
        var r = ReleaseRecordPolicy.CheckOn(true)!;
        Assert.Equal((403, "releases-off", 30), (r.Status, r.Code, r.Exit));
        Assert.Equal("this gateway runs with FACTORY_TEST_SKIP_REVOCATIONS: it cannot record a release", r.Reason);
    }

    [Fact]
    public void WriteAllowed_CapsNewRecordsOfTheDay_ButNeverARepeat()
    {
        Assert.Equal(20, ReleaseRecordPolicy.WritesPerDay); Assert.Equal(ReleaseRecordPolicy.WritesPerDay, Plant.ReleaseWritesPerDay);
        Assert.True(ReleaseRecordPolicy.WriteAllowed(false, 0));
        Assert.True(ReleaseRecordPolicy.WriteAllowed(false, 19));
        Assert.False(ReleaseRecordPolicy.WriteAllowed(false, 20));
        Assert.False(ReleaseRecordPolicy.WriteAllowed(false, 99));
        Assert.True(ReleaseRecordPolicy.WriteAllowed(true, 99));
    }
}

public class DerivedTextRuleTests
{
    static readonly DerivedTextRule.MessageParty Known = (id, session) => id == "m-0123abcd-0001" && session == "olga" ? (true, "worker:peer", "wanda", "") : (false, "", "", "");
    static readonly DerivedTextRule.MessageParty Answer = (id, session) => (true, "supervisor", "pm", "m-aaaaaaaa-0002");
    static JsonObject Body(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory]
    [InlineData("append-body", "content")] [InlineData("comment", "body")]
    public void TheTextFieldIsContentForAppendBodyAndBodyForComment(string verb, string field) => Assert.Equal(field, DerivedTextRule.FieldOf(verb));

    [Fact]
    public void WithoutFromMessage_PlainTextGoesOn_AndTheReservedMarkerIsRefused()
    {
        var b = Body("""{"body":"just a comment"}""");
        Assert.Null(DerivedTextRule.Apply("comment", b, "item", "olga", Known));
        Assert.Equal("""{"body":"just a comment"}""", b.ToJsonString());
        var forged = DerivedTextRule.Apply("comment", Body("""{"body":"<!-- AISF:ASSUMPTION source=x -->"}"""), "item", "olga", Known)!;
        Assert.Equal((409, "reserved-marker", 10), (forged.Status, forged.Code, forged.Exit));
        Assert.Null(DerivedTextRule.Apply("comment", Body("""{"body":"x","from_message":null}"""), "item", "olga", Known));
        Assert.Null(DerivedTextRule.Apply("comment", Body("{}"), "item", "olga", Known));
    }

    [Fact]
    public void FromMessage_BecomesOneMarkedQuotedBlock_AndTheKeyIsRemoved()
    {
        var b = Body("""{"item":7,"body":"we `decided`\r\nto go <!-- sneaky --> on","from_message":"m-0123abcd-0001"}""");
        Assert.Null(DerivedTextRule.Apply("comment", b, "item", "olga", Known));
        Assert.False(b.ContainsKey("from_message"));
        Assert.Equal("<!-- aisf:assumption source=worker:peer from=wanda -->\n> we decided\n> to go  sneaky  on\n<!-- /aisf:assumption -->", b["body"]!.ToString());
        Assert.Equal(7, b["item"]!.GetValue<int>());
    }

    [Fact]
    public void AnAnswer_NamesTheQuestionItAnswers_AndAppendBodyUsesContent()
    {
        var b = Body("""{"content":"the answer","from_message":"m-0123abcd-0001"}""");
        Assert.Null(DerivedTextRule.Apply("append-body", b, "item", "olga", Answer));
        Assert.Equal("<!-- aisf:assumption source=supervisor from=pm in_reply_to=m-aaaaaaaa-0002 -->\n> the answer\n<!-- /aisf:assumption -->", b["content"]!.ToString());
    }

    [Fact]
    public void TheTextIsCappedAt500Characters_AndMultilineQuotedPerLine()
    {
        var b = Body("{\"body\":\"" + new string('x', 600) + "\\nsecond\",\"from_message\":\"m-0123abcd-0001\"}");
        Assert.Null(DerivedTextRule.Apply("comment", b, "item", "olga", Known));
        var lines = b["body"]!.ToString().Split('\n');
        Assert.Equal(500 + 2, lines[1].Length);   // "> " and 500 characters
        Assert.Equal(3, lines.Length);
    }

    [Theory]
    [InlineData("worker")] [InlineData("admin")] [InlineData("role")]
    public void FromMessage_NeedsAnItemToken(string kind)
    {
        var r = DerivedTextRule.Apply("comment", Body("""{"body":"x","from_message":"m-0123abcd-0001"}"""), kind, "olga", Known)!;
        Assert.Equal((400, "usage", 2), (r.Status, r.Code, r.Exit));
    }

    [Theory]
    [InlineData("""{"from_message":"m-0123abcd-0001"}""")]
    [InlineData("""{"body":5,"from_message":"m-0123abcd-0001"}""")]
    [InlineData("""{"body":"x","from_message":"not-an-id"}""")]
    [InlineData("""{"body":"x","from_message":"m-0123ABCD-0001"}""")]
    [InlineData("""{"body":"x","from_message":"m-0123abcd-001"}""")]
    public void FromMessage_NeedsTheTextAndAWellFormedId(string json)
    {
        var r = DerivedTextRule.Apply("comment", Body(json), "item", "olga", Known)!;
        Assert.Equal((400, "usage"), (r.Status, r.Code));
    }

    [Fact]
    public void AMessageTheSessionNeitherSentNorReceived_IsRefused()
    {
        var r = DerivedTextRule.Apply("comment", Body("""{"body":"x","from_message":"m-0123abcd-0001"}"""), "item", "mallory", Known)!;
        Assert.Equal((404, "no-such-message", 12), (r.Status, r.Code, r.Exit));
    }

    [Fact]
    public void TextThatIsNothingAfterCleaning_IsRefused()
    {
        var r = DerivedTextRule.Apply("comment", Body("""{"body":"`` <!-- --> ","from_message":"m-0123abcd-0001"}"""), "item", "olga", Known)!;
        Assert.Equal((400, "usage", "there is no text left to write: send the assumption or the decision in your own words"), (r.Status, r.Code, r.Reason));
    }
}
