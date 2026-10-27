using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// A gateway for the answers of the C7b routes: C7a's pin gateway (a revocation read, the operator session, the backlog keeper and the supervisor
/// switched on and each holding its slot, a supervisor step budget of one per hour) plus the Octopus runbook runtime switched on against a server
/// that does not answer, with a host token, so the host-token checks and the answers behind them are reached.
/// </summary>
public sealed class C7bPinGateway : IAsyncLifetime
{
    public GatewayEnvironment Env { get; } = new();
    public GatewayHost Host { get; private set; } = null!;
    /// <summary>The Octopus hosts' token, composed at run time.</summary>
    public string HostToken { get; } = "host-" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        if (!StubPwsh.Supported) return;
        Env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        Env.Set("GH_TOKEN", "fake");
        Env.Set("FACTORY_TRANSCRIPTS", "0");
        Env.Set("OCTOPUS_RUNBOOK_API_KEY", "API-" + new string('K', 12));
        Env.Set("FACTORY_OCTOPUS_HOST_TOKEN", HostToken);
        Env.Apply();
        StubPwsh.Answer(Env.Dir, "operator-releases", "{\"ok\":true,\"releases\":[]}");
        var cfgPath = Path.Combine(Env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        cfg["operator"]!["enabled"] = true;
        cfg["operator"]!["allowedNames"] = new JsonArray("operator-session", "operator-two");
        cfg["keeper"]!["enabled"] = true;
        cfg["manager"]!["enabled"] = true;
        cfg["manager"]!["stepBudgetPerHour"] = 1;
        // tdd's runtimes block: the Octopus runtime on, against a port nothing listens on (every call to the server fails at once)
        var rt = (JsonObject)cfg["runtimes"]!.DeepClone();
        rt.Remove("environments");
        var oct = (JsonObject)rt["octopus"]!;
        oct["enabled"] = true; oct["serverUrl"] = "http://127.0.0.1:9"; oct["auth"] = "apiKey"; oct["maxConcurrent"] = 1; oct["maxExecutionsPerDay"] = 1;
        cfg["runtimes"]!["environments"] = new JsonObject { ["tdd"] = rt };
        File.WriteAllText(cfgPath, cfg.ToJsonString());
        Host = new GatewayHost();
        _ = Host.Server;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && (await Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status != 200) await Task.Delay(100);
        var releases = Host.Services.GetRequiredService<IOperatorReleaseLoader>();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && !releases.IsLoaded) await Task.Delay(50);
        var revocations = Host.Services.GetRequiredService<RevocationService>();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && !revocations.IsLoaded) await Task.Delay(50);
        Assert.True(revocations.IsLoaded, "the revoked credentials were not read");
        Assert.NotNull(Host.Services.GetRequiredService<RuntimeRegistry>().OctopusRuntime);
        var plant = Host.Services.GetRequiredService<Plant>();
        Assert.True(plant.RegisterOperator("operator-session", "claude-opus-5-5", "low", 0).Ok);
        Assert.True(plant.RegisterSupervisor("supervisor", "claude-opus-5-5", "high", 0).Ok);
        Assert.True(plant.RegisterKeeper("backlog-keeper", "claude-opus-5-5", "medium", 0).Ok);
    }

    public async Task DisposeAsync()
    {
        try { if (Host != null) await Host.DisposeAsync(); }
        finally { Env.Dispose(); }
    }
}

