using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// A gateway for the answers of the C7c routes (wait, events, presence, messaging, supervisor, test hooks): the route fixture's environment
/// changed by <see cref="Configure"/>, started, and waited for until /ready answers 200. A stream's `: ping` comes every second.
/// </summary>
public abstract class C7cGatewayBase : IAsyncLifetime
{
    public GatewayEnvironment Env { get; } = new();
    public GatewayHost Host { get; private set; } = null!;

    /// <summary>Changes the environment and the configuration file before the start.</summary>
    protected virtual void Configure(GatewayEnvironment env, JsonObject cfg) { }

    /// <summary>Runs after /ready answered 200.</summary>
    protected virtual void Started(GatewayHost host) { }

    public async Task InitializeAsync()
    {
        if (!StubPwsh.Supported) return;
        Env.Set("FACTORY_SSE_PING_SECONDS", "1");
        Env.Set("FACTORY_TRANSCRIPTS", "0");
        var cfg = new JsonObject();
        Configure(Env, cfg);
        Env.Apply();
        StubPwsh.Answer(Env.Dir, "revocations", "{\"ok\":true,\"revoked\":{}}");
        StubPwsh.Answer(Env.Dir, "operator-releases", "{\"ok\":true,\"releases\":[]}");
        var cfgPath = Path.Combine(Env.Dir, "instance.test.json");
        var file = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        foreach (var (section, value) in cfg)
            if (value is JsonObject changes && file[section] is JsonObject target) foreach (var (k, v) in changes) target[k] = v?.DeepClone();
            else file[section] = value?.DeepClone();
        File.WriteAllText(cfgPath, file.ToJsonString());
        Host = new GatewayHost();
        _ = Host.Server;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && (await Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status != 200) await Task.Delay(100);
        Started(Host);
    }

    public async Task DisposeAsync()
    {
        try { if (Host != null) await Host.DisposeAsync(); }
        finally { Env.Dispose(); }
    }
}

/// <summary>Owner mode, messaging with its test hooks, the supervisor (one step an hour), the backlog keeper and the operator session on, each holding its slot.</summary>
public sealed class C7cPinGateway : C7cGatewayBase
{
    protected override void Configure(GatewayEnvironment env, JsonObject cfg)
    {
        cfg["operator"] = new JsonObject { ["enabled"] = true, ["allowedNames"] = new JsonArray("operator-session", "operator-two") };
        cfg["keeper"] = new JsonObject { ["enabled"] = true };
        cfg["manager"] = new JsonObject { ["enabled"] = true, ["stepBudgetPerHour"] = 1 };
    }

    protected override void Started(GatewayHost host)
    {
        var plant = host.Services.GetRequiredService<Plant>();
        Assert.True(plant.RegisterOperator("operator-session", "claude-opus-5-5", "low", 0).Ok);
        Assert.True(plant.RegisterSupervisor("supervisor", "claude-opus-5-5", "high", 0).Ok);
        Assert.True(plant.RegisterKeeper("backlog-keeper", "claude-opus-5-5", "medium", 0).Ok);
    }
}

/// <summary>The same with nothing registered: the streams' own tests register the slots.</summary>
public sealed class C7cStreamGateway : C7cGatewayBase
{
    protected override void Configure(GatewayEnvironment env, JsonObject cfg)
    {
        cfg["operator"] = new JsonObject { ["enabled"] = true, ["allowedNames"] = new JsonArray("operator-session") };
        cfg["keeper"] = new JsonObject { ["enabled"] = true };
        cfg["manager"] = new JsonObject { ["enabled"] = true };
    }
}

/// <summary>Owner mode with messaging not configured (messaging.enabled false): the test hooks are not mapped, every messaging route refuses.</summary>
public sealed class C7cMessagingUnconfiguredGateway : C7cGatewayBase
{
    protected override void Configure(GatewayEnvironment env, JsonObject cfg)
    {
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("GH_TOKEN", "fake");
        cfg["messaging"] = new JsonObject { ["enabled"] = false, ["testHooks"] = false };
    }
}

/// <summary>Owner mode, messaging on, its test hooks off (messaging.testHooks false): the 16 test routes are not mapped.</summary>
public sealed class C7cTestHooksOffGateway : C7cGatewayBase
{
    protected override void Configure(GatewayEnvironment env, JsonObject cfg)
    {
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("GH_TOKEN", "fake");
        cfg["messaging"] = new JsonObject { ["testHooks"] = false };
    }
}

/// <summary>What the C7c pins read of an answer, stream or not, and how they write it as one line.</summary>
public static class C7cPins
{
    /// <summary>An answer: status, content type, Cache-Control and the body (of a stream: the frames up to the cut, see <see cref="Send"/>).</summary>
    public sealed record Full(int Status, string ContentType, string CacheControl, string Body, bool Stream);

    static void Build(HttpContext c, string method, string pathAndQuery, string? authorization, string? body, IReadOnlyDictionary<string, string>? headers)
    {
        var q = pathAndQuery.IndexOf('?');
        var port = GatewayEnvironment.ApiPort;
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
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        c.Request.Body = new MemoryStream(bytes);
        c.Request.ContentLength = bytes.Length;
        if (body != null) c.Request.ContentType = "application/json";
    }

