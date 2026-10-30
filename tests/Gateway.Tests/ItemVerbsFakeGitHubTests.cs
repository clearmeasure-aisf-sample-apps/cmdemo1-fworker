using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.2 (#354): the item verbs on the wire. The fake GitHub of the Tier 1 suites (tests/FakeGitHub, started as a process of its own) is the GitHub, the gateway's wiring of the verbs (ItemVerbsWiring: the wire, the state comment
/// store, the journal ledger, the lock file) is the real one, and the writes the fake records are compared with the golden files of tests/scenarios/golden, which the rule scripts wrote: the fake normalizes them (the numbers of
/// items and pull requests, the commits), so a line is the same text whichever engine wrote it.
/// </summary>
public sealed partial class ItemVerbsFakeGitHubTests : IAsyncLifetime
{
    Process? fake;
    HttpClient control = null!;
    string work = "";
    int port;
    IVerbRouter router = null!;

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    public async Task InitializeAsync()
    {
        var root = Root();
        var dll = new[] { "Release", "Debug" }.Select(c => Path.Combine(root, "tests", "FakeGitHub", "bin", c, "net10.0", "FakeGitHub.dll")).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("tests/FakeGitHub is not built (Gateway.Tests references it)");
        work = Directory.CreateTempSubdirectory("item-verbs-fake-").FullName;
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["FAKE_PORT"] = port.ToString();
        psi.Environment["FAKE_RECORD_DIR"] = Path.Combine(work, "writes");
        psi.Environment["FACTORY_CONFIG"] = Path.Combine(root, "config", "instance.json");
        fake = Process.Start(psi)!;
        _ = fake.StandardOutput.ReadToEndAsync(); _ = fake.StandardError.ReadToEndAsync();
        control = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) };
        for (var i = 0; ; i++)
        {
            try { if ((await control.GetAsync("_fake/state")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
            if (i > 100) throw new TimeoutException("the fake GitHub did not start");
            await Task.Delay(100);
        }
        var env = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "fake-token", ["GITHUB_API_URL"] = $"http://127.0.0.1:{port}", ["FACTORY_STATE_DIR"] = Path.Combine(work, "state"), ["FACTORY_SERVER_LOGIN"] = "factory-bot",
            ["FACTORY_ENVIRONMENT"] = "v2test",
        };
        var services = new ServiceCollection().AddSingleton(InstanceConfig.Load(Path.Combine(root, "config", "instance.json"), n => env.GetValueOrDefault(n))).BuildServiceProvider();
        router = ItemVerbsWiring.Make(services, n => env.GetValueOrDefault(n)) ?? throw new InvalidOperationException("the item verbs were not made");
    }

    public Task DisposeAsync()
    {
        try { fake?.Kill(true); } catch { }
        control?.Dispose();
        try { Directory.Delete(work, true); } catch { }
        return Task.CompletedTask;
    }

    async Task<JsonNode?> Control(string path, object? body = null)
    {
        var r = await control.PostAsync("_fake/" + path, new StringContent(System.Text.Json.JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json"));
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{path}: {(int)r.StatusCode} {text}");
        return JsonNode.Parse(text);
    }

    async Task<JsonObject> Verb(string verb, object args, int exit = 0)
    {
        var a = await router.RunAsync(verb, System.Text.Json.JsonSerializer.Serialize(args), TimeSpan.FromSeconds(60));
        Assert.True(a.Code == exit, $"{verb}: exit {a.Code} (expected {exit}): {a.Output}");
        return JsonNode.Parse(a.Output)!.AsObject();
    }

    async Task<int> Issue(string column, string body = "x", params string[] labels) =>
        (await Control("issue", new { title = "Trial", body, labels = labels.Length > 0 ? labels : ["factory worker"], column }))!["number"]!.GetValue<int>();

    async Task<JsonNode> Comments(int item) => JsonNode.Parse(await control.GetStringAsync($"repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/{item}/comments?per_page=100"))!;

    async Task<JsonObject> StateOf(int item)
    {
        foreach (var c in (await Comments(item)).AsArray())
            if (c!["body"]!.ToString().StartsWith("<!-- factory-state:factory-worker v1 -->", StringComparison.Ordinal))
            {
                var b = c["body"]!.ToString(); var s = b.IndexOf("```json", StringComparison.Ordinal) + 7; var e = b.LastIndexOf("```", StringComparison.Ordinal);
                return JsonNode.Parse(b[s..e])!.AsObject();
            }
        return new JsonObject();
    }

    // the lines the fake recorded, as it normalizes them, in the order they came
    async Task<List<string>> Recorded(string slug)
    {
        var end = await Control("scenario-end");
        var raw = end!["raw"]!.ToString();
        return File.ReadAllLines(raw).Select(l => JsonNode.Parse(l)!["line"]!.ToString()).ToList();
    }

    static List<string> Golden(string suite, string file) =>
        File.ReadAllLines(Path.Combine(Root(), "tests", "scenarios", "golden", suite, file)).Where(l => l.StartsWith("= ")).Select(l => l[2..]).ToList();

    [Fact]
    public async Task AnItemThroughItsDesignAndItsPullRequest_WritesWhatTheGoldenFileOfTheRuleScriptsShows()
    {
        await Control("reset");
        await Control("scenario-begin", new { suite = "w92", scenario = "item verbs", scenarioSlug = "item-flow" });
        var n = await Issue("Technical Design", "x");
        await Verb("claim-item", new { item = n, holder = "alice" });
        await Task.Delay(30);   // the append lands after the dispatch
        var append = await Verb("append-body", new { item = n, content = "## Technical Design\n\n<!-- aisf:docs-only -->\n- change as described in the item.\n", column = "Technical Design" });
        Assert.True((bool)append["appended"]!);
        var review = await Verb("record-review", new { item = n, column = "Technical Design", role = "reviewer", text = "REVIEW: COMPLETE" });
        var done = await Verb("complete", new { item = n, holder = "alice", comment = "REVIEW: COMPLETE - design done", review_ref = review["review_ref"]!.ToString(), column = "Technical Design" });
        Assert.Equal("Development", done["moved_to"]!.ToString());
        await Control("push-branch", new { branch = $"factory-worker/{n}-development", files = new[] { "src/App.cs" } });
        var pr = await Verb("open-pr", new { item = n, head = $"factory-worker/{n}-development", title = $"Trial {n}", body = "change" });
        Assert.True((bool)pr["created"]!);
        var number = (int)pr["pullRequest"]!["number"]!;
        var upd = await Verb("update-pr", new { item = n, body = "corrected text" });
        Assert.True((bool)upd["updated"]!);

        var lines = await Recorded("item-flow");
        var golden = Golden("owner", "owner-update-pr-replaces-the-body-of-the-item-s-own-open-pull-request-and-of-no-93a70b62.writes.txt");
        // the verbs' own writes, without the claim ref and the state comment (the "+" lines of the golden file)
        var mine = lines.Where(l => l.StartsWith("rule ") && !l.Contains(" STATE ") && !l.Contains("{factory-state") && !l.Contains("/git/refs") && !l.Contains("/issues/comments/")).ToList();
        // golden: the first four "=" lines are item #1 through design and development (the harness opens its pull request the same way)
        Assert.Equal(golden.Take(4), mine.Take(4));
        Assert.Equal($"rule PATCH /repos/{{repo}}/pulls/{{#{number - n + 1}}} 200 {{\"body\":\"corrected text\\n\\nRefs #{{#1}}\"}}", mine[4]);
        // the claim ref and the state comment are the script's too: the same lines, from "rule", not "gateway"
        Assert.Contains(lines, l => l.StartsWith("rule POST /repos/{repo}/git/refs 201 {\"ref\":\"refs/factory/factory-worker/claims/{#1}\""));
        Assert.Contains("rule STATE factory-worker v1 #{#1}", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("gateway "));
    }

    [Fact]
    public async Task TheClaimTheStateCommentAndTheJournalLineOfAnAcceptAreInTheFakesComments()
    {
        await Control("reset");
        var n = await Issue("Technical Design", "x");
        var a = await Verb("accept", new { item = n, assignment = "a-1", holder = "alice", model = "m", effort = "high" });
        Assert.True((bool)a["claimed"]!); Assert.Equal("own-item", a["kind"]!.ToString());
        var s = await StateOf(n);
        Assert.Equal("alice", s["claim"]!["holder"]!.ToString()); Assert.True((bool)s["claim"]!["owner"]!); Assert.Equal(2, (int)s["rev"]!);   // the claim, then the model tier of the accept
        Assert.Equal("Technical Design", s["flow"]![0]!["c"]!.ToString());
        // a second call reads the state it wrote (revision 2) and writes revision 3
        await Verb("claim-item", new { item = n, holder = "alice", renew = true });
        Assert.Equal(3, (int)(await StateOf(n))["rev"]!);
        // park and unpark: the journal lines are the ledger's comments, the park leaves the state
        await Verb("park-item", new { item = n, code = "runtime-failed", phase = "Technical Design", by = "the runtime adapter" });
        Assert.Equal("runtime-failed", (await StateOf(n))["park"]!["reason"]!.ToString());
        var un = await Verb("unpark-item", new { item = n, reason = "fixed by hand", by = "jeffrey" });
        Assert.True((bool)un["unparked"]!);
        var comments = (await Comments(n)).AsArray().Select(c => c!["body"]!.ToString()).ToList();
        Assert.Contains(comments, c => c.Contains("event=parked") && c.StartsWith("\U0001F3ED Plant manager:"));
        Assert.Contains(comments, c => c.Contains("park `runtime-failed` released early by jeffrey: fixed by hand."));
        var after = await StateOf(n);
        Assert.Null(after["park"]); Assert.False((bool)un["reoffer"]!);   // the owner still holds the item: nothing is offered
        var keys = after["journal"]!["keys"]!.AsArray().Select(k => k!.ToString()).ToList();
        Assert.Contains(keys, k => k.StartsWith("parked|")); Assert.Contains(keys, k => k.StartsWith("unparked|"));
    }

    [Fact]
    public async Task AFailVerdictDemotesTheItem_AndTheBoardIsReadByMyItems()
    {
        await Control("reset");
        var n = await Issue("Functional Testing", "x", "factory worker", "Auto Merge");
        await Verb("accept", new { item = n, assignment = "a-1", holder = "alice" });
        var rv = await Verb("record-review", new { item = n, column = "Functional Testing", role = "reviewer", text = "REVIEW: COMPLETE" });
        await Control("push-branch", new { branch = $"factory-worker/{n}-development", files = new[] { "src/App.cs" } });
        await Control("move", new { number = n, column = "Development" });
        var o = await Verb("open-pr", new { item = n, head = $"factory-worker/{n}-development", title = "Trial", body = "change" }, 0);
        var prn = (int)o["pullRequest"]!["number"]!;
        await Control("move", new { number = n, column = "Functional Testing" });
        var mine = await Verb("my-items", new { holder = "alice" });
        Assert.Equal(n, (int)mine["items"]![0]!["item"]!);
        var v = await Verb("validation-verdict", new { item = n, holder = "alice", passed = false, summary = "the total is wrong", pr = prn });
        Assert.Equal("demoted", v["next"]!.ToString());
        var item = JsonNode.Parse(await (await control.GetAsync("_fake/state")).Content.ReadAsStringAsync())!["issues"]!.AsArray().Single(i => (int)i!["number"]! == n)!;
        Assert.Equal("Development", item["column"]!.ToString());
        var s = await StateOf(n);
        Assert.Equal(1, (int)s["demotions"]!); Assert.Equal("the total is wrong", s["pr"]!["rejection"]!.ToString());
        var g = await Verb("get-item", new { item = n });
        Assert.Equal("Development", g["status"]!.ToString()); Assert.Equal(prn, (int)g["existing_pr"]!["number"]!);
        Assert.All(g["comments"]!.AsArray(), c => Assert.Equal("factory", c!["author_trust"]!.ToString()));   // the comments are the factory's own
    }
}
