using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

// W9.2 (#354): replays of Tier 1 scenarios (tests/scenarios/scenarios.ps1) through the item verbs of v2 against the fake GitHub: the same calls the scripted worker makes, the same checks, and the caused writes compared
// with the golden file of the scenario, which the rule scripts wrote. A difference is a line of the golden file the verbs did not write, or wrote differently (a comment's text, a body, a card move, the order).

public sealed partial class ItemVerbsFakeGitHubTests
{
    static readonly string[] Labels = ["factory worker", "Auto Merge"];

    /// <summary>The verbs' own caused writes in the order they came: not the claim refs, the state comments and the plant's journal lines (the golden file lists those apart).</summary>
    static List<string> Caused(IEnumerable<string> lines) =>
        lines.Where(l => l.StartsWith("rule ") && !l.Contains(" STATE ") && !l.Contains("{factory-state") && !l.Contains("/git/refs") && !l.Contains("\\uD83C\\uDFED") && !l.Contains("\U0001F3ED")).ToList();

    /// <summary>The golden file's "=" lines. The completion of Development is the rule script's still (W9.5), so its two writes (the comment, the card to Functional Testing) are not in the expectation.</summary>
    static List<string> GoldenWithoutScriptCompletion(string file) =>
        Golden("auto", file).Where(l => !l.Contains("PR open, CI green. REVIEW: COMPLETE") && !l.Contains("\"o\":\"opt_Functional_Testing\"")).ToList();

    async Task<JsonObject> Claim(int n, string column, string holder = "w1", int exit = 0) => await Verb("claim-item", new { item = n, holder, column }, exit);
    async Task<JsonObject> Review(int n, string column, string text, int exit = 0) => await Verb("record-review", new { item = n, holder = "w1", column, role = "reviewer", text }, exit);
    async Task<JsonObject> Append(int n, string column, string content, int exit = 0) { await Task.Delay(15); return await Verb("append-body", new { item = n, holder = "w1", column, content }, exit); }

    /// <summary>Do-TechnicalDesign of the harness.</summary>
    async Task TechnicalDesign(int n, bool docsOnly = true)
    {
        await Claim(n, "Technical Design");
        await Append(n, "Technical Design", $"## Technical Design\n\n{(docsOnly ? "<!-- aisf:docs-only -->" : "")}\n- change as described in the item.\n");
        var r = await Review(n, "Technical Design", "Section present and complete.\nREVIEW: COMPLETE");
        await Verb("complete", new { item = n, holder = "w1", column = "Technical Design", review_ref = r["review_ref"]!.ToString(), comment = "REVIEW: COMPLETE - design done" });
    }

    /// <summary>Do-Development of the harness: its claim, the branch and the pull request. The completion is the script's, so the card is moved by hand (DevComplete).</summary>
    async Task<int> Development(int n, params string[] files)
    {
        await Claim(n, "Development");
        await Control("push-branch", new { branch = $"factory-worker/{n}-development", files = files.Length > 0 ? files : ["docs/trial.md"], profile = "green", delaySeconds = 0 });
        var r = await Verb("open-pr", new { item = n, holder = "w1", head = $"factory-worker/{n}-development", title = $"Trial {n}", body = "change" });
        return (int)r["pullRequest"]!["number"]!;
    }

    async Task DevComplete(int n)
    {
        await Review(n, "Development", "Diff matches the plan.\nREVIEW: COMPLETE");
        await Control("move", new { number = n, column = "Functional Testing" });
    }

    [Fact]
    public async Task Replay_GatesRefuseWhatTheyShould_IsTheGoldenFile()
    {
        await Control("reset");
        await Control("scenario-begin", new { suite = "w92", scenario = "gates", scenarioSlug = "gates" });
        var n = await Issue("Technical Design", "x", Labels);
        await Claim(n, "Technical Design");
        Assert.Equal("claim-held", (await Claim(n, "Technical Design", "w2", 40))["code"]!.ToString());
        Assert.Equal("missing-required-section", (await Verb("complete", new { item = n, holder = "w1", column = "Technical Design", comment = "REVIEW: COMPLETE" }, 10))["code"]!.ToString());
        Assert.Equal("stale-column", (await Verb("complete", new { item = n, holder = "w1", column = "Development", comment = "x" }, 11))["code"]!.ToString());
        await Append(n, "Technical Design", "## Technical Design\n- x\n");
        Assert.Equal("reviewer-verdict-missing", (await Verb("complete", new { item = n, holder = "w1", column = "Technical Design", comment = "done" }, 10))["code"]!.ToString());
        Assert.Equal("review-ref-invalid", (await Verb("complete", new { item = n, holder = "w1", column = "Technical Design", comment = "REVIEW: COMPLETE", review_ref = "rv-forged" }, 10))["code"]!.ToString());
        Assert.Equal("no-verdict-line", (await Review(n, "Technical Design", "fine\nREVIEW: LGTM", 10))["code"]!.ToString());
        Assert.Equal("not-reviewer", (await Verb("record-review", new { item = n, holder = "w1", column = "Technical Design", role = "implementer", text = "x\nREVIEW: COMPLETE" }, 10))["code"]!.ToString());
        for (var i = 1; i <= 3; i++) await Review(n, "Technical Design", "gap\nREVIEW: FINDINGS\n- x");
        Assert.Equal("review-rounds-exhausted", (await Review(n, "Technical Design", "gap\nREVIEW: FINDINGS\n- x", 10))["code"]!.ToString());
        Assert.Equal("completion-owns-exit", (await Verb("move-next", new { item = n }, 10))["code"]!.ToString());
        var fail = await Verb("complete", new { item = n, holder = "w1", column = "Technical Design", succeeded = false, comment = "giving up" });
        Assert.True((bool)fail["parked"]!);
        Assert.Equal("parked", (await Claim(n, "Technical Design", "w9", 10))["code"]!.ToString());
        Assert.Equal("ambiguous-owner", (await Claim(await Issue("Technical Design", "x", "factory worker", "AI Factory"), "Technical Design", "w1", 10))["code"]!.ToString());
        Assert.Equal("no-worker-label", (await Claim(await Issue("Technical Design", "x", "Auto Merge"), "Technical Design", "w1", 10))["code"]!.ToString());
        Assert.Equal("demo-exclusion", (await Claim(await Issue("Technical Design", "x", "factory worker", "Demo"), "Technical Design", "w1", 10))["code"]!.ToString());
        Assert.Equal(Golden("auto", "gates-refuse-what-they-should-c7192337.writes.txt"), Caused(await Recorded("gates")));
    }