    /// <summary>
    /// One request on the API port. An answer that is not an event stream is read to its end. An event stream is read until <paramref name="cut"/>
    /// holds for the frames read so far (default: the first frame that holds `registered"`, or else the first `: ping`), at most 5 s; then the
    /// request is aborted, as a client that goes away, and <paramref name="closed"/> must turn true within 5 s (the handler's cleanup).
    /// </summary>
    public static async Task<Full> Send(GatewayHost host, string method, string pathAndQuery, string? authorization, string? body,
        Func<bool>? closed = null, Func<string, int>? cut = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ctx = await host.Server.SendAsync(c => Build(c, method, pathAndQuery, authorization, body, headers), cts.Token);
        var type = ctx.Response.ContentType ?? "";
        var cache = ctx.Response.Headers.CacheControl.ToString();
        if (type != "text/event-stream")
        {
            var ms = new MemoryStream();
            if (ctx.Response.Body is { CanRead: true } rb) await rb.CopyToAsync(ms, cts.Token);
            return new Full(ctx.Response.StatusCode, type, cache, Encoding.UTF8.GetString(ms.ToArray()), false);
        }
        cut ??= DefaultCut;
        var text = new StringBuilder();
        var buf = new byte[8192];
        var sw = Stopwatch.StartNew();
        var at = -1;
        using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            readCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while ((at = cut(text.ToString())) < 0)
                {
                    var n = await ctx.Response.Body.ReadAsync(buf, readCts.Token);
                    if (n == 0) break;
                    text.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { }
        }
        cts.Cancel();   // the client goes away
        var frames = at >= 0 ? text.ToString()[..at] : text.ToString() + "<no cut>";
        if (closed != null)
        {
            var w = Stopwatch.StartNew();
            while (!closed() && w.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Assert.True(closed(), $"{method} {pathAndQuery}: the stream's handler did not let go after the client went away");
        }
        return new Full(ctx.Response.StatusCode, type, cache, frames, true);
    }

    /// <summary>The end of the first frame that holds `registered"`, else of the first `: ping`; -1 while neither has arrived.</summary>
    public static int DefaultCut(string s)
    {
        var frames = 0;
        for (var i = s.IndexOf("\n\n", StringComparison.Ordinal); i >= 0; i = s.IndexOf("\n\n", i + 2, StringComparison.Ordinal))
        {
            var start = frames; frames = i + 2;
            var frame = s[start..i];
            if (frame.Contains("registered\"", StringComparison.Ordinal) || frame == ": ping") return frames;
        }
        return -1;
    }

    /// <summary>The text with what changes from run to run masked: the plant's epoch, the run part of message ids, times, minted credentials, retry delays.</summary>
    public static string Mask(string s, string epoch)
    {
        if (epoch != "") s = s.Replace(epoch, "<epoch>", StringComparison.Ordinal);
        s = Regex.Replace(s, @"\bm-[0-9a-f]{8}-(\d{4,})", "m-<run>-$1");
        s = Regex.Replace(s, @"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)?", "<time>");
        s = Regex.Replace(s, "\"(token|nonce)\":\"[^\"]*\"", "\"$1\":\"<masked>\"");
        s = Regex.Replace(s, "\"(retryAfterSeconds|ageSeconds|lastHeardSeconds|seconds|secondsAgo|ticket)\":\\d+(\\.\\d+)?", "\"$1\":\"<n>\"");
        return s;
    }

    /// <summary>One pin line: status, content type, Cache-Control, then the masked body (up to 600 characters as is, longer by length and hash).</summary>
    public static string Line(Full a, string epoch)
    {
        var body = Mask(a.Body, epoch);
        if (body.Length > 600) body = $"len={body.Length} sha256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant()}";
        return $"{a.Status} | {a.ContentType} | cache={a.CacheControl} | {(a.Stream ? "stream " : "")}{body}";
    }

    /// <summary>
    /// The key of a request in a pin file. A credential-shaped text of a body (the presence check's composed GitHub token) is masked, so the pin
    /// files never hold one and the secret scan stays meaningful.
    /// </summary>
    public static string Key(string label, string method, string path, string? body) =>
        $"{label} {method} {path}{(body == null ? "" : body.Length > 60 ? $" body[{body.Length}]" : " " + Regex.Replace(body, @"\bgh[pousr]_[A-Za-z0-9]+", "<credential>"))}";

    /// <summary>The C7c routes that answer at once, as (method, path and query, body): every route, its query and body rules.</summary>
    public static IReadOnlyList<(string Method, string Path, string? Body)> Requests(int maxBodyBytes) => new (string, string, string?)[]
    {
        // wait (a short long-poll: 1 s)
        ("GET", "/api/v1/wait", null), ("GET", "/api/v1/wait?item=x", null), ("GET", "/api/v1/wait?item=2&timeout=1", null), ("GET", "/api/v1/wait?item=1&timeout=1", null),
        ("GET", "/api/v1/wait?item=1&timeout=0&after=5", null), ("GET", "/api/v1/wait?item=1&messages=1&timeout=1", null),
        ("GET", "/api/v1/wait?item=1&messages=1&timeout=1&epoch=other&afterMsg=3", null),
        // presence
        ("POST", "/api/v1/presence", "{}"), ("POST", "/api/v1/presence", ""), ("POST", "/api/v1/presence", "not json"), ("POST", "/api/v1/presence", "{\"from\":\"x\"}"),
        ("POST", "/api/v1/presence", "{\"bad\":1}"), ("POST", "/api/v1/presence", "{\"summary\":5}"), ("POST", "/api/v1/presence", "{\"summary\":\"" + new string('s', 141) + "\"}"),
        ("POST", "/api/v1/presence", "{\"summary\":\"working on #1\"}"), ("POST", "/api/v1/presence", "{\"summary\":\"now #1 again\"}"),
        ("POST", "/api/v1/presence", "{\"summary\":\"ghp_" + new string('A', 36) + "\"}"),
        ("POST", "/api/v1/presence", "{\"progress\":\"x\"}"), ("POST", "/api/v1/presence", "{\"progress\":[{\"item\":1,\"ageSeconds\":5,\"source\":\"nope\"}]}"),
        ("POST", "/api/v1/presence", "{\"progress\":[{\"item\":1,\"ageSeconds\":5,\"source\":\"commit\"}]}"), ("POST", "/api/v1/presence", "{\"x\":\"" + new string('y', 17000) + "\"}"),
        // messaging
        ("GET", "/api/v1/peers", null), ("GET", "/api/v1/peers?all=1", null),
        ("POST", "/api/v1/messages", "{}"), ("POST", "/api/v1/messages", "not json"), ("POST", "/api/v1/messages", "{\"to\":\"bob\",\"type\":\"note\",\"text\":\"hello\"}"),
        ("POST", "/api/v1/messages", "{\"to\":\"bob\",\"text\":\"" + new string('z', maxBodyBytes + 1) + "\"}"),
        ("GET", "/api/v1/messages", null), ("GET", "/api/v1/messages?to=bad%20name", null), ("GET", "/api/v1/messages?ring=refused&limit=5", null), ("GET", "/api/v1/messages?to=operator&unread=1&item=1&worker=alice", null),
        ("POST", "/api/v1/messages/read", "{}"), ("POST", "/api/v1/messages/read", "{\"all\":true}"), ("POST", "/api/v1/messages/read", "{\"x\":\"" + new string('y', maxBodyBytes + 1) + "\"}"),
        ("GET", "/api/v1/messages/m-00000000-0001", null), ("GET", "/api/v1/messages/x?wait=0.1", null),
        ("POST", "/api/v1/messages/ack", "{}"), ("POST", "/api/v1/messages/ack", "{\"ids\":[\"m-00000000-0001\"]}"),
        ("GET", "/api/v1/inbox", null), ("GET", "/api/v1/inbox?after=x", null), ("GET", "/api/v1/inbox?include=x", null), ("GET", "/api/v1/inbox?include=delivered&limit=5&timeout=0&epoch=other", null),
        // supervisor
        ("GET", "/api/v1/questions", null), ("GET", "/api/v1/questions?state=x", null), ("GET", "/api/v1/questions?state=all", null),
        ("POST", "/api/v1/questions/q-x/answer", "{}"), ("POST", "/api/v1/questions/q-x/answer", "{\"text\":\"yes\"}"),
        ("GET", "/api/v1/digest", null), ("GET", "/api/v1/digest", null),
        ("POST", "/api/v1/supervisor/patrol-ack", "{}"), ("POST", "/api/v1/supervisor/patrol?minutes=2", null), ("POST", "/api/v1/supervisor/patrol?minutes=30", null),
        ("POST", "/api/v1/supervisor/budget?extra=0", null), ("POST", "/api/v1/supervisor/budget?extra=1", null),
        ("POST", "/api/v1/messaging/ping", null), ("POST", "/api/v1/messaging/enable", null),
        // the event streams' refusals (the streams themselves are opened and cut, see C7cPins.Send)
        ("GET", "/api/v1/events?name=bad%20name", null), ("GET", "/api/v1/events?name=gateway", null), ("GET", "/api/v1/events?name=alice&runtime=octopus", null),
        ("GET", "/api/v1/events?name=alice&type=bogus", null), ("GET", "/api/v1/events?name=supervisor&supervisor=1&model=x%20y", null),
        // test hooks
        ("POST", "/api/v1/_test/octopus-provision", "{}"), ("POST", "/api/v1/_test/runtime-nonce", "{}"), ("POST", "/api/v1/_test/runtime-nonce", "{\"name\":\"rt9\",\"runtime\":\"octopus\"}"),
        ("POST", "/api/v1/_test/role-token", "{}"), ("POST", "/api/v1/_test/role-token", "{\"name\":\"rw9\",\"role\":\"worker\",\"mode\":\"hosted\"}"),
        ("POST", "/api/v1/_test/deliver", "{}"), ("POST", "/api/v1/_test/deliver", "not json"), ("POST", "/api/v1/_test/deliver", "{\"x\":\"" + new string('y', 64 * 1024) + "\"}"),
        ("POST", "/api/v1/_test/deliver", "{\"to\":\"bob\",\"text\":\"hello\"}"), ("POST", "/api/v1/_test/deliver", "{\"routeItem\":1,\"note\":\"n\"}"),
        ("POST", "/api/v1/_test/deliver", "{\"to\":\"bob\",\"trigger\":\"t\"}"), ("POST", "/api/v1/_test/deliver", "{\"to\":\"bob\",\"assigned\":2}"),
        ("POST", "/api/v1/_test/deliver", "{\"to\":\"bob\",\"fromKind\":\"x\"}"),
        ("POST", "/api/v1/_test/answer", "{\"id\":\"q-x\",\"text\":\"a\"}"), ("POST", "/api/v1/_test/answer", "{\"fromKind\":\"x\"}"),
        ("POST", "/api/v1/_test/plan", "{\"owned\":{}}"), ("POST", "/api/v1/_test/release-queue", "{\"queue\":[]}"),
        ("POST", "/api/v1/_test/refusal", "{}"), ("POST", "/api/v1/_test/refusal", "{\"name\":\"alice\",\"item\":1,\"code\":\"x\"}"),
        ("POST", "/api/v1/_test/fact", "{\"item\":1}"), ("POST", "/api/v1/_test/fact", "{\"fact\":\"review\",\"item\":1}"),
        ("GET", "/api/v1/_test/plant-data", null), ("GET", "/api/v1/_test/stores", null),
        ("POST", "/api/v1/_test/plant-restore", "not json"),
        ("POST", "/api/v1/_test/roles", "{}"), ("POST", "/api/v1/_test/roles", "{\"item\":3,\"owner\":\"carol\"}"),
        ("POST", "/api/v1/_test/inflight", "{}"), ("POST", "/api/v1/_test/inflight", "{\"name\":\"alice\",\"item\":1,\"verb\":\"x\"}"),
        ("POST", "/api/v1/_test/mode", "{}"), ("POST", "/api/v1/_test/mode", "{\"name\":\"bob\",\"mode\":\"on\"}"),
    };

    /// <summary>The callers of the pins, as the Authorization header (null: none).</summary>
    public static IReadOnlyList<(string Caller, string? Authorization)> Callers(GatewayHost host, string secret)
    {
        var msg = host.Services.GetRequiredService<MessagingSettings>();
        var keeper = host.Services.GetRequiredService<KeeperSettings>();
        return new (string, string?)[]
        {
            ("anonymous", null),
            ("worker", AuthPins.Bearer(Auth.WorkerToken(secret, "alice"))),
            // each session its own name: presence's 10 s summary limit is kept per name, so no pin depends on how long the callers before took
            ("item-owner", AuthPins.Bearer(Auth.MintItemToken(secret, 1, "owner", "a1", "ivy", TimeSpan.FromHours(1)))),
            ("item-inspector", AuthPins.Bearer(Auth.MintItemToken(secret, 1, "inspector", "a2", "bob", TimeSpan.FromHours(1)))),
            ("item-qc", AuthPins.Bearer(Auth.MintItemToken(secret, 1, "qc", "a3", "qc1", TimeSpan.FromHours(1)))),
            ("supervisor", AuthPins.Bearer(Auth.ManagerToken(secret, "supervisor", msg.TokenGeneration))),
            ("keeper", AuthPins.Bearer(Auth.KeeperToken(secret, "backlog-keeper", keeper.TokenGeneration))),
            ("operator-active", AuthPins.Bearer(Auth.OperatorToken(secret, "operator-session", msg.OperatorTokenGeneration))),
            ("operator-idle", AuthPins.Bearer(Auth.OperatorToken(secret, "operator-two", msg.OperatorTokenGeneration))),
            ("admin", AuthPins.Bearer(GatewayEnvironment.AdminToken)),
        };
    }

    /// <summary>The event streams each caller opens (or is refused), with the cleanup that shows the handler let go.</summary>
    internal static IReadOnlyList<(string Path, Func<Plant, EventHub, bool> Closed)> Streams() => new (string, Func<Plant, EventHub, bool>)[]
    {
        ("/api/v1/events", (_, hub) => hub.SubscriberCount == 0),
        ("/api/v1/events?name=alice&roles=owner&capabilities=dotnet&capacity=2", (p, _) => !p.IsConnected("alice")),
        ("/api/v1/events?name=bob&messaging=1", (p, _) => !p.IsConnected("bob")),
        ("/api/v1/events?name=supervisor&supervisor=1&model=claude-opus-5-5&effort=high", (p, _) => p.MessagingStatus()["manager"]?["supervisor"]?["connected"]?.GetValue<bool>() != true),
        ("/api/v1/events?name=backlog-keeper&keeper=1&model=claude-opus-5-5&effort=medium", (p, _) => p.KeeperStatus()["session"]?["connected"]?.GetValue<bool>() != true),
        ("/api/v1/events?name=operator-session&operator=1&model=claude-opus-5-5&effort=low", (p, _) => p.OperatorStatus()["session"]?["connected"]?.GetValue<bool>() != true),
    };
}

/// <summary>
/// The answers of the routes C7c moves into endpoint modules (wait, events, presence, messaging, supervisor, test hooks), pinned from master
/// before the move (Pins/c7c-*.json) beyond what the caller matrix pins: the content type, Cache-Control and the whole body (times, the epoch and
/// minted credentials masked), every query and body rule, for each caller kind; the event streams' status, headers and frames up to the
/// registration line, with the handler's cleanup after the client goes away; the guards' order with messaging switched off at run time; the
/// same routes called in process (InProcess, as an MCP tool calls them); and the gateways with owner mode off, messaging not configured and
/// the test hooks off. The callers ask in a fixed order and some answers depend on it (the supervisor's one step, the presence rate limit).
/// </summary>
public sealed class C7cEndpointPinTests(C7cPinGateway gw) : IClassFixture<C7cPinGateway>
{
    [Fact]
    public async Task C7cRoutes_EveryCaller_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        var plant = gw.Host.Services.GetRequiredService<Plant>();
        var hub = gw.Host.Services.GetRequiredService<EventHub>();
        var msg = gw.Host.Services.GetRequiredService<MessagingSettings>();
        var callers = C7cPins.Callers(gw.Host, gw.Env.TokenSecret);
        var lines = new List<KeyValuePair<string, string>>();
        async Task Ask(string label, string? auth, string method, string path, string? body, Func<Plant, EventHub, bool>? closed = null)
        {
            var a = await C7cPins.Send(gw.Host, method, path, auth, body, closed == null ? null : () => closed(plant, hub));
            lines.Add(new(C7cPins.Key(label, method, path, body), C7cPins.Line(a, plant.Epoch)));
        }
        foreach (var (caller, auth) in callers)
        {
            foreach (var (m, p, b) in C7cPins.Requests(msg.MaxBodyBytes)) await Ask(caller, auth, m, p, b);
            foreach (var (p, closed) in C7cPins.Streams()) await Ask(caller, auth, "GET", p, null, closed);
        }

