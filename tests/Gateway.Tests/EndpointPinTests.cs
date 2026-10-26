using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
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
/// A gateway for the answers of the C7a routes: the route fixture's environment plus a revocation read, the operator's recorded releases read (none),
/// the operator session, the backlog keeper and the supervisor switched on and each holding its slot, and a supervisor step budget of one per hour
/// (so the step-budget refusal is reached by the second step).
/// </summary>
public sealed class EndpointPinGateway : IAsyncLifetime
{
    public GatewayEnvironment Env { get; } = new();
    public GatewayHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!StubPwsh.Supported) return;
        Env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        Env.Set("GH_TOKEN", "fake");
        Env.Set("FACTORY_TRANSCRIPTS", "0");
        Env.Apply();
        StubPwsh.Answer(Env.Dir, "operator-releases", "{\"ok\":true,\"releases\":[]}");
        var cfgPath = Path.Combine(Env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        cfg["operator"]!["enabled"] = true;
        cfg["operator"]!["allowedNames"] = new JsonArray("operator-session", "operator-two");
        cfg["keeper"]!["enabled"] = true;
        cfg["manager"]!["enabled"] = true;
        cfg["manager"]!["stepBudgetPerHour"] = 1;
        File.WriteAllText(cfgPath, cfg.ToJsonString());
        Host = new GatewayHost();
        _ = Host.Server;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && (await Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status != 200) await Task.Delay(100);
        var releases = Host.Services.GetRequiredService<IOperatorReleaseLoader>();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && !releases.IsLoaded) await Task.Delay(50);
        Assert.True(releases.IsLoaded, "the operator's recorded releases were not read");
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

/// <summary>A gateway with owner mode off (FACTORY_OWNER_MODE=0), messaging off.</summary>
public sealed class NonOwnerPinGateway : IAsyncLifetime
{
    public GatewayEnvironment Env { get; } = new();
    public GatewayHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!StubPwsh.Supported) return;
        Env.Set("FACTORY_OWNER_MODE", "0");
        // the revocation test switch needs messaging.testHooks, which needs owner mode: the stub answers the read instead
        Env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        Env.Set("GH_TOKEN", "fake");
        Env.Set("FACTORY_TRANSCRIPTS", "0");
        Env.Apply();
        var cfgPath = Path.Combine(Env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        // messaging (and so the operator session) needs owner mode: the gateway refuses to start with it
        cfg["messaging"]!["enabled"] = false;
        cfg["messaging"]!["testHooks"] = false;
        File.WriteAllText(cfgPath, cfg.ToJsonString());
        Host = new GatewayHost();
        _ = Host.Server;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && (await Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status != 200) await Task.Delay(100);
    }

    public async Task DisposeAsync()
    {
        try { if (Host != null) await Host.DisposeAsync(); }
        finally { Env.Dispose(); }
    }
}

/// <summary>What the pins of the C7a routes read of an answer, and how they write it as one line.</summary>
public static class EndpointPins
{
    /// <summary>A whole answer: status, the headers the pins read, and the body's bytes.</summary>
    public sealed record Full(int Status, string ContentType, string Location, string Disposition, byte[] Body);

