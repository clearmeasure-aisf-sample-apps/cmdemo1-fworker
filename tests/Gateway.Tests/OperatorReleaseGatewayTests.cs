using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The operator session's recorded releases as the gateway answers them from outside (C4b; the operator identity is a protected area): the read
/// at start keeps both operator checks at 503 starting until it succeeds, it retries, it reads the attempt's UTC day and keeps only valid rows, a
/// release it read is not written again, and nothing is read where the operator is off or the test switch is set. Written against the gateway
/// before OperatorReleaseLoader existed, so they pin today's behaviour, not the new class's.
/// </summary>
public sealed class OperatorReleaseGatewayTests
{
    // the rule scripts for these tests: operator-releases fails (exit 1) until the file releases-noarray exists, then answers exit 0 without a
    // "releases" array until releases-ok exists, then answers two valid rows among invalid and repeated ones. Every call is logged with its input.
    const string Stub = """
        #!/bin/sh
        verb="$6"
        input=$(cat)
        printf '%s\t%s\n' "$verb" "$input" >> "$FWTEST_DIR/ops.log"
        case "$verb" in
          revocations) echo '{"ok":true,"revoked":{}}' ;;
          operator-releases)
            if [ -e "$FWTEST_DIR/releases-ok" ]; then
              echo '{"ok":true,"day":"x","releases":[{"pr":101,"version":"1.0.5"},{"pr":102,"version":"1.0.6"},{"pr":0,"version":"1.0.7"},{"pr":103,"version":"bad"},{"pr":101,"version":"1.0.9"},{"pr":"104","version":"1.0.8"},{"version":"1.0.4"}]}'
            elif [ -e "$FWTEST_DIR/releases-noarray" ]; then echo '{"ok":true}';
            else echo '{"ok":false,"code":"stub-failing"}'; exit 1; fi ;;
          *) echo '{"ok":true}' ;;
        esac
        exit 0
        """;

    static void InstallStub(GatewayEnvironment env) => File.WriteAllText(Path.Combine(env.Dir, "bin", "pwsh"), Stub.Replace("\r\n", "\n") + "\n");

    static List<(string Verb, string Input)> Calls(GatewayEnvironment env)
    {
        var log = Path.Combine(env.Dir, "ops.log");
        if (!File.Exists(log)) return new();
        return File.ReadAllLines(log).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
    }

