using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The gateway's own Program.cs on TestServer: everything after Build() runs (routes, stopping hooks, loops, app.Run()). The container is built
/// with ValidateOnBuild and ValidateScopes, and the service collection is kept for an explicit validation of its own.
/// </summary>
public sealed class GatewayHost : WebApplicationFactory<Program>
{
    /// <summary>The gateway's service collection, as the host built it.</summary>
    public IServiceCollection? Captured { get; private set; }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
        builder.ConfigureServices(s => Captured = s);
        return base.CreateHost(builder);
    }

    /// <summary>
    /// One request, on an explicit local port: the gateway's middleware tells the API port from the webhook port by
    /// <c>Connection.LocalPort</c>, which TestServer leaves at 0.
    /// </summary>
    public async Task<Answer> SendAsync(string method, string pathAndQuery, int port, string? bearer = null, string? body = null, int timeoutSeconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var q = pathAndQuery.IndexOf('?');
        var ctx = await Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("127.0.0.1", port);
            c.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
            if (q >= 0) c.Request.QueryString = new QueryString(pathAndQuery[q..]);
            c.Connection.LocalPort = port;
            c.Connection.LocalIpAddress = IPAddress.Loopback;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (bearer != null) c.Request.Headers.Authorization = "Bearer " + bearer;
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            c.Request.Body = new MemoryStream(bytes);
            c.Request.ContentLength = bytes.Length;
            if (body != null) c.Request.ContentType = "application/json";
        }, cts.Token);
        var text = "";
        if (ctx.Response.Body is { CanRead: true } rb) { using var rd = new StreamReader(rb); text = await rd.ReadToEndAsync(cts.Token); }
        return new Answer(ctx.Response.StatusCode, text, ctx.Response.Headers.Location.ToString());
    }
}

/// <summary>A response: status, body text and the Location header.</summary>
public sealed record Answer(int Status, string Body, string Location)
{
    /// <summary>The JSON body's "code", or null when the body is not a JSON object with one.</summary>
    public string? Code
    {
        get { try { return JsonNode.Parse(Body) is JsonObject o && o["code"] is JsonValue v ? v.ToString() : null; } catch { return null; } }
    }

    /// <summary>The body as JSON, or null.</summary>
    public JsonNode? Json { get { try { return JsonNode.Parse(Body); } catch { return null; } } }
}

/// <summary>
/// The process environment a gateway reads at start, with a temporary state directory, HOME, a copy of config/instance.json for the tests,
/// and the stub pwsh first on the PATH. <see cref="Apply"/> sets it; <see cref="Dispose"/> restores every variable it changed.
/// </summary>
public sealed class GatewayEnvironment : IDisposable
{
    public const int ApiPort = 51010, HookPort = 51011;
    public const string AdminToken = "test-token";

    readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> saved = new(StringComparer.Ordinal);
    bool applied;

    /// <summary>The repository root (the directory with config/instance.json).</summary>
    public string Root { get; } = RepoRoot();
    /// <summary>This environment's temporary directory.</summary>
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gateway-tests-" + Guid.NewGuid().ToString("N")[..10]);
    /// <summary>The stub pwsh's log of calls.</summary>
    public string StubLog => Path.Combine(Dir, "pwsh.log");
    /// <summary>The token secret, composed at run time.</summary>
    public string TokenSecret { get; } = "gateway-tests-" + Guid.NewGuid().ToString("N");