    [Fact]
    public async Task Replay_EscalateEarlier_IsTheGoldenFile_TheTimingCommentIncluded()
    {
        await Control("reset");
        await Control("scenario-begin", new { suite = "w92", scenario = "escalate", scenarioSlug = "escalate" });
        const string crit = "The work order list shows the room number.";
        var n = await Issue("Technical Design", $"Acceptance criteria:\n- {crit}\n- The list loads within 2 seconds for 500 work orders.", Labels);
        var r = await Claim(n, "Technical Design");
        Assert.Single(r["timing_criteria"]!.AsArray()); Assert.Contains("within 2 seconds", r["timing_criteria"]![0]!.ToString());
        await Append(n, "Technical Design", "## Technical Design\n\nFiles: src/a.cs\n- add the column.\n");
        await Review(n, "Technical Design", $"REVIEW: FINDINGS\nFINDING: criterion \"{crit}\" - the design adds no column");
        var second = await Review(n, "Technical Design", "REVIEW: FINDINGS\nFINDING: criterion \"The work order list shows the   room number.\" - still missing", 10);
        Assert.Equal("same-criterion-repeated", second["code"]!.ToString());
        var g = await Verb("get-item", new { item = n });
        Assert.Equal("same-criterion-repeated", g["state"]!["park"]!["reason"]!.ToString());
        Assert.Equal("parked", (await Claim(n, "Technical Design", "w1", 10))["code"]!.ToString());
        Assert.False(g["state"]!.AsObject().ContainsKey("cited") && g["state"]!["cited"] is JsonObject { Count: > 0 });
        // a criterion that itself contains quotes is counted
        const string qc = "The button reads \"Save order\" in bold.";
        var k = await Issue("Technical Design", $"Acceptance criteria:\n- {qc}", Labels);
        await Claim(k, "Technical Design");
        await Append(k, "Technical Design", "## Technical Design\n\nFiles: src/a.cs\n- add the button.\n");
        await Review(k, "Technical Design", $"REVIEW: FINDINGS\nFINDING: criterion \"{qc}\" - no bold");
        Assert.Equal("same-criterion-repeated", (await Review(k, "Technical Design", $"REVIEW: FINDINGS\nFINDING: criterion \"{qc}\" - still no bold", 10))["code"]!.ToString());
        // different criteria, or a COMPLETE between, do not escalate
        var m = await Issue("Technical Design", $"Acceptance criteria:\n- {crit}\n- The list is sorted by the created date.", Labels);
        Assert.Null((await Claim(m, "Technical Design"))["timing_criteria"]);
        await Append(m, "Technical Design", "## Technical Design\n\nFiles: src/a.cs\n- add the column.\n");
        await Review(m, "Technical Design", $"REVIEW: FINDINGS\nFINDING: criterion \"{crit}\" - missing");
        await Review(m, "Technical Design", "REVIEW: FINDINGS\nFINDING: criterion \"The list is sorted by the created date.\" - missing");
        await Review(m, "Technical Design", $"REVIEW: FINDINGS\nFINDING: criterion \"{crit}\" - missing again");
        // two consecutive FAIL verdicts that cite the same criterion
        var v = await Issue("Technical Design", $"Acceptance criteria:\n- {crit}", Labels);
        await TechnicalDesign(v);
        var pr = await Development(v);
        await DevComplete(v);
        await Claim(v, "Functional Testing");
        var f1 = await Verb("validation-verdict", new { item = v, holder = "w1", passed = false, summary = $"criterion \"{crit}\" is not met: no column", pr });
        Assert.Equal("demoted", f1["next"]!.ToString());
        await Claim(v, "Development");
        await DevComplete(v);
        await Claim(v, "Functional Testing");
        var f2 = await Verb("validation-verdict", new { item = v, holder = "w1", passed = false, summary = $"criterion \"{crit}\" is still not met", pr });
        Assert.Equal("parked", f2["next"]!.ToString()); Assert.Equal("same-criterion-repeated", f2["reason"]!.ToString());
        Assert.Equal(GoldenWithoutScriptCompletion("escalate-earlier-the-same-criterion-cited-twice-parks-the-item-with-a-question-t-507987f0.writes.txt"), Caused(await Recorded("escalate")));
    }