    /// <summary>One request on the API port with the Authorization header as given (null: none); the body as bytes.</summary>
    public static async Task<Full> Send(GatewayHost host, string method, string pathAndQuery, string? authorization, string? body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var q = pathAndQuery.IndexOf('?');
        var port = GatewayEnvironment.ApiPort;
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
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            c.Request.Body = new MemoryStream(bytes);
            c.Request.ContentLength = bytes.Length;
            if (body != null) c.Request.ContentType = "application/json";
        }, cts.Token);
        var ms = new MemoryStream();
        if (ctx.Response.Body is { CanRead: true } rb) await rb.CopyToAsync(ms, cts.Token);
        return new Full(ctx.Response.StatusCode, ctx.Response.ContentType ?? "", ctx.Response.Headers.Location.ToString(), ctx.Response.Headers.ContentDisposition.ToString(), ms.ToArray());
    }

    // the answers whose 200 body changes from run to run (times, hashes, counters): their shape is pinned, not their text
    static readonly string[] ShapeOnly = { "/_build", "/api/v1/status", "/api/v1/dashboard", "/api/v1/operator/release-check" };

    /// <summary>
    /// One pin line: status, content type, Location, Content-Disposition, then the body. A JSON body is pinned as its text, except the
    /// answers of <see cref="ShapeOnly"/> (their keys in order, with the value of each boolean) and the step-budget refusal (its retryAfterSeconds
    /// masked); the plugin archive by its entries' rules; other text up to 300 characters as is, longer text by length and hash.
    /// </summary>
    public static string Line(string path, Full a)
    {
        var p = path.Split('?')[0];
        string body;
        if (a.ContentType == "application/gzip") body = Archive(a.Body);
        else
        {
            var text = Encoding.UTF8.GetString(a.Body);
            JsonNode? j = null; try { j = text.Length > 0 ? JsonNode.Parse(text) : null; } catch { }
            if (j is JsonObject o && a.Status is 200 or 409 && ShapeOnly.Contains(p)) body = "shape " + string.Join(",", o.Select(kv => kv.Value is JsonValue v && v.TryGetValue<bool>(out var b) ? $"{kv.Key}={b}" : kv.Key));
            else if (j is JsonObject r && a.Status == 429 && r["code"]?.ToString() == "step-budget") { r["retryAfterSeconds"] = "masked"; body = r.ToJsonString(); }
            else if (text.Length <= 300) body = text;
            else body = $"len={text.Length} sha256={Convert.ToHexString(SHA256.HashData(a.Body)).ToLowerInvariant()}";
        }
        return $"{a.Status} | {a.ContentType} | loc={a.Location} | disp={a.Disposition} | {body}";
    }

    // The plugin archive: a gzip of a tar whose entries are the plugin's files in ordinal order (no bin/, obj/, .plugin-version or .plugin-managed of
    // the source), then .plugin-version {hash, gatewayVersion} and .plugin-managed. Its file list changes with the plugin, so the rules are pinned.
    static string Archive(byte[] gz)
    {
        var names = new List<string>(); var formats = new List<TarEntryFormat>(); string version = "", managed = "";
        using (var tr = new TarReader(new GZipStream(new MemoryStream(gz), CompressionMode.Decompress)))
            for (var e = tr.GetNextEntry(); e != null; e = tr.GetNextEntry())
            {
                names.Add(e.Name); formats.Add(e.Format);
                if (e.DataStream != null && e.Name is ".plugin-version" or ".plugin-managed") { using var rd = new StreamReader(e.DataStream); if (e.Name == ".plugin-version") version = rd.ReadToEnd(); else managed = rd.ReadToEnd(); }
            }
        var files = names.Take(names.Count - 2).ToList();
        var v = JsonNode.Parse(version) as JsonObject;
        return string.Join(" ; ", new[]
        {
            $"last={string.Join(",", names.TakeLast(2))}",
            $"factory.ps1={files.Contains("scripts/factory.ps1")}",
            $"ordinal={files.SequenceEqual(files.Order(StringComparer.Ordinal))}",
            $"noBinObj={!files.Any(f => f.Contains("/bin/") || f.Contains("/obj/"))}",
            $"formats={string.Join(",", formats.Distinct())}",
            $"version={(v == null ? "-" : string.Join(",", v.Select(kv => kv.Key)))}",
            $"hash64={(v?["hash"]?.ToString() is { Length: 64 } h && h.All(char.IsAsciiHexDigitLower))}",
            $"managed={managed.StartsWith("downloaded from this gateway on ", StringComparison.Ordinal) && managed.EndsWith("; factory.ps1 plugin-sync may replace it\n", StringComparison.Ordinal)}",
        });
    }

    /// <summary>The C7a routes as (method, path and query, body): every one of each module, the metrics' query rules and the record's body rules.</summary>
    public static IReadOnlyList<(string Method, string Path, string? Body)> Requests() => new (string, string, string?)[]
    {
        ("GET", "/health", null), ("GET", "/alive", null), ("GET", "/ready", null), ("GET", "/", null), ("GET", "/_build", null), ("GET", "/dashboard", null),
        ("GET", "/api/v1/status", null), ("GET", "/api/v1/dashboard", null), ("GET", "/api/v1/plugin", null),
        ("GET", "/api/v1/metrics", null), ("GET", "/api/v1/metrics?since=24h", null), ("GET", "/api/v1/metrics?since=7d", null), ("GET", "/api/v1/metrics?since=1h", null),
        ("GET", "/api/v1/metrics?fresh=1", null), ("GET", "/api/v1/metrics?fresh=0", null), ("GET", "/api/v1/metrics?max=5", null), ("GET", "/api/v1/metrics?since=24h&max=x", null),
        ("GET", "/api/v1/metrics?since=" + new string('9', 41), null), ("GET", "/api/v1/metrics?max=12345", null),
        ("GET", "/api/v1/operator/release-check", null),
        ("POST", "/api/v1/operator/record-release", "{}"), ("POST", "/api/v1/operator/record-release", "not json"),
        ("POST", "/api/v1/operator/record-release", "{\"pr\":0,\"version\":\"1.0.1\"}"), ("POST", "/api/v1/operator/record-release", "{\"pr\":8,\"version\":\"v1\"}"),
        ("POST", "/api/v1/operator/record-release", "{\"pr\":7,\"version\":\"1.0.1\",\"x\":1}"), ("POST", "/api/v1/operator/record-release", "{\"pr\":7,\"version\":\"1.0.1\"}"),
        ("POST", "/api/v1/operator/record-release", "{\"pr\":7,\"version\":\"" + new string('1', 5000) + "\"}"),
    };
}

