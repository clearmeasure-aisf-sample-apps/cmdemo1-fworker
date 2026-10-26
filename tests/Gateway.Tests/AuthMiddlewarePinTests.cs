using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The order of the gateway's authentication middleware (task C6, pinned from master before it moved out of Program.cs): the port split first,
/// then the replaced process, then the bypasses (nonce redemption and Copilot exchange, the Octopus runbook's calls), then authentication, the
/// 401, the /mcp role gate and admission. Each case asks with a credential that a later step would treat differently, so a step that moves
/// before another changes an answer here. The route-and-auth table pins the answers without a credential.
/// </summary>
public sealed class AuthMiddlewareOrderTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    const int Api = GatewayEnvironment.ApiPort, Hook = GatewayEnvironment.HookPort;
    static string Admin => AuthPins.Bearer(GatewayEnvironment.AdminToken);
    const string Unauthorized = "{\"ok\":false,\"code\":\"unauthorized\",\"reason\":\"missing, malformed, forged or expired credential\",\"exit\":30}";

    [Fact]
    public async Task HookPort_ComesFirst_TheAdminTokenIsAnswered404_WithNoBody()
    {
        if (!StubPwsh.Supported) return;
        foreach (var (m, p) in new[] { ("GET", "/api/v1/status"), ("POST", "/api/v1/runtime/redeem"), ("GET", "/api/v1/runtime/octopus/desired"), ("POST", "/mcp/"), ("GET", "/health"), ("GET", "/webhooks/github") })
        {
            var a = await AuthPins.Raw(gw.Host, m, p, Hook, Admin, m == "POST" ? "{}" : null);
            Assert.True(a.Status == 404 && a.Body == "", $"{m} {p} on the webhook port: {a.Status} {a.Body}");
        }
    }

    [Fact]
    public async Task Bypasses_AreNotAuthenticated_AGarbageCredentialReachesTheEndpoint()
    {
        if (!StubPwsh.Supported) return;
        // the endpoint's own refusal (its code), never the middleware's 401 body
        var redeem = await AuthPins.Raw(gw.Host, "POST", "/api/v1/runtime/redeem", Api, AuthPins.Bearer("nope"), "{\"nonce\":\"n1.x\"}");
        Assert.Equal(401, redeem.Status);
        Assert.NotEqual(Unauthorized, redeem.Body);
        Assert.Equal("{\"ok\":false,\"code\":\"unauthorized\",\"reason\":\"unknown, spent or expired nonce\",\"exit\":30}", redeem.Body);
        var exchange = await AuthPins.Raw(gw.Host, "POST", "/api/v1/copilot/exchange", Api, AuthPins.Bearer("nope"), "{}");
        Assert.Equal("{\"ok\":false,\"code\":\"unauthorized\",\"reason\":\"missing or void bootstrap token\",\"exit\":30}", exchange.Body);
        foreach (var p in new[] { "/api/v1/runtime/octopus/desired", "/api/v1/runtime/octopus/report" })
        {
            var a = await AuthPins.Raw(gw.Host, p.EndsWith("report") ? "POST" : "GET", p, Api, AuthPins.Bearer("nope"), p.EndsWith("report") ? "{}" : null);
            Assert.Equal(404, a.Status);
            Assert.Equal("no-runtime", a.Code);
        }
        // the bypass is by exact path: a path below it is an /api path like any other
        Assert.Equal(Unauthorized, (await AuthPins.Raw(gw.Host, "POST", "/api/v1/runtime/redeem/x", Api, null, "{}")).Body);
    }

    [Fact]
    public async Task Refusal_IsTheMiddlewaresBody_ForEveryKindOfBadCredential()
    {
        if (!StubPwsh.Supported) return;
        foreach (var auth in new string?[] { null, "", "Bearer", "Bearer ", "bearer " + GatewayEnvironment.AdminToken, "Basic dGVzdA==", AuthPins.Bearer("nope"), AuthPins.Bearer(GatewayEnvironment.AdminToken + " ") })
        {
            var a = await AuthPins.Raw(gw.Host, "GET", "/api/v1/status", Api, auth);
            Assert.True(a.Status == 401 && a.Body == Unauthorized, $"[{auth}]: {a.Status} {a.Body}");
        }
    }

    [Fact]
    public async Task Mcp_IsAuthenticatedCaseInsensitively_AndTheRoleGateRefusesBeforeTheServerReadsTheBody()
    {
        if (!StubPwsh.Supported) return;
        foreach (var p in new[] { "/mcp", "/MCP", "/Mcp/", "/mcp/x" })
        {
            var a = await AuthPins.Raw(gw.Host, "POST", p, Api, null, "not json");
            Assert.True(a.Status == 401 && a.Body == Unauthorized, $"{p}: {a.Status} {a.Body}");
        }
        // a worker (seat) plays no role at /mcp: refused whatever the body is
        var w = await AuthPins.Raw(gw.Host, "POST", "/mcp", Api, AuthPins.Bearer(Auth.WorkerToken(gw.Env.TokenSecret, "alice")), "not json");
        Assert.Equal(403, w.Status);
        Assert.Equal("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"this credential plays no role at /mcp\",\"exit\":30}", w.Body);
    }

    [Fact]
    public async Task NonApiPaths_IgnoreTheCredential()
    {
        if (!StubPwsh.Supported) return;
        foreach (var (p, status) in new[] { ("/health", 200), ("/alive", 200), ("/ready", 200), ("/_build", 200), ("/dashboard", 200), ("/sessions/alice", 200), ("/apix", 404) })
            Assert.Equal(status, (await AuthPins.Raw(gw.Host, "GET", p, Api, AuthPins.Bearer("nope"))).Status);
    }

    /// <summary>
    /// Today's answer to an /api route asked with another case: the middleware's test is the ordinal prefix "/api/", routing is case-insensitive,
    /// so the handler runs with no principal and fails (500). Pinned as it is, a known defect (filed as a follow-up of task C6); the move must
    /// not change it, and its fix changes this test.
    /// </summary>
    [Fact]
    public async Task ApiPathInAnotherCase_SkipsTheMiddleware_AsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        Assert.Equal(500, (await AuthPins.Raw(gw.Host, "GET", "/API/v1/status", Api, null)).Status);
        Assert.Equal(500, (await AuthPins.Raw(gw.Host, "GET", "/Api/v1/plugin", Api, AuthPins.Bearer("nope"))).Status);
    }
}

