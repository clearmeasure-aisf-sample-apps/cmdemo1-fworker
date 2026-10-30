using System.Text.Json.Nodes;
using Fworker.Workflows.ReadModels;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.5 (#357): <c>pr-checks</c> and <c>wait-for-checks</c> on the fake GitHub of the Tier 1 suites, the cases of tests/scenarios/checks.ps1 (the scenarios that pin the rule script's answers): the detail of a failed job, the annotations, the
/// baseline, the pages of a log, a refusal or a fault of GitHub, the artifacts, the item's own pull request, the count of red heads and the one rerun. Where the fake is not built the tests return at once.
/// </summary>
public sealed class ReadModelsChecksFakeTests : IClassFixture<ReadModelsChecksFakeTests.Rig>, IDisposable
{
    public sealed class Rig : IDisposable
    {
        public readonly FakeGitHubRig? Fake = FakeGitHubRig.Start();
        public void Dispose() => Fake?.Dispose();
    }

    readonly FakeGitHubRig? fake;
    static readonly string Secret = "ghp_" + new string('A', 30);   // a fixture, composed so the secret scan does not read it as a credential

    public ReadModelsChecksFakeTests(Rig rig) { fake = rig.Fake; fake?.Fake("reset"); }
    public void Dispose() { }

    /// <summary>Where the run says the fake is built (FACTORY_REQUIRE_FAKE_RIG=1), a missing one is a failure and not a silent return.</summary>
    [Fact]
    public void TheFakeGitHubIsRunning_WhereTheRunRequiresIt()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_REQUIRE_FAKE_RIG") != "1") return;
        Assert.NotNull(fake);
    }

    static VerbArgs Args(object body) => new((JsonObject)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(body))!);

    static JsonArray Steps() => new(
        new JsonObject { ["name"] = "Set up job", ["number"] = 1, ["conclusion"] = "success", ["started_at"] = "2026-01-01T10:00:00Z", ["completed_at"] = "2026-01-01T10:00:05Z" },
        new JsonObject { ["name"] = "Build", ["number"] = 2, ["conclusion"] = "failure", ["started_at"] = "2026-01-01T10:00:10Z", ["completed_at"] = "2026-01-01T10:00:40Z" },
        new JsonObject { ["name"] = "Post", ["number"] = 3, ["conclusion"] = "success", ["started_at"] = "2026-01-01T10:00:50Z", ["completed_at"] = "2026-01-01T10:00:55Z" });

    // the log of a job whose step "Build" failed: lines of the build, a compiler error on line 5, a credential and a coloured line near the end
    static string Log(int lines)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("2026-01-01T10:00:01.0000000Z setup line one\n2026-01-01T10:00:02.0000000Z setup line two\n");
        for (var i = 1; i <= lines; i++)
        {
            var text = i == 5 ? "src/App.cs(10,5): error CS1002: ; expected" : i == lines - 2 ? "\u001b[31mred text\u001b[0m" : i == lines - 1 ? "pushing with token " + Secret : $"build line {i}.";
            sb.Append("2026-01-01T10:00:20.0000000Z ").Append(text).Append('\n');
        }
        sb.Append("2026-01-01T10:00:39.0000000Z ##[error]Process completed with exit code 1.\n");
        for (var i = 1; i <= 6; i++) sb.Append($"2026-01-01T10:00:5{i}.0000000Z post line {i}.\n");
        return sb.ToString();
    }

    (int N, int Pr, string Sha) RedPr() { var n = fake!.NewItem("Checks"); var (pr, sha) = fake.NewPr(n, null, "red"); return (n, pr, sha); }
    void BuildFailure(string sha, int lines = 400) => fake!.Fake("checks", new { sha, name = "Build result", conclusion = "failure", steps = Steps(), log = Log(lines) });
    void Qodana(string sha) => fake!.Fake("checks", new
    {
        sha, name = "Qodana", conclusion = "failure", app = "qodana-cloud", outputTitle = "3 new problems", outputSummary = "Problems: 3 \u001b[1mnew\u001b[0m, key " + Secret,
        annotations = new[]
        {
            new { path = "src/Legacy.cs", start_line = 7, end_line = 7, annotation_level = "warning", title = "RedundantUsing", message = "Using directive is not required" },
            new { path = "src/App.cs", start_line = 50, end_line = 51, annotation_level = "warning", title = "InconsistentNaming", message = "Name does not match the rule" },
            new { path = "src/App.cs", start_line = 1, end_line = 1, annotation_level = "failure", title = "UnusedVariable", message = "Local variable is never used" },
        },
    });

    static JsonNode? Check(JsonObject r, string name) => Ps.List(r, "checks").FirstOrDefault(c => Ps.Str(c, "name") == name);
    static async Task<(int Exit, JsonObject Body)> Call(ReadModelParts p, string verb, object body)
    {
        var (exit, json) = await VerbReply.Run(verb, () => verb switch
        {
            "pr-checks" => p.Checks.PrChecks(Args(body), CancellationToken.None),
            "wait-for-checks" => p.Checks.WaitForChecks(Args(body), CancellationToken.None),
            "inspect-facts" => p.Inspect.InspectFacts(Args(body), CancellationToken.None),
            "model-tier" => p.Tier.ModelTier(Args(body), CancellationToken.None),
            "get-delivery" => p.Delivery.GetDelivery(Args(body), CancellationToken.None),
            "check-secrets" => p.Secrets.CheckSecrets(CancellationToken.None),
            _ => throw new InvalidOperationException(verb),
        });
        return (exit, (JsonObject)JsonNode.Parse(json)!);
    }

    [Fact]
    public async Task AFailedJob_ReturnsItsFailedStep_TheEndOfItsLog_AndTheErrorLines_CleanedAndRedacted()
    {
        if (fake is null) return;
        var t = RedPr(); BuildFailure(t.Sha);
        var (parts, _) = fake.Parts();
        var (exit, r) = await Call(parts, "pr-checks", new { item = t.N });
        Assert.Equal(0, exit);
        Assert.True(Ps.Str(r, "status") == "failure" && Ps.List(r, "failed_checks").Any(x => Ps.Text(x) == "Build result") && Ps.Str(r, "sha") == t.Sha, r.ToJsonString());
        var c = Check(r, "Build result")!; var ok = Check(r, "secret-scan")!;
        Assert.True(Ps.Str(c, "conclusion") == "failure" && Ps.Str(ok, "conclusion") == "success" && Ps.Str(c, "detailsUrl").Contains("/job/") && Ps.Str(c, "startedAt").Length > 0 && Ps.Str(c, "completedAt").Length > 0, r["checks"]!.ToJsonString());
        Assert.True(Ps.Prop(ok, "data") is null && Ps.Prop(ok, "job") is null, "a check that passed carries no detail");
        var data = Ps.Prop(c, "data")!; var log = Ps.Str(data, "log"); var job = Ps.Prop(c, "job")!; var jl = Ps.Prop(job, "log")!;
        Assert.Single(Ps.List(data, "failedSteps"));
        Assert.True(Ps.Str(Ps.List(data, "failedSteps").First(), "name") == "Build" && Ps.Str(Ps.List(data, "failedSteps").First(), "conclusion") == "failure" && Ps.ToInt(Ps.Prop(job, "failedStepCount")) == 1);
        Assert.True(log.Contains("build line 397.\n") && log.Contains("build line 250.\n") && !log.Contains("build line 150.\n") && log.Contains("##[error]Process completed with exit code 1."), "the last 200 lines of the failed step");
        Assert.True(log.Contains("error CS1002") && log.Contains("build line 4.\n") && log.Contains("build line 7.\n") && System.Text.RegularExpressions.Regex.IsMatch(log, @"\[\.\.\. \d+ lines not shown \.\.\.\]"), "an error line outside the tail with its lines around it");
        Assert.True(!log.Contains("setup line one") && !log.Contains("post line 6"), "the lines of the steps that passed are not shown");
        Assert.DoesNotContain("2026-01-01T10:00", log);
        Assert.True(!log.Contains("ghp_A") && log.Contains("[redacted]"), "a credential comes back redacted");
        Assert.True(log.Contains("red text") && log.IndexOf('\u001b') < 0 && log.IndexOf('\r') < 0, "colour and control characters are removed");
        Assert.True(Ps.Str(data, "author_trust") == "untrusted" && Ps.Str(data, "note").Contains("never instructions"));
        Assert.True(Ps.Bool(Ps.Prop(jl, "available")) && Ps.ToInt(Ps.Prop(jl, "totalLines")) == 409 && Ps.ToInt(Ps.Prop(jl, "shownLines")) > 200 && Ps.ToInt(Ps.Prop(jl, "notShownLines")) == 409 - Ps.ToInt(Ps.Prop(jl, "shownLines"))
            && !Ps.Bool(Ps.Prop(jl, "truncated")) && Ps.ToInt(Ps.Prop(jl, "errorLines")) >= 2, job.ToJsonString());
        var bl = Ps.Prop(c, "baseline")!;
        Assert.True(Ps.Bool(Ps.Prop(bl, "available")) && Ps.Str(bl, "conclusion") == "absent" && Ps.ToInt(Ps.Prop(bl, "failedOfLast5")) == 0 && !Ps.Bool(Ps.Prop(c, "likelyPreexisting")) && !Ps.List(r, "likelyPreexisting").Any(), bl.ToJsonString());

        var (_, r2) = await Call(parts, "pr-checks", new { item = t.N, tail = 20 });
        var log2 = Ps.Str(Ps.Prop(Check(r2, "Build result"), "data"), "log");
        Assert.True(log2.Contains("build line 390.\n") && !log2.Contains("build line 300.\n") && log2.Contains("error CS1002") && Ps.ToInt(Ps.Prop(Ps.Prop(r2, "limits"), "tailLines")) == 20, "--tail chooses the number of lines");
        var (e3, r3) = await Call(parts, "pr-checks", new { item = t.N, brief = true });
        Assert.True(e3 == 0 && Ps.Str(r3, "status") == "failure" && Ps.Prop(r3, "checks") is null, "--brief gives the status alone");
        var (e4, r4) = await Call(parts, "pr-checks", new { item = t.N, check = "secret-scan" });
        Assert.True(e4 == 0 && Ps.List(r4, "checks").Count() == 1 && Ps.Str(Ps.List(r4, "checks").First(), "name") == "secret-scan", "--check selects one check");
        var (e5, r5) = await Call(parts, "pr-checks", new { item = t.N, check = "No such check" });
        Assert.True(e5 == 12 && Ps.Str(r5, "code") == "check-not-found" && Ps.List(r5, "checks").Any(), r5.ToJsonString());
        var (e6, w) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.True(e6 == 10 && Ps.Str(w, "code") == "checks-red", w.ToJsonString());
        var wc = Check((JsonObject)w["checks"]!, "Build result")!;
        Assert.True(Ps.Str(Ps.Prop(wc, "data"), "log").Contains("error CS1002") && Ps.Prop(wc, "baseline") is not null && Ps.List(w["checks"], "failed_checks").Any(x => Ps.Text(x) == "Build result"), "wait-for-checks carries the same detail");
    }

    [Fact]
    public async Task AQodanaLikeCheck_ReturnsItsAnnotations_SplitIntoThoseOnTheChangeAndThoseElsewhere()
    {
        if (fake is null) return;
        var t = RedPr(); Qodana(t.Sha);
        var (parts, _) = fake.Parts();
        var (exit, r) = await Call(parts, "pr-checks", new { item = t.N, check = "Qodana" });
        Assert.Equal(0, exit);
        var c = Check(r, "Qodana")!; var k = Ps.Prop(c, "annotationCounts")!; var data = Ps.Prop(c, "data")!;
        Assert.True(Ps.Prop(c, "job") is null && Ps.Prop(data, "log") is null && Ps.Str(c, "app") == "qodana-cloud", "it is no Actions job: no job, no log");
        Assert.True(Ps.Str(data, "outputTitle") == "3 new problems" && Ps.Str(data, "outputSummary").StartsWith("Problems: 3 new, key") && !Ps.Str(data, "outputSummary").Contains("ghp_A"), Ps.Str(data, "outputSummary"));
        Assert.True(Ps.ToInt(Ps.Prop(k, "total")) == 3 && Ps.ToInt(Ps.Prop(k, "onChangedLines")) == 1 && Ps.ToInt(Ps.Prop(k, "onChangedFiles")) == 1 && Ps.ToInt(Ps.Prop(k, "elsewhere")) == 1 && Ps.ToInt(Ps.Prop(k, "returned")) == 3, k.ToJsonString());
        var an = Ps.List(data, "annotations").ToList();
        Assert.True(Ps.Str(an[0], "path") == "src/App.cs" && Ps.ToInt(Ps.Prop(an[0], "line")) == 1 && Ps.Str(an[0], "level") == "failure" && Ps.Str(an[0], "message") == "Local variable is never used" && Ps.Str(an[0], "where") == "changed-line"
            && Ps.Str(an[1], "where") == "changed-file" && Ps.Str(an[2], "path") == "src/Legacy.cs" && Ps.Str(an[2], "where") == "elsewhere", Ps.Prop(data, "annotations")!.ToJsonString());
        var (e2, r2) = await Call(parts, "pr-checks", new { sha = t.Sha, check = "Qodana" });
        var k2 = Ps.Prop(Check(r2, "Qodana"), "annotationCounts")!;
        Assert.True(e2 == 0 && Ps.ToInt(Ps.Prop(k2, "total")) == 3 && Ps.Prop(k2, "elsewhere") is null && Ps.Str(k2, "diff").Contains("unknown"), "a call that names only a commit has no diff to compare with");
        var (e3, r3) = await Call(parts, "pr-checks", new { item = t.N, check = "Qodana", full_log = true });
        Assert.True(e3 == 10 && Ps.Str(r3, "code") == "not-an-actions-job");
    }

    [Fact]
    public async Task TheBaseline_TellsACheckTheBaseBranchAlreadyFailsFromOneThisChangeBroke()
    {
        if (fake is null) return;
        var t = RedPr(); BuildFailure(t.Sha, 30); Qodana(t.Sha);
        fake.Fake("base-history", new { name = "Qodana", app = "qodana-cloud", conclusions = new[] { "failure", "failure", "success" } });
        fake.Fake("base-history", new { name = "Build result", conclusions = new[] { "success", "success", "success", "failure" } });
        var (parts, _) = fake.Parts();
        var (exit, r) = await Call(parts, "pr-checks", new { item = t.N });
        Assert.Equal(0, exit);
        var q = Check(r, "Qodana")!; var b = Check(r, "Build result")!;
        var qb = Ps.Prop(q, "baseline")!; var bb = Ps.Prop(b, "baseline")!;
        Assert.True(Ps.Bool(Ps.Prop(q, "likelyPreexisting")) && Ps.Str(qb, "conclusion") == "failure" && Ps.ToInt(Ps.Prop(qb, "failedOfLast5")) == 2 && Ps.ToInt(Ps.Prop(qb, "ranOfLast5")) == 3 && Ps.Str(qb, "branch") == "master", qb.ToJsonString());
        Assert.True(!Ps.Bool(Ps.Prop(b, "likelyPreexisting")) && Ps.Str(bb, "conclusion") == "success" && Ps.ToInt(Ps.Prop(bb, "failedOfLast5")) == 1 && Ps.ToInt(Ps.Prop(bb, "ranOfLast5")) == 4, bb.ToJsonString());
        Assert.Equal(["Qodana"], Ps.List(r, "likelyPreexisting").Select(Ps.Text).ToArray());
    }

    [Fact]
    public async Task AnOversizeLog_IsCutAndSaysSo_TheWholeLogIsReadInPages()
    {
        if (fake is null) return;
        var t = RedPr();
        fake.Fake("checks", new { sha = t.Sha, name = "Build result", conclusion = "failure", logLines = new { count = 6000, prefix = new string('x', 80) } });
        var (parts, _) = fake.Parts();
        var (exit, r) = await Call(parts, "pr-checks", new { item = t.N, tail = 5000 });
        Assert.Equal(0, exit);
        var c = Check(r, "Build result")!; var log = Ps.Str(Ps.Prop(c, "data"), "log"); var jl = Ps.Prop(Ps.Prop(c, "job"), "log")!;
        Assert.True(log.Length <= 60200 && log.Length > 50000 && Ps.ToInt(Ps.Prop(Ps.Prop(r, "limits"), "logBudgetChars")) == 60000, log.Length.ToString());
        Assert.True(Ps.Bool(Ps.Prop(jl, "truncated")) && Ps.ToInt(Ps.Prop(jl, "cutChars")) > 300000 && Ps.ToInt(Ps.Prop(jl, "totalLines")) == 6000 && Ps.ToInt(Ps.Prop(jl, "notShownLines")) == 1000 && log.StartsWith("[... cut: the first "), jl.ToJsonString());
        Assert.EndsWith(" 6000", log.TrimEnd());
        var (e1, p1) = await Call(parts, "pr-checks", new { item = t.N, full_log = true });
        Assert.Equal(0, e1);
        var content = (JsonObject)p1["content"]!; var pages = Ps.ToInt(content["pages"]);
        Assert.True(pages >= 10 && Ps.ToInt(content["page"]) == 1 && Ps.Str(content, "text").Length == 48000 && Ps.Str(content, "kind") == "job-log" && Ps.Str(content, "author_trust") == "untrusted" && Ps.Str(p1, "check") == "Build result" && System.Text.RegularExpressions.Regex.IsMatch(Ps.Str(content, "text"), @" 1\n"));
        var (el, pl) = await Call(parts, "pr-checks", new { item = t.N, full_log = true, page = pages });
        Assert.True(el == 0 && Ps.Str(pl["content"], "text").TrimEnd().EndsWith(" 6000") && Ps.Str(pl["content"], "text").Length <= 48000);
        var (eo, po) = await Call(parts, "pr-checks", new { item = t.N, full_log = true, page = pages + 1 });
        Assert.True(eo == 2 && Ps.Str(po, "code") == "page-out-of-range" && Ps.ToInt(po["pages"]) == pages, po.ToJsonString());
    }

    [Fact]
    public async Task AMissingGitHubPermission_IsAClearCodeInTheAnswer_NeverAnErrorOfTheVerb()
    {
        if (fake is null) return;
        var t = RedPr(); BuildFailure(t.Sha, 30); Qodana(t.Sha);
        const string denied = "{\"message\":\"Resource not accessible by integration\"}";
        fake.Fake("fault", new { match = @"GET .*/actions/jobs/\d+/logs$", status = 403, count = 20, body = denied });
        var (parts, _) = fake.Parts();
        var (exit, r) = await Call(parts, "pr-checks", new { item = t.N });
        Assert.Equal(0, exit);
        var c = Check(r, "Build result")!; var job = Ps.Prop(c, "job")!; var jl = Ps.Prop(job, "log")!;
        Assert.True(Ps.Bool(Ps.Prop(job, "available")) && !Ps.Bool(Ps.Prop(jl, "available")) && Ps.Str(jl, "code") == "logs-not-permitted" && Ps.Str(jl, "reason").Contains("Actions: read") && Ps.Prop(Ps.Prop(c, "data"), "log") is null, job.ToJsonString());
        Assert.True(Ps.List(Ps.Prop(c, "data"), "failedSteps").Count() == 1 && Ps.Bool(Ps.Prop(Ps.Prop(c, "baseline"), "available")) && Ps.ToInt(Ps.Prop(Ps.Prop(Check(r, "Qodana"), "annotationCounts"), "total")) == 3);
        var (ef, f) = await Call(parts, "pr-checks", new { item = t.N, check = "Build result", full_log = true });
        Assert.True(ef == 10 && Ps.Str(f, "code") == "logs-not-permitted", f.ToJsonString());
        fake.Fake("fault", new { match = @"GET .*/actions/jobs/\d+$", status = 403, count = 20, body = denied });
        fake.Fake("fault", new { match = @"GET .*/check-runs/\d+/annotations", status = 403, count = 20, body = denied });
        var (e2, r2) = await Call(parts, "pr-checks", new { item = t.N });
        Assert.Equal(0, e2);
        var c2 = Check(r2, "Build result")!; var q2 = Check(r2, "Qodana")!;
        Assert.True(!Ps.Bool(Ps.Prop(Ps.Prop(c2, "job"), "available")) && Ps.Str(Ps.Prop(c2, "job"), "code") == "logs-not-permitted", Ps.Prop(c2, "job")!.ToJsonString());
        var ac = Ps.Prop(q2, "annotationCounts")!;
        Assert.True(!Ps.Bool(Ps.Prop(ac, "available")) && Ps.Str(ac, "code") == "annotations-not-permitted" && Ps.Str(ac, "reason").Contains("Checks: read"), ac.ToJsonString());
        Assert.True(Ps.Str(r2, "status") == "failure" && Ps.List(r2, "failed_checks").Count() == 2);
    }

    [Fact]
    public async Task WhenTheDetailCannotBeRead_TheStatusAndTheExitCodeAreWhatTheyAlwaysWere()
    {
        if (fake is null) return;
        var t = RedPr(); BuildFailure(t.Sha, 30); Qodana(t.Sha);
        fake.Fake("fault", new { match = @"GET \S+/(commits|pulls/\d+/files|actions/jobs/\d+(/logs)?|actions/runs/\d+/artifacts|check-runs/\d+/annotations)$", status = 500, count = 200 });
        var (parts, _) = fake.Parts();
        var (exit, r) = await Call(parts, "pr-checks", new { item = t.N });
        Assert.Equal(0, exit);
        var c = Check(r, "Build result")!;
        Assert.True(Ps.Str(r, "status") == "failure" && Ps.List(r, "failed_checks").Count() == 2 && Ps.Str(r, "sha") == t.Sha && Ps.List(r, "checks").Count() >= 4, r.ToJsonString());
        Assert.True(!Ps.Bool(Ps.Prop(Ps.Prop(c, "baseline"), "available")) && !Ps.Bool(Ps.Prop(Ps.Prop(c, "job"), "available")) && Ps.Str(Ps.Prop(c, "job"), "code") == "github-error" && !Ps.Bool(Ps.Prop(c, "likelyPreexisting"))
            && !Ps.Bool(Ps.Prop(Ps.Prop(Check(r, "Qodana"), "annotationCounts"), "available")), c.ToJsonString());
        var (ew, w) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.True(ew == 10 && Ps.Str(w, "code") == "checks-red" && Ps.List(w["checks"], "failed_checks").Any(x => Ps.Text(x) == "Build result"), w.ToJsonString());
    }

    [Fact]
    public async Task TheArtifactsOfAFailedRun_AreListed_AndATextFileOfOneIsReturned_BinaryAndOversizeOnesAreListedOnly()
    {
        if (fake is null) return;
        var t = RedPr(); BuildFailure(t.Sha, 30);
        fake.Fake("artifact", new { runId = 1, name = "qodana-report", files = new Dictionary<string, string> { ["results/qodana.sarif.json"] = "{\"runs\":[{\"tool\":\"qodana\",\"key\":\"" + Secret + "\"}]}", ["notes.txt"] = "two files" }, binary = new Dictionary<string, string> { ["shot.png"] = Convert.ToBase64String([137, 80, 78, 71, 0, 1, 2]) } });
        fake.Fake("artifact", new { runId = 1, name = "memory-dump", files = new Dictionary<string, string> { ["dump.txt"] = "x" }, sizeBytes = 900000000L });
        var (parts, _) = fake.Parts();
        var (exit, r) = await Call(parts, "pr-checks", new { item = t.N });
        Assert.Equal(0, exit);
        var bc = Check(r, "Build result")!; var arts = Ps.Prop(bc, "artifacts")!; var list = Ps.List(Ps.Prop(bc, "data"), "artifacts").ToList();
        var rep = list.First(x => Ps.Str(x, "name") == "qodana-report"); var dump = list.First(x => Ps.Str(x, "name") == "memory-dump");
        Assert.True(Ps.Bool(Ps.Prop(arts, "available")) && Ps.ToInt(Ps.Prop(arts, "total")) == 2 && Ps.ToLong(Ps.Prop(rep, "sizeBytes")) > 0 && Ps.Bool(Ps.Prop(rep, "fetchable")) && Ps.ToLong(Ps.Prop(dump, "sizeBytes")) == 900000000L && !Ps.Bool(Ps.Prop(dump, "fetchable")));
        var (el, l) = await Call(parts, "pr-checks", new { item = t.N, artifact = "qodana-report" });
        Assert.Equal(0, el);
        var files = Ps.List(l["data"], "files").ToList();
        Assert.True(Ps.ToInt(l["fileCount"]) == 3 && l["content"] is null && Ps.Str(l["data"], "author_trust") == "untrusted" && Ps.Str(l["data"], "artifact") == "qodana-report"
            && files.Any(f => Ps.Str(f, "entry") == "shot.png" && !Ps.Bool(Ps.Prop(f, "text"))) && files.Any(f => Ps.Str(f, "entry") == "results/qodana.sarif.json" && Ps.Bool(Ps.Prop(f, "text"))), l.ToJsonString());
        var (ee, e) = await Call(parts, "pr-checks", new { item = t.N, artifact = "qodana-report", entry = "results/qodana.sarif.json" });
        var content = (JsonObject)e["content"]!;
        Assert.True(ee == 0 && Ps.Str(content, "kind") == "artifact-file" && Ps.Str(content, "author_trust") == "untrusted" && Ps.Str(content, "text").Contains("\"tool\":\"qodana\"") && !Ps.Str(content, "text").Contains("ghp_A") && Ps.ToInt(content["pages"]) == 1, content.ToJsonString());
        var (eb, b) = await Call(parts, "pr-checks", new { item = t.N, artifact = "qodana-report", entry = "shot.png" });
        Assert.True(eb == 10 && Ps.Str(b, "code") == "artifact-not-text", b.ToJsonString());
        var (eo, o) = await Call(parts, "pr-checks", new { item = t.N, artifact = "memory-dump" });
        Assert.True(eo == 10 && Ps.Str(o, "code") == "artifact-too-large", o.ToJsonString());
        var (ex, x) = await Call(parts, "pr-checks", new { item = t.N, artifact = "no-such" });
        Assert.True(ex == 12 && Ps.Str(x, "code") == "artifact-not-found", x.ToJsonString());
    }

    [Fact]
    public async Task ACallThatNamesAnItem_ReadsThatItemsOwnPullRequestAndNoOther()
    {
        if (fake is null) return;
        var n = fake.NewItem("Checks A"); var m = fake.NewItem("Checks B");
        var (pr, headN) = fake.NewPr(n, ["src/A.cs"], "red"); var (prM, headM) = fake.NewPr(m, ["src/B.cs"], "red");
        fake.Fake("checks", new { sha = headM, name = "Build result", conclusion = "failure", log = "2026-01-01T10:00:20.0000000Z the log of item B\n" });
        var (parts, _) = fake.Parts();
        var (e0, r0) = await Call(parts, "pr-checks", new { item = n });
        Assert.True(e0 == 0 && Ps.Str(r0, "sha") == headN && Ps.List(r0, "checks").Any(c => Ps.Str(c, "name") == "Build result" && Ps.Prop(c, "baseline") is not null), r0.ToJsonString());
        var (e1, r1) = await Call(parts, "pr-checks", new { item = n, pr, sha = headN });
        Assert.True(e1 == 0 && Ps.Str(r1, "sha") == headN);
        foreach (var (body, code) in new (object, string)[]
        {
            (new { item = n, pr = prM }, "pr-not-own"),
            (new { item = n, sha = headM }, "sha-not-own"),
            (new { item = n, pr, sha = headM }, "sha-not-own"),
            (new { item = n, pr = prM, check = "Build result", full_log = true }, "pr-not-own"),
            (new { item = n, sha = headM, artifact = "x" }, "sha-not-own"),
        })
        {
            var (ex, rx) = await Call(parts, "pr-checks", body);
            Assert.True(ex == 10 && Ps.Str(rx, "code") == code && rx["checks"] is null && rx["content"] is null && !rx.ToJsonString().Contains("the log of item B"), rx.ToJsonString());
        }
        var (eo, ro) = await Call(parts, "pr-checks", new { pr = prM });
        Assert.True(eo == 0 && Ps.Str(ro, "sha") == headM && Ps.Str(Ps.Prop(Check(ro, "Build result"), "data"), "log").Contains("the log of item B"), "a call that names no item reads any pull request");
    }

    [Fact]
    public async Task WaitForChecks_CountsARedHeadOnce_AndASecondRedHeadOfTheSameItemReachesTheGate()
    {
        if (fake is null) return;
        var t = RedPr();
        var (parts, _) = fake.Parts();
        var (e1, r1) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.True(e1 == 10 && Ps.Str(r1, "code") == "checks-red", r1.ToJsonString());
        var (e2, r2) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.True(e2 == 10 && Ps.Str(r2, "code") == "checks-red");
        var s = fake.StateComment(t.N)!;
        Assert.True(Ps.ToInt(Ps.At(s, "checks_red", "count")) == 1 && Ps.Items(Ps.At(s, "checks_red", "shas")).Select(Ps.Text).SequenceEqual([t.Sha]) && Ps.ToInt(Ps.Prop(s, "rev")) >= 1, "one red head, asked twice, is counted once: " + s.ToJsonString());
        var sha2 = fake.Push(t.N, null, "red");
        var (e3, _) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.Equal(10, e3);
        s = fake.StateComment(t.N)!;
        Assert.True(Ps.ToInt(Ps.At(s, "checks_red", "count")) == 2 && Ps.Items(Ps.At(s, "checks_red", "shas")).Select(Ps.Text).SequenceEqual([t.Sha, sha2]), s.ToJsonString());
    }

    [Fact]
    public async Task OneFailedCheckTheBaseBranchFailsToo_IsRerunOncePerHead_AndNotCounted()
    {
        if (fake is null) return;
        var t = RedPr();
        fake.Fake("base-history", new { name = "Build result", conclusions = new[] { "failure", "failure", "success" } });
        var before = fake.State()["rerunJobs"]!.GetValue<int>();
        var (parts, _) = fake.Parts();
        var (e1, r1) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.True(e1 == 21 && Ps.Str(r1, "code") == "rerun-started", r1.ToJsonString());
        Assert.Equal(before + 1, fake.State()["rerunJobs"]!.GetValue<int>());
        Assert.True(Ps.Prop(fake.StateComment(t.N), "checks_red") is null && Ps.Items(Ps.Prop(fake.StateComment(t.N), "preexisting_reruns")).Select(Ps.Text).SequenceEqual([t.Sha]), "not counted, and the rerun is recorded");
        var (e2, _) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.Equal(0, e2);   // the rerun passed
        var sha2 = fake.Push(t.N, null, "red");
        fake.Fake("checks", new { sha = sha2, name = "Other platform", conclusion = "failure" });
        var (e3, r3) = await Call(parts, "wait-for-checks", new { item = t.N });
        Assert.True(e3 == 10 && Ps.Str(r3, "code") == "checks-red", "two failed checks are not rerun");
        Assert.Equal(before + 1, fake.State()["rerunJobs"]!.GetValue<int>());
    }

    [Fact]
    public async Task WaitForChecks_GreenIsTheAnswer_PendingIsStillPendingAfterTheTime()
    {
        if (fake is null) return;
        var n = fake.NewItem("Green"); var (pr, sha) = fake.NewPr(n);
        var (parts, clock) = fake.Parts();
        var (e1, r1) = await Call(parts, "wait-for-checks", new { item = n });
        Assert.True(e1 == 0 && Ps.Str(r1, "status") == "success" && Ps.Str(r1, "sha") == sha && Ps.Str(r1, "verb") == "wait-for-checks", r1.ToJsonString());
        fake.Fake("pr", new { number = pr, delaySeconds = 3600 });
        var (e2, r2) = await Call(parts, "wait-for-checks", new { item = n, timeoutSeconds = 25 });
        Assert.True(e2 == 21 && Ps.Str(r2, "code") == "still-pending" && Ps.Str(r2["checks"], "status") == "pending", r2.ToJsonString());
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], clock.Waits);   // 0, 10, 20 s: the poll at 30 s is past the 25 s
    }
}
