using System.Globalization;
using System.Text;
using static HttpResultsX;

// A messenger's answer as the HTTP answer: its status and its JSON body (task C7c; the wait, messaging and supervisor modules write them).
static class MessagingResults
{
    public static IResult AsResult(MsgResult r) => Results.Text(r.Body.ToJsonString(), "application/json", Encoding.UTF8, r.Status);

    // Who has an inbox, and so may read it, acknowledge it and ask for the state of what it sent: a worker, the item token of an owner or an inspector,
    // the supervisor. A name outside both lists has none; a QC item token never does (4.1).
    public static MsgResult? InboxRefusal(Principal who, MessagingSettings messaging, Plant plant)
    {
        if (who.Kind == "item" && who.Role is not ("owner" or "inspector")) return Messenger.Plain("forbidden", $"an item token of role '{who.Role}' has no inbox");
        if (who.Kind is not ("worker" or "item" or "manager")) return Messenger.Plain("forbidden", "an inbox belongs to a session credential: a worker, an item token or the supervisor");
        if (who.Kind != "manager" && !(messaging.WorkerAllowed(who.Name) || (messaging.ManagerEnabled && messaging.AskerAllowed(who.Name)))) return Messenger.Plain("not-allowed", "this name is not in messaging.allowedWorkers");
        // only the supervisor that holds the slot has an inbox (4.1, 4.6): another supervisor name's credential reads, acknowledges and stamps nothing
        if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Messenger.Plain("not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first");
        return null;
    }
}

