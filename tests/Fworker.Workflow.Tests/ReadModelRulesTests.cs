using System.Text.Json.Nodes;
using Fworker.Domain;
using Fworker.Workflows.ReadModels;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.5 (#357): the rules of the read models that need no GitHub, pinned against what plugin/scripts/lib/Prs.ps1 (Get-ChecksFor), Checks.ps1, Models.ps1 (Get-TierFor) and Verbs.Pr.ps1 (Add-ChecksRed) decide, with the cases the Tier 1
/// scenarios (tests/scenarios/checks.ps1, cirule.ps1, scenarios.ps1) and the smoke test put to them; and the PowerShell semantics the ports lean on.
/// </summary>
public sealed class ReadModelRulesTests
{
    static JsonNode Run(string name, string status = "completed", string? conclusion = "success", string started = "2026-01-01T10:00:00Z") =>
        new JsonObject { ["name"] = name, ["status"] = status, ["conclusion"] = conclusion, ["started_at"] = started };

    static readonly string[] Gates = ["Build result", "secret-scan"];

    // ---- Get-ChecksFor ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AHeadWithNoCheckRuns_IsPending_NeverGreen()
    {
        var c = CheckRules.Classify([], Gates);
        Assert.Equal("pending", c.Status);
        Assert.Equal(0, c.Total);
        Assert.Equal([("Build result", "absent"), ("secret-scan", "absent")], c.Gates.Select(g => (g.Key, g.Value)));
        Assert.Equal("pending", CheckRules.Classify([], []).Status);
    }

    [Fact]
    public void EveryGateSuccessful_IsSuccess_AFailedGateIsFailure_APendingOrAbsentGateIsPending()
    {
        Assert.Equal("success", CheckRules.Classify([Run("Build result"), Run("secret-scan"), Run("Detect code changes", conclusion: "skipped")], Gates).Status);
        var red = CheckRules.Classify([Run("Build result", conclusion: "failure"), Run("secret-scan")], Gates);
        Assert.Equal(("failure", 1, 2), (red.Status, red.Failed, red.Total));
        Assert.Equal(["Build result"], red.FailedChecks);
        Assert.Equal("pending", CheckRules.Classify([Run("Build result", "in_progress", null), Run("secret-scan")], Gates).Status);
        Assert.Equal("pending", CheckRules.Classify([Run("Build result")], Gates).Status);   // secret-scan absent
        Assert.Equal("failure", CheckRules.Classify([Run("Build result", "in_progress", null), Run("secret-scan", conclusion: "timed_out")], Gates).Status);   // a failed gate beats a pending one
    }

    [Fact]
    public void AFailedCheckThatIsNoGate_IsListedAndNeverMakesTheHeadRed_IssueOneHundredAndTwo()
    {
        var c = CheckRules.Classify([Run("Build result"), Run("secret-scan"), Run("QDNETC", conclusion: "failure"), Run("Other", conclusion: "cancelled")], Gates);
        Assert.Equal("success", c.Status);
        Assert.Equal(["QDNETC"], c.FailedChecks); Assert.Equal(["QDNETC"], c.NongateFailed); Assert.Equal(["Other"], c.PendingChecks); Assert.Equal(["Other"], c.NongateCancelled);
        // without configured gates any failed check is a failure
        Assert.Equal("failure", CheckRules.Classify([Run("Build result"), Run("QDNETC", conclusion: "failure")], []).Status);
        Assert.Equal("pending", CheckRules.Classify([Run("Build result"), Run("x", "queued", null)], []).Status);
    }

    [Theory]
    [InlineData("failure", "failure")]
    [InlineData("timed_out", "failure")]
    [InlineData("action_required", "failure")]
    [InlineData("startup_failure", "failure")]
    [InlineData("cancelled", "pending")]
    [InlineData("neutral", "success")]
    [InlineData("skipped", "success")]
    [InlineData("success", "success")]
    public void TheConclusionsAreClassified(string conclusion, string status) =>
        Assert.Equal(status, CheckRules.Classify([Run("Build result", conclusion: conclusion), Run("secret-scan")], Gates).Status);

    [Fact]
    public void OfTwoRunsOfACheck_TheNewestStartedCounts_NamesWithoutRegardToCase()
    {
        var c = CheckRules.Classify([Run("Build result", conclusion: "failure", started: "2026-01-01T10:00:00Z"), Run("build RESULT", conclusion: "success", started: "2026-01-01T11:00:00Z"), Run("secret-scan")], Gates);
        Assert.Equal(("success", 2), (c.Status, c.Total));
        var older = CheckRules.Classify([Run("Build result", conclusion: "success", started: "2026-01-01T11:00:00Z"), Run("Build result", conclusion: "failure", started: "2026-01-01T10:00:00Z"), Run("secret-scan")], Gates);
        Assert.Equal("success", older.Status);
    }

    [Fact]
    public void IgnoredChecks_AreLeftOutOfTheVerdictAndAreNoGateEither_ButStayReadable()
    {
        var c = CheckRules.Classify([Run("Build result"), Run("secret-scan"), Run("release", conclusion: "skipped"), Run("architecture", conclusion: "failure")], Gates, ["release", "packages", "ARCHITECTURE"]);
        Assert.Equal(("success", 2, 0), (c.Status, c.Total, c.Failed));
        Assert.True(c.Runs.ContainsKey("architecture"));
        var gateIgnored = CheckRules.Classify([Run("Build result"), Run("secret-scan", conclusion: "failure")], Gates, ["secret-scan"]);
        Assert.Equal(("success", 1), (gateIgnored.Status, gateIgnored.Gates.Count));
    }

    [Fact]
    public void ThePublicChecks_AreTheFieldsTheScriptAnswers()
    {
        var p = CheckRules.Classify([Run("Build result", conclusion: "failure"), Run("secret-scan"), Run("Q", conclusion: "failure")], Gates).Public();
        Assert.Equal(["status", "total", "failed", "failed_checks", "pending_checks", "gates", "nongate_failed"], p.Select(kv => kv.Key));
        Assert.Equal("failure", p["gates"]!["Build result"]!.ToString());
        Assert.Equal(["Q"], p["nongate_failed"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal(0, p["pending_checks"]!.AsArray().Count);
    }

    // ---- the text of a check, a log or an artifact -----------------------------------------------------------------------------------------------

    static readonly IReadOnlyList<string> Secrets = JournalText.FallbackSecretPatterns;
    static readonly string Token = "ghp_" + new string('A', 30);

    [Fact]
    public void ProtectedText_HasNoColourNoControlCharactersNoDirectionMarksAndNoSecret()
    {
        var text = $"\u001b[31mred\u001b[0m line\r\nsecond\u0007 bell\u202e reversed\u200b zero token {Token}\tand a tab\u001b]0;title\u0007 done";
        var t = CheckRules.Protect(text, Secrets);
        Assert.Equal("red line\nsecond bell reversed zero token [redacted]\tand a tab done", t);
        Assert.Equal("", CheckRules.Protect(null, Secrets));
        Assert.Equal("", CheckRules.Protect("", Secrets));
        Assert.Equal("abcde [cut: 5 more characters]", CheckRules.Protect("abcdefghij", Secrets, 5));
        Assert.Equal("abcdefghij", CheckRules.Protect("abcdefghij", Secrets, 10));
    }

    [Fact]
    public void APageOfATextIsCutFromTheCleanedText_AndAPageOutOfRangeIsRefused()
    {
        var text = new string('x', CheckRules.PageChars * 2 + 10);
        var p = CheckRules.TextPage(text, null);
        Assert.Equal((1, 3, text.Length, CheckRules.PageChars), (p.Page, p.Pages, p.TotalChars, p.Text.Length));
        Assert.Equal(10, CheckRules.TextPage(text, "3").Text.Length);
        Assert.Equal((1, 1, ""), (CheckRules.TextPage("", "1").Page, CheckRules.TextPage("", null).Pages, CheckRules.TextPage("", null).Text));
        var past = Assert.Throws<VerbRefusal>(() => CheckRules.TextPage(text, "4"));
        Assert.Equal((2, "page-out-of-range", "there are 3 pages; page 4 does not exist"), (past.Exit, past.Code, past.Message));
        Assert.Equal(3, past.Extra["pages"]!.GetValue<int>());
        Assert.Equal("page-out-of-range", Assert.Throws<VerbRefusal>(() => CheckRules.TextPage(text, "0")).Code);
        var bad = Assert.Throws<VerbRefusal>(() => CheckRules.TextPage(text, "two"));
        Assert.Equal((2, "usage", "page must be a number, from 1"), (bad.Exit, bad.Code, bad.Message));
    }

    [Fact]
    public void TheDiffMap_HoldsTheAddedLinesOfEveryFile_AndAnAnnotationIsSplitByWhereItPoints()
    {
        var files = new JsonNode[]
        {
            new JsonObject { ["filename"] = "src/App.cs", ["patch"] = "@@ -1,3 +1,5 @@\n context\n+added two\n+added three\n-removed\n context\n@@ -20,2 +22,3 @@\n+late\n x\n\\ No newline" },
            new JsonObject { ["filename"] = "docs/a.md", ["patch"] = null },
        };
        var map = CheckRules.DiffMap(files);
        Assert.Equal([2, 3, 22], map["src/App.cs"].Order());
        Assert.Empty(map["docs/a.md"]);
        Assert.True(map.ContainsKey("SRC/APP.CS"));
        JsonNode An(string path, int s, int e) => new JsonObject { ["path"] = path, ["start_line"] = s, ["end_line"] = e, ["annotation_level"] = "warning", ["title"] = "t", ["message"] = "m " + Token };
        var (counts, list) = CheckRules.Annotations([An("src/Legacy.cs", 7, 7), An("src/App.cs", 50, 51), An("src/App.cs", 1, 3), An("src/App.cs", 22, 22)], map, Secrets);
        Assert.Equal((4, 4, 2, 1, 1), (counts["total"]!.GetValue<int>(), counts["returned"]!.GetValue<int>(), counts["onChangedLines"]!.GetValue<int>(), counts["onChangedFiles"]!.GetValue<int>(), counts["elsewhere"]!.GetValue<int>()));
        Assert.Equal(["changed-line", "changed-line", "changed-file", "elsewhere"], list.Select(a => a!["where"]!.ToString()));   // the ones on the change first, in their order
        Assert.Equal([1, 22, 50, 7], list.Select(a => a!["line"]!.GetValue<int>()));
        Assert.DoesNotContain("ghp_A", list.ToJsonString());
        // no diff to compare with
        var (c2, l2) = CheckRules.Annotations([An("src/App.cs", 1, 1)], null, Secrets);
        Assert.Equal("unknown", l2[0]!["where"]!.ToString());
        Assert.Null(c2["onChangedLines"]); Assert.StartsWith("unknown", c2["diff"]!.ToString());
        // at most 50 are returned, all are counted
        var many = Enumerable.Range(1, 120).Select(i => An("x.cs", i, i)).ToList();
        var (c3, l3) = CheckRules.Annotations(many, map, Secrets);
        Assert.Equal((120, 50), (c3["total"]!.GetValue<int>(), l3.Count));
    }

    static string Stamped(int i, string text) => $"2026-01-01T10:00:{20 + i % 20:00}.0000000Z {text}\n";

    [Fact]
    public void ALogExcerpt_IsTheEndOfEachFailedStepPlusTheErrorLinesWithTheirNeighbours_StampsRemoved()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("2026-01-01T10:00:01.0000000Z setup\n");
        for (var i = 1; i <= 100; i++) sb.Append("2026-01-01T10:00:20.0000000Z ").Append(i == 5 ? "src/App.cs(10,5): error CS1002: ; expected" : $"build line {i}.").Append('\n');
        sb.Append("2026-01-01T10:00:55.0000000Z post\n");
        var steps = new[] { new CheckRules.StepRange("2026-01-01T10:00:10", "2026-01-01T10:00:40") };
        var x = CheckRules.LogExcerpt(sb.ToString(), steps, 10, 60000, Secrets);
        Assert.Equal((102, false), (x.TotalLines, x.WholeJob));
        Assert.Contains("build line 100.\n", x.Text); Assert.Contains("build line 91.\n", x.Text); Assert.DoesNotContain("build line 90.\n", x.Text);
        Assert.Contains("error CS1002", x.Text); Assert.Contains("build line 3.\n", x.Text); Assert.Contains("build line 7.\n", x.Text); Assert.DoesNotContain("build line 8.\n", x.Text);
        Assert.DoesNotContain("setup", x.Text); Assert.DoesNotContain("post", x.Text); Assert.DoesNotContain("2026-01-01", x.Text);
        Assert.Matches(@"\[\.\.\. \d+ lines not shown \.\.\.\]", x.Text);
        Assert.Equal((1, 0, 0), (x.ErrorLines, x.ErrorLinesNotShown, x.CutChars));
        // no step to match: the end of the job
        var whole = CheckRules.LogExcerpt(sb.ToString(), [], 3, 60000, Secrets);
        Assert.True(whole.WholeJob);
        Assert.Contains("post\n", whole.Text);
        // the budget cuts from the start, at a line, and says so
        var cut = CheckRules.LogExcerpt(sb.ToString(), steps, 100, 200, Secrets);
        Assert.True(cut.CutChars > 0 && cut.Text.StartsWith($"[... cut: the first {cut.CutChars} characters of this excerpt are not shown (limit 200) ...]\n"));
        Assert.True(cut.Text.Length <= 200 + 120);
    }

    [Fact]
    public void ALogOfNoLines_IsAnEmptyExcerpt()
    {
        var x = CheckRules.LogExcerpt("", [], 200, 1000, Secrets);
        Assert.Equal((0, 0, true), (x.TotalLines, x.ShownLines, x.WholeJob));
        Assert.Equal("", x.Text);
    }

    // ---- the model ladder and the count of red heads ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(60, 2)]
    public void TheTierIsTheNumberOfThresholdsTheValueHasReached(int refusals, int tier) => Assert.Equal(tier, ModelTierReadModel.TierFor(refusals, [3, 6]));

    [Fact]
    public void ARedHeadIsCountedOnce_TheLastTenAreKept_AndTheCountKeepsRising()
    {
        var s = new JsonObject();
        var at = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(ChecksReadModel.AddChecksRed(s, "", at));
        Assert.Null(s["checks_red"]);
        Assert.True(ChecksReadModel.AddChecksRed(s, "aaa", at));
        Assert.False(ChecksReadModel.AddChecksRed(s, "AAA", at));   // the same head, whatever the case
        Assert.Equal(1, ChecksReadModel.RedCount(s));
        Assert.Equal("2026-10-29T12:00:00.000Z", s["checks_red"]!["last_at"]!.ToString());
        for (var i = 0; i < 12; i++) Assert.True(ChecksReadModel.AddChecksRed(s, "h" + i, at));
        Assert.Equal(13, ChecksReadModel.RedCount(s));
        Assert.Equal(["h2", "h3", "h4", "h5", "h6", "h7", "h8", "h9", "h10", "h11"], s["checks_red"]!["shas"]!.AsArray().Select(x => x!.ToString()));
        Assert.True(ChecksReadModel.AddChecksRed(s, "aaa", at));   // fell out of the last ten: counted again, as the script does
        Assert.Equal(0, ChecksReadModel.RedCount(new JsonObject()));
    }

    // ---- PowerShell's conversions ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" 7 ", 7)]
    [InlineData("12.5", 12)]
    [InlineData("13.5", 14)]
    [InlineData("-3", -3)]
    [InlineData("07", 7)]
    public void ANumberIsConvertedAsCastingToIntDoesIt(string text, int expected) => Assert.Equal(expected, Ps.ToInt(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e99")]
    public void AValueThatIsNoNumber_IsAnErrorOfTheScript(string text) => Assert.Throws<FormatException>(() => Ps.ToInt(text));

    [Fact]
    public void ValuesBecomeTextAsAStringInterpolationMakesThem()
    {
        var o = JsonNode.Parse("""{"s":"x","b":true,"f":false,"i":12,"d":1.5,"n":null,"Mixed":"cased"}""")!;
        Assert.Equal(["x", "True", "False", "12", "1.5", "", "cased"], new[] { "s", "b", "f", "i", "d", "n", "mixed" }.Select(k => Ps.Text(Ps.Prop(o, k))));
        Assert.Equal("", Ps.Text(null));
        Assert.True(Ps.Bool(Ps.Prop(o, "b")) && !Ps.Bool(Ps.Prop(o, "f")) && !Ps.Bool(Ps.Prop(o, "n")) && Ps.Bool(Ps.Prop(o, "s")) && Ps.Bool(Ps.Prop(o, "i")) && !Ps.Bool(JsonValue.Create(0)) && !Ps.Bool(JsonValue.Create("")));
        Assert.Equal("cased", Ps.Str(o, "MIXED"));
        Assert.Equal(["a", "b"], Ps.Items(JsonNode.Parse("""["a",null,"b"]""")).Select(Ps.Text));
        Assert.Empty(Ps.Items(null));
    }

    [Fact]
    public void TimesAreReadAsUtcAndWrittenTheWayTheScriptWritesThem()
    {
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), Ps.FromIso("2026-01-01T10:00:00Z"));
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), Ps.FromIso("2026-01-01T10:00:00+01:00"));
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), Ps.FromIso("2026-01-01T10:00:00"));   // no zone: UTC
        Assert.Null(Ps.FromIso(""));
        Assert.Equal("2026-01-01T10:00:00.000Z", Ps.ToIso(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-01-01T10:00:05.000Z", CheckRules.IsoOrNull("2026-01-01T10:00:05Z"));
        Assert.Null(CheckRules.IsoOrNull(null)); Assert.Equal("soon", CheckRules.IsoOrNull("soon"));
        Assert.Matches(@"^2026-10-2[789]T\d\d:\d\d:\d\d\.\d{7}(Z|[+-]\d\d:\d\d)$", Ps.JsonTime("2026-10-28T10:00:00.000+00:00"));
        Assert.Equal("not a time", Ps.JsonTime("not a time"));
    }

    [Fact]
    public void MatchIsCaseInsensitive_TheWayMatchIs_ReplaceIsNot()
    {
        Assert.True(Ps.Match("Src/APP.cs", @"^src/app\.cs$"));
        Assert.False(Ps.Match(null, "x"));
        Assert.True(Ps.Ieq("Build", "build")); Assert.False(Ps.Ieq("a", null));
        Assert.True(Ps.NameIn("SECRET-scan", ["secret-scan"]));
        Assert.Equal("aXb", Ps.Replace("aAb", "A", "X"));
        Assert.Equal("XAb", Ps.Replace("aAb", "a", "X"));   // [regex]::Replace is case-sensitive
    }

    // ---- the arguments and the answer ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheArguments_AreReadAsTheScriptReadsItsHashtable()
    {
        var a = VerbArgs.Parse("""{"item":"12","brief":"false","full_log":true,"empty":null,"tail":"5","Check":"Build result"}""");
        Assert.True(a.Has("item") && !a.Has("empty") && !a.Has("missing") && a.Has("check"));   // a key without regard to case
        Assert.Equal(12, a.ItemNumber());
        Assert.False(a.Flag("brief"));   // "false" in any case is not set
        Assert.True(a.Flag("full_log"));
        Assert.False(a.Flag("missing"));
        Assert.Equal("Build result", a.Text("check"));
        Assert.Equal("", a.Text("empty"));
        var e = Assert.Throws<VerbRefusal>(() => a.Need("missing"));
        Assert.Equal((2, "usage", "missing argument: missing"), (e.Exit, e.Code, e.Message));
        Assert.Throws<VerbRefusal>(() => VerbArgs.Parse("""{"item":""}""").ItemNumber());
        Assert.False(VerbArgs.Parse("").Has("item"));
        Assert.Throws<FormatException>(() => VerbArgs.Parse("""{"item":"abc"}""").ItemNumber());
    }

    [Fact]
    public async Task TheAnswerCarriesOkVerbAndExit_AsTheServerWrapperPrintsThem()
    {
        var (c1, j1) = await VerbReply.Run("pr-checks", () => Task.FromResult(new JsonObject { ["status"] = "success" }));
        Assert.Equal(0, c1);
        Assert.Equal("""{"ok":true,"status":"success","verb":"pr-checks","exit":0}""", j1);
        var (c2, j2) = await VerbReply.Run("pr-checks", () => Task.FromResult(new JsonObject { ["ok"] = true, ["note"] = "<a & b>" }));
        Assert.Equal("""{"ok":true,"note":"<a & b>","verb":"pr-checks","exit":0}""", j2);   // written as the script wrote it: no escaping of < > &
        var (c3, j3) = await VerbReply.Run("wait-for-checks", () => throw new VerbRefusal(21, "still-pending", "checks are still running; call again", new JsonObject { ["checks"] = new JsonObject { ["status"] = "pending" } }));
        Assert.Equal(21, c3);
        Assert.Equal("""{"ok":false,"verb":"wait-for-checks","code":"still-pending","reason":"checks are still running; call again","checks":{"status":"pending"},"exit":21}""", j3);
        var (c4, j4) = await VerbReply.Run("model-tier", () => throw new InvalidOperationException("boom"));
        Assert.Equal(20, c4);
        Assert.Equal("""{"ok":false,"verb":"model-tier","code":"internal-error","reason":"boom","retryAfterSeconds":30,"exit":20}""", j4);
        await Assert.ThrowsAsync<OperationCanceledException>(() => VerbReply.Run("x", () => throw new OperationCanceledException()));
    }

    // ---- the settings ----------------------------------------------------------------------------------------------------------------------------

    static JsonNode Shipped()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "instance.json"))) dir = dir.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "config", "instance.json")))!;
    }

    [Fact]
    public void TheSettings_AreWhatTheShippedConfigurationSays()
    {
        var s = ReadModelSettings.From(Shipped(), _ => null);
        Assert.Equal(("clearmeasure-aisf-sample-apps", "cmdemo1-workorders", "factory-worker", "master", 679, "factory worker", "factory test"), (s.Owner, s.Repo, s.Name, s.DefaultBranch, s.Project, s.WorkerLabel, s.TestLabel));
        Assert.Equal(["Build result", "secret-scan"], s.CiGates);
        Assert.Equal(["Technical Design", "Development", "Functional Testing"], s.AiColumns); Assert.Equal(["Release Queue"], s.HoldColumns); Assert.Equal(["Done"], s.DoneColumns);
        Assert.Equal(("<!-- aisf:docs-only -->", "video"), (s.DocsOnly.Marker, s.DocsOnly.VideoLabel));
        Assert.Equal(["Technical Design"], s.DocsOnly.DesignColumns);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), s.DocsOnly.LegacyBefore);
        Assert.Equal((15, 30, 400), (s.Inspector.MaxFiles, s.Inspector.DocsMaxFiles, s.Inspector.MaxLines));
        Assert.Equal(5, s.Inspector.SecretPatterns.Count); Assert.Equal(5, s.Inspector.AiDirectedText.Count); Assert.Contains("png", s.Inspector.BinaryExtensions);
        Assert.Equal([2d, 3d], s.Escalation.Launches); Assert.Equal([3d, 6d], s.Escalation.Refusals); Assert.Equal([1d, 2d], s.Escalation.Demotions);
        Assert.Empty(s.Escalation.MinTierByColumn);
        Assert.Equal(("https://clearmeasure.octopus.app", "Spaces-355", "cmdemo1-ui", "prod"), (s.Octopus.Url, s.Octopus.Space, s.Octopus.Project, s.Octopus.ProdEnvironment));
        Assert.Equal(8, s.SecretPatterns.Count);   // secrets.patterns (7) and redactOnly (1)
        Assert.False(s.MessagingOn); Assert.Equal("", s.EnvLabel); Assert.True(s.InScope(["a", "env:other", "env:x"]));
    }

    [Fact]
    public void TheEnvironmentScope_AndTheMessagingSwitches_FollowTheEnvironment()
    {
        var cfg = Shipped(); cfg["envScope"] = new JsonArray("tdd");
        Func<string, string?> env(params (string, string)[] kv) { var d = kv.ToDictionary(x => x.Item1, x => x.Item2); return k => d.GetValueOrDefault(k); }
        var tdd = ReadModelSettings.From(cfg, env(("FACTORY_ENVIRONMENT", "TDD")));
        Assert.Equal("env:tdd", tdd.EnvLabel);
        Assert.True(tdd.InScope(["x", "ENV:tdd"])); Assert.False(tdd.InScope(["x"])); Assert.False(tdd.InScope(["env:tdd", "env:uat"])); Assert.False(tdd.InScope(["env:uat"]));
        Assert.Equal("env:tdd", ReadModelSettings.From(cfg, env()).EnvLabel);   // no environment named: tdd
        Assert.Equal("", ReadModelSettings.From(cfg, env(("FACTORY_ENVIRONMENT", "prod"))).EnvLabel);
        Assert.Equal("env:prod", ReadModelSettings.From(cfg, env(("FACTORY_ENVIRONMENT", "prod"), ("FACTORY_ENV_SCOPE", "1"))).EnvLabel);
        Assert.Equal("", ReadModelSettings.From(cfg, env(("FACTORY_ENVIRONMENT", "tdd"), ("FACTORY_ENV_SCOPE", "0"))).EnvLabel);
        var msg = Shipped(); msg["messaging"]!["enabled"] = true;
        Assert.True(ReadModelSettings.From(msg, _ => null).MessagingOn);
        Assert.False(ReadModelSettings.From(msg, env(("FACTORY_MESSAGING", "0"))).MessagingOn);
        Assert.True(ReadModelSettings.From(Shipped(), env(("FACTORY_MESSAGING", "1"))).MessagingOn);
        Assert.True(ReadModelSettings.From(Shipped(), env(("FACTORY_MANAGER", "1"))).MessagingOn);
    }

    [Fact]
    public void ARootThatIsMissingOrMalformed_GivesTheDefaultsAndStopsNothing()
    {
        var s = ReadModelSettings.From(null, _ => null);
        Assert.Equal(("", "factory-worker", "master", 0), (s.Owner, s.Name, s.DefaultBranch, s.Project));
        Assert.Empty(s.CiGates);
        Assert.Equal(JournalText.FallbackSecretPatterns, s.SecretPatterns);
        var odd = ReadModelSettings.From(JsonNode.Parse("""{"ciGates":"Build result","columns":5,"inspector":[1],"escalation":{"launches":4,"minTierByColumn":{"Development":1}}}"""), _ => null);
        Assert.Equal(["Build result"], odd.CiGates);
        Assert.Equal([4d], odd.Escalation.Launches.ToArray());
        Assert.Equal(1, odd.Escalation.MinTierByColumn["development"]);   // a column compares without regard to case
        Assert.Equal((15, 30, 400), (odd.Inspector.MaxFiles, odd.Inspector.DocsMaxFiles, odd.Inspector.MaxLines));
    }
}