/// <summary>What the pins of the C7b routes ask and how they write an answer as one line.</summary>
public static class C7bPins
{
    /// <summary>One request on <paramref name="port"/> with the Authorization header as given (null: none), extra headers and the body as bytes.</summary>
    public static async Task<EndpointPins.Full> Send(GatewayHost host, string method, string pathAndQuery, string? authorization, byte[]? body,
        int port = GatewayEnvironment.ApiPort, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var q = pathAndQuery.IndexOf('?');
        var ctx = await host.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("127.0.0.1", port);
            c.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
            if (q >= 0) c.Request.QueryString = new QueryString(pathAndQuery[q..]);
            c.Connection.LocalPort = port;
            c.Connection.LocalIpAddress = IPAddress.Loopback;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (authorization != null) c.Request.Headers.Authorization = authorization;
            if (headers != null) foreach (var (k, v) in headers) c.Request.Headers[k] = v;
            var bytes = body ?? Array.Empty<byte>();
            c.Request.Body = new MemoryStream(bytes);
            c.Request.ContentLength = bytes.Length;
            if (body != null) c.Request.ContentType = "application/json";
        }, cts.Token);
        var ms = new MemoryStream();
        if (ctx.Response.Body is { CanRead: true } rb) await rb.CopyToAsync(ms, cts.Token);
        return new EndpointPins.Full(ctx.Response.StatusCode, ctx.Response.ContentType ?? "", ctx.Response.Headers.Location.ToString(), ctx.Response.Headers.ContentDisposition.ToString(), ms.ToArray());
    }

    // the 200 answers whose body changes from run to run (times, counters): their shape is pinned, not their text
    static readonly string[] ShapeOnly = { "/api/v1/base-update", "/api/v1/manager/actions" };

    /// <summary>
    /// One pin line as <see cref="EndpointPins.Line"/> writes it, with what changes from run to run masked first: a minted credential (its kind
    /// prefix kept), a runtime credential's expiry, a build slot's end and every retryAfterSeconds; the answers of <see cref="ShapeOnly"/> and a transcript read by shape.
    /// </summary>
    public static string Line(string path, EndpointPins.Full a)
    {
        var p = path.Split('?')[0];
        var text = Encoding.UTF8.GetString(a.Body);
        JsonNode? j = null; try { j = text.Length > 0 && a.ContentType.StartsWith("application/json", StringComparison.Ordinal) ? JsonNode.Parse(text) : null; } catch { }
        if (j is JsonObject o)
        {
            if (a.Status == 200 && (ShapeOnly.Contains(p) || p.StartsWith("/api/v1/sessions/", StringComparison.Ordinal)))
                return $"{a.Status} | {a.ContentType} | loc={a.Location} | disp={a.Disposition} | shape " + string.Join(",", o.Select(kv => kv.Value is JsonValue v && v.TryGetValue<bool>(out var b) ? $"{kv.Key}={b}" : kv.Key));
            var masked = false;
            if (o["token"] is JsonValue tv && tv.TryGetValue<string>(out var tok)) { o["token"] = (tok.IndexOf('.') is var dot and > 0 ? tok[..dot] : "") + ".masked"; masked = true; }
            foreach (var k in new[] { "expires", "until" }) if (o.ContainsKey(k)) { o[k] = "masked"; masked = true; }
            if (o.ContainsKey("retryAfterSeconds")) { o["retryAfterSeconds"] = "masked"; masked = true; }
            if (masked) return $"{a.Status} | {a.ContentType} | loc={a.Location} | disp={a.Disposition} | {o.ToJsonString()}";
        }
        return EndpointPins.Line(path, a);
    }

    /// <summary>A header of an MP4 file (an ftyp box), then a few bytes.</summary>
    public static readonly byte[] Mp4 = { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, 1, 2, 3, 4 };

    /// <summary>The C7b routes as (method, path and query, body label, body): each route of each module with the bodies and queries its rules read.</summary>
    public static IReadOnlyList<(string Method, string Path, string Label, byte[]? Body)> Requests()
    {
        (string, string, string, byte[]?) J(string m, string p, string? body) => (m, p, body == null ? "" : body.Length > 60 ? $" body[{body.Length}]" : " " + body, body == null ? null : Encoding.UTF8.GetBytes(body));
        (string, string, string, byte[]?) B(string m, string p, string label, byte[] body) => (m, p, " " + label, body);
        return new[]
        {
            // worker admin
            J("POST", "/api/v1/base-update", null),
            J("POST", "/api/v1/release-queue/promotion-request", null), J("POST", "/api/v1/release-queue/promotion-request", "{}"),
            J("POST", "/api/v1/release-queue/promotion-request", "not json"), J("POST", "/api/v1/release-queue/promotion-request", "{\"release\":\"1.2.3\"}"),
            J("POST", "/api/v1/release-queue/promotion-request", "{\"release\":\"" + new string('1', 5000) + "\"}"),
            J("POST", "/api/v1/workers/alice/credential", null), J("POST", "/api/v1/workers/alice/credential?role=bogus", null),
            J("POST", "/api/v1/workers/a.b/credential", null), J("POST", "/api/v1/workers/gateway/credential", null),
            J("POST", "/api/v1/workers/supervisor/credential?role=manager", null), J("POST", "/api/v1/workers/alice/credential?role=manager", null),
            J("POST", "/api/v1/workers/operator-session/credential?role=operator", null), J("POST", "/api/v1/workers/alice/credential?role=operator", null),
            J("POST", "/api/v1/workers/backlog-keeper/credential?role=keeper", null), J("POST", "/api/v1/workers/alice/credential?role=keeper", null),
            J("POST", "/api/v1/workers/a.b/revoke", null),
            J("POST", "/api/v1/runtimes/octopus/reset", null), J("POST", "/api/v1/runtimes/cursor/reset", null),
            J("POST", "/api/v1/workers/alice/capacity", null), J("POST", "/api/v1/workers/alice/capacity?value=3", null), J("POST", "/api/v1/workers/alice/capacity?value=21", null),
            J("POST", "/api/v1/workers/nobody/capacity?value=2", null), J("POST", "/api/v1/workers/supervisor/capacity?value=2", null),
            J("POST", "/api/v1/workers/alice/messaging?value=mute", null), J("POST", "/api/v1/workers/alice/messaging?value=on", null), J("POST", "/api/v1/workers/alice/messaging?value=loud", null),
            J("POST", "/api/v1/workers/alice/drain", null), J("POST", "/api/v1/workers/alice/resume", null), J("POST", "/api/v1/workers/a.b/drain", null),
            J("POST", "/api/v1/workers/alice/ping", null), J("POST", "/api/v1/workers/bob/drain", null),
            // runtime hosts (with the caller's credential, which is no host token)
            J("GET", "/api/v1/runtime/octopus/desired?host=h1", null), J("POST", "/api/v1/runtime/octopus/report", "{\"host\":\"h1\",\"containers\":[]}"),
            J("POST", "/api/v1/runtime/redeem", "{}"), J("POST", "/api/v1/runtime/redeem", "{\"nonce\":\"n1.00\"}"), J("POST", "/api/v1/runtime/redeem", "{\"nonce\":\"" + new string('n', 1001) + "\"}"),
            J("POST", "/api/v1/build-slot", "not json"), J("POST", "/api/v1/build-slot", "{\"action\":\"x\"}"), J("POST", "/api/v1/build-slot", "{\"ttlSeconds\":-1}"),
            J("POST", "/api/v1/build-slot", "{\"ttlSeconds\":\"x\"}"), J("POST", "/api/v1/build-slot", "{\"action\":\"acquire\",\"ttlSeconds\":60,\"item\":3}"),
            J("POST", "/api/v1/build-slot", "{\"action\":\"release\",\"item\":3}"), J("POST", "/api/v1/build-slot", "{\"x\":\"" + new string('y', 5000) + "\"}"),
            // Copilot
            J("POST", "/api/v1/copilot/exchange", "{\"code\":\"c\"}"), J("POST", "/api/v1/copilot/bootstrap-token", null), J("GET", "/fworker-mcp.mjs", null),
            // transcripts
            J("POST", "/api/v1/runtime/transcript", "{}"), J("POST", "/api/v1/runtime/transcript", "not json"), J("POST", "/api/v1/runtime/transcript", "{\"lines\":[\"one\",\"two\"]}"),
            J("POST", "/api/v1/runtime/transcript", "{\"lines\":[" + string.Join(",", Enumerable.Repeat("\"l\"", 501)) + "]}"),
            J("GET", "/api/v1/sessions/alice", null), J("GET", "/api/v1/sessions/alice?after=1", null), J("GET", "/api/v1/sessions/nobody", null), J("GET", "/api/v1/sessions/a.b", null),
            J("GET", "/sessions/alice", null), J("GET", "/sessions/a.b", null),
            // item admin
            J("GET", "/api/v1/manager/actions", null),
            J("POST", "/api/v1/manager/mode", null), J("POST", "/api/v1/manager/mode?value=loud", null), J("POST", "/api/v1/manager/mode?value=advise", null), J("POST", "/api/v1/manager/mode?value=act", null),
            J("POST", "/api/v1/items/1/unpark", null), J("POST", "/api/v1/items/1/unpark", "not json"), J("POST", "/api/v1/items/1/unpark", "{\"reason\":\"\"}"),
            J("POST", "/api/v1/items/1/unpark", "{\"reason\":\"the criterion was relaxed\"}"),
            J("POST", "/api/v1/items/1/resettle", null), J("POST", "/api/v1/items/1/resettle", "[1]"), J("POST", "/api/v1/items/1/resettle", "{\"reason\":\"" + new string('r', 501) + "\"}"),
            J("POST", "/api/v1/items/1/resettle", "{\"reason\":\"merged before the gateway knew\"}"),
            // demo
            B("POST", "/api/v1/demo/1", "empty", Array.Empty<byte>()), B("POST", "/api/v1/demo/1", "text", Encoding.UTF8.GetBytes("not a video at all")),
            B("POST", "/api/v1/demo/1", "mp4", Mp4), B("POST", "/api/v1/demo/2", "mp4", Mp4),
        };
    }

    /// <summary>"sha256=" and the HMAC of the body with the webhook secret, as GitHub signs a delivery.</summary>
    public static string Sign(string secret, byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
}