// ---- messages (task C7c; worker-messaging.md 3.1 to 3.7): send, state, inbox, acknowledgement, and the operator's traffic log. They are not /verbs:
// a worker credential may not call verbs, and a message never travels the path that decides items. The sender is the credential, never the body
// (4.2). Every route's first guard is MessagingOn; the operator-only ones then Admin with the reason they always gave.
sealed class MessagingEndpoints(MessagingSettings messaging, Plant plant, Messenger messenger) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        // GET /peers (3.2): the directory. A worker or the item token of an owner or inspector session sees the allow-listed sessions; the operator sees
        // all; the supervisor is refused (the digest replaces it), as is a QC item token.
        routes.MapGet("/api/v1/peers", (HttpContext c) => Peers(c))
            .MessagingOn();
        routes.MapPost("/api/v1/messages", async (HttpContext c) => { return await Send(c); })
            .MessagingOn();
        // GET /messages: the traffic log and the ring of refusals and flags, for the operator only (never a worker, never the supervisor)
        routes.MapGet("/api/v1/messages", (HttpContext c) => TrafficLog(c))
            .MessagingOn()
            .Admin(CallerView.Admitted, "the traffic log is for the operator only");
        // POST /messages/read: the operator marks notes addressed to it read (the operator has no inbox to acknowledge; this is its counterpart)
        routes.MapPost("/api/v1/messages/read", async (HttpContext c) => { return await MarkRead(c); })
            .MessagingOn()
            .Admin(CallerView.Admitted, "only the operator marks the notes addressed to the operator read");
        routes.MapGet("/api/v1/messages/{id}", async (string id, HttpContext c) => { return await Status(id, c); })
            .MessagingOn();
        routes.MapPost("/api/v1/messages/ack", async (HttpContext c) => { return await Ack(c); })
            .MessagingOn();
        routes.MapGet("/api/v1/inbox", async (HttpContext c) => { return await Inbox(c); })
            .MessagingOn();
    }

    IResult Peers(HttpContext ctx)
    {
        var who = ctx.Caller().Admitted;
        if (who.Kind == "manager") return MessagingResults.AsResult(Messenger.Plain("forbidden", "the supervisor does not read the directory; the digest replaces it"));
        if (who.Kind == "item" && who.Role is not ("owner" or "inspector")) return MessagingResults.AsResult(Messenger.Plain("forbidden", $"an item token of role '{who.Role}' cannot read the directory"));
        if (who.Kind is not ("admin" or "worker" or "item")) return MessagingResults.AsResult(Messenger.Plain("forbidden", "the directory is read with a session credential or by the operator"));
        if (who.Kind != "admin" && !messaging.WorkerAllowed(who.Name)) return MessagingResults.AsResult(Messenger.Plain("not-allowed", "this name is not in messaging.allowedWorkers"));
        var d = plant.Directory(who.Kind == "admin" ? null : who.Name, who.Kind == "item" && who.Role == "inspector", ctx.Request.Query["all"].ToString() == "1");
        return Results.Text(d.ToJsonString(), "application/json");
    }

    async Task<IResult> Send(HttpContext ctx)
    {
        var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
        return MessagingResults.AsResult(await messenger.SendAsync(ctx.Caller().Admitted, tooLarge ? "" : text, tooLarge));
    }

    IResult TrafficLog(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var worker = q["worker"].ToString();
        int? item = int.TryParse(q["item"], out var it) ? it : null;
        var limit = int.TryParse(q["limit"], out var lm) ? Math.Clamp(lm, 1, 1000) : 200;
        var to = q["to"].ToString();
        if (to != "" && !Auth.ValidName(to)) return Fail(400, "usage", "to is a session name or operator", 2);
        return Results.Text(messenger.TrafficLog(worker == "" ? null : worker, item, limit, q["ring"].ToString() == "refused", to == "" ? null : to, q["unread"].ToString() == "1").ToJsonString(), "application/json");
    }

    async Task<IResult> MarkRead(HttpContext ctx)
    {
        var who = ctx.Caller().Admitted;
        var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
        return MessagingResults.AsResult(tooLarge ? messenger.TooLarge(who, "read") : messenger.MarkRead(text));
    }

    async Task<IResult> Status(string id, HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        if (who.Kind != "admin" && MessagingResults.InboxRefusal(who, messaging, plant) is { } no) return MessagingResults.AsResult(no);
        var wait = double.TryParse(ctx.Request.Query["wait"], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w) ? w : 0;
        return MessagingResults.AsResult(await messenger.StatusAsync(who, id, wait, ctx.RequestAborted));
    }

    async Task<IResult> Ack(HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        if (who.Kind == "admin") return MessagingResults.AsResult(Messenger.Plain("forbidden", "the operator has no inbox to acknowledge"));
        if (MessagingResults.InboxRefusal(who, messaging, plant) is { } no) return MessagingResults.AsResult(no);
        var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
        return MessagingResults.AsResult(tooLarge ? messenger.TooLarge(who, "ack") : messenger.Ack(who, text));
    }

    async Task<IResult> Inbox(HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        if (who.Kind == "admin") return MessagingResults.AsResult(Messenger.Plain("forbidden", "the operator has no inbox; the traffic log is GET /api/v1/messages"));
        if (MessagingResults.InboxRefusal(who, messaging, plant) is { } no) return MessagingResults.AsResult(no);
        var q = ctx.Request.Query;
        long after = 0; if (q["after"].ToString() is { Length: > 0 } aq && (!long.TryParse(aq, NumberStyles.None, CultureInfo.InvariantCulture, out after))) return Fail(400, "usage", "after is a sequence number", 2);
        var timeout = double.TryParse(q["timeout"], NumberStyles.Float, CultureInfo.InvariantCulture, out var tq) && double.IsFinite(tq) ? tq : 0;
        var limit = int.TryParse(q["limit"], out var lq) ? lq : 50;
        var include = q["include"].ToString();
        if (include != "" && include != "delivered") return Fail(400, "usage", "include is delivered", 2);
        var idq = q["id"].ToString();
        var epoch = q["epoch"].ToString();
        if (epoch != "") plant.NoteStaleEpoch(who.Name, epoch);
        return MessagingResults.AsResult(await messenger.InboxAsync(who.Name, after, epoch == "" ? null : epoch, timeout, limit, include == "delivered", idq == "" ? null : idq, ctx.RequestAborted));
    }
}