    // the operator on in the test configuration
    static void OperatorOn(GatewayEnvironment env)
    {
        var cfgPath = Path.Combine(env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        cfg["operator"]!["enabled"] = true;
        File.WriteAllText(cfgPath, cfg.ToJsonString());
    }

    static async Task Until(Func<bool> condition, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (!condition()) { if (sw.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("the condition did not hold in time"); await Task.Delay(50); }
    }

    static string Today() => DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    static void AssertStarting(Answer a)
    {
        Assert.Equal(503, a.Status);
        Assert.Equal("starting", a.Code);
        Assert.Equal(false, (bool?)a.Json?["ok"]);
        Assert.Equal("the gateway is still reading the recorded releases; try again", (string?)a.Json?["reason"]);
        Assert.Equal(5, (int?)a.Json?["retryAfterSeconds"]);
        Assert.Equal(20, (int?)a.Json?["exit"]);
    }

    [Fact]
    public async Task ReadFailing_ChecksAnswerStarting_Retried_ThenValidRowsCounted_AndLoadedReleaseNotWrittenAgain()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("GH_TOKEN", "fake");                 // a GitHub identity, and the test switch off: the releases are read
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Apply();
        InstallStub(env);
        OperatorOn(env);
        await using var host = new GatewayHost();
        _ = host.Server;
        var api = GatewayEnvironment.ApiPort; var admin = GatewayEnvironment.AdminToken;
        var dayBefore = Today();

        // 1. the read fails: both checks answer 503 starting, the read is tried again, /ready does not wait for it
        await Until(() => Calls(env).Any(c => c.Verb == "operator-releases"), 15);
        AssertStarting(await host.SendAsync("GET", "/api/v1/operator/release-check", api, admin));
        AssertStarting(await host.SendAsync("POST", "/api/v1/operator/record-release", api, admin, "{\"pr\":7,\"version\":\"1.0.1\"}"));
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", api)).Status);
        // the operator's checks are the admin's: a worker is refused before the read is looked at
        var worker = await host.SendAsync("GET", "/api/v1/operator/release-check", api, Auth.WorkerToken(env.TokenSecret, "alice"));
        Assert.Equal(403, worker.Status);
        Assert.Equal("forbidden", worker.Code);

        // 2. exit 0 without a list of releases is not a read either: it is tried again (after the fixed pause)
        File.WriteAllText(Path.Combine(env.Dir, "releases-noarray"), "");
        await Until(() => Calls(env).Count(c => c.Verb == "operator-releases") >= 2, 15);
        await Task.Delay(300);
        AssertStarting(await host.SendAsync("GET", "/api/v1/operator/release-check", api, admin));

        // 3. the read succeeds: the checks answer, and only the valid rows are counted (pr > 0, a version of digits and dots, one per pull request)
        File.WriteAllText(Path.Combine(env.Dir, "releases-ok"), "");
        await Until(() => Calls(env).Count(c => c.Verb == "operator-releases") >= 3, 15);
        var sw = Stopwatch.StartNew();
        var check = await host.SendAsync("GET", "/api/v1/operator/release-check", api, admin);
        while (check.Status == 503 && sw.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(50); check = await host.SendAsync("GET", "/api/v1/operator/release-check", api, admin); }
        Assert.Equal(200, check.Status);
        Assert.Equal(true, (bool?)check.Json?["ok"]);
        Assert.Equal(2, (int?)check.Json?["releasesToday"]);
        Assert.Equal(12, (int?)check.Json?["limit"]);
        var reads = Calls(env).Where(c => c.Verb == "operator-releases").ToList();
        // the loader retries every 5 s until a read succeeds, so under load the failing stub can be read more than twice: at least the failing,
        // the no-array and the successful read happened, and the count is pinned below once the read has succeeded
        Assert.True(reads.Count >= 3, $"expected at least 3 reads, saw {reads.Count}");
        var readsWhenLoaded = reads.Count;
        var dayAfter = Today();
        Assert.All(reads, c => Assert.Contains(c.Input, new[] { $"{{\"day\":\"{dayBefore}\"}}", $"{{\"day\":\"{dayAfter}\"}}" }));
        var op = (await host.SendAsync("GET", "/api/v1/status", api, admin)).Json?["operator"];
        Assert.Equal(2, (int?)op?["releasesToday"]);
        var rows = op?["releases"] as JsonArray;
        Assert.Equal(new[] { "101:1.0.5", "102:1.0.6" }, rows!.Select(r => $"{(int?)r?["pr"]}:{(string?)r?["version"]}").ToArray());

        // 4. a release the read found is not written again; a new one is written with the day, its pull request and version
        var again = await host.SendAsync("POST", "/api/v1/operator/record-release", api, admin, "{\"pr\":101,\"version\":\"1.0.5\"}");
        Assert.Equal(200, again.Status);
        Assert.Equal(2, (int?)again.Json?["releasesToday"]);
        Assert.DoesNotContain(Calls(env), c => c.Verb == "operator-release");
        var day = Today();
        var recorded = await host.SendAsync("POST", "/api/v1/operator/record-release", api, admin, "{\"pr\":104,\"version\":\"1.0.10\"}");
        Assert.Equal(200, recorded.Status);
        Assert.Equal(true, (bool?)recorded.Json?["ok"]);
        Assert.Equal(104, (int?)recorded.Json?["pr"]);
        Assert.Equal("1.0.10", (string?)recorded.Json?["version"]);
        Assert.Equal(3, (int?)recorded.Json?["releasesToday"]);
        Assert.Equal(12, (int?)recorded.Json?["limit"]);
        Assert.Equal(0, (int?)recorded.Json?["exit"]);
        var write = Assert.Single(Calls(env), c => c.Verb == "operator-release");
        Assert.Equal($"{{\"day\":\"{day}\",\"pr\":104,\"version\":\"1.0.10\"}}", write.Input);
        // the read is made once: a successful read is never repeated
        await Task.Delay(300);
        Assert.Equal(readsWhenLoaded, Calls(env).Count(c => c.Verb == "operator-releases"));
    }

    [Fact]
    public async Task OperatorOff_NothingRead_ChecksRefusedOperatorDisabled()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Apply();
        InstallStub(env);   // operator.enabled stays false (config/instance.json)
        await using var host = new GatewayHost();
        _ = host.Server;
        var api = GatewayEnvironment.ApiPort; var admin = GatewayEnvironment.AdminToken;
        var sw = Stopwatch.StartNew();   // the revoked credentials are read first (the stub answers at once)
        while ((await host.SendAsync("GET", "/ready", api)).Status != 200 && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", api)).Status);
        foreach (var a in new[] { await host.SendAsync("GET", "/api/v1/operator/release-check", api, admin),
                                  await host.SendAsync("POST", "/api/v1/operator/record-release", api, admin, "{\"pr\":7,\"version\":\"1.0.1\"}") })
        {
            Assert.Equal(403, a.Status);
            Assert.Equal("operator-disabled", a.Code);
        }
        await Task.Delay(300);
        Assert.DoesNotContain(Calls(env), c => c.Verb is "operator-releases" or "operator-release");
        Assert.Null((await host.SendAsync("GET", "/api/v1/status", api, admin)).Json?["operator"]);
    }

    [Fact]
    public async Task TestSwitch_NothingRead_CheckAnswersAtOnce_RecordRefusedReleasesOff()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();   // FACTORY_TEST_SKIP_REVOCATIONS=1, the route fixtures' default
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Apply();
        InstallStub(env);
        OperatorOn(env);
        await using var host = new GatewayHost();
        _ = host.Server;
        var api = GatewayEnvironment.ApiPort; var admin = GatewayEnvironment.AdminToken;
        var check = await host.SendAsync("GET", "/api/v1/operator/release-check", api, admin);
        Assert.Equal(200, check.Status);
        Assert.Equal(0, (int?)check.Json?["releasesToday"]);
        var record = await host.SendAsync("POST", "/api/v1/operator/record-release", api, admin, "{\"pr\":7,\"version\":\"1.0.1\"}");
        Assert.Equal(403, record.Status);
        Assert.Equal("releases-off", record.Code);
        Assert.Equal("this gateway runs with FACTORY_TEST_SKIP_REVOCATIONS: it cannot record a release", (string?)record.Json?["reason"]);
        await Task.Delay(300);
        Assert.DoesNotContain(Calls(env), c => c.Verb is "operator-releases" or "operator-release");
    }
}
