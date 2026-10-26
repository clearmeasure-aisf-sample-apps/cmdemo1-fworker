using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// A gateway for the admission pins: the operator session on and holding the slot, and a stub whose get-item waits while the file "hold"
/// exists in its directory (so a verb call can be seen in flight).
/// </summary>
public sealed class AdmissionGateway : IAsyncLifetime
{
    const string Stub = """
        #!/bin/sh
        verb="$6"
        input=$(cat)
        printf '%s\t%s\n' "$verb" "$input" >> "$FWTEST_PWSH_LOG.in"
        if [ "$verb" = "get-item" ]; then n=0; while [ -e "$FWTEST_DIR/hold" ] && [ $n -lt 600 ]; do sleep 0.05; n=$((n+1)); done; fi
        echo '{"ok":true}'
        exit 0
        """;

    public GatewayEnvironment Env { get; } = new();
    public GatewayHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!StubPwsh.Supported) return;
        Env.Apply();
        File.WriteAllText(Path.Combine(Env.Dir, "bin", "pwsh"), Stub.Replace("\r\n", "\n") + "\n");
        var cfgPath = Path.Combine(Env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        cfg["operator"]!["enabled"] = true;
        cfg["operator"]!["allowedNames"] = new JsonArray("operator-session", "operator-two");
        File.WriteAllText(cfgPath, cfg.ToJsonString());
        Host = new GatewayHost();
        _ = Host.Server;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && (await Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status != 200) await Task.Delay(100);
        Assert.True(Host.Services.GetRequiredService<Plant>().RegisterOperator("operator-session", "claude-opus-5-5", "low", 0).Ok);
    }

    public async Task DisposeAsync()
    {
        try { if (Host != null) await Host.DisposeAsync(); }
        finally { Env.Dispose(); }
    }
}

/// <summary>
/// Admission (task C6, pinned from master before Admit moved out of Program.cs): the per-principal work of an admitted /api request, run by the
/// middleware and, for an MCP tool's in-process call, by InProcess with the same function. Presence for a session's own credential (a worker,
/// an item token of owner or inspector; never QC, a role token or the admin), the operator session heard, and an item token's verb call in
/// flight for the operator's quiet-plant check. Each over HTTP and in process.
/// </summary>
public sealed class AdmissionPinTests(AdmissionGateway gw) : IClassFixture<AdmissionGateway>
{
    const int Api = GatewayEnvironment.ApiPort;
    string Secret => gw.Env.TokenSecret;

    async Task<string> Peers() => (await gw.Host.SendAsync("GET", "/api/v1/peers?all=1", Api, GatewayEnvironment.AdminToken)).Body;
    static bool Lists(string peers, string name) => peers.Contains($"\"name\":\"{name}\"", StringComparison.Ordinal);

