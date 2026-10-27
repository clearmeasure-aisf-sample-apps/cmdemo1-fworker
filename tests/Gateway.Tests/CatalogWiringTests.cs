using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Integrations.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>The settings of the catalog's wiring (W1.2): off unless named, what each key and variable does.</summary>
public sealed class CatalogSettingsTests
{
    static InstanceConfig Config(string? json)
    {
        var p = Path.Combine(Path.GetTempPath(), "catalog-settings-" + Guid.NewGuid().ToString("N") + ".json");
        if (json != null) File.WriteAllText(p, json);
        try { return InstanceConfig.Load(p, _ => null); } finally { try { File.Delete(p); } catch { } }
    }

    static Func<string, string?> Env(params (string, string)[] kv) => n => kv.Where(x => x.Item1 == n).Select(x => x.Item2).FirstOrDefault();

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"catalog\":[1]}")]
    [InlineData("{\"catalog\":{\"enabled\":\"yes\"}}")]
    [InlineData("{\"catalog\":{\"enabled\":false}}")]
    public void OffUnlessNamed(string? json) => Assert.False(CatalogSettings.From(Config(json), Env()).Enabled);

    [Fact]
    public void ShippedDefaults_AreOff_AndNameCmdemo1System()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) { root = d.FullName; break; }
        var s = CatalogSettings.From(InstanceConfig.Load(Path.Combine(root, "config", "instance.json"), _ => null), Env());
        Assert.False(s.Enabled);
        Assert.Equal("clearmeasure-aisf-sample-apps", s.Owner);
        Assert.Equal("cmdemo1-system", s.Repository);
        Assert.Equal("system.json", s.Path);
        Assert.Equal(300, s.RefreshSeconds);
    }

    [Fact]
    public void ConfigurationAndVariable()
    {
        var on = "{\"owner\":\"acme\",\"catalog\":{\"enabled\":true,\"repository\":\"sys\",\"path\":\"/cfg/system.json\",\"ref\":\"main\",\"refreshSeconds\":90}}";
        var s = CatalogSettings.From(Config(on), Env());
        Assert.True(s.Enabled);
        Assert.Equal(("acme", "sys", "cfg/system.json", "main", 90), (s.Owner, s.Repository, s.Path, s.Ref, s.RefreshSeconds));
        Assert.False(CatalogSettings.From(Config(on), Env(("FACTORY_CATALOG", "0"))).Enabled);
        Assert.True(CatalogSettings.From(Config("{}"), Env(("FACTORY_CATALOG", "1"))).Enabled);
        var q = CatalogSettings.From(Config("{\"owner\":\"acme\",\"catalog\":{\"repository\":\"other/sys\"}}"), Env());
        Assert.Equal(("other", "sys"), (q.Owner, q.Repository));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(45, 45)]
    [InlineData(10000000, 86400)]
    public void RefreshSeconds_IsClamped(int configured, int effective) =>
        Assert.Equal(effective, CatalogSettings.From(Config("{\"catalog\":{\"refreshSeconds\":" + configured + "}}"), Env()).RefreshSeconds);

    [Fact]
    public void ApiUrlAndToken_FromTheEnvironment_PlaceholderIsNone()
    {
        var s = CatalogSettings.From(Config("{}"), Env(("GITHUB_API_URL", "http://127.0.0.1:1/"), ("GH_TOKEN", " tok ")));
        Assert.Equal("http://127.0.0.1:1", s.ApiUrl);
        Assert.Equal("tok", s.Token);
        var none = CatalogSettings.From(Config("{}"), Env(("GH_TOKEN", "unset")));
        Assert.Equal("https://api.github.com", none.ApiUrl);
        Assert.Equal("", none.Token);
    }
}