/// <summary>
/// The answers of the routes C7b moves into endpoint modules (runtime hosts, Copilot, transcripts, webhook, demo, item admin, worker admin), pinned
/// from master before the move (Pins/c7b-endpoints.json) beyond what the caller matrix pins: the content type, Location and Content-Disposition, the
/// whole body (masked where it changes from run to run), each route's body and query rules for each caller kind, the Octopus hosts' token, the
/// runtime nonce's redemption, the Copilot exchange, the webhook's signature, events and duplicate deliveries, the operator's revoke last, and the
/// same endpoints called in process (InProcess, as an MCP tool would call them), so their endpoint filters are shown to run on that path too. The
/// callers ask in a fixed order and some answers depend on it (the supervisor's one step, the build slot, the admin's mode switch).
/// </summary>
public sealed class C7bEndpointPinTests(C7bPinGateway gw) : IClassFixture<C7bPinGateway>
{
    [Fact]
    public async Task C7bRoutes_EveryCaller_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        var secret = gw.Env.TokenSecret;
        var msg = gw.Host.Services.GetRequiredService<MessagingSettings>();
        var keeper = gw.Host.Services.GetRequiredService<KeeperSettings>();
        var copilot = gw.Host.Services.GetRequiredService<CopilotCloudSettings>();
        var hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var callers = new (string Caller, string? Authorization)[]
        {
            ("anonymous", null),
            ("worker", AuthPins.Bearer(Auth.WorkerToken(secret, "alice"))),
            ("runtime-worker", AuthPins.Bearer(Auth.WorkerToken2(secret, "rt1", "octopus", hour))),
            ("item-owner", AuthPins.Bearer(Auth.MintItemToken(secret, 1, "owner", "a1", "alice", TimeSpan.FromHours(1)))),
            ("item-inspector", AuthPins.Bearer(Auth.MintItemToken(secret, 1, "inspector", "a2", "bob", TimeSpan.FromHours(1)))),
            ("supervisor", AuthPins.Bearer(Auth.ManagerToken(secret, "supervisor", msg.TokenGeneration))),
            ("keeper", AuthPins.Bearer(Auth.KeeperToken(secret, "backlog-keeper", keeper.TokenGeneration))),
            ("operator-active", AuthPins.Bearer(Auth.OperatorToken(secret, "operator-session", msg.OperatorTokenGeneration))),
            ("operator-idle", AuthPins.Bearer(Auth.OperatorToken(secret, "operator-two", msg.OperatorTokenGeneration))),
            ("admin", AuthPins.Bearer(GatewayEnvironment.AdminToken)),
        };
        var admin = callers.Single(c => c.Caller == "admin").Authorization;
        var lines = new List<KeyValuePair<string, string>>();
        async Task Ask(string label, string? auth, string method, string path, string shown, byte[]? body, int port = GatewayEnvironment.ApiPort, IReadOnlyDictionary<string, string>? headers = null)
        {
            var a = await C7bPins.Send(gw.Host, method, path, auth, body, port, headers);
            lines.Add(new($"{label} {method} {path}{shown}", C7bPins.Line(path, a)));
        }
        byte[] U(string s) => Encoding.UTF8.GetBytes(s);