        // messaging switched off at run time: every messaging guard answers messaging-disabled, the operator session is no longer admitted
        var admin = callers.Single(c => c.Caller == "admin").Authorization;
        Assert.Equal(200, (await C7cPins.Send(gw.Host, "POST", "/api/v1/messaging/disable", admin, null)).Status);
        foreach (var (caller, auth) in callers.Where(c => c.Caller is "worker" or "item-owner" or "supervisor" or "operator-active" or "admin"))
            foreach (var (m, p, b) in new (string, string, string?)[] {
                         ("GET", "/api/v1/peers", null), ("POST", "/api/v1/messages", "{}"), ("GET", "/api/v1/messages", null), ("POST", "/api/v1/messages/read", "{}"),
                         ("GET", "/api/v1/messages/x", null), ("POST", "/api/v1/messages/ack", "{}"), ("GET", "/api/v1/inbox", null), ("GET", "/api/v1/questions", null),
                         ("POST", "/api/v1/questions/q-x/answer", "{}"), ("GET", "/api/v1/digest", null), ("POST", "/api/v1/supervisor/patrol-ack", "{}"),
                         ("POST", "/api/v1/supervisor/patrol?minutes=30", null), ("POST", "/api/v1/supervisor/budget?extra=1", null), ("POST", "/api/v1/presence", "{}"),
                         ("GET", "/api/v1/wait?item=1&messages=1&timeout=1", null), ("GET", "/api/v1/wait?item=1&timeout=1", null), ("POST", "/api/v1/messaging/ping", null),
                         ("GET", "/api/v1/_test/stores", null), ("POST", "/api/v1/_test/mode", "{\"name\":\"bob\",\"mode\":\"mute\"}") })
                await Ask("messaging-off " + caller, auth, m, p, b);
        foreach (var (caller, auth) in callers.Where(c => c.Caller is "worker" or "admin"))
            foreach (var (p, closed) in C7cPins.Streams()) await Ask("messaging-off " + caller, auth, "GET", p, null, closed);
        Assert.Equal(200, (await C7cPins.Send(gw.Host, "POST", "/api/v1/messaging/enable", admin, null)).Status);