/// <summary>The GitHub source: the request it makes and how it reads the answers.</summary>
public sealed class GitHubSystemJsonTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(answer(request)); }
    }

    static CatalogSettings Settings(string token = "", string @ref = "") =>
        new() { Enabled = true, Owner = "acme", Repository = "sys", Path = "a b/system.json", Ref = @ref, ApiUrl = "https://example.test", Token = token };

    [Fact]
    public void Url_EscapesTheSegments_AndAddsTheRef() =>
        Assert.Equal("https://example.test/repos/acme/sys/contents/a%20b/system.json?ref=feature%2Fx", new GitHubSystemJson(Settings(@ref: "feature/x")).Url);

    [Fact]
    public async Task Read_SendsTheRawAcceptAndTheToken_ReturnsTheBody()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"a\":1}") });
        var text = await new GitHubSystemJson(Settings("tok"), new HttpClient(h)).ReadAsync(CancellationToken.None);
        Assert.Equal("{\"a\":1}", text);
        var r = Assert.Single(h.Requests);
        Assert.Equal("Bearer tok", r.Headers.Authorization?.ToString());
        Assert.Contains("application/vnd.github.raw+json", r.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Read_WithoutAToken_SendsNoAuthorization()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        await new GitHubSystemJson(Settings(), new HttpClient(h)).ReadAsync(CancellationToken.None);
        Assert.Null(h.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Read_404IsNull_OtherRefusalsThrow()
    {
        var notFound = new GitHubSystemJson(Settings(), new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await notFound.ReadAsync(CancellationToken.None));
        var refused = new GitHubSystemJson(Settings(), new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => refused.ReadAsync(CancellationToken.None));
        Assert.Contains("403", e.Message);
    }
}

/// <summary>The refresh service and its status block on a real loader (the delegate stands for GitHub).</summary>
public sealed class CatalogRefreshServiceTests
{
    internal const string Instance = "{\"owner\":\"acme\",\"repo\":\"home\",\"project\":7,\"octopus\":{\"project\":\"sys-ui\",\"healthPath\":\"/health\"}}";

    internal const string SystemJson = "{\"system\":{\"slug\":\"sys\",\"githubOrg\":\"acme\"},\"home\":\"other\",\"deployables\":[{\"name\":\"ui\",\"repository\":\"other\",\"healthPath\":\"/ready\"}]}";

    static CatalogSettings Settings() => new() { Enabled = true, Owner = "acme", Repository = "sys", RefreshSeconds = 30 };

    sealed class Source
    {
        public string? Text = SystemJson;
        public Exception? Throw;
        public int Calls;
        public Task<string?> Read(CancellationToken ct) { Interlocked.Increment(ref Calls); if (Throw != null) throw Throw; return Task.FromResult(Text); }
    }

    static (CatalogRefreshService Service, SystemCatalogLoader Loader, Source Source) Make(TimeSpan? interval = null)
    {
        var source = new Source();
        var loader = new SystemCatalogLoader(source.Read, Instance);
        return (new CatalogRefreshService(loader, Settings()) { Interval = interval ?? TimeSpan.FromMinutes(5) }, loader, source);
    }

    static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("the condition did not hold in time"); await Task.Delay(20); }
    }

    [Fact]
    public void BeforeTheFirstRead_StatusIsTheInstanceFallback()
    {
        var (service, _, source) = Make();
        var s = service.Status();
        Assert.Equal("instance.json", (string?)s["source"]);
        Assert.Equal("acme/home", (string?)s["home"]);
        Assert.Equal(0, (long?)s["refreshes"]);
        Assert.Null(s["lastError"]);
        Assert.Empty(s["mismatches"]!.AsArray());
        Assert.Equal(0, source.Calls);
        Assert.Equal("acme/sys/system.json", (string?)s["from"]);
    }

    [Fact]
    public async Task Refresh_LoadsSystemJson_AndReportsTheMismatches()
    {
        var (service, loader, _) = Make();
        var r = await service.RefreshOnceAsync(CancellationToken.None);
        Assert.True(r.Succeeded);
        var s = service.Status();
        Assert.Equal("system.json", (string?)s["source"]);
        Assert.Equal("acme/other", (string?)s["home"]);
        Assert.Equal(1, (long?)s["refreshes"]);
        Assert.Equal(0, (long?)s["failures"]);
        var mismatches = s["mismatches"]!.AsArray().Select(m => (string?)m).ToList();
        Assert.Contains(mismatches, m => m!.StartsWith("home: system.json says acme/other, instance.json says acme/home"));
        Assert.Contains(mismatches, m => m!.Contains("healthPath"));
        Assert.Equal(loader.Current.Mismatches.Count, mismatches.Count);
        Assert.Equal(7, (int?)s["board"]!["number"]);
        Assert.Equal("acme/other", (string?)s["repositories"]![0]!["repository"]);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheLastGoodSnapshot_AndSaysWhy()
    {
        var (service, loader, source) = Make();
        await service.RefreshOnceAsync(CancellationToken.None);
        var good = loader.Current;

        source.Text = "not json";
        Assert.False((await service.RefreshOnceAsync(CancellationToken.None)).Succeeded);
        source.Text = null;
        Assert.False((await service.RefreshOnceAsync(CancellationToken.None)).Succeeded);
        source.Throw = new HttpRequestException("GitHub answered 502");
        Assert.False((await service.RefreshOnceAsync(CancellationToken.None)).Succeeded);

        Assert.Same(good, loader.Current);
        var s = service.Status();
        Assert.Equal("system.json", (string?)s["source"]);
        Assert.Equal(4, (long?)s["refreshes"]);
        Assert.Equal(3, (long?)s["failures"]);
        Assert.Contains("502", (string?)s["lastError"]);

        source.Throw = null; source.Text = SystemJson;
        await service.RefreshOnceAsync(CancellationToken.None);
        Assert.Null(service.Status()["lastError"]);
    }

    [Fact]
    public async Task FirstFailure_LeavesTheInstanceFallbackInUse()
    {
        var (service, loader, source) = Make();
        source.Throw = new HttpRequestException("down");
        await service.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(CatalogSource.InstanceFallback, loader.Current.Source);
        Assert.Equal("acme/home", (string?)service.Status()["home"]);
    }

    [Fact]
    public async Task HostedService_ReadsAtStart_ThenEveryInterval_AndStops()
    {
        var (service, _, source) = Make(TimeSpan.FromMilliseconds(30));
        await service.StartAsync(CancellationToken.None);
        await Until(() => source.Calls >= 3);
        await service.StopAsync(CancellationToken.None);
        var after = source.Calls;
        await Task.Delay(150);
        Assert.Equal(after, source.Calls);
    }

    [Fact]
    public async Task HostedService_SurvivesAThrowingSource()
    {
        var (service, _, source) = Make(TimeSpan.FromMilliseconds(30));
        source.Throw = new InvalidOperationException("boom");
        await service.StartAsync(CancellationToken.None);
        await Until(() => source.Calls >= 3);
        source.Throw = null;
        await Until(() => service.Status()["lastError"] is null && (string?)service.Status()["source"] == "system.json");
        await service.StopAsync(CancellationToken.None);
    }
}

