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
        if (who.Kind == "keeper") return plant.ActiveKeeper() == who.Name ? Results.Json(new { ok = true, presence = "online", epoch = plant.Epoch, exit = 0 }) : Fail(403, "not-keeper", "this credential is not the active backlog keeper: register with subscribe --own --keeper first", 30);
        if (who.Kind == "operator") return ctx.Caller().OperatorActive ? Results.Json(new { ok = true, presence = "online", epoch = plant.Epoch, exit = 0 }) : Fail(403, "not-operator", "this credential is not the active operator session: register with subscribe --own --operator first", 30);
        if (who.Kind is not ("worker" or "item" or "manager")) return Fail(403, "forbidden", "presence is sent with a session's own credential", 30);
        if (who.Kind == "item" && who.Role is not ("owner" or "inspector")) return Fail(403, "role-cannot-send", "only the item tokens of an owner or an inspector session have presence (a QC token is pull request code's credential)", 30);
        if (who.Kind != "manager" && !messaging.WorkerAllowed(who.Name)) return Fail(403, "not-allowed", "this name is not in messaging.allowedWorkers", 30);
        if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
        var (text, tooLarge) = await ReadBody(ctx, 16 * 1024);
        if (tooLarge) return Fail(413, "too-large", "the request body is over 16 KB", 2);
        JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : Messenger.ParseObject(text); } catch { return Fail(400, "usage", "the body is a JSON object: {} or { \"summary\": \"...\" }", 2); }
        foreach (var kv in body)
        {
            if (kv.Key is "from" or "sender" or "holder" or "fromKind" or "role") return Fail(403, "forged-sender", "the sender comes from the credential; a body never names it", 30);
            if (kv.Key is not ("summary" or "progress")) return Fail(400, "usage", $"unknown field '{kv.Key}'", 2);
        }
        // local progress of owned items (gateway/Lease.cs rule 3): [{ "item": N, "ageSeconds": S, "source": "commit|files|process|marker|work" }], 20 at most;
        // the age (by the client's clock) and not a time, so a client whose clock is off still reports what is recent
        var progress = new List<(int Item, DateTime At, string Source)>();
        if (body["progress"] is JsonNode pn)
        {
            if (who.Kind == "manager") return Fail(400, "usage", "the supervisor owns no item: progress is sent by an owner's client", 2);
            if (pn is not JsonArray pa || pa.Count > 20) return Fail(400, "usage", "progress is an array of at most 20 { item, ageSeconds, source }", 2);
            foreach (var e in pa)
            {
                if (e is not JsonObject eo || !int.TryParse(eo["item"]?.ToString(), out var pi) || pi <= 0
                    || !double.TryParse(eo["ageSeconds"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var age) || !double.IsFinite(age) || age < 0 || age > 86400
                    || eo["source"]?.ToString() is not ("commit" or "files" or "process" or "marker" or "work"))
                    return Fail(400, "usage", "each progress entry is { \"item\": N, \"ageSeconds\": 0 to 86400, \"source\": \"commit|files|process|marker|work\" }", 2);
                progress.Add((pi, DateTime.UtcNow.AddSeconds(-age), eo["source"]!.ToString()));
            }
        }
        string? summary = null;
        if (body["summary"] is JsonNode sn) { if (sn.GetValueKind() != System.Text.Json.JsonValueKind.String) return Fail(400, "usage", "summary is text", 2); summary = sn.GetValue<string>(); if (summary.Length > 140) return Fail(413, "too-large", "summary is 140 characters at most", 2); }
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