        // in process, as an MCP tool calls them (no middleware): the same guards and handlers answer
        var inProcess = new InProcess(new EndpointRoutes(gw.Host.Services), (_, _, next) => next());
        var principals = new[] { new Principal("worker", "alice"), new Principal("item", "alice", 1, "owner", "a1"), new Principal("item", "qc1", 1, "qc", "a3"),
            new Principal("manager", "supervisor"), new Principal("keeper", "backlog-keeper"), new Principal("operator", "operator-session"), new Principal("operator", "operator-two"), new Principal("admin") };
        foreach (var who in principals)
            foreach (var (m, p, rk, rv, q, b) in new (string, string, string?, string?, string, string?)[] {
                         ("GET", "/api/v1/peers", null, null, "", null), ("POST", "/api/v1/messages", null, null, "", "{\"to\":\"bob\",\"type\":\"note\",\"text\":\"in process\"}"),
                         ("GET", "/api/v1/messages", null, null, "limit=3", null), ("GET", "/api/v1/messages/{id}", "id", "m-x", "", null), ("POST", "/api/v1/messages/read", null, null, "", "{}"),
                         ("GET", "/api/v1/inbox", null, null, "", null), ("POST", "/api/v1/messages/ack", null, null, "", "{}"), ("GET", "/api/v1/questions", null, null, "", null),
                         ("POST", "/api/v1/questions/{id}/answer", "id", "q-x", "", "{}"), ("GET", "/api/v1/digest", null, null, "", null),
                         ("POST", "/api/v1/supervisor/patrol-ack", null, null, "", "{}"), ("POST", "/api/v1/supervisor/budget", null, null, "extra=1", null),
                         ("POST", "/api/v1/supervisor/patrol", null, null, "minutes=30", null), ("POST", "/api/v1/presence", null, null, "", "{}"),
                         ("POST", "/api/v1/messaging/{action}", "action", "ping", "", null), ("POST", "/api/v1/messaging/{action}", "action", "enable", "", null),
                         ("GET", "/api/v1/wait", null, null, "item=1&timeout=1", null), ("GET", "/api/v1/_test/stores", null, null, "", null),
                         ("POST", "/api/v1/_test/mode", null, null, "", "{\"name\":\"bob\",\"mode\":\"on\"}") })
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var route = rk == null ? null : new Dictionary<string, string> { [rk] = rv! };
                var (status, text) = await inProcess.Call(m, p, who, route, q, b, cts.Token);
                var path = (rk == null ? p : p.Replace("{" + rk + "}", rv)) + (q == "" ? "" : "?" + q);
                lines.Add(new(C7cPins.Key($"in-process {who.Kind}:{who.Name}:{who.Role}", m, path, b), C7cPins.Line(new C7cPins.Full(status, "", "", text, false), plant.Epoch)));
            }
        AuthPins.Compare("c7c-endpoints.json", lines);
    }
}

