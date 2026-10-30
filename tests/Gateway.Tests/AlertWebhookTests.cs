using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Correlation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>W8.2: the Azure Monitor webhook route (AlertsEndpoints.cs): the per-source secret in the URI or a header, the subject, the bounded body, the answers Azure's retry policy needs, and the event that reaches the inbox.</summary>
public sealed class AlertWebhookTests
{
    const string Secret = "azure-secret-for-tests";
    static readonly RepoId Home = new("acme", "orders");

    static WebhookSecrets Secrets(string? s = Secret) => new(n => n == "FACTORY_WEBHOOK_SECRET_AZURE_MONITOR" ? s : null);

    sealed class Catalog(params string[] names) : ISystemCatalog
    {
        public CatalogSnapshot Current { get; } = new(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, names.Select(n => new DeployableEntry(n, Home, "/health", null, null, null, false)).ToList())], []);
        public string? LastError => null;
        public Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default) => Task.FromResult(new CatalogRefreshResult(true, null, Current));
    }

    static byte[] Alert(string condition = "Fired", string id = "alert-1", string schema = "azureMonitorCommonAlertSchema") => Encoding.UTF8.GetBytes(
        new JsonObject { ["schemaId"] = schema, ["data"] = new JsonObject { ["essentials"] = new JsonObject { ["alertId"] = id, ["alertRule"] = "tdd-ui-5xx", ["severity"] = "Sev1", ["monitorCondition"] = condition } } }.ToJsonString());

    sealed class Run
    {
        public int Status; public JsonNode? Json; public IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>();
        public List<IngressInboxMessage> Stored = [];
    }

    static async Task<Run> Post(string query, byte[]? body = null, Func<IngressInboxMessage, Task<bool>>? inbox = null, bool hasInbox = true, WebhookSecrets? secrets = null, string? header = null,
        string environment = "tdd", ISystemCatalog? catalog = null, AlertsEndpoints? module = null, byte[]? raw = null)
    {
        var run = new Run();
        var sc = new ServiceCollection().AddLogging();
        if (hasInbox) sc.AddSingleton(new IngressInboxPort(m => { run.Stored.Add(m); return (inbox ?? (_ => Task.FromResult(true)))(m); }));
        if (catalog != null) sc.AddSingleton(catalog);
        var sp = sc.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Method = "POST"; ctx.Request.QueryString = new QueryString(query);
        ctx.Request.Body = new MemoryStream(raw ?? body ?? Alert()); ctx.Response.Body = new MemoryStream();
        if (header != null) ctx.Request.Headers[AlertsEndpoints.SecretHeader] = header;
        var m = module ?? new AlertsEndpoints(sp, environment, secrets ?? Secrets());
        var recv = typeof(AlertsEndpoints).GetMethod("Receive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = await (Task<IResult>)recv.Invoke(m, [ctx])!;
        await result.ExecuteAsync(ctx);
        run.Status = ctx.Response.StatusCode;
        ctx.Response.Body.Position = 0;
        var text = new StreamReader(ctx.Response.Body).ReadToEnd();
        run.Json = text.Length > 0 ? JsonNode.Parse(text) : null;
        run.Headers = ctx.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
        return run;
    }

    static string Q(string secret = Secret, string deployable = "ui", string environment = "tdd") => $"?secret={Uri.EscapeDataString(secret)}&deployable={deployable}&environment={environment}";

    [Fact]
    public async Task AnAlertWithTheSecretInTheUriIsStoredAsAnIncidentTrigger()
    {
        var r = await Post(Q());
        Assert.Equal(202, r.Status);
        Assert.Equal("incident.alert", (string?)r.Json!["event"]);
        var m = Assert.Single(r.Stored);
        Assert.Equal("azure-monitor", m.Source);
        var ev = JsonNode.Parse(m.EventJson)!;
        Assert.Equal(("incident.alert", "ui", "tdd", "alert-1|Fired"), ((string?)ev["event"], (string?)ev["deployable"], (string?)ev["environment"], (string?)ev["ref"]));
    }

    [Fact] public async Task TheSecretMayComeInTheHeaderOfARelay() => Assert.Equal(202, (await Post("?deployable=ui&environment=tdd", header: Secret)).Status);
    [Fact] public async Task TheSubjectIsLowerCased() { var r = await Post(Q(deployable: "UI", environment: "TDD")); Assert.Equal(202, r.Status); Assert.Equal("ui", (string?)JsonNode.Parse(r.Stored[0].EventJson)!["deployable"]); }

    [Fact] public async Task AWrongSecretIsRefusedAndNothingIsStored() { var r = await Post(Q("nope")); Assert.Equal(401, r.Status); Assert.Empty(r.Stored); }
    [Fact] public async Task AMissingSecretIsRefused() => Assert.Equal(401, (await Post("?deployable=ui&environment=tdd")).Status);
    [Fact] public async Task TheOtherSourcesSecretsDoNotOpenThisRoute() => Assert.Equal(401, (await Post(Q("gh-secret"), secrets: new WebhookSecrets(n => n switch { "FACTORY_WEBHOOK_SECRET" => "gh-secret", "FACTORY_WEBHOOK_SECRET_OCTOPUS" => "oct", _ => null }))).Status);
    [Fact] public async Task ASourceWithoutASecretRefusesEverything() { Assert.Equal(401, (await Post(Q(""), secrets: Secrets(""))).Status); Assert.Equal(401, (await Post(Q(Secret), secrets: Secrets(null))).Status); }
    [Fact] public async Task WithoutTheInboxTheRouteIsNotThere() => Assert.Equal(404, (await Post(Q(), hasInbox: false)).Status);
    [Fact] public async Task TheSecretIsCheckedBeforeTheBodyIsRead() => Assert.Equal(401, (await Post(Q("nope"), raw: new byte[AlertsEndpoints.MaxBodyBytes * 2])).Status);

    [Fact] public async Task ADeliveryWithoutASubjectIsNotUsable() { var r = await Post($"?secret={Secret}"); Assert.Equal(400, r.Status); Assert.Equal("subject-missing", (string?)r.Json!["reason"]); }
    [Fact] public async Task ASubjectThatIsNotANameIsNotUsable() => Assert.Equal(400, (await Post($"?secret={Secret}&deployable=a%20b&environment=tdd")).Status);

    [Fact]
    public async Task AnAlertOfAnotherEnvironmentIsAcknowledgedAndIgnored_TddOnly()
    {
        var r = await Post(Q(environment: "prod"));
        Assert.Equal(202, r.Status); Assert.Equal("environment-not-served", (string?)r.Json!["reason"]); Assert.Empty(r.Stored);
    }

    [Fact]
    public async Task TheFactorysOwnAlertIsNeverAnIncidentOfTheFactory_ItNotifiesTheOwnerDirectly()
    {
        var r = await Post(Q(deployable: "fworker"));
        Assert.Equal(202, r.Status); Assert.Equal("factory-alert-is-out-of-band", (string?)r.Json!["reason"]); Assert.Empty(r.Stored);
    }

    [Fact] public async Task ADeployableTheCatalogDoesNotListIsAMistakeOfTheUri() { var r = await Post(Q(deployable: "nope"), catalog: new Catalog("ui", "api")); Assert.Equal(400, r.Status); Assert.Equal("unknown-deployable", (string?)r.Json!["reason"]); Assert.Empty(r.Stored); }
    [Fact] public async Task AListedDeployableIsAccepted() => Assert.Equal(202, (await Post(Q(), catalog: new Catalog("ui", "api"))).Status);
    [Fact] public async Task ACatalogWithoutDeployablesCannotSayAndDoesNotRefuse() => Assert.Equal(202, (await Post(Q(), catalog: new Catalog())).Status);

    [Fact] public async Task ABodyThatIsNotAnAlertIsNotUsable() { var r = await Post(Q(), body: Alert(schema: "other")); Assert.Equal(400, r.Status); Assert.Equal("unsupported-schema", (string?)r.Json!["reason"]); Assert.Empty(r.Stored); }
    [Fact] public async Task ATooLargeBodyIsRefused() => Assert.Equal(413, (await Post(Q(), raw: new byte[AlertsEndpoints.MaxBodyBytes + 1])).Status);

    [Fact]
    public async Task AnInboxThatDoesNotTakeTheAlertAnswers503SoAzureTriesAgain()
    {
        var r = await Post(Q(), inbox: _ => Task.FromResult(false));
        Assert.Equal(503, r.Status); Assert.Equal("30", r.Headers["Retry-After"]);
    }

    [Fact] public async Task AnInboxThatThrowsAnswers503() => Assert.Equal(503, (await Post(Q(), inbox: _ => throw new InvalidOperationException("host down"))).Status);

    [Fact]
    public async Task TheCountsSayWhatTheRouteDid()
    {
        var sp = new ServiceCollection().AddSingleton(new IngressInboxPort(_ => Task.FromResult(true))).BuildServiceProvider();
        var m = new AlertsEndpoints(sp, "tdd", Secrets());
        await Post(Q(), module: m); await Post(Q("x"), module: m); await Post(Q(environment: "uat"), module: m); await Post("?secret=" + Secret, module: m);
        Assert.Equal((1, 1, 1, 1, 0), (m.Counts.StoredCount, m.Counts.RefusedCount, m.Counts.IgnoredCount, m.Counts.UnusableCount, m.Counts.NotStoredCount));
    }

    [Fact]
    public void TheHandlerFilesAnIncidentTriggerUnderItsIncident_NotUnderTheBoard()
    {
        var p = new IngressInboxProcessor(new InMemoryIngressDedup(), new InMemoryCorrelationMap(), Home);
        var json = Fworker.Integrations.Alerts.AzureMonitorAlerts.Parse(Alert(), "ui", "tdd", DateTimeOffset.UtcNow).Event!.ToJsonString();
        var rec = IngressInboxHandler.Handle(new IngressInboxMessage("azure-monitor", json), p, new Wolverine.Envelope());
        Assert.Equal(("incident:ui/tdd", "ingress.inbox.azure-monitor.incident.alert"), (rec!.ItemKey, rec.Type));
    }

    [Fact] public void TheSecretHasItsOwnVariable() { Assert.Equal("FACTORY_WEBHOOK_SECRET_AZURE_MONITOR", WebhookSecrets.EnvVar(WebhookSecrets.AzureMonitor)); Assert.False(Secrets().Verify(WebhookSecrets.AzureMonitor, Secret + "x", [])); Assert.True(Secrets().Verify(WebhookSecrets.AzureMonitor, Secret, [])); }
}