        // the webhook (anonymous, signed with the webhook secret): the signature, ping, an event the gateway ingests, its duplicate delivery, an event it
        // ignores; on both ports of two-port mode. The ingested events are counted from the stub's log.
        var hookSecret = new string('x', 40);
        foreach (var (port, pl) in new[] { (GatewayEnvironment.HookPort, "hook-port"), (GatewayEnvironment.ApiPort, "api-port") })
        {
            var issues = U("{\"action\":\"edited\",\"issue\":{\"number\":5}}");
            var d = pl + "-d1";
            Dictionary<string, string> H(string ev, string delivery, byte[] b, string? sig = null) => new() { ["X-GitHub-Event"] = ev, ["X-GitHub-Delivery"] = delivery, ["X-Hub-Signature-256"] = sig ?? C7bPins.Sign(hookSecret, b) };
            await Ask($"webhook {pl} unsigned", null, "POST", "/webhooks/github", " issues", issues, port, new Dictionary<string, string> { ["X-GitHub-Event"] = "issues" });
            await Ask($"webhook {pl} bad-signature", null, "POST", "/webhooks/github", " issues", issues, port, H("issues", d, issues, "sha256=" + new string('0', 64)));
            await Ask($"webhook {pl} ping", null, "POST", "/webhooks/github", " {}", U("{}"), port, H("ping", pl + "-p", U("{}")));
            await Ask($"webhook {pl} issues", null, "POST", "/webhooks/github", " issues", issues, port, H("issues", d, issues));
            await Ask($"webhook {pl} issues again", null, "POST", "/webhooks/github", " issues", issues, port, H("issues", d, issues));
            await Ask($"webhook {pl} star", null, "POST", "/webhooks/github", " {}", U("{}"), port, H("star", pl + "-s", U("{}")));
            await Ask($"webhook {pl} no delivery", null, "POST", "/webhooks/github", " issues", issues, port, H("issues", "", issues));
            await Ask($"webhook {pl} no delivery again", null, "POST", "/webhooks/github", " issues", issues, port, H("issues", "", issues));
        }
        lines.Add(new("webhook ingest-event calls", StubPwsh.Read(gw.Env.StubLog).Count(c => c.Verb == "ingest-event").ToString()));

