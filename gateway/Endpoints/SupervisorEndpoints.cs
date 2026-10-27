using System.Globalization;
using System.Text.Json.Nodes;
using static HttpResultsX;

// ---- the supervisor (task C7c; worker-messaging.md 3.1, 3.8, 3.9): its questions and the operator's view and answer of them, the digest, the patrol
// and the supervisor's budgets; and the operator's switch of messaging. Every supervisor route's first guard is SupervisorOn; the operator-only ones
// then Admin with the reason they always gave. Rules that came in another order stay in their handler: the digest's step budget comes after its
// own caller rule (a refused call spends no step), and messaging/{action} answers 404 for an unknown action before any guard.
sealed class SupervisorEndpoints(GatewayOptions gateway, MessagingSettings messaging, Plant plant, Messenger messenger) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/questions", (HttpContext c) => Questions(c))
            .SupervisorOn();
        routes.MapPost("/api/v1/questions/{id}/answer", async (string id, HttpContext c) => { return await Answer(id, c); })
            .SupervisorOn()
            .Admin(CallerView.Admitted, "a question is answered here by the operator only; the supervisor answers with a message of type answer");
        // GET /digest: the digest of this moment for the active supervisor (after a restart or a compaction; the routine never polls it) and the operator.
        routes.MapGet("/api/v1/digest", (HttpContext c) => Digest(c))
            .SupervisorOn();
        routes.MapPost("/api/v1/supervisor/patrol-ack", async (HttpContext c) => { return await PatrolAck(c); })
            .SupervisorOn();
        // the operator's two controls of the supervisor's cost: the patrol interval (5 to 240 minutes, in memory) and extra deliveries for one hour
        routes.MapPost("/api/v1/supervisor/patrol", (HttpContext c) => Patrol(c))
            .SupervisorOn()
            .Admin(CallerView.Who, "only the operator sets the patrol interval");
        routes.MapPost("/api/v1/supervisor/budget", (HttpContext c) => Budget(c))
            .SupervisorOn()
            .Admin(CallerView.Admitted, "only the operator grants deliveries");
        // POST /messaging/enable and /messaging/disable (worker-messaging.md 4.5): the operator's switch for the whole gateway, in memory. Only a gateway
        // that was loaded with messaging on has the switch; elsewhere both answer messaging-disabled, as every messaging endpoint does there.
        routes.MapPost("/api/v1/messaging/{action}", (string action, HttpContext c) => Switch(action, c));
    }

    IResult Questions(HttpContext ctx)
    {
        var who = ctx.Caller().Admitted;
        if (who.Kind is not ("admin" or "manager")) return Fail(403, "forbidden", "the questions are read by the supervisor and the operator", 30);
        if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
        var state = ctx.Request.Query["state"].ToString();
        if (state is not ("" or "open" or "all")) return Fail(400, "usage", "state is open or all", 2);
        return Results.Text(plant.Questions(state == "all", who.Kind == "admin").ToJsonString(), "application/json");
    }

    async Task<IResult> Answer(string id, HttpContext ctx)
    {
        var who = ctx.Caller().Admitted;
        var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
        return MessagingResults.AsResult(tooLarge ? messenger.TooLarge(who, "operator-note") : messenger.OperatorAnswer(id, text, who.Name));
    }

    IResult Digest(HttpContext ctx)
    {
        var who = ctx.Caller().Admitted;
        if (who.Kind is not ("admin" or "manager")) return Fail(403, "forbidden", "the digest is read by the supervisor and the operator", 30);
        if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
        // for the supervisor it is a step of its hourly budget (3.9), like its read verbs
        if (who.Kind == "manager" && plant.SupervisorStep(false) is { Ok: false } noStep)
            return Results.Json(new { ok = false, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }, statusCode: 429);
        return Results.Text(new JsonObject { ["ok"] = true, ["digest"] = plant.DigestNow(), ["exit"] = 0 }.ToJsonString(), "application/json");
    }

    async Task<IResult> PatrolAck(HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        if (who.Kind != "manager" || plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "a patrol is acknowledged by the active supervisor only", 30);
        var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
        return MessagingResults.AsResult(tooLarge ? messenger.TooLarge(who, "patrol-ack") : messenger.PatrolAck(who, text));
    }

    IResult Patrol(HttpContext ctx)
    {
        if (!double.TryParse(ctx.Request.Query["minutes"], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || !double.IsFinite(minutes) || minutes < 5 || minutes > 240)
            return Fail(400, "usage", "minutes is 5 to 240", 2);
        plant.SetPatrolMinutes(minutes);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "patrol-interval", ["minutes"] = minutes, ["by"] = "operator" }.ToJsonString());
        return Results.Json(new { ok = true, patrolMinutes = minutes, exit = 0 });
    }

    IResult Budget(HttpContext ctx)
    {
        var caller = ctx.Caller();
        if (!int.TryParse(ctx.Request.Query["extra"], NumberStyles.None, CultureInfo.InvariantCulture, out var extra) || extra < 1 || extra > 100) return Fail(400, "usage", "extra is 1 to 100 deliveries", 2);
        // the operator session may grant at most 12 deliveries in an hour, all its grants together: the person's grants are not capped
        if (caller.OperatorActive && !plant.OperatorGrant(extra)) return Results.Json(new { ok = false, code = "grant-limit", reason = "the operator session grants at most 12 extra deliveries in an hour; more needs the operator", retryAfterSeconds = 600, exit = 20 }, statusCode: 429);
        var total = plant.GrantDeliveries(extra);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "budget-granted", ["extra"] = extra, ["by"] = caller.By }.ToJsonString());
        return Results.Json(new { ok = true, extra, extraThisHour = total, exit = 0 });
    }

    IResult Switch(string action, HttpContext ctx)
    {
        if (action is not ("enable" or "disable")) return Results.NotFound();
        if (!gateway.OwnerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
        if (!messaging.Configured) return Fail(403, "messaging-disabled", "peer messaging is not enabled on this gateway (messaging.enabled); the switch exists only where it is", 30);
        if (ctx.Caller().Who.Kind != "admin") return Fail(403, "forbidden", "only the operator switches messaging", 30);
        var changed = plant.SetMessagingEnabled(action == "enable");
        if (changed) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = action == "enable" ? "messaging-enabled" : "messaging-disabled", ["by"] = "operator" }.ToJsonString());
        return Results.Json(new { ok = true, enabled = messaging.Enabled, changed, exit = 0 });
    }
}