/// <summary>The same routes on the gateways that switch them off: owner mode off, messaging not configured, the test hooks off.</summary>
public static class C7cOffPins
{
    public static async Task Run(GatewayHost host, string secret, string pinFile, bool streams)
    {
        var plant = host.Services.GetRequiredService<Plant>();
        var hub = host.Services.GetRequiredService<EventHub>();
        var msg = host.Services.GetRequiredService<MessagingSettings>();
        var lines = new List<KeyValuePair<string, string>>();
        foreach (var (caller, auth) in C7cPins.Callers(host, secret).Where(c => c.Caller is "worker" or "item-owner" or "supervisor" or "admin"))
        {
            foreach (var (m, p, b) in C7cPins.Requests(msg.MaxBodyBytes))
            {
                var a = await C7cPins.Send(host, m, p, auth, b);
                lines.Add(new(C7cPins.Key(caller, m, p, b), C7cPins.Line(a, plant.Epoch)));
            }
            if (streams)
                foreach (var (p, closed) in C7cPins.Streams())
                {
                    var a = await C7cPins.Send(host, "GET", p, auth, null, () => closed(plant, hub));
                    lines.Add(new(C7cPins.Key(caller, "GET", p, null), C7cPins.Line(a, plant.Epoch)));
                }
        }
        // nothing of the test hooks is mapped where they are off
        var testRoutes = Endpoints.Of(host.Services).Where(e => e.Pattern.StartsWith("/api/v1/_test/", StringComparison.Ordinal)).Select(e => $"{e.Method} {e.Pattern}").OrderBy(x => x, StringComparer.Ordinal);
        lines.Add(new("mapped test hooks", string.Join(", ", testRoutes)));
        AuthPins.Compare(pinFile, lines);
    }
}