        // the Octopus hosts' token: none, a wrong one, the right one with and without a host and a body (the server does not answer)
        var hostAuth = AuthPins.Bearer(gw.HostToken);
        foreach (var (label, auth) in new[] { ("host-none", (string?)null), ("host-wrong", AuthPins.Bearer("host-" + new string('0', 32))), ("host-token", hostAuth) })
        {
            await Ask(label, auth, "GET", "/api/v1/runtime/octopus/desired", "", null);
            await Ask(label, auth, "GET", "/api/v1/runtime/octopus/desired?host=h1", "", null);
            await Ask(label, auth, "GET", "/api/v1/runtime/octopus/desired?host=" + new string('h', 201), "", null);
            await Ask(label, auth, "POST", "/api/v1/runtime/octopus/report", " not json", U("not json"));
            await Ask(label, auth, "POST", "/api/v1/runtime/octopus/report", " {\"host\":\"h1\"}", U("{\"host\":\"h1\"}"));
            await Ask(label, auth, "POST", "/api/v1/runtime/octopus/report", " {\"host\":\"h1\",\"containers\":[]}", U("{\"host\":\"h1\",\"containers\":[]}"));
        }

        // a runtime worker's nonce (minted by the test hook, as the adapter mints one), redeemed once
        var minted = await C7bPins.Send(gw.Host, "POST", "/api/v1/_test/runtime-nonce", admin, U("{\"name\":\"rt9\",\"runtime\":\"octopus\"}"));
        var nonce = JsonNode.Parse(minted.Body)!["nonce"]!.ToString();
        await Ask("nonce", null, "POST", "/api/v1/runtime/redeem", " minted", U(new JsonObject { ["nonce"] = nonce }.ToJsonString()));
        await Ask("nonce", null, "POST", "/api/v1/runtime/redeem", " minted again", U(new JsonObject { ["nonce"] = nonce }.ToJsonString()));

        // the Copilot exchange: the bootstrap token with a bad code, a task's code, the code again; a bootstrap token of another generation
        var boot = AuthPins.Bearer(Auth.CopilotBootstrapToken(secret, copilot.BootstrapGeneration));
        var code = gw.Host.Services.GetRequiredService<RuntimeRegistry>().CopilotCodes.Mint("cp1", Auth.WorkerToken(secret, "cp1"), TimeSpan.FromMinutes(5));
        await Ask("bootstrap", boot, "POST", "/api/v1/copilot/exchange", " bad code", U("{\"code\":\"nope\"}"));
        await Ask("bootstrap", boot, "POST", "/api/v1/copilot/exchange", " not json", U("not json"));
        await Ask("bootstrap", boot, "POST", "/api/v1/copilot/exchange", " too large", U("{\"code\":\"" + new string('c', 1000) + "\"}"));
        await Ask("bootstrap", boot, "POST", "/api/v1/copilot/exchange", " code", U(new JsonObject { ["code"] = code }.ToJsonString()));
        await Ask("bootstrap", boot, "POST", "/api/v1/copilot/exchange", " code again", U(new JsonObject { ["code"] = code }.ToJsonString()));
        await Ask("bootstrap-old", AuthPins.Bearer(Auth.CopilotBootstrapToken(secret, copilot.BootstrapGeneration + 1)), "POST", "/api/v1/copilot/exchange", " code", U("{\"code\":\"nope\"}"));

