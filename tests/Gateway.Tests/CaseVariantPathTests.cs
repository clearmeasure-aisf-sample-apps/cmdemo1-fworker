using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// Case variants of the paths (issue 409, a protected area: authorization). ASP.NET routing matches a route whatever the case of the request's
/// path, so every path test of the gateway's middleware and admission ignores case too: a case variant of a path is answered exactly as the
/// path itself, by every caller kind. The lower-case answers stay pinned where they were (the route-and-auth table, the caller matrix).
/// </summary>
public static class CaseVariants
{
    /// <summary>The pattern with its literal text upper-cased and its route parameters left alone ("/api/v1/sessions/{name}" to "/API/V1/SESSIONS/{name}").</summary>
    public static string Upper(string pattern)
    {
        var sb = new StringBuilder(pattern.Length);
        var depth = 0;
        foreach (var ch in pattern)
        {
            if (ch == '{') depth++;
            sb.Append(depth > 0 ? ch : char.ToUpperInvariant(ch));
            if (ch == '}') depth--;
        }
        return sb.ToString();
    }

    /// <summary>Theory rows: method, pattern, port; every route of the route-and-auth table on both ports of two-port mode.</summary>
    public static IEnumerable<object[]> TwoPortCases() => RouteTable.TwoPortCases();

    /// <summary>Theory rows: method, pattern; every route on the one port of single-port mode.</summary>
    public static IEnumerable<object[]> SinglePortCases() => RouteTable.SinglePortCases();

    /// <summary>
    /// One row per guard of the middleware and admission: the method, the lower-case path and its case variants. The /api prefix (authentication,
    /// the revocation wait and admission), the two credential-less bypasses and the Octopus runbook's two, the bypass by exact path only, the
    /// webhook (the only route of the webhook port), QC, and /mcp (case-insensitive before the fix too).
    /// </summary>
    public static readonly (string Method, string Path, string[] Variants)[] Guards =
    {
        ("GET", "/api/v1/status", new[] { "/API/v1/status", "/Api/V1/Status", "/aPI/v1/STATUS" }),
        ("GET", "/api/v1/plugin", new[] { "/API/v1/plugin", "/Api/v1/Plugin" }),
        ("GET", "/api/v1/dashboard", new[] { "/API/V1/DASHBOARD" }),
        ("GET", "/api/v1/messages", new[] { "/API/v1/messages" }),
        ("POST", "/api/v1/verbs/status", new[] { "/API/v1/verbs/status", "/Api/V1/Verbs/status" }),
        ("POST", "/api/v1/runtime/redeem", new[] { "/API/v1/runtime/redeem", "/Api/V1/Runtime/Redeem" }),
        ("POST", "/api/v1/copilot/exchange", new[] { "/API/v1/copilot/exchange", "/Api/V1/Copilot/Exchange" }),
        ("GET", "/api/v1/runtime/octopus/desired", new[] { "/API/v1/runtime/octopus/desired", "/Api/V1/Runtime/Octopus/Desired" }),
        ("POST", "/api/v1/runtime/octopus/report", new[] { "/API/V1/RUNTIME/OCTOPUS/REPORT" }),
        ("POST", "/api/v1/runtime/redeem/x", new[] { "/API/v1/runtime/redeem/x", "/api/v1/RUNTIME/REDEEM/x" }),
        ("POST", "/webhooks/github", new[] { "/WEBHOOKS/GITHUB", "/Webhooks/GitHub" }),
        ("GET", "/qc/1/status", new[] { "/QC/1/status", "/Qc/1/Status" }),
        ("POST", "/mcp/", new[] { "/MCP/", "/Mcp/" }),
    };

    /// <summary>The body a POST gets, as in the caller matrix: a JSON-RPC tools/list for /mcp, else an empty object.</summary>
    public static string? BodyOf(string method, string path) => method != "POST" ? null
        : path.Equals("/mcp/", StringComparison.OrdinalIgnoreCase) ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}"
        : "{}";
}