public sealed class C7cNonOwnerPinTests(NonOwnerPinGateway gw) : IClassFixture<NonOwnerPinGateway>
{
    [Fact]
    public async Task C7cRoutes_OwnerModeOff_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        await C7cOffPins.Run(gw.Host, gw.Env.TokenSecret, "c7c-endpoints-nonowner.json", streams: true);
    }
}

public sealed class C7cMessagingUnconfiguredPinTests(C7cMessagingUnconfiguredGateway gw) : IClassFixture<C7cMessagingUnconfiguredGateway>
{
    [Fact]
    public async Task C7cRoutes_MessagingNotConfigured_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        await C7cOffPins.Run(gw.Host, gw.Env.TokenSecret, "c7c-endpoints-messaging-unconfigured.json", streams: true);
    }
}

public sealed class C7cTestHooksOffPinTests(C7cTestHooksOffGateway gw) : IClassFixture<C7cTestHooksOffGateway>
{
    [Fact]
    public async Task C7cRoutes_TestHooksOff_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        await C7cOffPins.Run(gw.Host, gw.Env.TokenSecret, "c7c-endpoints-testhooks-off.json", streams: false);
    }
}

/// <summary>
/// The streaming behaviour of the event streams and the long-poll, asserted (not pinned: it is timing): the first frames and their order, the
/// `: ping` comment at the configured interval (1 s here) on an idle stream, an event written as it is published with its id line, and the
/// handler's cleanup when the client goes away; /wait's timeout, its clamp to 1 to 120 s, its early answer when a trigger arrives, and its end
/// when the client goes away.
/// </summary>
public sealed class C7cStreamTests(C7cStreamGateway gw) : IClassFixture<C7cStreamGateway>
{
    sealed record Opened(HttpContext Context, CancellationTokenSource Cts, Stopwatch Clock);