/// <summary>
/// The answers of the routes C7a moves into endpoint modules (health, status, metrics, dashboard, plugin, operator releases), pinned from master before
/// the move (Pins/c7a-endpoints.json) beyond what the caller matrix pins: the content type, Location and Content-Disposition, the whole body (or its
/// shape where it changes from run to run), the metrics' query rules for each caller kind, the supervisor's step budget, the record's body rules, the
/// guards' order with messaging switched off at run time, and the same endpoints called in process (InProcess, as an MCP tool calls them), so the
/// endpoint filters are shown to run on that path too. The callers ask in a fixed order and some answers depend on it (the supervisor's one step,
/// the operator session's record of pull request 7 before the admin's).
/// </summary>
public sealed class EndpointPinTests(EndpointPinGateway gw) : IClassFixture<EndpointPinGateway>
{
    [Fact]
    public async Task C7aRoutes_EveryCaller_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        var secret = gw.Env.TokenSecret;
        var msg = gw.Host.Services.GetRequiredService<MessagingSettings>();
        var keeper = gw.Host.Services.GetRequiredService<KeeperSettings>();
        var callers = new (string Caller, string? Authorization)[]
        {
            ("anonymous", null),
            ("worker", AuthPins.Bearer(Auth.WorkerToken(secret, "alice"))),
            ("item-owner", AuthPins.Bearer(Auth.MintItemToken(secret, 1, "owner", "a1", "alice", TimeSpan.FromHours(1)))),
            ("supervisor", AuthPins.Bearer(Auth.ManagerToken(secret, "supervisor", msg.TokenGeneration))),
            ("keeper", AuthPins.Bearer(Auth.KeeperToken(secret, "backlog-keeper", keeper.TokenGeneration))),
            ("operator-active", AuthPins.Bearer(Auth.OperatorToken(secret, "operator-session", msg.OperatorTokenGeneration))),
            ("operator-idle", AuthPins.Bearer(Auth.OperatorToken(secret, "operator-two", msg.OperatorTokenGeneration))),
            ("admin", AuthPins.Bearer(GatewayEnvironment.AdminToken)),
        };
        var lines = new List<KeyValuePair<string, string>>();
        async Task Ask(string label, string? auth, string method, string path, string? body)
        {
            var a = await EndpointPins.Send(gw.Host, method, path, auth, body);
            var shown = body == null ? "" : body.Length > 60 ? $" body[{body.Length}]" : " " + body;
            lines.Add(new($"{label} {method} {path}{shown}", EndpointPins.Line(path, a)));
        }
        foreach (var (caller, auth) in callers)
            foreach (var (m, p, b) in EndpointPins.Requests()) await Ask(caller, auth, m, p, b);