/// <summary>The default gateway: the catalog is composed and informational, nothing is started and /api/v1/status answers as before.</summary>
public sealed class CatalogOffGatewayTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public async Task Off_ComposesTheFallback_StartsNothing_AddsNoStatusBlock()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        Assert.False(sp.GetRequiredService<CatalogSettings>().Enabled);
        var catalog = sp.GetRequiredService<ISystemCatalog>();
        Assert.Same(catalog, sp.GetRequiredService<ISystemCatalog>());
        Assert.Same(catalog, sp.GetRequiredService<SystemCatalogLoader>());
        Assert.Equal(CatalogSource.InstanceFallback, catalog.Current.Source);
        // the home repository is what instance.json says
        Assert.Equal("cmdemo1-workorders", catalog.Current.Home.Repo);
        Assert.Null(sp.GetService<CatalogRefreshService>());
        Assert.DoesNotContain(sp.GetServices<IHostedService>(), h => h is CatalogRefreshService);
        var r = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, r.Status);
        Assert.Null(r.Json!["catalog"]);
    }
}

/// <summary>A gateway with the catalog on, reading system.json from a GitHub stand-in on a local port.</summary>
public sealed class CatalogOnGateway : GatewayFixture, IDisposable
{
    public HttpListener Listener { get; private set; } = null!;
    public readonly List<string> Requests = new();
    public volatile string Body = CatalogRefreshServiceTests.SystemJson.Replace("\"slug\":\"sys\",\"githubOrg\":\"acme\"", "\"slug\":\"cmdemo1\",\"githubOrg\":\"clearmeasure-aisf-sample-apps\"")
        .Replace("\"other\"", "\"cmdemo1-fworker\"");
    public volatile int Status = 200;
    CancellationTokenSource? stop;