    async Task<Opened> Open(string path, string? authorization, IReadOnlyDictionary<string, string>? headers = null)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var clock = Stopwatch.StartNew();
        var q = path.IndexOf('?');
        var ctx = await gw.Host.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("127.0.0.1", GatewayEnvironment.ApiPort);
            c.Request.Path = q < 0 ? path : path[..q];
            if (q >= 0) c.Request.QueryString = new QueryString(path[q..]);
            c.Connection.LocalPort = GatewayEnvironment.ApiPort;
            c.Connection.LocalIpAddress = IPAddress.Loopback;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (authorization != null) c.Request.Headers.Authorization = authorization;
            if (headers != null) foreach (var (k, v) in headers) c.Request.Headers[k] = v;
            c.Request.Body = new MemoryStream();
        }, cts.Token);
        return new Opened(ctx, cts, clock);
    }

    // reads frames ("...\n\n") until `until` holds for the frames read so far, or the time is up; each frame with the time it was read at
    static async Task<List<(string Frame, TimeSpan At)>> Frames(Opened o, Func<List<(string Frame, TimeSpan At)>, bool> until, TimeSpan max)
    {
        var frames = new List<(string, TimeSpan)>();
        var pending = new StringBuilder();
        var buf = new byte[8192];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(o.Cts.Token);
        readCts.CancelAfter(max);
        try
        {
            while (!until(frames))
            {
                var n = await o.Context.Response.Body.ReadAsync(buf, readCts.Token);
                if (n == 0) break;
                pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                var s = pending.ToString(); int i;
                while ((i = s.IndexOf("\n\n", StringComparison.Ordinal)) >= 0) { frames.Add((s[..i], o.Clock.Elapsed)); s = s[(i + 2)..]; }
                pending.Clear().Append(s);
            }
        }
        catch (OperationCanceledException) { }
        return frames;
    }

    static async Task Eventually(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.True(condition(), what);
    }

    static void AssertPings(List<(string Frame, TimeSpan At)> frames)
    {
        var pings = frames.Where(f => f.Frame == ": ping").Select(f => f.At).ToList();
        Assert.True(pings.Count >= 2, $"wanted two pings within the time, got {pings.Count}: {string.Join(" | ", frames.Select(f => f.Frame))}");
        for (var i = 1; i < pings.Count; i++)
        {
            var gap = (pings[i] - pings[i - 1]).TotalSeconds;
            Assert.True(gap is > 0.6 and < 2.5, $"pings {gap:0.00} s apart, wanted about 1 s");
        }
    }

    string Admin => AuthPins.Bearer(GatewayEnvironment.AdminToken);

    [Fact]
    public async Task Broadcast_HeadersPendingFramesEventsPingsAndCleanup()
    {
        if (!StubPwsh.Supported) return;
        var hub = gw.Host.Services.GetRequiredService<EventHub>();
        hub.Publish("{\"event\":\"test.before\"}");
        // the offers wanted now (legacy broadcast mode) come first, without an id line; without Last-Event-ID nothing published before is replayed
        hub.SetOffers(new List<string> { "{\"event\":\"test.offer\"}" });
        var o = await Open("/api/v1/events", Admin);
        try
        {
            Assert.Equal(200, o.Context.Response.StatusCode);
            Assert.Equal("text/event-stream", o.Context.Response.ContentType);
            Assert.Equal("no-cache", o.Context.Response.Headers.CacheControl.ToString());
            var first = await Frames(o, f => f.Count > 0, TimeSpan.FromSeconds(5));
            Assert.Equal("data: {\"event\":\"test.offer\"}", first[0].Frame);
            Assert.Equal(1, hub.SubscriberCount);
            hub.Publish("{\"event\":\"test.live\"}");
            var more = await Frames(o, f => f.Any(x => x.Frame.Contains("test.live")) && f.Count(x => x.Frame == ": ping") >= 2, TimeSpan.FromSeconds(4));
            Assert.Matches(@"^id: \d+\ndata: \{""event"":""test.live""\}$", more.Single(x => x.Frame.Contains("test.live")).Frame);
            Assert.DoesNotContain(first.Concat(more), x => x.Frame.Contains("test.before"));
            AssertPings(more);
        }
        finally { o.Cts.Cancel(); }
        await Eventually(() => hub.SubscriberCount == 0, "the broadcast subscription was not removed after the client went away");
    }

    [Fact]
    public async Task Broadcast_LastEventIdReplaysWhatCameAfterIt()
    {
        if (!StubPwsh.Supported) return;
        var hub = gw.Host.Services.GetRequiredService<EventHub>();
        hub.Publish("{\"event\":\"test.r1\"}");
        var id = hub.LastId;
        hub.Publish("{\"event\":\"test.r2\"}");
        var o = await Open("/api/v1/events", Admin, new Dictionary<string, string> { ["Last-Event-ID"] = id.ToString() });
        List<(string Frame, TimeSpan At)> replay;
        try { replay = await Frames(o, f => f.Any(x => x.Frame.Contains("test.r2")) || f.Any(x => x.Frame == ": ping"), TimeSpan.FromSeconds(5)); }
        finally { o.Cts.Cancel(); }
        Assert.DoesNotContain(replay, x => x.Frame.Contains("test.r1"));
        Assert.Contains($"id: {id + 1}\ndata: {{\"event\":\"test.r2\"}}", replay.Select(x => x.Frame));
        await Eventually(() => hub.SubscriberCount == 0, "the broadcast subscription was not removed");
    }

    [Fact]
    public async Task WorkerStream_RegisteredFirstThenEventsWithEpochIdsPingsAndDisconnect()
    {
        if (!StubPwsh.Supported) return;
        var plant = gw.Host.Services.GetRequiredService<Plant>();
        var o = await Open("/api/v1/events?name=wanda&messaging=1", AuthPins.Bearer(Auth.WorkerToken(gw.Env.TokenSecret, "wanda")));
        Assert.Equal(200, o.Context.Response.StatusCode);
        Assert.Equal("text/event-stream", o.Context.Response.ContentType);
        Assert.Equal("no-cache", o.Context.Response.Headers.CacheControl.ToString());
        var first = await Frames(o, f => f.Any(x => x.Frame.Contains("peers.snapshot")), TimeSpan.FromSeconds(5));
        var registered = first.FindIndex(x => x.Frame.Contains("\"worker.registered\""));
        Assert.True(registered >= 0, "no worker.registered frame: " + string.Join(" | ", first.Select(f => f.Frame)));
        Assert.StartsWith("data: ", first[registered].Frame);   // no id line: never a client's cursor
        Assert.Contains($"\"epoch\":\"{plant.Epoch}\"", first[registered].Frame);
        Assert.StartsWith("data: ", first[registered + 1].Frame);
        Assert.Contains("peers.snapshot", first[registered + 1].Frame);
        Assert.True(plant.IsConnected("wanda"));
        plant.SendTo("wanda", new JsonObject { ["provider"] = "gateway", ["event"] = "test.trigger", ["note"] = "n" });
        var more = await Frames(o, f => f.Any(x => x.Frame.Contains("test.trigger")) && f.Count(x => x.Frame == ": ping") >= 2, TimeSpan.FromSeconds(4));
        Assert.Matches($@"^id: {Regex.Escape(plant.Epoch)}\.\d+\ndata: .*""test.trigger"".*$", more.Single(x => x.Frame.Contains("test.trigger")).Frame);
        AssertPings(more);
        o.Cts.Cancel();
        await Eventually(() => !plant.IsConnected("wanda"), "the worker stayed connected after its client went away");
    }

    [Theory]
    [InlineData("supervisor")]
    [InlineData("keeper")]
    [InlineData("operator")]
    public async Task SlotStream_RegisteredFirstPingsAndDisconnect(string slot)
    {
        if (!StubPwsh.Supported) return;
        var plant = gw.Host.Services.GetRequiredService<Plant>();
        var msg = gw.Host.Services.GetRequiredService<MessagingSettings>();
        var keeper = gw.Host.Services.GetRequiredService<KeeperSettings>();
        var secret = gw.Env.TokenSecret;
        var (path, token, evt, connected) = slot switch
        {
            "supervisor" => ("/api/v1/events?name=supervisor&supervisor=1&model=claude-opus-5-5&effort=high", Auth.ManagerToken(secret, "supervisor", msg.TokenGeneration), "supervisor.registered",
                (Func<bool>)(() => plant.MessagingStatus()["manager"]?["supervisor"]?["connected"]?.GetValue<bool>() == true)),
            "keeper" => ("/api/v1/events?name=backlog-keeper&keeper=1&model=claude-opus-5-5&effort=medium", Auth.KeeperToken(secret, "backlog-keeper", keeper.TokenGeneration), "keeper.registered",
                () => plant.KeeperStatus()["session"]?["connected"]?.GetValue<bool>() == true),
            _ => ("/api/v1/events?name=operator-session&operator=1&model=claude-opus-5-5&effort=low", Auth.OperatorToken(secret, "operator-session", msg.OperatorTokenGeneration), "operator.registered",
                () => plant.OperatorStatus()["session"]?["connected"]?.GetValue<bool>() == true),
        };
        var o = await Open(path, AuthPins.Bearer(token));
        Assert.Equal(200, o.Context.Response.StatusCode);
        Assert.Equal("text/event-stream", o.Context.Response.ContentType);
        Assert.Equal("no-cache", o.Context.Response.Headers.CacheControl.ToString());
        var frames = await Frames(o, f => f.Count(x => x.Frame == ": ping") >= 2, TimeSpan.FromSeconds(4));
        var reg = frames.FindIndex(x => x.Frame.Contains($"\"{evt}\""));
        Assert.True(reg >= 0, $"no {evt} frame: " + string.Join(" | ", frames.Select(f => f.Frame)));
        Assert.StartsWith("data: ", frames[reg].Frame);
        Assert.True(frames.FindIndex(x => x.Frame == ": ping") > reg, "a ping came before the registration line");
        AssertPings(frames);
        Assert.True(connected());
        o.Cts.Cancel();
        await Eventually(() => !connected(), $"the {slot} slot stayed connected after its client went away");
    }

    [Fact]
    public async Task Wait_TimesOutClampsAnswersEarlyAndEndsWhenTheClientGoesAway()
    {
        if (!StubPwsh.Supported) return;
        var plant = gw.Host.Services.GetRequiredService<Plant>();
        var owner = AuthPins.Bearer(Auth.MintItemToken(gw.Env.TokenSecret, 7, "owner", "a7", "olga", TimeSpan.FromHours(1)));

        // timeout=0 is clamped to 1 s; the answer says it timed out
        var sw = Stopwatch.StartNew();
        var a = await C7cPins.Send(gw.Host, "GET", "/api/v1/wait?item=7&timeout=0", owner, null);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 5);
        Assert.Equal(200, a.Status);
        Assert.Equal("{\"ok\":true,\"item\":7,\"timeout\":true,\"last\":0,\"events\":[]}", a.Body);

        // a trigger that arrives during the wait answers it at once
        sw.Restart();
        var waiting = C7cPins.Send(gw.Host, "GET", "/api/v1/wait?item=7&timeout=30", owner, null);
        await Task.Delay(300);
        plant.Route(7, new JsonObject { ["provider"] = "gateway", ["event"] = "test.item-trigger", ["item"] = 7 });
        var woke = await waiting;
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"the wait answered after {sw.Elapsed.TotalSeconds:0.0} s");
        var j = JsonNode.Parse(woke.Body)!;
        Assert.False(j["timeout"]!.GetValue<bool>());
        Assert.Equal("test.item-trigger", j["events"]![0]!["event"]!.ToString());

        // with messages=1 a message for the session wakes it too
        sw.Restart();
        var both = C7cPins.Send(gw.Host, "GET", $"/api/v1/wait?item=7&messages=1&timeout=30&after={j["last"]}", owner, null);
        await Task.Delay(300);
        plant.Deliver("olga", new JsonObject { ["provider"] = "peer", ["relay"] = "gateway", ["event"] = "message.received", ["type"] = "operator-note", ["from"] = "operator", ["fromKind"] = "operator", ["to"] = "olga", ["kind"] = "direct", ["text"] = "hi", ["flags"] = new JsonArray() });
        var bj = JsonNode.Parse((await both).Body)!;
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"the wait answered after {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.False(bj["timeout"]!.GetValue<bool>());
        Assert.Single(bj["messages"]!.AsArray());
        Assert.Equal(plant.Epoch, bj["epoch"]!.ToString());

        // the client goes away: the request ends without waiting out its timeout
        sw.Restart();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var gone = gw.Host.Server.SendAsync(c =>
        {
            c.Request.Method = "GET"; c.Request.Scheme = "http"; c.Request.Host = new HostString("127.0.0.1", GatewayEnvironment.ApiPort);
            c.Request.Path = "/api/v1/wait"; c.Request.QueryString = new QueryString("?item=7&timeout=60&after=999");
            c.Connection.LocalPort = GatewayEnvironment.ApiPort; c.Connection.LocalIpAddress = IPAddress.Loopback; c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = owner; c.Request.Body = new MemoryStream();
        }, cts.Token);
        try { await gone; } catch (OperationCanceledException) { }
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"the abandoned wait held on for {sw.Elapsed.TotalSeconds:0.0} s");
    }
}
