using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The revoked credentials as the gateway answers them from outside (C4a, a protected area): the read at start keeps /ready at 503 and makes a
/// session credential wait, the admin does not wait, a revoked generation is refused once read, and the revoke route's answers. Written against
/// the gateway before RevocationService existed, so they pin today's behaviour, not the new class's.
/// </summary>
public sealed class RevocationGatewayTests
{
    // the rule scripts for these tests: the read of the revoked credentials fails until the file revocations-ok exists, then answers mallory at
    // generation 3 (and two entries that must be ignored); revoke-worker fails while revoke-fail exists. Every call is logged with its input.
    const string Stub = """
        #!/bin/sh
        verb="$6"
        input=$(cat)
        printf '%s\t%s\n' "$verb" "$input" >> "$FWTEST_DIR/rev.log"
        case "$verb" in
          revocations)
            if [ -e "$FWTEST_DIR/revocations-ok" ]; then echo '{"ok":true,"revoked":{"mallory":3,"bad name!":4,"zed":0}}'; else echo '{"ok":false,"code":"stub-failing"}'; exit 1; fi ;;
          revoke-worker)
            if [ -e "$FWTEST_DIR/revoke-fail" ]; then echo '{"ok":false,"code":"stub-failing"}'; exit 1; else echo '{"ok":true}'; fi ;;
          *) echo '{"ok":true}' ;;
        esac
        exit 0
        """;

    static void InstallStub(GatewayEnvironment env)
    {
        var path = Path.Combine(env.Dir, "bin", "pwsh");
        File.WriteAllText(path, Stub.Replace("\r\n", "\n") + "\n");
    }

    static List<(string Verb, string Input)> Calls(GatewayEnvironment env)
    {
        var log = Path.Combine(env.Dir, "rev.log");
        if (!File.Exists(log)) return new();
        return File.ReadAllLines(log).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
    }

    static async Task<JsonNode?> RevocationsBlock(GatewayHost host)
    {
        var a = await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        return a.Json?["revocations"];
    }

    [Fact]
    public async Task ReadFailing_ReadyWaits_SessionWaits_ThenRevokedGenerationsRefused_AndRevokeRoute()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("GH_TOKEN", "fake");                 // a GitHub identity: the read is made
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Apply();
        InstallStub(env);
        await using var host = new GatewayHost();
        _ = host.Server;
        var api = GatewayEnvironment.ApiPort; var admin = GatewayEnvironment.AdminToken;