    async Task<DateTime> OperatorHeard()
    {
        var s = await gw.Host.SendAsync("GET", "/api/v1/status", Api, GatewayEnvironment.AdminToken);
        return DateTime.Parse((string)s.Json!["operator"]!["session"]!["lastHeard"]!, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    async Task<JsonArray> Reasons() => (JsonArray)(await gw.Host.SendAsync("GET", "/api/v1/operator/release-check", Api, GatewayEnvironment.AdminToken)).Json!["reasons"]!;
    static bool MidVerb(JsonArray reasons, string worker, int item, string verb) =>
        reasons.Any(r => (string?)r?["code"] == "owner-mid-verb" && (string?)r?["worker"] == worker && (int?)r?["item"] == item && (string?)r?["verb"] == verb);

    // the InProcess the gateway gave its MCP tools (with its own Admit)
    static InProcess GatewaysInProcess() => (InProcess)typeof(McpTools).GetField("api", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(GatewayMcp.Tools)!;

    [Fact]
    public async Task Presence_OverHttp_TheSessionsOwnCredentialsOnly_EvenWhenTheEndpointRefuses()
    {
        if (!StubPwsh.Supported) return;
        var asked = new (string Name, string Token, bool Heard)[]
        {
            ("p-worker", Auth.WorkerToken(Secret, "p-worker"), true),
            ("p-runtime", Auth.WorkerToken2(Secret, "p-runtime", "octopus", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600), true),
            ("p-owner", Auth.MintItemToken(Secret, 3, "owner", "a1", "p-owner", TimeSpan.FromHours(1)), true),
            ("p-inspector", Auth.MintItemToken(Secret, 3, "inspector", "a2", "p-inspector", TimeSpan.FromHours(1)), true),
            ("p-qc", Auth.MintItemToken(Secret, 3, "qc", "a3", "p-qc", TimeSpan.FromHours(1)), false),
            ("p-role", Auth.RoleToken(Secret, new RoleClaims("p-role", "worker", "hosted", "", "tdd", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, 3, "a4", "development")), false),
        };
        var before = await Peers();
        foreach (var x in asked) Assert.False(Lists(before, x.Name), x.Name);
        // the status route refuses every one of them (403): admission ran before the endpoint all the same
        foreach (var x in asked) Assert.Equal(403, (await gw.Host.SendAsync("GET", "/api/v1/status", Api, x.Token)).Status);
        var after = await Peers();
        foreach (var x in asked) Assert.True(Lists(after, x.Name) == x.Heard, $"{x.Name}: heard {Lists(after, x.Name)}, pinned {x.Heard}");
    }

    [Fact]
    public async Task OperatorHeard_OverHttp_OnlyTheNameHoldingTheSlot_AtAnyEndpoint()
    {
        if (!StubPwsh.Supported) return;
        var t0 = await OperatorHeard();
        await Task.Delay(30);
        // operator-two holds no slot: not heard
        Assert.Equal(403, (await gw.Host.SendAsync("GET", "/api/v1/dashboard", Api, Auth.OperatorToken(Secret, "operator-two", 1))).Status);
        Assert.Equal(t0, await OperatorHeard());
        // the slot holder, at an endpoint that refuses it (the dashboard JSON is the person's): heard
        Assert.Equal(403, (await gw.Host.SendAsync("GET", "/api/v1/dashboard", Api, Auth.OperatorToken(Secret, "operator-session", 1))).Status);
        Assert.True(await OperatorHeard() > t0);
    }

    [Fact]
    public async Task VerbInFlight_OverHttp_AnItemTokensVerbCall_FromArrivalToAnswer()
    {
        if (!StubPwsh.Supported) return;
        var hold = Path.Combine(gw.Env.Dir, "hold");
        File.WriteAllText(hold, "");
        var call = gw.Host.SendAsync("POST", "/api/v1/verbs/get-item", Api, Auth.MintItemToken(Secret, 7, "owner", "a7", "f-owner", TimeSpan.FromHours(1)), "{\"item\":7}", 60);
        var sw = Stopwatch.StartNew();
        while (!MidVerb(await Reasons(), "f-owner", 7, "get-item") && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Assert.True(MidVerb(await Reasons(), "f-owner", 7, "get-item"));
        Assert.False(call.IsCompleted);
        File.Delete(hold);
        Assert.Equal(200, (await call).Status);
        Assert.False(MidVerb(await Reasons(), "f-owner", 7, "get-item"));
    }

    [Fact]
    public async Task InProcess_TheGatewaysAdmitRuns_Presence_OperatorHeard_VerbInFlight_NoMiddleware()
    {
        if (!StubPwsh.Supported) return;
        var inProcess = GatewaysInProcess();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;

        // presence: a worker's call in process stamps it, a QC item token's does not; the endpoint answers as over HTTP (no middleware: no 401)
        var (s1, _) = await inProcess.Call("GET", "/api/v1/status", new Principal("worker", "ip-worker"), null, "", null, cts.Token);
        var (s2, _) = await inProcess.Call("GET", "/api/v1/status", new Principal("item", "ip-qc", 3, "qc", "a3", exp), null, "", null, cts.Token);
        Assert.Equal(403, s1);
        Assert.Equal(403, s2);
        var peers = await Peers();
        Assert.True(Lists(peers, "ip-worker"));
        Assert.False(Lists(peers, "ip-qc"));

        // the operator session heard, and admitted as the operator at the status
        var t0 = await OperatorHeard();
        await Task.Delay(30);
        var (s3, b3) = await inProcess.Call("GET", "/api/v1/status", new Principal("operator", "operator-session"), null, "", null, cts.Token);
        Assert.Equal(200, s3);
        Assert.True(JsonNode.Parse(b3)?["plant"] != null);
        Assert.True(await OperatorHeard() > t0);

        // an item token's verb in flight, from the call to its answer
        var hold = Path.Combine(gw.Env.Dir, "hold");
        File.WriteAllText(hold, "");
        var call = inProcess.Call("POST", "/api/v1/verbs/{verb}", new Principal("item", "ip-owner", 9, "owner", "a9", exp), new Dictionary<string, string> { ["verb"] = "get-item" }, "", "{\"item\":9}", cts.Token);
        var sw = Stopwatch.StartNew();
        while (!MidVerb(await Reasons(), "ip-owner", 9, "get-item") && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Assert.True(MidVerb(await Reasons(), "ip-owner", 9, "get-item"));
        File.Delete(hold);
        Assert.Equal(200, (await call).Status);
        Assert.False(MidVerb(await Reasons(), "ip-owner", 9, "get-item"));
    }

    // issue 409: a case variant of an /api path is admitted as the path itself (presence, the operator heard, the verb in flight)
    [Fact]
    public async Task CaseVariant_OverHttp_IsAdmittedAsTheLowerCasePath()
    {
        if (!StubPwsh.Supported) return;
        // presence: a worker's and an owner's call stamp them, a QC item token's does not
        var asked = new (string Name, string Token, bool Heard)[]
        {
            ("cv-worker", Auth.WorkerToken(Secret, "cv-worker"), true),
            ("cv-owner", Auth.MintItemToken(Secret, 4, "owner", "a1", "cv-owner", TimeSpan.FromHours(1)), true),
            ("cv-qc", Auth.MintItemToken(Secret, 4, "qc", "a3", "cv-qc", TimeSpan.FromHours(1)), false),
        };
        foreach (var x in asked) Assert.Equal(403, (await gw.Host.SendAsync("GET", "/API/v1/status", Api, x.Token)).Status);
        var peers = await Peers();
        foreach (var x in asked) Assert.True(Lists(peers, x.Name) == x.Heard, $"{x.Name}: heard {Lists(peers, x.Name)}, wanted {x.Heard}");

        // the operator session holding the slot: heard
        var t0 = await OperatorHeard();
        await Task.Delay(30);
        Assert.Equal(403, (await gw.Host.SendAsync("GET", "/Api/V1/Dashboard", Api, Auth.OperatorToken(Secret, "operator-session", 1))).Status);
        Assert.True(await OperatorHeard() > t0);

        // an item token's verb in flight, from arrival to answer
        var hold = Path.Combine(gw.Env.Dir, "hold");
        File.WriteAllText(hold, "");
        var call = gw.Host.SendAsync("POST", "/API/V1/Verbs/get-item", Api, Auth.MintItemToken(Secret, 8, "owner", "a8", "cv-flight", TimeSpan.FromHours(1)), "{\"item\":8}", 60);
        var sw = Stopwatch.StartNew();
        while (!MidVerb(await Reasons(), "cv-flight", 8, "get-item") && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Assert.True(MidVerb(await Reasons(), "cv-flight", 8, "get-item"));
        Assert.False(call.IsCompleted);
        File.Delete(hold);
        Assert.Equal(200, (await call).Status);
        Assert.False(MidVerb(await Reasons(), "cv-flight", 8, "get-item"));
    }
}

/// <summary>
/// The nonce redemption (task C6, pinned from master): no gateway credential (the nonce is the credential), once; the w2 it answers is a worker
/// credential the middleware then admits.
/// </summary>
public sealed class NonceRedemptionPinTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public async Task Redeem_Once_ThenTheW2IsAWorker_AndTheNonceIsSpent()
    {
        if (!StubPwsh.Supported) return;
        const int api = GatewayEnvironment.ApiPort;
        var mint = await gw.Host.SendAsync("POST", "/api/v1/_test/runtime-nonce", api, GatewayEnvironment.AdminToken, "{\"name\":\"rn1\",\"runtime\":\"cae-job\"}");
        Assert.Equal(200, mint.Status);
        var nonce = (string)mint.Json!["nonce"]!;
        var body = new JsonObject { ["nonce"] = nonce }.ToJsonString();
        // a gateway credential is not needed, and a bad one does not matter
        var r = await AuthPins.Raw(gw.Host, "POST", "/api/v1/runtime/redeem", api, AuthPins.Bearer("nope"), body);
        Assert.Equal(200, r.Status);
        Assert.Equal(true, (bool?)r.Json?["ok"]);
        Assert.Equal("rn1", (string?)r.Json?["name"]);
        var token = (string)r.Json!["token"]!;
        Assert.StartsWith("w2.rn1.cae-job.", token);
        // the w2: a worker at the plugin (200), not the admin at the status (403)
        Assert.Equal(200, (await gw.Host.SendAsync("GET", "/api/v1/plugin", api, token)).Status);
        Assert.Equal(403, (await gw.Host.SendAsync("GET", "/api/v1/status", api, token)).Status);
        // spent: refused like an unknown nonce
        var again = await AuthPins.Raw(gw.Host, "POST", "/api/v1/runtime/redeem", api, null, body);
        Assert.Equal(401, again.Status);
        Assert.Equal("unauthorized", again.Code);
        // on the webhook port: the port split first
        Assert.Equal(404, (await AuthPins.Raw(gw.Host, "POST", "/api/v1/runtime/redeem", GatewayEnvironment.HookPort, null, body)).Status);
    }
}
