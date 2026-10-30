using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using static HttpResultsX;

// The deliveries of the last ten minutes, by GitHub's delivery id: a delivery GitHub sends again is accepted and not ingested twice.
sealed class WebhookDeduplicator
{
    readonly ConcurrentDictionary<string, DateTime> seen = new();

    // Forgets the deliveries older than ten minutes, then: true when this delivery was seen before (an empty id is never a repeat).
    public bool Repeated(string delivery)
    {
        foreach (var k in seen.Where(p => p.Value < DateTime.UtcNow.AddMinutes(-10)).Select(p => p.Key).ToList()) seen.TryRemove(k, out _);
        return delivery != "" && !seen.TryAdd(delivery, DateTime.UtcNow);
    }
}

// W4.0: the secret of each webhook source and the check of its signature. A source's secret comes from its own environment variable
// (github: FACTORY_WEBHOOK_SECRET, the one the gateway always had; octopus: FACTORY_WEBHOOK_SECRET_OCTOPUS; azure-monitor, W8.2: FACTORY_WEBHOOK_SECRET_AZURE_MONITOR); a source without a secret (unset or empty)
// refuses every delivery, and an unknown source has none. The secrets are never logged; the comparison is in constant time. GitHub signs the body
// (X-Hub-Signature-256, HMAC-SHA256); an Octopus subscription cannot compute a signature, so it sends the shared secret itself in a header
// (X-Octopus-Webhook-Secret) over TLS; an Azure Monitor action group cannot set a header either, so its secret rides in the webhook URI (query "secret") or, from a relay, in
// X-Azure-Monitor-Secret, and is compared like Octopus's. A protected area (CLAUDE.md).
sealed class WebhookSecrets(Func<string, string?> env)
{
    public const string GitHub = "github", Octopus = "octopus", AzureMonitor = "azure-monitor";
    public static string EnvVar(string source) => source switch { GitHub => "FACTORY_WEBHOOK_SECRET", Octopus => "FACTORY_WEBHOOK_SECRET_OCTOPUS", AzureMonitor => "FACTORY_WEBHOOK_SECRET_AZURE_MONITOR", _ => "" };

    /// <summary>The secret of the source, or null when there is none (unset, empty, unknown source).</summary>
    public string? For(string source) { var v = EnvVar(source) is { Length: > 0 } n ? env(n) : null; return string.IsNullOrEmpty(v) ? null : v; }

    /// <summary>True only when the source has a secret and the delivery proves it: signature is the value of the source's header, body the raw request body.</summary>
    public bool Verify(string source, string? signature, byte[] body)
    {
        var secret = For(source);
        if (secret == null || signature == null) return false;
        switch (source)
        {
            case GitHub:
                var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
                return HttpResultsX.FixedEquals(signature, expected);
            case Octopus or AzureMonitor: return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(signature)), SHA256.HashData(Encoding.UTF8.GetBytes(secret)));   // hashed first: no length leak
            default: return false;
        }
    }
}

// GitHub's webhook (task C7b): the one route of the webhook port (the port a tunnel exposes; the middleware answers 404 for any other path there).
// The signature (X-Hub-Signature-256, HMAC-SHA256 with the webhook secret, compared in constant time) is its only credential: a protected area
// (CLAUDE.md). An accepted event runs the rule verb ingest-event; the events it returns are published, routed to the items' owners and ask for a sweep.
sealed class WebhookEndpoints(GatewayOptions gateway, Runner runner, IEventIngress ingress, Plant plant, QcHub qc, BaseWatch baseWatch, WebhookDeduplicator deliveries) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/webhooks/github", async (HttpContext c) => { return await Receive(c); });

    async Task<IResult> Receive(HttpContext ctx)
    {
        var ownerMode = gateway.OwnerMode;
        using var ms = new MemoryStream(); await ctx.Request.Body.CopyToAsync(ms);
        var raw = ms.ToArray();
        var sig = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
        if (!new WebhookSecrets(n => n == "FACTORY_WEBHOOK_SECRET" ? gateway.WebhookSecret : null).Verify(WebhookSecrets.GitHub, sig, raw)) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"webhook\":\"bad-signature\"}}"); return Results.StatusCode(401); }
        var ev = ctx.Request.Headers["X-GitHub-Event"].ToString();
        var delivery = ctx.Request.Headers["X-GitHub-Delivery"].ToString();
        if (ev == "ping") return Results.Ok("pong");
        if (deliveries.Repeated(delivery)) return Results.Accepted();
        if (ev is not ("issues" or "workflow_run" or "projects_v2_item" or "pull_request" or "pull_request_review")) return Results.Accepted();
        var input = new JsonObject { ["event"] = ev, ["delivery"] = delivery, ["payload"] = JsonNode.Parse(raw) };
        var (code, output) = await runner.RunAsync("ingest-event", input.ToJsonString(), TimeSpan.FromSeconds(60));
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"webhook\":\"{ev}\",\"delivery\":\"{delivery}\",\"exit\":{code}}}");
        if (code == 0 && JsonNode.Parse(output)?["events"] is JsonArray events)
        {
            foreach (var e in events)
            {
                if (e is not JsonObject eo) continue;
                ingress.Ingest(new WebhookEventIngested(eo.ToJsonString()));
                // master moved (a pull request merged into the default branch), or an item's branch got a new head: the back-merge trigger (BaseUpdate.cs)
                if (eo["event"]?.ToString() == "base.merged") baseWatch.OnMerged(eo["pr"] is JsonValue bp && bp.TryGetValue<int>(out var bpn) ? bpn : 0);
                if (eo["event"]?.ToString() is "pr.head_changed" or "pr.opened" && eo["item"] is JsonValue hv && hv.TryGetValue<int>(out var hn)) baseWatch.NoteHeadChange(hn);
                if (ownerMode && eo["item"] is JsonNode itn && itn.GetValueKind() == JsonValueKind.Number)
                {
                    var item = itn.GetValue<int>();
                    plant.NoteWebhook(item, eo["event"]?.ToString() ?? ev, eo["ref"]?.ToString() ?? delivery);
                    plant.Route(item, (JsonObject)JsonNode.Parse(eo.ToJsonString())!);
                    // a review of the PR: ask GitHub what it says now and wake the owner with qc.approved when a listed person approved the current head
                    if (eo["event"]?.ToString() == "pr.reviewed") { var evo = (JsonObject)JsonNode.Parse(eo.ToJsonString())!; _ = Task.Run(async () => { try { await qc.OnReview(evo); } catch (Exception ex) { Console.Error.WriteLine($"qc review: {ex.Message}"); } }); }
                }
            }
            // each event's item gets an incremental pass; an event of no item (master moved, a build of master) asks for a full sweep (issue 92)
            // a pull request event also asks for a full sweep (still at most every sweepMinSeconds): the QC offers and teardowns are made only there
            foreach (var e in events) if (e is JsonObject eo2)
            {
                if (eo2["item"] is JsonValue iv2 && iv2.TryGetValue<int>(out var ii2) && ii2 > 0) ingress.Ingest(new SweepRequested(ii2)); else ingress.Ingest(new SweepRequested());
                if (ownerMode && eo2["event"]?.ToString() is "pr.opened" or "pr.head_changed" or "pr.closed" or "pr.merged") ingress.Ingest(new SweepRequested());
            }
        }
        return Results.Accepted();
    }
}