        // 1. the read fails: /ready 503 says what it waits for; /health stays 200; the admin is answered at once and sees "loading"
        var ready = await host.SendAsync("GET", "/ready", api);
        Assert.Equal(503, ready.Status);
        Assert.Equal(false, (bool?)ready.Json?["ready"]);
        Assert.Equal("revocations", (string?)ready.Json?["waitingFor"]);
        Assert.NotNull((int?)ready.Json?["attempts"]);
        Assert.True(DateTime.TryParse((string?)ready.Json?["since"], out _));
        Assert.Equal(200, (await host.SendAsync("GET", "/health", api)).Status);
        var sw = Stopwatch.StartNew();
        JsonNode? block = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            block = await RevocationsBlock(host);
            if ((int?)block?["failedAttempts"] >= 1) break;
            await Task.Delay(100);
        }
        Assert.Equal("loading", (string?)block?["state"]);
        Assert.True((int?)block?["failedAttempts"] >= 1);
        Assert.Equal(2, ((JsonObject)block!).Count);
        Assert.True((int?)(await host.SendAsync("GET", "/ready", api)).Json?["attempts"] >= 1);
        // the revoke route refuses while the read has not succeeded, and writes nothing
        var early = await host.SendAsync("POST", "/api/v1/workers/alice/revoke", api, admin);
        Assert.Equal(503, early.Status);
        Assert.Equal("starting", early.Code);
        Assert.DoesNotContain(Calls(env), c => c.Verb == "revoke-worker");

        // 2. a session credential waits for the read (up to 20 s) instead of being answered: it is admitted once the read succeeds
        var waiting = host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "alice"));
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        File.WriteAllText(Path.Combine(env.Dir, "revocations-ok"), "");
        var admitted = await waiting;
        Assert.Equal(403, admitted.Status);          // authenticated as a worker (status is the admin's): not 503, not 401
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", api)).Status);
        Assert.Equal("ok", (await host.SendAsync("GET", "/ready", api)).Body);
        block = await RevocationsBlock(host);
        Assert.Equal("loaded", (string?)block?["state"]);
        Assert.True((int?)block?["failedAttempts"] >= 1);
        Assert.Equal(2, ((JsonObject)block!).Count);
        Assert.All(Calls(env).Where(c => c.Verb == "revocations"), c => Assert.Equal("{}", c.Input));

        // 3. the generations read: mallory's credentials below generation 3 are refused; one minted at 3 works
        Assert.Equal(401, (await host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "mallory"))).Status);
        Assert.Equal(401, (await host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "mallory", 2))).Status);
        Assert.Equal(403, (await host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "mallory", 3))).Status);
        Assert.Equal(403, (await host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "zed"))).Status);

        // 4. the revoke route: operator only, a valid name, then the generation is written and takes effect
        var notAdmin = await host.SendAsync("POST", "/api/v1/workers/alice/revoke", api, Auth.WorkerToken(env.TokenSecret, "alice"));
        Assert.Equal(403, notAdmin.Status);
        Assert.Equal("forbidden", notAdmin.Code);
        var badName = await host.SendAsync("POST", "/api/v1/workers/bad.name/revoke", api, admin);
        Assert.Equal(400, badName.Status);
        Assert.Equal("usage", badName.Code);
        var revoked = await host.SendAsync("POST", "/api/v1/workers/alice/revoke", api, admin);
        Assert.Equal(200, revoked.Status);
        Assert.Equal(true, (bool?)revoked.Json?["ok"]);
        Assert.Equal("alice", (string?)revoked.Json?["name"]);
        Assert.Equal(1, (int?)revoked.Json?["generation"]);
        Assert.Equal(false, (bool?)revoked.Json?["streamClosed"]);
        Assert.Equal(0, (int?)revoked.Json?["exit"]);
        var write = Assert.Single(Calls(env), c => c.Verb == "revoke-worker");
        Assert.Equal("{\"name\":\"alice\",\"generation\":1}", write.Input);
        Assert.Equal(401, (await host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "alice"))).Status);
        Assert.Equal(403, (await host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "alice", 1))).Status);
        // a second revoke of the same name raises it again; mallory's next generation follows the one read
        Assert.Equal(2, (int?)(await host.SendAsync("POST", "/api/v1/workers/alice/revoke", api, admin)).Json?["generation"]);
        Assert.Equal(4, (int?)(await host.SendAsync("POST", "/api/v1/workers/mallory/revoke", api, admin)).Json?["generation"]);

        // 5. a write that fails revokes nothing
        File.WriteAllText(Path.Combine(env.Dir, "revoke-fail"), "");
        var failed = await host.SendAsync("POST", "/api/v1/workers/bob/revoke", api, admin);
        Assert.Equal(503, failed.Status);
        Assert.Equal("not-recorded", failed.Code);
        Assert.Equal(15, (int?)failed.Json?["retryAfterSeconds"]);
        Assert.Equal(403, (await host.SendAsync("GET", "/api/v1/status", api, Auth.WorkerToken(env.TokenSecret, "bob"))).Status);

        // 6. the rule scripts of a revocation are not verbs
        foreach (var verb in new[] { "revoke-worker", "revocations" })
        {
            var v = await host.SendAsync("POST", $"/api/v1/verbs/{verb}", api, admin, "{}");
            Assert.Equal(400, v.Status);
            Assert.Equal("use-endpoint", v.Code);
        }
    }

    [Fact]
    public async Task NoGitHubIdentity_SkippedAndReadyAtOnce_RevokeUnavailable()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);   // GH_TOKEN stays the placeholder "unset"
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Apply();
        InstallStub(env);
        await using var host = new GatewayHost();
        _ = host.Server;
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
        var block = await RevocationsBlock(host);
        Assert.Equal("skipped", (string?)block?["state"]);
        Assert.Equal("github-not-configured", (string?)block?["reason"]);
        Assert.Equal(2, ((JsonObject)block!).Count);
        var r = await host.SendAsync("POST", "/api/v1/workers/alice/revoke", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(409, r.Status);
        Assert.Equal("revocations-unavailable", r.Code);
        Assert.Equal(403, (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, Auth.WorkerToken(env.TokenSecret, "alice"))).Status);
        Assert.DoesNotContain(Calls(env), c => c.Verb is "revocations" or "revoke-worker");
    }

    [Fact]
    public async Task NoSessionsInConfiguration_SkippedOwnerSessionsOff()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Apply();
        InstallStub(env);
        // owner sessions, messaging and the operator off: no session credential is issued, so there is nothing to read
        var cfgPath = Path.Combine(env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        cfg["ownerSessions"] = false;
        cfg["messaging"]!["enabled"] = false;
        if (cfg["operator"] is JsonObject op) op["enabled"] = false;
        File.WriteAllText(cfgPath, cfg.ToJsonString());
        await using var host = new GatewayHost();
        _ = host.Server;
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
        var block = await RevocationsBlock(host);
        Assert.Equal("skipped", (string?)block?["state"]);
        Assert.Equal("owner-sessions-off", (string?)block?["reason"]);
        var r = await host.SendAsync("POST", "/api/v1/workers/alice/revoke", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(409, r.Status);
        Assert.Equal("revocations-unavailable", r.Code);
        Assert.DoesNotContain(Calls(env), c => c.Verb is "revocations" or "revoke-worker");
    }
}