    [Fact]
    public async Task Replay_DocsOnlyFastPath_IsTheGoldenFile()
    {
        await Control("reset");
        await Control("scenario-begin", new { suite = "w92", scenario = "fast path", scenarioSlug = "fast-path" });
        const string crit = "The README names the health check route /_healthcheck.";
        var n = await Issue("Technical Design", $"Acceptance criteria:\n- {crit}", Labels);
        await Claim(n, "Technical Design");
        await Append(n, "Technical Design", "## Technical Design\n\nFiles: README.md\n<!-- aisf:docs-only -->\n- add one sentence under the heading Health.\n");
        Assert.Equal("finding-without-criterion", (await Review(n, "Technical Design", "The wording could be nicer.\nREVIEW: FINDINGS\n- could also mention the port", 10))["code"]!.ToString());
        Assert.Equal("finding-without-criterion", (await Review(n, "Technical Design", "REVIEW: FINDINGS\nFINDING: criterion \"The README lists every environment variable.\" - missing", 10))["code"]!.ToString());
        Assert.Equal("finding-without-criterion", (await Review(n, "Technical Design", "REVIEW: FINDINGS\nFINDING: criterion \"add one sentence under the heading Health.\" - vague", 10))["code"]!.ToString());
        Assert.Equal("finding-without-criterion", (await Review(n, "Technical Design", "REVIEW: FINDINGS\nFINDING: file \"src/Startup.cs\" - outside the scope", 10))["code"]!.ToString());
        Assert.Null((await Verb("get-item", new { item = n }))["state"]!["reviews"]!["Technical Design"]);   // a refused review used no round
        var r = await Review(n, "Technical Design", $"CRITERION: \"{crit}\" - NO\nFINDING: criterion \"The README names the   health check route /_healthcheck.\" - the design never names the route\nFINDING: nicer wording please\nREVIEW: FINDINGS");
        Assert.Equal(1, (int)r["round"]!); Assert.Equal(2, (int)r["max_rounds"]!); Assert.Single(r["findings"]!.AsArray()); Assert.Equal(1, (int)r["dropped"]!);
        await Review(n, "Technical Design", "REVIEW: FINDINGS\nFINDING: file \"README.md\" - outside the scope");
        Assert.Equal("review-rounds-exhausted", (await Review(n, "Technical Design", "FILE: \"README.md\" - YES\nREVIEW: COMPLETE", 10))["code"]!.ToString());
        // a criterion that itself contains double quotes is quoted whole
        var q = await Issue("Technical Design", "Acceptance criteria:\n- The guide says \"Ready\" in bold.", Labels);
        await Claim(q, "Technical Design");
        await Append(q, "Technical Design", "## Technical Design\n\nFiles: docs/guide.md\n<!-- aisf:docs-only -->\n- edit the guide.\n");
        await Review(q, "Technical Design", "REVIEW: FINDINGS\nFINDING: criterion \"The guide says \"Ready\" in bold.\" - the design does not say bold");
        // the same reviews on an item without the marker: the free form and the three rounds are unchanged
        var m = await Issue("Technical Design", $"Acceptance criteria:\n- {crit}", Labels);
        await Claim(m, "Technical Design");
        await Append(m, "Technical Design", "## Technical Design\n\nFiles: src/a.cs\n- change the handler.\n");
        JsonObject last = new();
        foreach (var _ in new[] { 1, 2 }) last = await Review(m, "Technical Design", "gap\nREVIEW: FINDINGS\n- x");
        Assert.Equal(2, (int)last["round"]!); Assert.Equal(3, (int)last["max_rounds"]!); Assert.False(last.ContainsKey("findings"));
        var done3 = await Review(m, "Technical Design", "fixed\nREVIEW: COMPLETE");
        Assert.Equal(3, (int)done3["round"]!); Assert.NotNull(done3["review_ref"]);
        // one clean round takes a docs-only item out of Technical Design, and the Development review of that item keeps the free form
        var d = await Issue("Technical Design", $"Acceptance criteria:\n- {crit}", Labels);
        await TechnicalDesign(d);
        await Claim(d, "Development");
        var dev = await Review(d, "Development", "gap\nREVIEW: FINDINGS\n- x");
        Assert.Equal(3, (int)dev["max_rounds"]!);
        Assert.Equal(Golden("auto", "docs-only-fast-path-one-design-review-round-a-second-only-for-a-finding-that-quo-2e2268be.writes.txt"), Caused(await Recorded("fast-path")));
    }
}
