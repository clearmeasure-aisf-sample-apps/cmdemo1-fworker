using System.Globalization;
using System.Text.Json.Nodes;
using static HttpResultsX;

// POST /presence (task C7c; worker-messaging.md 3.1, 3.2): the heartbeat of a session's own credential, with an optional activity summary. The stream
// client sends one every 30 s; it is what keeps a session online, because a write into its stream proves nothing about the client. Its first two
// checks, owner mode then messaging.enabled, are the MessagingOn filter's, text for text.
sealed class PresenceEndpoints(MessagingSettings messaging, Plant plant, Messenger messenger) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/presence", async (HttpContext c) => { return await Presence(c); })
            .MessagingOn();

    async Task<IResult> Presence(HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        // the operator session's heartbeat: the request itself stamped it (the middleware), and it has no summary and no directory entry
        var (verdict, caller) = PresencePolicy.CheckCaller(who.Kind, who.Name, who.Role, ctx.Caller().OperatorActive, plant.ActiveKeeper, () => plant.ActiveSupervisor().Name, messaging.WorkerAllowed);
        if (verdict == PresencePolicy.Verdict.Online) return Results.Json(new { ok = true, presence = "online", epoch = plant.Epoch, exit = 0 });
        if (caller != null) return Fail(caller.Status, caller.Code, caller.Reason, caller.Exit);
        var (text, tooLarge) = await ReadBody(ctx, 16 * 1024);
        if (tooLarge) return Fail(413, "too-large", "the request body is over 16 KB", 2);
        JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : Messenger.ParseObject(text); } catch { return Fail(400, "usage", "the body is a JSON object: {} or { \"summary\": \"...\" }", 2); }
        if (PresencePolicy.CheckFields(body) is { } fields) return Fail(fields.Status, fields.Code, fields.Reason, fields.Exit);
        var progress = new List<(int Item, DateTime At, string Source)>();
        if (body["progress"] is JsonNode pn)
        {
            var (parsed, bad) = PresencePolicy.ParseProgress(pn, who.Kind == "manager", DateTime.UtcNow);
            if (bad != null) return Fail(bad.Status, bad.Code, bad.Reason, bad.Exit);
            progress = parsed!;
        }
        string? summary = null;
        if (body["summary"] is JsonNode sn)
        {
            if (PresencePolicy.CheckSummary(sn, out var summaryText) is { } badSummary) return Fail(badSummary.Status, badSummary.Code, badSummary.Reason, badSummary.Exit);
            summary = summaryText;
        }
        // the summary is shown to every peer (3.2), so it passes the same credential check as a message (5.4)
        if (summary != null && messenger.LooksSecret(summary)) return Fail(409, "secret-in-message", "the summary looks like a credential or token; summaries never carry secrets (worker-messaging.md 5.4)", 10);
        var (presence, retry) = plant.Heartbeat(who.Name, summary);
        if (retry > 0) messenger.CountRefusal("rate-limited");
        if (retry > 0) return Results.Json(new { ok = false, code = "rate-limited", reason = "the summary changes at most once per 10 seconds", retryAfterSeconds = retry, exit = 20 }, statusCode: 429);
        // a summary that names owned items ("#N") renews their leases, and local progress counts as the owner's activity (gateway/Lease.cs)
        if (who.Kind is "worker" or "item") plant.NoteHeartbeat(who.Name);
        if (progress.Count > 0) return Results.Json(new { ok = true, presence, epoch = plant.Epoch, progress = plant.NoteProgress(who.Name, progress), exit = 0 });
        return Results.Json(new { ok = true, presence, epoch = plant.Epoch, exit = 0 });
    }
}
