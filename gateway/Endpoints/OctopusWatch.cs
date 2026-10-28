using System.Text.Json;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Octopus;

// W4.2: the Octopus watcher in the gateway. Only where the engine acts (Engine.V2Acts) and OCTOPUS_API_KEY is set; v1 and shadow keep promoting with the rule scripts and
// this does nothing at all. It reads Octopus (GET only; never creates, changes or promotes anything) every octopus.watchSeconds (default 60) while this process may act
// (the leader lease), and at once when the Octopus subscription's webhook arrives. Its events go to the ingress (S3), where W4.1 routes them.
sealed class OctopusWatchService : BackgroundService
{
    readonly OctopusWatcher? watcher;
    readonly TimeSpan interval;
    readonly Func<bool> mayAct;
    readonly SemaphoreSlim wake = new(0, 1);

    public OctopusWatchService(OctopusWatcher? watcher, TimeSpan interval, Func<bool> mayAct) { this.watcher = watcher; this.interval = interval; this.mayAct = mayAct; }

    public bool Enabled => watcher != null;

    /// <summary>The settings from octopus.url, .space, .project, .watchSeconds and OCTOPUS_API_KEY; a watcher that is off (no key, URL, space or project) is built as off.</summary>
    public static OctopusWatchService From(InstanceConfig cfg, IEventIngress ingress, Func<string, string?> env, Func<bool> mayAct, HttpClient? http = null)
    {
        var o = cfg.Root?["octopus"];
        string S(string n) => o?[n]?.ToString() ?? "";
        var key = env("OCTOPUS_API_KEY") ?? "";
        var seconds = int.TryParse(S("watchSeconds"), out var w) && w >= 5 ? w : 60;
        if (key == "" || S("url") == "" || S("space") == "" || S("project") == "") return new OctopusWatchService(null, TimeSpan.FromSeconds(seconds), mayAct);
        var reader = new HttpOctopusReader(http ?? new HttpClient(), S("url"), key);
        return new OctopusWatchService(new OctopusWatcher(reader, ingress.Ingest, new OctopusWatchSettings(S("space"), S("project"))), TimeSpan.FromSeconds(seconds), mayAct);
    }

    /// <summary>Asks for a poll now (the webhook); several asks before the poll start are one.</summary>
    public void PollNow() { try { wake.Release(); } catch (SemaphoreFullException) { } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (watcher == null) return;
        while (!ct.IsCancellationRequested)
        {
            try { if (mayAct()) await watcher.Poll(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"octopusWatch\":\"error\",\"type\":{JsonSerializer.Serialize(e.GetType().Name)},\"message\":{JsonSerializer.Serialize(e.Message)}}}"); }
            catch (Exception) { return; }
            try { await wake.WaitAsync(interval, ct); } catch (OperationCanceledException) { return; }
        }
    }
}

// The Octopus subscription's webhook. The shared secret in X-Octopus-Webhook-Secret (FACTORY_WEBHOOK_SECRET_OCTOPUS, WebhookSecrets) is its only credential; a source without a
// secret refuses every delivery. The payload of a subscription names only ids, so the body is not read for facts: an accepted delivery asks the watcher to poll now, and the
// poll publishes the events. Where the watcher is not active the route answers 404, as if it were not there.
sealed class OctopusWebhookEndpoints(IServiceProvider services, WebhookSecrets? secrets = null) : IEndpointModule
{
    readonly WebhookSecrets secrets = secrets ?? new WebhookSecrets(Environment.GetEnvironmentVariable);

    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/webhooks/octopus", async (HttpContext c) => { return await Receive(c); });   // (braces: an expression body would bind to RequestDelegate and drop the result)

    async Task<IResult> Receive(HttpContext ctx)
    {
        var watch = services.GetService<OctopusWatchService>();
        if (watch is not { Enabled: true }) return Results.NotFound();
        using var ms = new MemoryStream(); await ctx.Request.Body.CopyToAsync(ms);
        if (!secrets.Verify(WebhookSecrets.Octopus, ctx.Request.Headers["X-Octopus-Webhook-Secret"].ToString(), ms.ToArray()))
        { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"webhook\":\"octopus-bad-secret\"}}"); return Results.StatusCode(401); }
        watch.PollNow();
        return Results.Accepted();
    }
}
