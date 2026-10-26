using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// A stand-in for the Copilot API (api.copilot.apiUrl) on a free loopback port: it answers 201 to every POST and 404 to everything else, and
/// keeps each request (method, path, body). With no task running, the Copilot runtime lists without a call, so only the master fix reaches it.
/// </summary>
public sealed class FakeCopilotApi : IDisposable
{
    readonly HttpListener listener = new();
    readonly List<(string Method, string Path, string Body)> requests = new();
    public string Url { get; }

    public FakeCopilotApi()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/");
        listener.Start();
        _ = Task.Run(Serve);
    }

    async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            using var rd = new StreamReader(ctx.Request.InputStream);
            var body = await rd.ReadToEndAsync();
            lock (requests) requests.Add((ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, body));
            ctx.Response.StatusCode = ctx.Request.HttpMethod == "POST" ? 201 : 404;
            ctx.Response.ContentType = "application/json";
            var bytes = "{}"u8.ToArray();
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public IReadOnlyList<(string Method, string Path, string Body)> Requests { get { lock (requests) return requests.ToList(); } }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}

/// <summary>
/// Cursor and Copilot switched on in owner mode: the CI release every second (api.copilot.ciReleaseSeconds 1), the Cursor owner watch every
/// second (api.cursor.ownerWatchSeconds 1), the Copilot credit cap at 50, Copilot's API a <see cref="FakeCopilotApi"/>, Cursor's unreachable.
/// Pinned before C5c moved the CI release, the owner verbs, the settle client and the two owner watches out of Program.cs.
/// </summary>
public sealed class CloudOwnersGateway : GatewayFixture, IDisposable
{
    public FakeCopilotApi Api { get; } = new();

    protected override void Configure(GatewayEnvironment env)
    {
        Directory.CreateDirectory(env.Dir);
        var path = Path.Combine(env.Dir, "instance.owners.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(env.Root, "config", "instance.json")))!;
        cfg["ownerSessions"] = true;
        var m = cfg["messaging"] as JsonObject ?? new JsonObject(); cfg["messaging"] = m;
        m["enabled"] = true; m["testHooks"] = true; m["allowedWorkers"] = new JsonArray("*");
        cfg["intake"] = new JsonObject { ["enabled"] = false };
        cfg["plant"] = new JsonObject { ["oneItemPerSession"] = false };
        var api = (JsonObject)cfg["api"]!;
        var cur = (JsonObject)api["cursor"]!;
        cur["enabled"] = true; cur["apiUrl"] = "http://127.0.0.1:9"; cur["gatewayUrl"] = "http://127.0.0.1:" + GatewayEnvironment.ApiPort; cur["apiKey"] = "test-cursor";
        cur["environments"] = new JsonObject(); cur["ownerWatchSeconds"] = 1;
        var cop = (JsonObject)api["copilot"]!;
        cop["enabled"] = true; cop["apiUrl"] = Api.Url; cop["gatewayUrl"] = "http://127.0.0.1:" + GatewayEnvironment.ApiPort; cop["token"] = "test-copilot";
        cop["environments"] = new JsonObject(); cop["ciRelease"] = true; cop["ciReleaseSeconds"] = 1; cop["listSeconds"] = 1; cop["maxCreditsPerTask"] = 50;
        File.WriteAllText(path, cfg.ToJsonString());
        env.Set("FACTORY_CONFIG", path);
    }

    public void Dispose() => Api.Dispose();
}

public sealed class CloudOwnerGatewayTests(CloudOwnersGateway gw) : IClassFixture<CloudOwnersGateway>
{
    RuntimeRegistry Runtimes => gw.Host.Services.GetRequiredService<RuntimeRegistry>();

    IReadOnlyList<JsonObject> InputsOf(string verb, int item) =>
        StubPwsh.Inputs(gw.Env.StubLog, verb).Select(i => (JsonObject)JsonNode.Parse(i.Text)!).Where(j => (int?)j["item"] == item).ToList();

    static async Task<bool> Until(Func<bool> done, double seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (done()) return true; await Task.Delay(100); }
        return done();
    }

    async Task<JsonNode?> RuntimeStatus(string kind)
    {
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        return a.Json?["runtimes"]?[kind];
    }

    // ---- the owner verbs (rule verb copilot-owner) as the runtimes use them -----------------------------------------------------------------------

