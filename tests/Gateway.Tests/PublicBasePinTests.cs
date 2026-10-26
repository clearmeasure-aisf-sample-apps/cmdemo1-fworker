using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The gateway's own address for the links it writes (task C6, pinned from master before PublicBase moved out of Program.cs): FACTORY_PUBLIC_URL,
/// else the scheme and host an admin request, a nonce redemption or a Copilot exchange last reached it on (X-Forwarded-Proto when it is exactly
/// https or http). Read from outside where a handler uses it: the session link a rule script gets with a verb call of a runtime worker
/// (`_session` of the input; an octopus runtime's link is the gateway's transcript page).
/// </summary>
public sealed class PublicBasePinTests
{
    const int Api = GatewayEnvironment.ApiPort, Hook = GatewayEnvironment.HookPort;

    static GatewayEnvironment Env(Action<GatewayEnvironment>? change = null)
    {
        var env = new GatewayEnvironment();
        change?.Invoke(env);
        env.Apply();
        return env;
    }

    // the session link the rule script gets for a verb call of runtime worker rt1 (octopus): {"name":"rt1","url":...}
    static async Task<JsonNode?> SessionOf(GatewayHost host, GatewayEnvironment env, string token)
    {
        var before = StubPwsh.Inputs(env.StubLog, "my-items").Count;
        var a = await host.SendAsync("POST", "/api/v1/verbs/my-items", Api, token, "{}");
        Assert.Equal(200, a.Status);
        var inputs = StubPwsh.Inputs(env.StubLog, "my-items");
        Assert.Equal(before + 1, inputs.Count);
        return JsonNode.Parse(inputs[^1].Text)?["_session"];
    }

    static async Task<string?> LinkOf(GatewayHost host, GatewayEnvironment env) =>
        (string?)(await SessionOf(host, env, Auth.WorkerToken2(env.TokenSecret, "rt1", "octopus", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600)))?["url"];

    static Dictionary<string, string> Proto(string p) => new() { ["X-Forwarded-Proto"] = p };