        // messaging switched off at run time: the operator's checks answer messaging-disabled after operator-disabled is passed, the operator session is
        // no longer admitted (its slot needs messaging); /status and the dashboard JSON keep their messaging blocks (they follow messaging as loaded,
        // MessagingSettings.On, not the run-time switch)
        var admin = callers.Single(c => c.Caller == "admin").Authorization;
        var opActive = callers.Single(c => c.Caller == "operator-active").Authorization;
        Assert.Equal(200, (await EndpointPins.Send(gw.Host, "POST", "/api/v1/messaging/disable", admin, null)).Status);
        foreach (var (label, auth) in new[] { ("messaging-off operator-active", opActive), ("messaging-off admin", admin) })
            foreach (var (m, p, b) in new (string, string, string?)[] { ("GET", "/api/v1/operator/release-check", null), ("POST", "/api/v1/operator/record-release", "{\"pr\":9,\"version\":\"1.0.2\"}"),
                         ("GET", "/api/v1/metrics?since=24h", null), ("GET", "/api/v1/status", null), ("GET", "/api/v1/dashboard", null) })
                await Ask(label, auth, m, p, b);
        Assert.Equal(200, (await EndpointPins.Send(gw.Host, "POST", "/api/v1/messaging/enable", admin, null)).Status);

        // in process, as an MCP tool calls them (no middleware): the same guards answer
        var inProcess = new InProcess(new EndpointRoutes(gw.Host.Services), (_, _, next) => next());
        var principals = new[] { new Principal("worker", "alice"), new Principal("item", "alice", 1, "owner", "a1"), new Principal("manager", "supervisor"),
            new Principal("keeper", "backlog-keeper"), new Principal("operator", "operator-session"), new Principal("operator", "operator-two"), new Principal("admin") };
        foreach (var who in principals)
            foreach (var (m, p, q, b) in new (string, string, string, string?)[] { ("GET", "/api/v1/status", "", null), ("GET", "/api/v1/metrics", "", null), ("GET", "/api/v1/metrics", "since=24h", null),
                         ("GET", "/api/v1/metrics", "since=1h", null), ("GET", "/api/v1/operator/release-check", "", null), ("POST", "/api/v1/operator/record-release", "", "{\"pr\":7,\"version\":\"1.0.1\"}") })
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var (status, text) = await inProcess.Call(m, p, who, null, q, b, cts.Token);
                var path = q == "" ? p : p + "?" + q;
                lines.Add(new($"in-process {who.Kind}:{who.Name} {m} {path}{(b == null ? "" : " " + b)}", EndpointPins.Line(path, new EndpointPins.Full(status, "", "", "", Encoding.UTF8.GetBytes(text)))));
            }
        AuthPins.Compare("c7a-endpoints.json", lines);
    }
}

/// <summary>The same routes on a gateway with owner mode off, asked by the admin (pinned with the owner-mode answers, under their own keys).</summary>
public sealed class NonOwnerEndpointPinTests(NonOwnerPinGateway gw) : IClassFixture<NonOwnerPinGateway>
{
    [Fact]
    public async Task C7aRoutes_OwnerModeOff_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        var lines = new List<KeyValuePair<string, string>>();
        foreach (var (m, p, b) in EndpointPins.Requests())
        {
            var a = await EndpointPins.Send(gw.Host, m, p, AuthPins.Bearer(GatewayEnvironment.AdminToken), b);
            lines.Add(new($"{m} {p}{(b == null ? "" : b.Length > 60 ? $" body[{b.Length}]" : " " + b)}", EndpointPins.Line(p, a)));
        }
        AuthPins.Compare("c7a-endpoints-nonowner.json", lines);
    }
}
