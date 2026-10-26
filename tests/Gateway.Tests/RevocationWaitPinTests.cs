using System.Diagnostics;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The middleware's wait for the revoked credentials (task C6, pinned from master; RevocationGatewayTests has the worker, the admin and /ready):
/// every /api and /mcp request that is not the admin's waits for the read, up to 20 s, whatever its credential; then it is answered
/// 503 starting, or, once the read succeeded, authenticated again (a bad credential: 401). The bypasses do not wait in the middleware.
/// </summary>
public sealed class RevocationWaitPinTests
{
    const string Starting = "{\"ok\":false,\"code\":\"starting\",\"reason\":\"the gateway is still reading the revoked credentials; try again\",\"retryAfterSeconds\":5,\"exit\":20}";
    const string Unauthorized = "{\"ok\":false,\"code\":\"unauthorized\",\"reason\":\"missing, malformed, forged or expired credential\",\"exit\":30}";

    [Fact]
    public async Task UntilTheReadSucceeds_NonAdminCallsWait20Seconds_ThenStarting_BypassesDoNotWait_ThenReauthenticated()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Apply();
        StubPwsh.Answer(env.Dir, "revocations", "{\"ok\":false,\"code\":\"stub-failing\"}", 1);
        await using var host = new GatewayHost();
        _ = host.Server;
        const int api = GatewayEnvironment.ApiPort;

        // 1. a malformed credential at /api and none at /mcp: each waits the full 20 s, then 503 starting
        var sw = Stopwatch.StartNew();
        var malformed = AuthPins.Raw(host, "GET", "/api/v1/status", api, AuthPins.Bearer("nope"), timeoutSeconds: 60);
        var mcp = AuthPins.Raw(host, "POST", "/mcp", api, null, "{}", timeoutSeconds: 60);
        // 2. meanwhile the bypasses answer at once: the Octopus runbook's call and the Copilot exchange (their own refusals)
        var octopus = await AuthPins.Raw(host, "GET", "/api/v1/runtime/octopus/desired", api, null);
        Assert.Equal(404, octopus.Status);
        Assert.Equal("no-runtime", octopus.Code);
        var exchange = await AuthPins.Raw(host, "POST", "/api/v1/copilot/exchange", api, null, "{}");
        Assert.Equal(401, exchange.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"the bypasses took {sw.Elapsed}");
        // the nonce redemption passes the middleware too, and its endpoint waits for the read by itself
        var redeem = AuthPins.Raw(host, "POST", "/api/v1/runtime/redeem", api, null, "{\"nonce\":\"n1.x\"}", timeoutSeconds: 60);
        var a1 = await malformed; var a2 = await mcp;
        var waited = sw.Elapsed;
        Assert.True(a1.Status == 503 && a1.Body == Starting, $"malformed: {a1.Status} {a1.Body}");
        Assert.True(a2.Status == 503 && a2.Body == Starting, $"/mcp: {a2.Status} {a2.Body}");
        Assert.True(waited >= TimeSpan.FromSeconds(19) && waited < TimeSpan.FromSeconds(40), $"waited {waited}");
        var r = await redeem;
        Assert.Equal(503, r.Status);
        Assert.Equal("starting", r.Code);

        // 3. a request without a credential waits, and is answered 401 once the read succeeds (authenticated again, not admitted)
        var anonymous = AuthPins.Raw(host, "GET", "/api/v1/plugin", api, null, timeoutSeconds: 60);
        await Task.Delay(300);
        Assert.False(anonymous.IsCompleted);
        StubPwsh.Answer(env.Dir, "revocations", "{\"ok\":true,\"revoked\":{}}");
        var a3 = await anonymous;
        Assert.True(a3.Status == 401 && a3.Body == Unauthorized, $"anonymous: {a3.Status} {a3.Body}");
        Assert.Equal(200, (await AuthPins.Raw(host, "GET", "/ready", api, null)).Status);
        // and after the read nothing waits
        sw.Restart();
        Assert.Equal(Unauthorized, (await AuthPins.Raw(host, "GET", "/api/v1/status", api, AuthPins.Bearer("nope"))).Body);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }
}