/// <summary>
/// A process that a newer one superseded (Leader.cs): the replaced check comes after the port split and before every bypass and the
/// authentication. Only the operator's status and dashboard are still answered. The role is set directly here: how a process learns it was
/// superseded is LeadershipGatewayTests' subject, what the middleware does then is this one's.
/// </summary>
public sealed class ReplacedProcessOrderTests
{
    const string Replaced = "{\"ok\":false,\"code\":\"gateway-replaced\",\"reason\":\"this gateway process was replaced by a newer one; try again\",\"retryAfterSeconds\":5,\"exit\":20}";

    [Fact]
    public async Task Replaced_AnswersGatewayReplaced_ExceptTheAdminsStatusAndDashboard()
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

        // the admin's status and dashboard: answered
        Assert.Equal(200, (await AuthPins.Raw(host, "GET", "/api/v1/status", api, admin)).Status);
        Assert.Equal(200, (await AuthPins.Raw(host, "GET", "/api/v1/dashboard", api, admin)).Status);
        // replaced, before any authentication: no credential, a worker's, the admin's elsewhere; the bypasses; the webhook on both ports; QC; /mcp
        foreach (var (m, p, port, auth) in new (string, string, int, string?)[]
        {
            ("GET", "/api/v1/status", api, null), ("GET", "/api/v1/status", api, worker), ("GET", "/api/v1/dashboard", api, worker), ("GET", "/api/v1/plugin", api, admin),
            ("POST", "/api/v1/verbs/status", api, admin), ("POST", "/api/v1/runtime/redeem", api, null), ("POST", "/api/v1/copilot/exchange", api, null),
            ("GET", "/api/v1/runtime/octopus/desired", api, null), ("POST", "/webhooks/github", api, null), ("POST", "/webhooks/github", hook, null),
            ("GET", "/qc/1", api, null), ("POST", "/mcp", api, admin), ("POST", "/MCP/", api, null),
        })
        {
            var a = await AuthPins.Raw(host, m, p, port, auth, m == "POST" ? "{}" : null);
            Assert.True(a.Status == 503 && a.Body == Replaced, $"{m} {p} ({port}, {auth?[..Math.Min(12, auth.Length)]}): {a.Status} {a.Body}");
        }
        // the port split still comes first; the paths outside /api, /qc, /mcp and the webhook are answered as usual
        Assert.Equal(404, (await AuthPins.Raw(host, "GET", "/api/v1/status", hook, admin)).Status);
        Assert.Equal(200, (await AuthPins.Raw(host, "GET", "/health", api, null)).Status);
        Assert.Equal(200, (await AuthPins.Raw(host, "GET", "/dashboard", api, null)).Status);
        var ready = await AuthPins.Raw(host, "GET", "/ready", api, null);
        Assert.Equal(503, ready.Status);
        Assert.Equal("{\"ready\":false,\"waitingFor\":\"nothing\",\"replaced\":true,\"reason\":\"a newer gateway process holds the lease\"}", ready.Body);
    }
}