/// <summary>The route-and-auth table with every route's literal text upper-cased: each answers as its pinned row, on both ports of two-port mode.</summary>
public sealed class CaseVariantRouteTableTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Theory]
    [MemberData(nameof(CaseVariants.TwoPortCases), MemberType = typeof(CaseVariants))]
    public async Task Unauthenticated_UpperCase_AnswersAsThePinnedRow(string method, string pattern, int port)
    {
        if (!StubPwsh.Supported) return;
        var row = RouteTable.Find(method, pattern);
        var a = await gw.Host.SendAsync(method, RouteTable.PathOf(CaseVariants.Upper(pattern)), port);
        if (port == GatewayEnvironment.HookPort && !(method == "POST" && pattern == "/webhooks/github"))
        {
            Assert.Equal(404, a.Status);
            return;
        }
        RouteTable.AssertAnswer(row, a);
        if (row.Status == 302) Assert.Equal("/dashboard", a.Location);
    }

    [Fact]
    public async Task HookPort_TheWebhookInAnotherCase_IsTheWebhook_EveryOtherPathStays404()
    {
        if (!StubPwsh.Supported) return;
        const int hook = GatewayEnvironment.HookPort;
        var lower = await gw.Host.SendAsync("POST", "/webhooks/github", hook, null, "{}");
        Assert.Equal(401, lower.Status);
        foreach (var p in new[] { "/WEBHOOKS/GITHUB", "/Webhooks/GitHub" })
        {
            var a = await gw.Host.SendAsync("POST", p, hook, null, "{}");
            Assert.True(a.Status == lower.Status && a.Body == lower.Body, $"{p}: {a.Status} {a.Body}");
        }
        foreach (var (m, p) in new[] { ("GET", "/API/v1/status"), ("POST", "/API/v1/runtime/redeem"), ("GET", "/WEBHOOKS/GITHUB") })
        {
            var a = await AuthPins.Raw(gw.Host, m, p, hook, AuthPins.Bearer(GatewayEnvironment.AdminToken), m == "POST" ? "{}" : null);
            Assert.True(a.Status == 404 && a.Body == "", $"{m} {p} on the webhook port: {a.Status} {a.Body}");
        }
    }
}

/// <summary>The same in container mode (FACTORY_SINGLE_PORT): one port, the API column for every route.</summary>
public sealed class CaseVariantSinglePortRouteTableTests(SinglePortGateway gw) : IClassFixture<SinglePortGateway>
{
    [Theory]
    [MemberData(nameof(CaseVariants.SinglePortCases), MemberType = typeof(CaseVariants))]
    public async Task Unauthenticated_UpperCase_AnswersAsThePinnedRow(string method, string pattern)
    {
        if (!StubPwsh.Supported) return;
        var row = RouteTable.Find(method, pattern);
        RouteTable.AssertAnswer(row, await gw.Host.SendAsync(method, RouteTable.PathOf(CaseVariants.Upper(pattern)), GatewayEnvironment.ApiPort));
    }
}

/// <summary>
/// Every guard's case variants asked by every caller kind of the caller matrix (no credential, malformed, forged, QC link, worker, runtime
/// worker, item tokens, operator session holding the slot and not, supervisor, keeper, role tokens, revoked and reinstated names, the admin),
/// each right after its lower-case path: the status, JSON code, exit and reason are the same. A gateway of its own, so the caller matrix's
/// ordered pins are untouched.
/// </summary>
public sealed class CaseVariantCallerTests(CallerMatrixGateway gw) : IClassFixture<CallerMatrixGateway>
{
    static string LineOf(string path, Answer a) => path.Equals("/mcp/", StringComparison.OrdinalIgnoreCase) && a.Status == 200 ? "200 mcp" : AuthPins.Line(a);

    [Fact]
    public async Task EveryCallerKind_EveryGuard_TheCaseVariantAnswersAsTheLowerCasePath()
    {
        if (!StubPwsh.Supported) return;
        var diffs = new List<string>();
        var asked = 0;
        foreach (var (caller, auth) in AuthPins.Callers(gw.Host, gw.Env.TokenSecret))
            foreach (var (m, p, variants) in CaseVariants.Guards)
            {
                var want = LineOf(p, await AuthPins.Raw(gw.Host, m, p, GatewayEnvironment.ApiPort, auth, CaseVariants.BodyOf(m, p)));
                foreach (var v in variants)
                {
                    var got = LineOf(v, await AuthPins.Raw(gw.Host, m, v, GatewayEnvironment.ApiPort, auth, CaseVariants.BodyOf(m, v)));
                    asked++;
                    if (got != want) diffs.Add($"  {caller} {m} {v}\n      {p}: {want}\n      variant: {got}");
                }
            }
        Assert.True(asked > 500, $"asked {asked}");
        if (diffs.Count > 0) Assert.Fail($"{diffs.Count} case variants answer otherwise than their lower-case path\n" + string.Join("\n", diffs.Take(60)));
    }

