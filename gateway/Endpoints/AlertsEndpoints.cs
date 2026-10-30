using System.Text.Json;
using Fworker.Contracts.Catalog;
using Fworker.Integrations.Alerts;
using Fworker.Integrations.Correlation;

// W8.2: the alert ingress. Azure Monitor's action group calls POST /webhooks/azure-monitor?deployable=<name>&environment=<name>&secret=<secret> (common alert schema); an accepted alert becomes an incident trigger
// event of the ingress inbox (AzureMonitorAlerts, W4.1's durable queue "ingress-inbox"), where the IncidentSaga (W8.1) takes it from. Where the engine does not act (no inbox) the route answers 404 as if it
// were not there, so v1, the shadow run and FACTORY_V2_OFF are what they were.
//
// Credential: the per-source secret of W4.0 (WebhookSecrets, FACTORY_WEBHOOK_SECRET_AZURE_MONITOR). An action group cannot send a header, so the secret is the query parameter "secret" of the webhook URI
// (TLS, never logged); a relay may send it in X-Azure-Monitor-Secret instead. A source without a secret refuses every delivery. The check is made before the body is read, and the body is bounded.
// The answer is made for Azure's retry policy: 202 stored (or a repeat of a stored delivery), 401 refused, 400 a delivery that is not usable (no retry), 503 the inbox did not take it (Azure retries).
// A protected area (CLAUDE.md): authentication of a webhook.
//
// The subject (which deployable, which environment) comes from the URI, one action group per deployable and environment, because the common schema names the resources but not the factory's
// deployable. Only this gateway's environment is served ("tdd only"): an alert for another environment is acknowledged and ignored. The factory's own deployable is never an incident of itself: its failures
// cannot run on its own bus, an Azure Monitor alert on its health notifies the owner directly (docs/deploy/incident-alert-rules.md), so such a delivery is acknowledged and ignored too.
sealed class AlertsEndpoints(IServiceProvider services, string environment, WebhookSecrets? secrets = null) : IEndpointModule
{
    public const string Route = "/webhooks/azure-monitor", SecretHeader = "X-Azure-Monitor-Secret", SecretQuery = "secret";
    public const int MaxBodyBytes = 256 * 1024;
    /// <summary>The deployable of the factory itself (cmdemo1's fworker): its alerts go to the owner directly, not through the factory.</summary>
    public const string FactoryDeployable = "fworker";

    readonly WebhookSecrets secrets = secrets ?? new WebhookSecrets(Environment.GetEnvironmentVariable);
    readonly AlertIngressCounts counts = new();

    public AlertIngressCounts Counts => counts;

    public void Map(IEndpointRouteBuilder routes) => routes.MapPost(Route, async (HttpContext c) => { return await Receive(c); });   // (braces: an expression body would bind to RequestDelegate and drop the result)

    async Task<IResult> Receive(HttpContext ctx)
    {
        var inbox = services.GetService<IngressInboxPort>();
        if (inbox is null) return Results.NotFound();
        var presented = ctx.Request.Headers[SecretHeader].ToString();
        if (presented == "") presented = ctx.Request.Query[SecretQuery].ToString();
        if (!secrets.Verify(WebhookSecrets.AzureMonitor, presented, []))
        { counts.Refused(); Log("bad-secret"); return Results.StatusCode(401); }

        var deployable = AzureMonitorAlerts.Subject(ctx.Request.Query["deployable"].ToString());
        var env = AzureMonitorAlerts.Subject(ctx.Request.Query["environment"].ToString());
        if (deployable is null || env is null) { counts.Unusable(); Log("subject-missing"); return Results.Json(new { accepted = false, reason = "subject-missing", hint = "the webhook URI needs deployable=<name> and environment=<name>" }, statusCode: 400); }

        if (ctx.Request.ContentLength is > MaxBodyBytes) { counts.Unusable(); Log("too-large"); return Results.StatusCode(413); }
        byte[] body;
        using (var ms = new MemoryStream())
        {
            var buffer = new byte[8192]; int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (ms.Length + read > MaxBodyBytes) { counts.Unusable(); Log("too-large"); return Results.StatusCode(413); }
                ms.Write(buffer, 0, read);
            }
            body = ms.ToArray();
        }

        if (!string.Equals(env, environment.Trim(), StringComparison.OrdinalIgnoreCase)) return Ignored(deployable, env, "environment-not-served");
        if (deployable == FactoryDeployable) return Ignored(deployable, env, "factory-alert-is-out-of-band");
        // a deployable the catalog does not list is a mistake in the action group's URI; a catalog with no deployables (the single repository fallback) cannot say
        if (services.GetService<ISystemCatalog>()?.Current is { } catalog && catalog.Deployables.Any() && catalog.FindDeployable(deployable) is null)
        { counts.Unusable(); Log("unknown-deployable", deployable, env); return Results.Json(new { accepted = false, reason = "unknown-deployable", deployable }, statusCode: 400); }

        var parsed = AzureMonitorAlerts.Parse(body, deployable, env, DateTimeOffset.UtcNow);
        if (!parsed.Ok) { counts.Unusable(); Log(parsed.Reason, deployable, env); return Results.Json(new { accepted = false, reason = parsed.Reason }, statusCode: 400); }

        bool stored;
        try { stored = await inbox.Publish(new IngressInboxMessage(AzureMonitorAlerts.InboxSource, parsed.Event!.ToJsonString())); }
        catch (Exception e) when (e is not OperationCanceledException) { stored = false; Log("inbox-error:" + e.GetType().Name, deployable, env); }
        // the host is not up or the database does not answer: Azure Monitor tries again (it retries a 5xx); the log line is what the owner-direct rule on the factory watches
        if (!stored) { counts.NotStored(); Log("not-stored", deployable, env); ctx.Response.Headers.RetryAfter = "30"; return Results.Json(new { accepted = false, reason = "not-stored" }, statusCode: 503); }
        counts.Stored();
        Log("stored", deployable, env, parsed.Event!["event"]?.ToString());
        return Results.Json(new { accepted = true, @event = parsed.Event!["event"]?.ToString(), deployable, environment = env }, statusCode: 202);
    }

    IResult Ignored(string deployable, string env, string reason)
    {
        counts.Ignored();
        Log("ignored:" + reason, deployable, env);
        return Results.Json(new { accepted = false, ignored = true, reason }, statusCode: 202);
    }

    // one JSON line, never the secret, the query or the body
    static void Log(string outcome, string? deployable = null, string? env = null, string? ev = null) =>
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"alertIngress\":{JsonSerializer.Serialize(outcome)},\"deployable\":{JsonSerializer.Serialize(deployable ?? "")},\"environment\":{JsonSerializer.Serialize(env ?? "")},\"event\":{JsonSerializer.Serialize(ev ?? "")}}}");
}

/// <summary>What the alert route did since the process started (a restart starts again); for the tests and a later status view.</summary>
sealed class AlertIngressCounts
{
    long stored, refused, unusable, ignored, notStored;
    public long StoredCount => Interlocked.Read(ref stored);
    public long RefusedCount => Interlocked.Read(ref refused);
    public long UnusableCount => Interlocked.Read(ref unusable);
    public long IgnoredCount => Interlocked.Read(ref ignored);
    public long NotStoredCount => Interlocked.Read(ref notStored);
    public void Stored() => Interlocked.Increment(ref stored);
    public void Refused() => Interlocked.Increment(ref refused);
    public void Unusable() => Interlocked.Increment(ref unusable);
    public void Ignored() => Interlocked.Increment(ref ignored);
    public void NotStored() => Interlocked.Increment(ref notStored);
}