        // every caller, every request, in order (the admin last)
        foreach (var (caller, auth) in callers)
            foreach (var (m, p, shown, b) in C7bPins.Requests()) await Ask(caller, auth, m, p, shown, b);
        // the operator's revoke of a name no caller uses, last: it changes nothing the requests above read
        await Ask("admin", admin, "POST", "/api/v1/workers/zed/revoke", "", null);
        await Ask("admin", admin, "POST", "/api/v1/workers/zed/revoke", " again", null);

        // in process, as an MCP tool would call them (no middleware): the same guards answer
        var inProcess = new InProcess(new EndpointRoutes(gw.Host.Services), (_, _, next) => next());
        var principals = new[] { new Principal("worker", "alice"), new Principal("item", "alice", 1, "owner", "a1"), new Principal("manager", "supervisor"),
            new Principal("keeper", "backlog-keeper"), new Principal("operator", "operator-session"), new Principal("operator", "operator-two"), new Principal("admin") };
        var inProcessCalls = new (string Method, string Pattern, Dictionary<string, string>? Route, string Query, string? Body)[]
        {
            ("POST", "/api/v1/base-update", null, "", null),
            ("POST", "/api/v1/release-queue/promotion-request", null, "", "{}"),
            ("POST", "/api/v1/workers/{name}/credential", new() { ["name"] = "alice" }, "role=bogus", null),
            ("POST", "/api/v1/workers/{name}/revoke", new() { ["name"] = "a.b" }, "", null),
            ("POST", "/api/v1/runtimes/{kind}/reset", new() { ["kind"] = "cursor" }, "", null),
            ("POST", "/api/v1/workers/{name}/{action}", new() { ["name"] = "alice", ["action"] = "capacity" }, "value=0", null),
            ("POST", "/api/v1/build-slot", null, "", "{\"action\":\"x\"}"),
            ("POST", "/api/v1/copilot/bootstrap-token", null, "", null),
            ("POST", "/api/v1/runtime/transcript", null, "", "{}"),
            ("GET", "/api/v1/sessions/{name}", new() { ["name"] = "nobody" }, "", null),
            ("GET", "/api/v1/manager/actions", null, "", null),
            ("POST", "/api/v1/manager/mode", null, "value=loud", null),
            ("POST", "/api/v1/items/{n:int}/unpark", new() { ["n"] = "1" }, "", "{}"),
            ("POST", "/api/v1/items/{n:int}/resettle", new() { ["n"] = "1" }, "", "{}"),
            ("POST", "/api/v1/demo/{item:int}", new() { ["item"] = "1" }, "", ""),
        };
        foreach (var who in principals)
            foreach (var (m, p, route, q, b) in inProcessCalls)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var (status, text) = await inProcess.Call(m, p, who, route, q, b, cts.Token);
                var path = q == "" ? p : p + "?" + q;
                var ct = text.StartsWith('{') ? "application/json" : "";
                lines.Add(new($"in-process {who.Kind}:{who.Name} {m} {path}{(b == null ? "" : " " + b)}", C7bPins.Line(path, new EndpointPins.Full(status, ct, "", "", Encoding.UTF8.GetBytes(text)))));
            }
        AuthPins.Compare("c7b-endpoints.json", lines);
    }
}

/// <summary>The same routes on a gateway with owner mode off, asked by the admin (pinned under their own keys).</summary>
public sealed class C7bNonOwnerEndpointPinTests(NonOwnerPinGateway gw) : IClassFixture<NonOwnerPinGateway>
{
    [Fact]
    public async Task C7bRoutes_OwnerModeOff_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        var lines = new List<KeyValuePair<string, string>>();
        foreach (var (m, p, shown, b) in C7bPins.Requests())
        {
            var a = await C7bPins.Send(gw.Host, m, p, AuthPins.Bearer(GatewayEnvironment.AdminToken), b);
            lines.Add(new($"{m} {p}{shown}", C7bPins.Line(p, a)));
        }
        lines.Add(new("POST /api/v1/workers/zed/revoke", C7bPins.Line("/api/v1/workers/zed/revoke", await C7bPins.Send(gw.Host, "POST", "/api/v1/workers/zed/revoke", AuthPins.Bearer(GatewayEnvironment.AdminToken), null))));
        AuthPins.Compare("c7b-endpoints-nonowner.json", lines);
    }
}