    [Fact]
    public async Task WithoutPublicUrl_TheAdminRedeemAndExchangeNoteTheBase_NoOtherCallerDoes()
    {
        if (!StubPwsh.Supported) return;
        using var env = Env();
        await using var host = new GatewayHost();
        _ = host.Server;
        var admin = AuthPins.Bearer(GatewayEnvironment.AdminToken);

        // nothing noted yet: no link; the session's name only. A worker without a runtime or a transcript: no link
        Assert.Equal("{\"name\":\"rt1\",\"url\":\"\"}", (await SessionOf(host, env, Auth.WorkerToken2(env.TokenSecret, "rt1", "octopus", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600)))?.ToJsonString());
        Assert.Equal("{\"name\":\"alice\",\"url\":\"\"}", (await SessionOf(host, env, Auth.WorkerToken(env.TokenSecret, "alice")))?.ToJsonString());

        // not noted: no credential, a bad one, a worker's, the Octopus runbook's call, a page, the webhook, the admin on the webhook port
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, null, hostHeader: "evil1.example:1", headers: Proto("https"));
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, AuthPins.Bearer("nope"), hostHeader: "evil2.example:2");
        await AuthPins.Raw(host, "GET", "/api/v1/plugin", Api, AuthPins.Bearer(Auth.WorkerToken(env.TokenSecret, "alice")), hostHeader: "evil3.example:3");
        await AuthPins.Raw(host, "GET", "/api/v1/runtime/octopus/desired", Api, null, hostHeader: "evil4.example:4");
        await AuthPins.Raw(host, "GET", "/health", Api, admin, hostHeader: "evil5.example:5");
        await AuthPins.Raw(host, "POST", "/webhooks/github", Hook, admin, "{}", hostHeader: "evil6.example:6");
        await AuthPins.Raw(host, "GET", "/api/v1/status", Hook, admin, hostHeader: "evil7.example:7");
        Assert.Equal("", await LinkOf(host, env));

        // the admin's own verb call: no session link in its input, and its host (the default 127.0.0.1:port here) is noted
        Assert.Null(await SessionOf(host, env, GatewayEnvironment.AdminToken));
        Assert.Equal($"http://127.0.0.1:{Api}/sessions/rt1", await LinkOf(host, env));

        // the admin: its scheme and host; X-Forwarded-Proto only when it is exactly https or http
        Assert.Equal(200, (await AuthPins.Raw(host, "GET", "/api/v1/status", Api, admin, hostHeader: "gw.example:8443", headers: Proto("https"))).Status);
        Assert.Equal("https://gw.example:8443/sessions/rt1", await LinkOf(host, env));
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, admin, hostHeader: "a.example", headers: Proto("ftp"));
        Assert.Equal("http://a.example/sessions/rt1", await LinkOf(host, env));
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, admin, hostHeader: "b.example", headers: Proto("HTTPS"));
        Assert.Equal("http://b.example/sessions/rt1", await LinkOf(host, env));
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, admin, hostHeader: "c.example:80", headers: Proto("http"));
        Assert.Equal("http://c.example:80/sessions/rt1", await LinkOf(host, env));
        // noted before the admin's request is answered, whatever the answer: a route the admin is refused at
        await AuthPins.Raw(host, "POST", "/api/v1/presence", Api, admin, "{}", hostHeader: "d.example");
        Assert.Equal("http://d.example/sessions/rt1", await LinkOf(host, env));
        // the admin at /mcp
        await AuthPins.Raw(host, "POST", "/mcp", Api, admin, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", hostHeader: "m.example");
        Assert.Equal("http://m.example/sessions/rt1", await LinkOf(host, env));

        // the nonce redemption and the Copilot exchange carry no gateway credential and note the base all the same, refused or not
        Assert.Equal(401, (await AuthPins.Raw(host, "POST", "/api/v1/runtime/redeem", Api, null, "{\"nonce\":\"n1.x\"}", hostHeader: "r.example:9")).Status);
        Assert.Equal("http://r.example:9/sessions/rt1", await LinkOf(host, env));
        Assert.Equal(401, (await AuthPins.Raw(host, "POST", "/api/v1/copilot/exchange", Api, null, "{}", hostHeader: "x.example", headers: Proto("https"))).Status);
        Assert.Equal("https://x.example/sessions/rt1", await LinkOf(host, env));
    }

    [Fact]
    public async Task WithPublicUrl_ItIsTheBase_TrailingSlashTrimmed_NothingNoted()
    {
        if (!StubPwsh.Supported) return;
        using var env = Env(e => e.Set("FACTORY_PUBLIC_URL", "https://pub.example/"));
        await using var host = new GatewayHost();
        _ = host.Server;
        Assert.Equal("https://pub.example/sessions/rt1", await LinkOf(host, env));
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, AuthPins.Bearer(GatewayEnvironment.AdminToken), hostHeader: "gw.example:8443", headers: Proto("http"));
        await AuthPins.Raw(host, "POST", "/api/v1/runtime/redeem", Api, null, "{}", hostHeader: "r.example");
        Assert.Equal("https://pub.example/sessions/rt1", await LinkOf(host, env));
    }

    [Fact]
    public async Task BehindATrustedProxy_TheBaseReadsWhatTheForwardedHeadersMiddlewareLeft()
    {
        if (!StubPwsh.Supported) return;
        using var env = Env();
        var cfgPath = Path.Combine(env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        cfg["qc"]!["trustProxy"] = true;
        cfg["qc"]!["knownProxies"] = new JsonArray("127.0.0.1");
        File.WriteAllText(cfgPath, cfg.ToJsonString());
        await using var host = new GatewayHost();
        _ = host.Server;
        var admin = AuthPins.Bearer(GatewayEnvironment.AdminToken);
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, admin, hostHeader: "p.example", headers: new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https", ["X-Forwarded-For"] = "10.0.0.1" });
        Assert.Equal("https://p.example/sessions/rt1", await LinkOf(host, env));
        // a chain: the forwarded-headers middleware applies the trusted proxy's last entry (scheme https) and leaves the rest ("http") in the header,
        // which the base reads first: the base follows the header the middleware left, as on master
        await AuthPins.Raw(host, "GET", "/api/v1/status", Api, admin, hostHeader: "q.example", headers: new Dictionary<string, string> { ["X-Forwarded-Proto"] = "http, https", ["X-Forwarded-For"] = "10.0.0.2, 10.0.0.1" });
        Assert.Equal("http://q.example/sessions/rt1", await LinkOf(host, env));
    }
}