    protected override void Configure(GatewayEnvironment env)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Listener = new HttpListener(); Listener.Prefixes.Add($"http://127.0.0.1:{port}/"); Listener.Start();
        stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await Listener.GetContextAsync(); } catch { return; }
                lock (Requests) Requests.Add(ctx.Request.Url!.PathAndQuery);
                var bytes = Encoding.UTF8.GetBytes(Body);
                ctx.Response.StatusCode = Status;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        env.Set("GITHUB_API_URL", $"http://127.0.0.1:{port}");
        env.Set("FACTORY_CATALOG", "1");
    }

    // xunit disposes a fixture that is IDisposable after IAsyncLifetime.DisposeAsync of the base
    public void Dispose()
    {
        stop?.Cancel();
        try { Listener?.Close(); } catch { }
    }
}

public sealed class CatalogOnGatewayTests(CatalogOnGateway gw) : IClassFixture<CatalogOnGateway>
{
    static async Task Until(Func<bool> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("the condition did not hold in time"); await Task.Delay(50); }
    }

    [Fact]
    public async Task On_RefreshIsAHostedService_LastInOrder_AndStatusCarriesTheReport()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        var order = CompositionGraph.GatewayHostedOrder(sp);
        Assert.Equal("CatalogRefreshService", order[^1]);
        Assert.Equal(new[] { "RevocationService", "OperatorReleaseLoader", "TranscriptService", "LeadershipService", "BaseWatch", "Sweeper", "AutoPromoter", "PlantTicker", "McpSessionSweeper" }, order[..^1]);
        Assert.Same(sp.GetRequiredService<CatalogRefreshService>(), sp.GetServices<IHostedService>().OfType<CatalogRefreshService>().Single());

        var catalog = sp.GetRequiredService<ISystemCatalog>();
        await Until(() => catalog.Current.Source == CatalogSource.SystemJson);
        var r = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, r.Status);
        var block = r.Json!["catalog"]!.AsObject();
        Assert.Equal("system.json", (string?)block["source"]);
        Assert.Null(block["lastError"]);
        Assert.StartsWith("clearmeasure-aisf-sample-apps/cmdemo1-system/system.json", (string?)block["from"]);
        // the report names the difference to instance.json (the home is cmdemo1-workorders there); behaviour is unchanged: the loader's home is informational
        Assert.Contains(block["mismatches"]!.AsArray().Select(m => (string?)m), m => m!.Contains("instance.json says clearmeasure-aisf-sample-apps/cmdemo1-workorders"));
        lock (gw.Requests) Assert.Contains("/repos/clearmeasure-aisf-sample-apps/cmdemo1-system/contents/system.json", gw.Requests);
        // the existing top-level keys are still there
        Assert.NotNull(r.Json["plant"]);
        Assert.NotNull(r.Json["sweep"]);
    }

    [Fact]
    public async Task StatusBlock_IsAdminGated_LikeTheRestOfStatus()
    {
        if (!StubPwsh.Supported) return;
        var r = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort);
        Assert.NotEqual(200, r.Status);
        Assert.DoesNotContain("catalog", r.Body);
    }
}
