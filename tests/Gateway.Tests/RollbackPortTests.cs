using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W8.3 in the gateway, without a database: the executor's retry rule and what it tells the train, the read of the files two commits differ by (GET only), the settings of the migration paths, and the host
/// that runs the executor as generated Wolverine code (a refusal reaches the train as its input).
/// </summary>
public sealed class RollbackPortTests
{
    static readonly RepoId Home = RepoId.Parse("acme/work");
    static readonly RollbackRelease Command = new("RollbackRelease|orders|inc-1|1.0.1|tdd", "orders", "orders-ui", "1.0.1", "tdd", "inc-1");

    sealed class Octo : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path)> Calls = [];
        public int CreateStatus = 200;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            lock (Calls) Calls.Add((request.Method, path));
            (int, string) a = path switch
            {
                "api/Spaces-1/projects/orders-ui" => (200, """{"Id":"Projects-1"}"""),
                "api/Spaces-1/projects/Projects-1/releases/1.0.1" => (200, """{"Id":"Releases-1"}"""),
                "api/Spaces-1/environments?take=1000" => (200, """{"Items":[{"Id":"E1","Name":"Tdd"}]}"""),
                "api/Spaces-1/deployments?projects=Projects-1&environments=E1&take=1" => (200, """{"Items":[{"ReleaseId":"Releases-2","TaskId":"ServerTasks-9"}]}"""),
                "api/Spaces-1/deployments/create/untenanted/v1" => CreateStatus == 200 ? (200, """{"DeploymentServerTasks":[{"ServerTaskId":"ServerTasks-77"}]}""") : (CreateStatus, """{"ErrorMessage":"Missing permission: DeploymentCreate"}"""),
                _ => (404, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)a.Item1) { Content = new StringContent(a.Item2, Encoding.UTF8, "application/json") });
        }
    }

    static RollbackExecutorService Service(Octo octo, IIdempotencyLedger? ledger = null)
        => new(new RollbackReleaseHandler(ledger ?? new InMemoryIdempotencyLedger(),
            new RollbackExecutor(new HttpClient(octo) { BaseAddress = new Uri("https://octopus.example/") }, new RollbackSettings("Spaces-1", new Dictionary<string, string> { ["tdd"] = "Tdd" }, () => "k"))));

    // ---- the executor's run ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRetryRule_AndTheLastRun_AreOneNumber()
    {
        Assert.Equal(RollbackExecutorService.RetryPauses.Length + 1, RollbackExecutorService.MaxAttempts);
        Assert.Equal(5, RollbackExecutorService.MaxAttempts);
    }

    [Fact]
    public async Task ADeployedRollback_IsTold_AndNothingGoesToTheTrain()
    {
        var octo = new Octo();
        var messages = await Service(octo).Messages(Command, 1, CancellationToken.None);
        var done = Assert.IsType<RollbackCommandFinished>(Assert.Single(messages));
        Assert.True(done.Ok);
        Assert.Equal("ServerTasks-77", done.Execution);
        Assert.Equal(1, octo.Calls.Count(c => c.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ARefusalForGood_TellsTheTrainThroughItsInput_AtOnce()
    {
        var octo = new Octo { CreateStatus = 403 };
        var messages = await Service(octo).Messages(Command, 1, CancellationToken.None);
        Assert.False(Assert.IsType<RollbackCommandFinished>(messages[0]).Ok);
        Assert.Equal(FailureKind.Permanent, Assert.IsType<IntegrationFailed>(messages[1]).Kind);
        var input = Assert.IsType<TrainRollbackFailed>(messages[2]);
        Assert.Equal("orders/tdd", input.Key);
        Assert.Equal("inc-1", input.IncidentId);
        Assert.Contains("missing permission DeploymentCreate", input.Error);
        Assert.DoesNotContain(messages, m => m is TrainRollback);
    }

    [Fact]
    public async Task ATransientFailure_ThrowsBeforeTheLastRun_AndSettlesAsARefusalOnIt()
    {
        var octo = new Octo { CreateStatus = 500 };
        var s = Service(octo);
        for (var attempt = 1; attempt < RollbackExecutorService.MaxAttempts; attempt++)
            await Assert.ThrowsAsync<RollbackRetryException>(() => s.Messages(Command, attempt, CancellationToken.None));
        var last = await s.Messages(Command, RollbackExecutorService.MaxAttempts, CancellationToken.None);
        Assert.StartsWith(RuntimeOutcome.ExhaustedPrefix, Assert.IsType<TrainRollbackFailed>(last[2]).Error);
    }

    [Fact]
    public async Task AnyEnvironmentButTdd_IsRefusedWithoutACallToOctopus()
    {
        var octo = new Octo();
        var messages = await Service(octo).Messages(Command with { Environment = "prod" }, 1, CancellationToken.None);
        Assert.Contains("tdd only", Assert.IsType<TrainRollbackFailed>(messages[2]).Error);
        Assert.Empty(octo.Calls);
    }

    [Fact]
    public async Task AFailureOfTheLedger_IsTriedAgain_WithoutItsMessage()
    {
        var s = Service(new Octo(), new ThrowingLedger());
        var e = await Assert.ThrowsAsync<RollbackRetryException>(() => s.Messages(Command, 1, CancellationToken.None));
        Assert.DoesNotContain("connection detail", e.Message);   // (the type only: a database error's message can carry the connection string)
    }

    sealed class ThrowingLedger : IIdempotencyLedger
    {
        public CommandResult? TryGet(string key) => throw new InvalidOperationException("connection detail that must not travel");
        public void Record(string key, CommandResult result) { }
        public ClaimOutcome Claim(string key) => throw new InvalidOperationException("connection detail that must not travel");
        public void ReleaseClaim(string key) { }
        public void ExpireClaim(string key) { }
    }

    // ---- the read of the files two commits differ by --------------------------------------------------------------------------------------------------------

    sealed class Compare : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url)> Seen = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.PathAndQuery));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    static HttpMigrationProbe Probe(Compare c) => new(new HttpClient(c) { BaseAddress = new Uri("https://api.example/") });

    static string Files(int n, string? first = null) => new JsonObject { ["status"] = "ahead", ["files"] = new JsonArray(Enumerable.Range(0, n).Select(i => (JsonNode?)new JsonObject { ["filename"] = i == 0 && first is not null ? first : $"src/f{i}.cs" }).ToArray()) }.ToJsonString();

    [Fact]
    public async Task TheCompare_IsAGet_OfTheTwoCommits_AndListsTheFiles()
    {
        var c = new Compare { Body = Files(3, "src/Database/scripts/Update/0042_x.sql") };
        var files = await Probe(c).ChangedBetween(Home, new string('a', 40), new string('b', 40), CancellationToken.None);
        var seen = Assert.Single(c.Seen);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.Equal($"/repos/acme/work/compare/{new string('a', 40)}...{new string('b', 40)}", seen.Url);
        Assert.Equal("ahead", files!.Status);
        Assert.Contains("src/Database/scripts/Update/0042_x.sql", files.Paths);
        Assert.True(files.Complete);
    }

    [Fact]
    public async Task AListAsLongAsGitHubsCap_IsNotComplete_AndARenamedFileCountsUnderBothNames()
    {
        var c = new Compare { Body = Files(HttpMigrationProbe.FileCap) };
        Assert.False((await Probe(c).ChangedBetween(Home, "a", "b", CancellationToken.None))!.Complete);
        c.Body = new JsonObject { ["status"] = "ahead", ["files"] = new JsonArray(new JsonObject { ["filename"] = "docs/x.sql", ["previous_filename"] = "src/Database/scripts/Update/0001_x.sql" }) }.ToJsonString();
        var files = await Probe(c).ChangedBetween(Home, "a", "b", CancellationToken.None);
        Assert.True(RollbackRules.Judge(files, RollbackRules.DefaultMigrationPaths, "1", "2").Safe is false);
        c.Body = """{"status":"ahead"}""";   // no list at all
        Assert.False((await Probe(c).ChangedBetween(Home, "a", "b", CancellationToken.None))!.Complete);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ACommitGitHubCannotCompare_IsNull_AndAFaultOfTheReadThrows(HttpStatusCode status)
    {
        var c = new Compare { Status = status };
        Assert.Null(await Probe(c).ChangedBetween(Home, "a", "b", CancellationToken.None));
        c.Status = HttpStatusCode.BadGateway;
        await Assert.ThrowsAsync<HttpRequestException>(() => Probe(c).ChangedBetween(Home, "a", "b", CancellationToken.None));
    }

    // ---- the settings ----------------------------------------------------------------------------------------------------------------------------------

    static InstanceConfig Config(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), $"rbp-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        return InstanceConfig.Load(p, n => n == "FACTORY_ENVIRONMENT" ? "tdd" : null);
    }

    [Fact]
    public void TheMigrationPaths_AreTheConfigurationsOrDbUps()
    {
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        static string Json(string rollback) => "{\"owner\":\"acme\",\"repo\":\"work\",\"octopus\":{\"project\":\"acme-ui\"},\"delivery\":{" + rollback + "\"autoPromote\":{\"enabled\":false}}}";
        Assert.All(new CatalogDeliveryPolicies(() => snapshot, DeliveryShadowSettings.For(Config(Json("")), () => snapshot)).All(), p => Assert.Equal(RollbackRules.DefaultMigrationPaths, p.Migrations));
        Assert.All(new CatalogDeliveryPolicies(() => snapshot, DeliveryShadowSettings.For(Config(Json("""
            "rollback":{"migrationPaths":["db/changes/","sql/"]},
            """)), () => snapshot)).All(), p => Assert.Equal(["db/changes/", "sql/"], p.Migrations));
    }

    [Fact]
    public void TheShippedDefault_NamesDbUpsUpdateFolder()
    {
        var root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) { root = d.FullName; break; }
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "instance.json")))!;
        Assert.Equal(RollbackRules.DefaultMigrationPaths, json["delivery"]!["rollback"]!["migrationPaths"]!.AsArray().Select(x => x!.ToString()).ToArray());
    }

    // ---- the host --------------------------------------------------------------------------------------------------------------------------------------

    static async Task<IHost> Host(RollbackExecutorService? service, IMigrationProbe? probe, List<TrainRollbackFailed> failures)
        => await new HostBuilder().ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Critical))
            .UseWolverine(w =>
            {
                w.ServiceName = "fworker-rollback-tests";
                w.Discovery.DisableConventionalDiscovery();
                w.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                RollbackHostSinks.Failures = failures;
                w.Discovery.IncludeType(typeof(RollbackHostSinks));
                RollbackWiring.Register(w.Services, w, service, probe, durable: false);
            }).StartAsync();

    static async Task<bool> Until(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(100);
        return cond();
    }

    [Fact]
    public async Task TheHost_RunsTheExecutorAsGeneratedCode_AndARefusalReachesTheTrainsInput()
    {
        var failures = new List<TrainRollbackFailed>();
        var octo = new Octo { CreateStatus = 403 };
        using var host = await Host(Service(octo), null, failures);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(Command);
        Assert.True(await Until(() => { lock (failures) return failures.Count > 0; }));
        Assert.Equal(("orders/tdd", "inc-1"), (failures[0].Key, failures[0].IncidentId));
        Assert.Equal(1, octo.Calls.Count(c => c.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task TheHost_DeploysOnce_WhenTheCommandIsPublishedTwice()
    {
        var failures = new List<TrainRollbackFailed>();
        var octo = new Octo();
        using var host = await Host(Service(octo), null, failures);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(Command); await bus.PublishAsync(Command);
        Assert.True(await Until(() => { lock (octo.Calls) return octo.Calls.Count(c => c.Method == HttpMethod.Get && c.Path.StartsWith("api/Spaces-1/projects/orders-ui")) >= 1; }));
        await Task.Delay(1500);
        Assert.Equal(1, octo.Calls.Count(c => c.Method == HttpMethod.Post));   // the second run is answered from the ledger
        Assert.Empty(failures);
    }

    [Fact]
    public async Task WithoutAnExecutor_TheHostRegistersOnlyTheProbe_AndTheLogHandlers()
    {
        var probe = new HttpMigrationProbe(new HttpClient(new Compare()) { BaseAddress = new Uri("https://api.example/") });
        using var host = await Host(null, probe, []);
        Assert.Same(probe, host.Services.GetRequiredService<IMigrationProbe>());
        Assert.Null(host.Services.GetService<RollbackExecutorService>());
    }
}

/// <summary>What the test host's handlers of the train's input and of the saga's message do: keep the first, drop the second (the gateway's runtime command wiring handles IntegrationFailed; this host has only the rollback's). Public static: Wolverine's generated code refers to it.</summary>
public static class RollbackHostSinks
{
    public static List<TrainRollbackFailed> Failures = [];
    public static void Handle(TrainRollbackFailed m) { lock (Failures) Failures.Add(m); }
    public static void Handle(IntegrationFailed m) { }
}