/// <summary>
/// The test switch (FACTORY_TEST_SKIP_REVOCATIONS=1, the route fixtures' default, there without a GitHub identity): nothing is read, ready at
/// once, revoke refused. Without an identity the status names the skip reason, while the revoke route names the switch (each checks in its
/// own order).
/// </summary>
public sealed class RevocationTestSwitchTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public async Task TestSwitchWithoutIdentity_StatusSkipped_RevokeRefusedAsOff()
    {
        if (!StubPwsh.Supported) return;
        Assert.NotNull(gw.ReadyAfter);
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        var block = a.Json?["revocations"] as JsonObject;
        Assert.Equal("skipped", (string?)block?["state"]);
        Assert.Equal("github-not-configured", (string?)block?["reason"]);
        Assert.Equal(2, block!.Count);
        var r = await gw.Host.SendAsync("POST", "/api/v1/workers/alice/revoke", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(403, r.Status);
        Assert.Equal("revocations-off", r.Code);
        Assert.DoesNotContain(StubPwsh.Read(gw.Env.StubLog), c => c.Verb is "revocations" or "revoke-worker");
    }

    [Fact]
    public async Task TestSwitchWithIdentity_StatusOff_ReadyAtOnce()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("GH_TOKEN", "fake");
        env.Apply();
        await using var host = new GatewayHost();
        _ = host.Server;
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
        var block = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json?["revocations"] as JsonObject;
        Assert.Equal("off", (string?)block?["state"]);
        Assert.Equal("test-switch", (string?)block?["reason"]);
        Assert.Equal(2, block!.Count);
        var r = await host.SendAsync("POST", "/api/v1/workers/alice/revoke", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(403, r.Status);
        Assert.Equal("revocations-off", r.Code);
        Assert.DoesNotContain(StubPwsh.Read(env.StubLog), c => c.Verb is "revocations" or "revoke-worker");
    }
}
