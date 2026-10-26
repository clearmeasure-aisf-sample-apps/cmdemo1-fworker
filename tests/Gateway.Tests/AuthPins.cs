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
/// Helpers of the authorization pins (task C6, a protected area): requests with any Authorization header, Host and extra headers; the
/// credentials of every caller kind; and pin files recorded from master (FWTEST_RECORD_PINS=1 writes them into the source tree).
/// </summary>
public static class AuthPins
{
    /// <summary>
    /// One request like <see cref="GatewayHost.SendAsync"/>, with the Authorization header as given (null: none), the Host header
    /// (default 127.0.0.1:port) and extra headers.
    /// </summary>
    public static async Task<Answer> Raw(GatewayHost host, string method, string pathAndQuery, int port, string? authorization = null, string? body = null,
        string? hostHeader = null, IReadOnlyDictionary<string, string>? headers = null, int timeoutSeconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var q = pathAndQuery.IndexOf('?');
        var ctx = await host.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Scheme = "http";
            c.Request.Host = hostHeader != null ? HostString.FromUriComponent(hostHeader) : new HostString("127.0.0.1", port);
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
            if (path(c) == "/mcp" || path(c).StartsWith("/mcp/", StringComparison.OrdinalIgnoreCase)) c.Request.Headers.Accept = "application/json, text/event-stream";
        }, cts.Token);
        var text = "";
        if (ctx.Response.Body is { CanRead: true } rb) { using var rd = new StreamReader(rb); text = await rd.ReadToEndAsync(cts.Token); }
        return new Answer(ctx.Response.StatusCode, text, ctx.Response.Headers.Location.ToString());
        static string path(HttpContext c) => c.Request.Path.Value ?? "";
    }

    /// <summary>"Bearer " + token.</summary>
    public static string Bearer(string token) => "Bearer " + token;

    /// <summary>One line of an answer for a pin: status, JSON code, exit and reason ("-" where the body has none).</summary>
    public static string Line(Answer a)
    {
        var j = a.Json as JsonObject;
        string F(string k) => j?[k] is JsonValue v ? v.ToString() : "-";
        return $"{a.Status} {F("code")} {F("exit")} {F("reason")}";
    }

    /// <summary>A q1 QC link (Auth.VerifyQcToken's format), composed here: the rule scripts mint it, not the gateway.</summary>
    public static string QcLink(string secret, int pr, string sha, int item, TimeSpan ttl)
    {
        var o = new JsonObject { ["u"] = "qc-open", ["pr"] = pr, ["sha"] = sha, ["i"] = item, ["n"] = "nonce1", ["x"] = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds() };
        var p = Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("qc:" + p))).ToLowerInvariant();
        return $"q1.{p}.{mac}";
    }

    /// <summary>
    /// Every caller kind the gateway tells apart, as the Authorization header it sends (null: none), for a gateway with the operator, keeper and
    /// manager switched on and "mallory" revoked at generation 3. Names: see each line.
    /// </summary>
    public static IReadOnlyList<(string Caller, string? Authorization)> Callers(GatewayHost host, string secret)
    {
        var msg = host.Services.GetRequiredService<MessagingSettings>();
        var keeper = host.Services.GetRequiredService<KeeperSettings>();
        var hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        RoleClaims Role(string name, string role, string mode, string agent = "", string env = "tdd", int item = 0) => new(name, role, mode, agent, env, hour, item, item > 0 ? "a1" : "", item > 0 ? "development" : "");
        return new (string, string?)[]
        {
            ("anonymous", null),
            ("not-bearer", "Basic dGVzdDp0ZXN0"),
            ("malformed", Bearer("nope")),
            ("forged-worker", Bearer("w1.alice." + new string('0', 64))),
            ("qc-link", Bearer(QcLink(secret, 1, new string('a', 40), 1, TimeSpan.FromHours(1)))),
            ("worker", Bearer(Auth.WorkerToken(secret, "alice"))),
            ("runtime-worker", Bearer(Auth.WorkerToken2(secret, "rt1", "octopus", hour))),
            ("item-owner", Bearer(Auth.MintItemToken(secret, 1, "owner", "a1", "alice", TimeSpan.FromHours(1)))),
            ("item-inspector", Bearer(Auth.MintItemToken(secret, 1, "inspector", "a2", "bob", TimeSpan.FromHours(1)))),
            ("item-qc", Bearer(Auth.MintItemToken(secret, 1, "qc", "a3", "qc1", TimeSpan.FromHours(1)))),
            ("item-expired", Bearer(Auth.MintItemToken(secret, 1, "owner", "a1", "alice", TimeSpan.FromHours(-1)))),
            ("operator-active", Bearer(Auth.OperatorToken(secret, "operator-session", msg.OperatorTokenGeneration))),
            ("operator-idle", Bearer(Auth.OperatorToken(secret, "operator-two", msg.OperatorTokenGeneration))),
            ("supervisor", Bearer(Auth.ManagerToken(secret, "supervisor", msg.TokenGeneration))),
            ("keeper", Bearer(Auth.KeeperToken(secret, "backlog-keeper", keeper.TokenGeneration))),
            ("role-worker", Bearer(Auth.RoleToken(secret, Role("rw1", "worker", "hosted", item: 1)))),
            ("role-copilot", Bearer(Auth.RoleToken(secret, Role("rc1", "worker", "api", "copilot-cloud-agent", item: 1)))),
            ("role-owner", Bearer(Auth.RoleToken(secret, Role("ro1", "owner", "manual")))),
            ("role-manager", Bearer(Auth.RoleToken(secret, Role("supervisor", "manager", "manual")))),
            ("role-other-env", Bearer(Auth.RoleToken(secret, Role("rw2", "worker", "hosted", env: "prod", item: 1)))),
            ("revoked-worker", Bearer(Auth.WorkerToken(secret, "mallory"))),
            ("revoked-item", Bearer(Auth.MintItemToken(secret, 1, "owner", "a1", "mallory", TimeSpan.FromHours(1)))),
            ("revoked-role", Bearer(Auth.RoleToken(secret, Role("mallory", "worker", "hosted", item: 1)))),
            ("reinstated-worker", Bearer(Auth.WorkerToken(secret, "mallory", 3))),
            ("admin", Bearer(GatewayEnvironment.AdminToken)),
        };
    }

    static string PinPath(string name, bool source) =>
        source ? Path.Combine(new GatewayEnvironment().Root, "tests", "Gateway.Tests", "Pins", name) : Path.Combine(AppContext.BaseDirectory, "Pins", name);

    /// <summary>
    /// Compares <paramref name="actual"/> (key to line, in order) with the pin file <paramref name="name"/>; with FWTEST_RECORD_PINS=1 writes it into
    /// the source tree instead (recorded from master before a move, never edited by hand afterwards).
    /// </summary>
    public static void Compare(string name, IReadOnlyList<KeyValuePair<string, string>> actual)
    {
        if (Environment.GetEnvironmentVariable("FWTEST_RECORD_PINS") == "1")
        {
            var o = new JsonObject();
            foreach (var (k, v) in actual) o[k] = v;
            File.WriteAllText(PinPath(name, true), o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            return;
        }
        var pinned = (JsonObject)JsonNode.Parse(File.ReadAllText(PinPath(name, false)))!;
        var diffs = new List<string>();
        foreach (var (k, v) in actual)
            if (pinned[k]?.ToString() is not { } want) diffs.Add($"  not pinned: {k} => {v}");
            else if (want != v) diffs.Add($"  {k}\n      pinned: {want}\n      now:    {v}");
        foreach (var kv in pinned) if (!actual.Any(a => a.Key == kv.Key)) diffs.Add($"  pinned, not asked: {kv.Key}");
        if (diffs.Count > 0) Assert.Fail($"{name}: {diffs.Count} answers differ from the pin\n" + string.Join("\n", diffs.Take(60)));
    }
}