    [Fact]
    public async Task LoadOwner_EachRuntimeTakesOnlyItsOwnRecords()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-get-9201", """{"ok":true,"task":{"id":"task-9201","url":"https://github.com/o/r/tasks/1","branch":"copilot/x","phase":"Development","pr":12,"since":"2026-10-01T10:00:00Z","previous":["old-1","","old-2"],"runtime":"copilot-cloud"}}""");
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-get-9202", """{"ok":true,"task":{"id":"agent-9202","url":"u2","branch":"cursor/y","phase":"Functional Testing","pr":"13","runtime":"cursor-cloud"}}""");
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-get-9203", """{"ok":true,"task":{"id":"task-9203"}}""");
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-get-9204", """{"ok":true,"task":{"id":"","runtime":"copilot-cloud"}}""");
        var cop = Runtimes.CopilotRuntime!; var cur = Runtimes.CursorRuntime!;
        Assert.NotNull(cop.LoadOwner); Assert.NotNull(cur.LoadOwner);

        var o = await cop.LoadOwner!(9201, default);
        Assert.NotNull(o);
        Assert.Equal((9201, "task-9201", "https://github.com/o/r/tasks/1", "copilot/x", "Development", 12, "restored"), (o!.Item, o.TaskId, o.Url, o.Branch, o.Phase, o.Pr, o.WaitingOn));
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), o.Since);
        Assert.Equal(DateTimeKind.Utc, o.Since.Kind);
        Assert.Equal(new[] { "old-1", "old-2" }, o.Previous);
        Assert.Null(await cur.LoadOwner!(9201, default));

        // the inverted test: Copilot takes every record but cursor-cloud's, Cursor only cursor-cloud's
        Assert.Null(await cop.LoadOwner!(9202, default));
        var c = await cur.LoadOwner!(9202, default);
        Assert.NotNull(c);
        Assert.Equal((9202, "agent-9202", "u2", "cursor/y", "Functional Testing", 13, "restored"), (c!.Item, c.TaskId, c.Url, c.Branch, c.Phase, c.Pr, c.WaitingOn));
        Assert.Empty(c.Previous);

        Assert.Equal("task-9203", (await cop.LoadOwner!(9203, default))?.TaskId);
        Assert.Null(await cur.LoadOwner!(9203, default));
        Assert.Null(await cop.LoadOwner!(9204, default));
        Assert.Null(await cur.LoadOwner!(9204, default));
        // no record at all (the stub's default answer {"ok":true})
        Assert.Null(await cop.LoadOwner!(9206, default));

        Assert.All(InputsOf("copilot-owner", 9201), j => Assert.Equal("""{"item":9201,"action":"get"}""", j.ToJsonString()));
        Assert.Equal(2, InputsOf("copilot-owner", 9201).Count);
    }

    [Fact]
    public async Task OwnerVerb_ExitNotZero_Throws()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-get-9205", """{"ok":false}""", exit: 3);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Runtimes.CopilotRuntime!.LoadOwner!(9205, default));
        Assert.Equal("copilot-owner get answered exit 3", e.Message);
        e = await Assert.ThrowsAsync<InvalidOperationException>(() => Runtimes.CursorRuntime!.LoadOwner!(9205, default));
        Assert.Equal("copilot-owner get answered exit 3", e.Message);
    }

    [Fact]
    public async Task SaveOwner_WritesTheRecordWithItsRuntime()
    {
        if (!StubPwsh.Supported) return;
        await Runtimes.CopilotRuntime!.SaveOwner!(new CopilotOwner { Item = 9211, TaskId = "t-1", Url = "https://github.com/o/r/tasks/t-1", Branch = "copilot/a", Pr = 21, Phase = "Development", WaitingOn = "x", Sessions = 3 });
        await Runtimes.CursorRuntime!.SaveOwner!(new CopilotOwner { Item = 9212, TaskId = "a-2", Url = "u", Branch = "b", Pr = 0, Phase = "Done" });
        Assert.Equal("""{"item":9211,"action":"set","task":{"id":"t-1","url":"https://github.com/o/r/tasks/t-1","branch":"copilot/a","pr":21,"phase":"Development","runtime":"copilot-cloud"}}""", InputsOf("copilot-owner", 9211).Single().ToJsonString());
        Assert.Equal("""{"item":9212,"action":"set","task":{"id":"a-2","url":"u","branch":"b","pr":0,"phase":"Done","runtime":"cursor-cloud"}}""", InputsOf("copilot-owner", 9212).Single().ToJsonString());
    }

    [Fact]
    public async Task WhyResume_IsTheStatusRework()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-status-9213", """{"ok":true,"rework":"the checks failed: build"}""");
        Assert.Equal("the checks failed: build", await Runtimes.CopilotRuntime!.WhyResume!(9213, default));
        Assert.Equal("the checks failed: build", await Runtimes.CursorRuntime!.WhyResume!(9213, default));
        Assert.Equal("", await Runtimes.CopilotRuntime!.WhyResume!(9214, default));
        Assert.Equal("", await Runtimes.CursorRuntime!.WhyResume!(9214, default));
        Assert.All(InputsOf("copilot-owner", 9213), j => Assert.Equal("""{"item":9213,"action":"status"}""", j.ToJsonString()));
    }

    // ---- the settle client (rule verb copilot-settle) and the managers' Ending -------------------------------------------------------------------

    [Fact]
    public async Task Settle_AnswersTheRuleVerbsAction_OrTheFallbackOnAFailure()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "copilot-settle-9221", """{"ok":true,"action":"awaiting","pr":5}""");
        StubPwsh.Answer(gw.Env.Dir, "copilot-settle-9222", """{"ok":false,"code":"boom"}""", exit: 2);
        foreach (var settle in new[] { Runtimes.CopilotRuntime!.Settle!, Runtimes.CursorRuntime!.Settle! })
        {
            Assert.Equal("awaiting", await settle(9221, "launch", null, default));
            Assert.Equal("awaiting", await settle(9221, "end", "copilot/h", default));
            Assert.Equal("launch", await settle(9222, "launch", null, default));
            Assert.Equal("none", await settle(9222, "end", "", default));
            Assert.Equal("", await settle(9223, "end", null, default));   // exit 0 with no action
        }
        Assert.Equal(new[] { """{"item":9221,"stage":"launch"}""", """{"item":9221,"stage":"end","head":"copilot/h"}""", """{"item":9221,"stage":"launch"}""", """{"item":9221,"stage":"end","head":"copilot/h"}""" },
            InputsOf("copilot-settle", 9221).Select(j => j.ToJsonString()));
        Assert.All(InputsOf("copilot-settle", 9222).Where(j => (string?)j["stage"] == "end"), j => Assert.Equal("""{"item":9222,"stage":"end"}""", j.ToJsonString()));
    }

    [Fact]
    public async Task Ending_SettlesOnlyDevelopment()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "copilot-settle-9231", """{"ok":true,"action":"awaiting"}""");
        StubPwsh.Answer(gw.Env.Dir, "copilot-settle-9232", """{"ok":true,"action":"none"}""");
        foreach (var rm in new[] { Runtimes.Copilot!, Runtimes.Cursor! })
        {
            Assert.True(await rm.Ending!(new RuntimeManager.Exec { Item = 9231, Phase = "development" }));
            Assert.False(await rm.Ending!(new RuntimeManager.Exec { Item = 9232, Phase = "Development" }));
            Assert.False(await rm.Ending!(new RuntimeManager.Exec { Item = 9233, Phase = "Functional Testing" }));
        }
        // the end stage, with no head: Copilot's head of the item is the branch its task reported (none here), Cursor never sends one
        Assert.Equal(new[] { """{"item":9231,"stage":"end"}""", """{"item":9231,"stage":"end"}""" }, InputsOf("copilot-settle", 9231).Select(j => j.ToJsonString()));
        Assert.Empty(InputsOf("copilot-settle", 9233));
    }

    // ---- the Copilot credit guard ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OverBudget_ParksTheItem_CopilotOnly()
    {
        if (!StubPwsh.Supported) return;
        await Runtimes.CopilotRuntime!.OverBudget!(9241, "Development", 61.4);
        Assert.Equal("""{"item":9241,"code":"credit-guard","phase":"Development","by":"the Copilot credit guard: one task reported 61 ai_credits, over api.copilot.maxCreditsPerTask 50"}""", InputsOf("park-item", 9241).Single().ToJsonString());
        StubPwsh.Answer(gw.Env.Dir, "park-item-9242", """{"ok":false}""", exit: 2);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Runtimes.CopilotRuntime!.OverBudget!(9242, "Development", 70));
        Assert.Equal("park-item answered exit 2", e.Message);
    }

    // ---- the wiring the plant and the managers read --------------------------------------------------------------------------------------------

    [Fact]
    public void Wiring_PlantAndManagersReadTheRuntimesOwners()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services; var plant = sp.GetRequiredService<Plant>(); var r = Runtimes;
        Assert.Same(r.CopilotRuntime, plant.CopilotOwns!.Target);
        Assert.Same(r.CopilotRuntime, plant.CopilotOwnersSnapshot!.Target);
        Assert.Same(r.CopilotRuntime, plant.CopilotOwnersRestore!.Target);
        Assert.Same(r.CursorRuntime, plant.CursorOwns!.Target);
        Assert.Same(r.CursorRuntime, plant.CursorOwnersSnapshot!.Target);
        Assert.Same(r.CursorRuntime, plant.CursorOwnersRestore!.Target);
        Assert.Same(r.CopilotRuntime, r.Copilot!.Owned!.Target);
        Assert.Same(r.CopilotRuntime, r.Copilot!.Quota!.Target);
        Assert.Same(r.CursorRuntime, r.Cursor!.Owned!.Target);
        Assert.Same(r.CursorRuntime, r.Cursor!.Quota!.Target);
        Assert.Same(r.CopilotRuntime, sp.GetRequiredService<CopilotCloudSettings>().QuotaBlock!.Target);
        Assert.Same(r.CursorRuntime, sp.GetRequiredService<CursorCloudSettings>().QuotaBlock!.Target);
        Assert.NotNull(r.CopilotRuntime!.OverBudget);
        Assert.Null(typeof(CursorCloudRuntime).GetProperty("OverBudget"));   // the credit guard is Copilot's only
    }

    // ---- the CI release loop ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CiRelease_PassesEveryInterval_WithTheOwnedItems()
    {
        if (!StubPwsh.Supported) return;
        // an owned Copilot item is a candidate of every pass (with the plant's Copilot items)
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-status-9251", """{"ok":true,"waiting":"checks","column":"Development"}""");
        Runtimes.CopilotRuntime!.Restore(new JsonArray(new JsonObject { ["item"] = 9251, ["task"] = "task-9251", ["branch"] = "copilot/c", ["pr"] = 31, ["phase"] = "Development" }));
        Assert.True(await Until(() => StubPwsh.Inputs(gw.Env.StubLog, "ci-release").Any(i => JsonNode.Parse(i.Text)!["items"]!.AsArray().Any(x => (int?)x == 9251))), "no CI release pass with the owned item");
        var pass = (JsonObject)JsonNode.Parse(StubPwsh.Inputs(gw.Env.StubLog, "ci-release").Last().Text)!;
        Assert.Equal(new[] { "items", "skip", "routeAll", "probe" }, pass.Select(kv => kv.Key));
        Assert.Equal(false, (bool?)pass["routeAll"]);
        var before = StubPwsh.Inputs(gw.Env.StubLog, "ci-release").Count;
        Assert.True(await Until(() => StubPwsh.Inputs(gw.Env.StubLog, "ci-release").Count >= before + 2, 10), "the CI release does not pass every second");
        var ci = (await RuntimeStatus("copilot-cloud"))?["ciRelease"];
        Assert.NotNull(ci);
        Assert.Equal(true, (bool?)ci!["enabled"]);
        Assert.Equal(1, (double?)ci["intervalSeconds"]);
        Assert.True((int?)ci["passes"] >= 2);
        // the stub's {"ok":true} names no release: no journal line
        Assert.DoesNotContain(StubPwsh.Inputs(gw.Env.StubLog, "journal"), i => i.Text.Contains("ci-release"));
        await Runtimes.CopilotRuntime!.CloseOwner(9251);
    }

    // ---- the owner watches ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OwnerWatch_Done_ClosesTheOwnership()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-status-9301", """{"ok":true,"waiting":"done","column":"Done","pr":"41","branch":"cursor/done"}""");
        Runtimes.CursorRuntime!.Restore(new JsonArray(new JsonObject { ["item"] = 9301, ["task"] = "agent-9301", ["branch"] = "cursor/done", ["pr"] = 41, ["phase"] = "Functional Testing" }));
        Assert.True(Runtimes.CursorRuntime!.Owns(9301));
        Assert.True(await Until(() => !Runtimes.CursorRuntime!.Owns(9301)), "the Cursor owner watch did not close a done ownership");
        Assert.Equal(new[] { """{"item":9301,"action":"status"}""", """{"item":9301,"action":"close"}""" }, InputsOf("copilot-owner", 9301).Select(j => j.ToJsonString()));
    }

    [Fact]
    public async Task OwnerWatch_Closed_ClosesUnmerged()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-status-9302", """{"ok":true,"waiting":"closed","column":"Done"}""");
        Runtimes.CopilotRuntime!.Restore(new JsonArray(new JsonObject { ["item"] = 9302, ["task"] = "task-9302", ["branch"] = "copilot/closed", ["pr"] = 42, ["phase"] = "Development" }));
        Assert.True(await Until(() => !Runtimes.CopilotRuntime!.Owns(9302)), "the Copilot owner watch did not close an item closed without a merge");
        Assert.Equal(new[] { """{"item":9302,"action":"status"}""", """{"item":9302,"action":"close","unmerged":true}""" }, InputsOf("copilot-owner", 9302).Select(j => j.ToJsonString()));
    }

    [Fact]
    public async Task OwnerWatch_MasterBroken_CopilotResumesItsTaskOnce_CursorOnlyWaits()
    {
        if (!StubPwsh.Supported) return;
        var r = Runtimes;
        var todayBefore = (int?)(await RuntimeStatus("copilot-cloud"))?["today"];
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-status-9303", """{"ok":true,"waiting":"master-broken","column":"Done","pr":"88","branch":"copilot/fix-x","merge_sha":"0123456789abcdef","failed":["build","","unit tests"]}""");
        StubPwsh.Answer(gw.Env.Dir, "copilot-owner-status-9304", """{"ok":true,"waiting":"master-broken","column":"Done","pr":"89","branch":"cursor/z","merge_sha":"fedcba9876543210","failed":["build"]}""");
        r.CopilotRuntime!.Restore(new JsonArray(new JsonObject { ["item"] = 9303, ["task"] = "task-9303", ["url"] = "https://github.com/o/r/tasks/9303", ["phase"] = "Functional Testing" }));
        r.CursorRuntime!.Restore(new JsonArray(new JsonObject { ["item"] = 9304, ["task"] = "agent-9304", ["phase"] = "Functional Testing" }));

        Assert.True(await Until(() => gw.Api.Requests.Any(q => q.Path.EndsWith("/issues/88/comments", StringComparison.Ordinal))), "no master fix was asked for");
        var post = gw.Api.Requests.Single(q => q.Path.EndsWith("/issues/88/comments", StringComparison.Ordinal));
        Assert.Equal(("POST", "/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/88/comments"), (post.Method, post.Path));
        Assert.Equal("@copilot Follow-up session of the task that owns #9303: master is red after PR #88 was merged as 0123456 (failed: build, unit tests). Fix forward: make a new branch from master, fix the failing checks, run the tests, push and open ONE pull request against master titled \"Fix master after #88 (#9303)\". Never wait for CI; end the session after opening it.",
            (string?)JsonNode.Parse(post.Body)!["body"]);
        var own = r.CopilotRuntime!.Owners().Single(o => o.Item == 9303);
        Assert.True(await Until(() => own.WaitingOn == "master-fix"));
        Assert.Equal(("0123456789abcdef", 88, "copilot/fix-x", "Done"), (own.MasterFixFor, own.Pr, own.Branch, own.Phase));
        Assert.Equal(2, own.Sessions);   // restored as one session, the master fix is the second
        Assert.Equal((todayBefore ?? 0) + 1, (int?)(await RuntimeStatus("copilot-cloud"))?["today"]);

        // Cursor has no master fixer: it records what the item waits on, and nothing is posted
        Assert.True(await Until(() => r.CursorRuntime!.Owners().Single(o => o.Item == 9304).WaitingOn == "master-broken"));
        var cu = r.CursorRuntime!.Owners().Single(o => o.Item == 9304);
        Assert.Equal(("", 89, "cursor/z", "Done"), (cu.MasterFixFor, cu.Pr, cu.Branch, cu.Phase));
        Assert.DoesNotContain(gw.Api.Requests, q => q.Path.EndsWith("/issues/89/comments", StringComparison.Ordinal));

        // one status read per owned item at most every max(30 s, 3 x the interval): no second read within the next 3 s
        await Task.Delay(3000);
        Assert.Single(InputsOf("copilot-owner", 9303));
        Assert.Single(InputsOf("copilot-owner", 9304));
        Assert.Single(gw.Api.Requests, q => q.Path.EndsWith("/issues/88/comments", StringComparison.Ordinal));
        await r.CopilotRuntime!.CloseOwner(9303);
        await r.CursorRuntime!.CloseOwner(9304);
    }
}