    [Fact]
    public async Task NoCaseVariant_EverReachesAHandlerWithoutAPrincipal()
    {
        if (!StubPwsh.Supported) return;
        // the defect's answer (500): an /api handler that read a principal the middleware never set
        foreach (var (caller, auth) in AuthPins.Callers(gw.Host, gw.Env.TokenSecret))
            foreach (var (m, _, variants) in CaseVariants.Guards)
                foreach (var v in variants)
                    Assert.NotEqual(500, (await AuthPins.Raw(gw.Host, m, v, GatewayEnvironment.ApiPort, auth, CaseVariants.BodyOf(m, v))).Status);
    }
}

/// <summary>
/// A replaced process (Leader.cs) answers the case variants as the paths themselves: 503 gateway-replaced for /api, /qc, /mcp and the webhook,
/// except the admin's status and dashboard. As ReplacedProcessOrderTests, the role is set directly.
/// </summary>
public sealed class CaseVariantReplacedProcessTests
{
    const string Replaced = "{\"ok\":false,\"code\":\"gateway-replaced\",\"reason\":\"this gateway process was replaced by a newer one; try again\",\"retryAfterSeconds\":5,\"exit\":20}";

    [Fact]
    public async Task Replaced_CaseVariants_AnswerAsTheLowerCasePaths()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Apply();
        await using var host = new GatewayHost();
        _ = host.Server;
        var leader = host.Services.GetRequiredService<GatewayLeader>();
        typeof(GatewayLeader).GetField("role", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(leader, GatewayLeader.Role.Superseded);
        Assert.True(leader.Replaced);
        const int api = GatewayEnvironment.ApiPort, hook = GatewayEnvironment.HookPort;
        var admin = AuthPins.Bearer(GatewayEnvironment.AdminToken);
        var worker = AuthPins.Bearer(Auth.WorkerToken(env.TokenSecret, "alice"));

        // the admin's status and dashboard: answered, in any case
        foreach (var p in new[] { "/API/v1/status", "/Api/V1/Status", "/API/V1/DASHBOARD", "/api/v1/Dashboard" })
            Assert.True((await AuthPins.Raw(host, "GET", p, api, admin)).Status == 200, $"admin {p}");
        // replaced, before any authentication, in any case
        foreach (var (m, p, port, auth) in new (string, string, int, string?)[]
        {
            ("GET", "/API/v1/status", api, null), ("GET", "/Api/v1/status", api, worker), ("GET", "/API/v1/dashboard", api, worker), ("GET", "/API/v1/plugin", api, admin),
            ("POST", "/API/v1/verbs/status", api, admin), ("POST", "/API/v1/runtime/redeem", api, null), ("POST", "/Api/V1/Copilot/Exchange", api, null),
            ("GET", "/API/v1/runtime/octopus/desired", api, null), ("POST", "/WEBHOOKS/GITHUB", api, null), ("POST", "/Webhooks/GitHub", hook, null),
            ("GET", "/QC/1", api, null), ("GET", "/Qc/1/status", api, null), ("POST", "/MCP", api, admin),
        })
        {
            var a = await AuthPins.Raw(host, m, p, port, auth, m == "POST" ? "{}" : null);
            Assert.True(a.Status == 503 && a.Body == Replaced, $"{m} {p} ({port}): {a.Status} {a.Body}");
        }
        // the paths outside /api, /qc, /mcp and the webhook are answered as usual in any case
        Assert.Equal(200, (await AuthPins.Raw(host, "GET", "/HEALTH", api, null)).Status);
        Assert.Equal(404, (await AuthPins.Raw(host, "GET", "/API/v1/status", hook, admin)).Status);
    }
}