    public GatewayEnvironment()
    {
        // the base environment of the route fixtures: owner mode, test hooks, no leader, no snapshot, no revocation read, no GitHub
        Set("FACTORY_PLUGIN_SCRIPTS", Path.Combine(Root, "plugin", "scripts"));
        Set("FACTORY_PROMPTS", Path.Combine(Root, "prompts"));
        Set("FACTORY_OWNER_MODE", "1");
        Set("FACTORY_TOKEN_SECRET", TokenSecret);
        Set("FACTORY_DEV_TOKEN", AdminToken);
        Set("FACTORY_WEBHOOK_SECRET", new string('x', 40));
        Set("FACTORY_TEST_SKIP_REVOCATIONS", "1");
        Set("FACTORY_SWEEP_SECONDS", "3600");
        Set("FACTORY_LEADER_LEASE", "0");
        Set("FACTORY_PLANT_SNAPSHOT", "0");
        Set("GITHUB_API_URL", "http://127.0.0.1:9");
        Set("GH_TOKEN", "unset");
        Set("FACTORY_STATE_DIR", Path.Combine(Dir, "state"));
        Set("HOME", Path.Combine(Dir, "home"));
        Set("FACTORY_CONFIG", Path.Combine(Dir, "instance.test.json"));
        Set("FACTORY_API_PORT", ApiPort.ToString());
        Set("FACTORY_WEBHOOK_PORT", HookPort.ToString());
        Set("FACTORY_RUNNER_POOL", "0");
        Set("FACTORY_ENVIRONMENT", "tdd");
        Set("FWTEST_PWSH_LOG", StubLog);
        Set("FWTEST_DIR", Dir);
        // whatever the machine or the CI runner has set must not reach the gateway
        foreach (var k in new[] { "FACTORY_SINGLE_PORT", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_URLS", "FACTORY_PUBLIC_URL", "CURSOR_API_KEY", "COPILOT_TASKS_TOKEN", "OCTOPUS_API_KEY",
                     "OCTOPUS_RUNBOOK_API_KEY", "FACTORY_AUTHOR_APP", "FACTORY_FAKE_APPS", "FACTORY_APP_AUTHOR_KEY", "FACTORY_TRANSCRIPTS", "FACTORY_TRANSCRIPT_SAVE_SECONDS",
                     "FACTORY_ONE_ITEM_PER_SESSION", "FACTORY_ASSIGN_ACK_SECONDS", "FACTORY_SWEEP_MIN_SECONDS", "FACTORY_SSE_PING_SECONDS", "FACTORY_URL", "FACTORY_TOKEN" })
            Set(k, null);
        foreach (var k in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("FACTORY_TIMING_", StringComparison.Ordinal))) Set(k, null);
    }

    /// <summary>Changes the configuration file before it is written (for example engine.shadowEnvironments, W2.3).</summary>
    public Action<JsonObject>? EditConfig { get; set; }

    /// <summary>Sets (or, with null, removes) a variable when <see cref="Apply"/> runs.</summary>
    public void Set(string name, string? value) => values[name] = value;

    /// <summary>Writes the configuration and the stub, then sets the variables (the gateway reads them as its top-level statements run).</summary>
    public void Apply()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(Path.Combine(Dir, "state"));
        Directory.CreateDirectory(Path.Combine(Dir, "home"));
        var testConfig = TestConfig(Root); EditConfig?.Invoke(testConfig);
        File.WriteAllText(Path.Combine(Dir, "instance.test.json"), testConfig.ToJsonString());
        var stub = StubPwsh.Install(Path.Combine(Dir, "bin"));
        values["PATH"] = stub + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        foreach (var (k, v) in values)
        {
            if (!saved.ContainsKey(k)) saved[k] = Environment.GetEnvironmentVariable(k);
            Environment.SetEnvironmentVariable(k, v);
        }
        applied = true;
    }

    public void Dispose()
    {
        if (applied) foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        applied = false;
        try { Directory.Delete(Dir, true); } catch { }
    }

    /// <summary>config/instance.json as the Tier 1 suites change it, plus owner sessions, messaging with test hooks and every worker name.</summary>
    static JsonObject TestConfig(string root)
    {
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "instance.json")))!;
        cfg["ownerSessions"] = true;
        var m = cfg["messaging"] as JsonObject ?? new JsonObject(); cfg["messaging"] = m;
        m["enabled"] = true; m["testHooks"] = true; m["allowedWorkers"] = new JsonArray("*");
        cfg["intake"] = new JsonObject { ["enabled"] = false };
        cfg["plant"] = new JsonObject { ["oneItemPerSession"] = false };
        return cfg;
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json")) && Directory.Exists(Path.Combine(d.FullName, "gateway"))) return d.FullName;
        throw new InvalidOperationException("the repository root (config/instance.json) was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// A started gateway in an environment of its own, for one test class (<c>IClassFixture</c>). Subclasses change the environment.
/// On Windows nothing starts (the stub pwsh is a shell script) and every test returns early.
/// </summary>
public abstract class GatewayFixture : IAsyncLifetime
{
    public GatewayEnvironment Env { get; } = new();
    public GatewayHost Host { get; private set; } = null!;
    /// <summary>How long the start took until /ready answered 200 (null: it did not within 10 s).</summary>
    public TimeSpan? ReadyAfter { get; private set; }

    protected virtual void Configure(GatewayEnvironment env) { }

    public async Task InitializeAsync()
    {
        if (!StubPwsh.Supported) return;
        Configure(Env);
        Env.Apply();
        var sw = Stopwatch.StartNew();
        Host = new GatewayHost();
        _ = Host.Server;   // runs the gateway's top-level statements up to app.Run()
        while (sw.Elapsed < TimeSpan.FromSeconds(20))   // cold JIT of the whole gateway on a CI runner
        {
            if ((await Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status == 200) { ReadyAfter = sw.Elapsed; break; }
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        try { if (Host != null) await Host.DisposeAsync(); }
        finally { Env.Dispose(); }
    }
}

/// <summary>Two listeners, as in Tier 1 and local runs: the API port and the webhook port.</summary>
public sealed class TwoPortGateway : GatewayFixture { }

/// <summary>Container mode: one port (FACTORY_SINGLE_PORT) for the API and the webhook.</summary>
public sealed class SinglePortGateway : GatewayFixture
{
    protected override void Configure(GatewayEnvironment env) => env.Set("FACTORY_SINGLE_PORT", GatewayEnvironment.ApiPort.ToString());
}
