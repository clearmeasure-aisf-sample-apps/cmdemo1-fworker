using System.Text.Json.Nodes;
using System.Threading.Channels;
using static HttpResultsX;

// The event streams' framing (task C7c): the replay, the registration line and the channel of a worker or slot stream, the `: ping` cadence of every
// stream, and the stream ids. Every event stream writes a `: ping` comment at FACTORY_SSE_PING_SECONDS (default 25), busy or idle: a proxy or the
// platform's ingress that sees no byte for a while closes the connection, and a client then sees a drop it did not cause (enhancement 11). Clients
// ignore comment lines.
sealed class SseStreams(GatewayOptions gateway, MessagingSettings messaging, KeeperSettings keeper, Plant plant)
{
    // One worker or supervisor stream: the replay snapshot, the registration line, then the channel until it ends. An entry with id 0 (stream.replaced)
    // carries no id line, so it never becomes a client's cursor.
    public async Task Pump(HttpContext ctx, Channel<(long, string)> ch, List<(long, string)> replay, string registered, string? snapshot = null)
    {
        foreach (var (id, json) in replay) await ctx.Response.WriteAsync($"id: {Sid(id)}\ndata: {json}\n\n");
        await ctx.Response.WriteAsync($"data: {registered}\n\n");
        if (snapshot != null) await ctx.Response.WriteAsync($"data: {snapshot}\n\n");
        await ctx.Response.Body.FlushAsync();
        var lastPing = DateTime.UtcNow;
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted); cts.CancelAfter(PingDue(lastPing));
            try { var (id, json) = await ch.Reader.ReadAsync(cts.Token); await ctx.Response.WriteAsync(id == 0 ? $"data: {json}\n\n" : $"id: {Sid(id)}\ndata: {json}\n\n"); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { }
            catch (ChannelClosedException) { break; }
            if (PingDue(lastPing) == TimeSpan.Zero) { await ctx.Response.WriteAsync(": ping\n\n"); lastPing = DateTime.UtcNow; }
            await ctx.Response.Body.FlushAsync();
        }
    }

    // GET /events?name=N&supervisor=1&model=&effort= with the m1. credential of that name (worker-messaging.md 4.6): the supervisor's slot, never a worker.
    public async Task SupervisorStream(HttpContext ctx, Principal who, string name, long after)
    {
        if (who.Kind != "manager" || who.Name != name) { await Refuse(ctx, 403, "forbidden", "only the supervisor credential issued for this name registers as the supervisor"); return; }
        var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
        if (!Auth.ValidRegistration("persistent-local", Array.Empty<string>(), Array.Empty<string>(), model, effort)) { await Refuse(ctx, 400, "usage", "model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
        // the model requirement (4.7): flagged by default, refused only when manager.enforceModel is on; declared, never verified
        if (messaging.EnforceModel && messaging.ModelCheck(model, effort) != "ok")
        { await Refuse(ctx, 403, "supervisor-model-too-weak", $"this gateway enforces the supervisor's model: the {messaging.RequiredModelTier} tier at effort {messaging.RequiredEffort} or higher, declared with --model and --effort"); return; }
        var (ok, active, since, ch, replay) = plant.RegisterSupervisor(name, model, effort, after);
        if (ok) plant.NoteHandover(name, ctx.Request.Query["handover"].ToString() == "1");
        if (!ok) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "supervisor-active", reason = "another supervisor session is active and was heard within timing.mgrTakeoverSeconds", name = active, since, exit = 40 }); return; }
        ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
        try { await Pump(ctx, ch!, replay, new JsonObject { ["provider"] = "gateway", ["event"] = "supervisor.registered", ["name"] = name, ["epoch"] = plant.Epoch, ["modelCheck"] = messaging.ModelCheck(model, effort) }.ToJsonString()); }
        catch (OperationCanceledException) { }
        finally { plant.DisconnectSupervisor(ch!); }
    }

    // GET /events?name=N&keeper=1&model=&effort= with the k1. credential of that name: the backlog keeper's slot (Keeper.cs), never a worker.
    public async Task KeeperStream(HttpContext ctx, Principal who, string name, long after)
    {
        if (who.Kind != "keeper" || who.Name != name) { await Refuse(ctx, 403, "forbidden", "only the keeper credential issued for this name registers as the backlog keeper"); return; }
        var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
        if (!Auth.ValidRegistration("persistent-local", Array.Empty<string>(), Array.Empty<string>(), model, effort)) { await Refuse(ctx, 400, "usage", "model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
        var (ok, active, since, ch, replay) = plant.RegisterKeeper(name, model, effort, after);
        if (!ok) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "keeper-active", reason = "another backlog keeper is active and was heard within timing.mgrTakeoverSeconds", name = active, since, exit = 40 }); return; }
        plant.KeeperRaiseOpen();
        ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
        try { await Pump(ctx, ch!, replay, new JsonObject { ["provider"] = "gateway", ["event"] = "keeper.registered", ["name"] = name, ["epoch"] = plant.Epoch, ["modelCheck"] = messaging.ModelCheckFor(model, effort, keeper.RequiredModelTier, keeper.RequiredEffort) }.ToJsonString()); }
        catch (OperationCanceledException) { }
        finally { plant.DisconnectKeeper(ch!); }
    }

    // GET /events?name=N&operator=1&model=&effort= with the o1. credential of that name: the operator session's slot (Operator.cs), never a worker.
    public async Task OperatorStream(HttpContext ctx, Principal who, string name, long after)
    {
        if (who.Kind != "operator" || who.Name != name) { await Refuse(ctx, 403, "forbidden", "only the operator session credential issued for this name registers as the operator session"); return; }
        if (!messaging.Enabled) { await Refuse(ctx, 403, "messaging-disabled", "peer messaging is not enabled on this gateway (messaging.enabled)"); return; }
        var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
        if (!Auth.ValidRegistration("persistent-local", Array.Empty<string>(), Array.Empty<string>(), model, effort)) { await Refuse(ctx, 400, "usage", "model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
        var (ok, active, since, ch, replay) = plant.RegisterOperator(name, model, effort, after);
        if (!ok) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "operator-active", reason = "another operator session is active and was heard within timing.mgrTakeoverSeconds", name = active, since, exit = 40 }); return; }
        ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
        try { await Pump(ctx, ch!, replay, new JsonObject { ["provider"] = "gateway", ["event"] = "operator.registered", ["name"] = name, ["epoch"] = plant.Epoch, ["modelCheck"] = messaging.OperatorModelCheck(model, effort) }.ToJsonString()); }
        catch (OperationCanceledException) { }
        finally { plant.DisconnectOperator(ch!); }
    }

    // how long until the next `: ping` of a stream is due (zero: now)
    public TimeSpan PingDue(DateTime lastPing) { var left = lastPing.AddSeconds(gateway.SsePingSeconds) - DateTime.UtcNow; return left > TimeSpan.Zero ? left : TimeSpan.Zero; }
    public string Sid(long id) => messaging.On ? $"{plant.Epoch}.{id}" : id.ToString();
}

// GET /api/v1/events (task C7c): with ?name= a worker registers by opening its stream, or the supervisor, the backlog keeper or the operator session
// opens its slot's stream; without a name the operator token's broadcast stream. A stream answers with raw writes, not an IResult, so the handler is
// a RequestDelegate (as it was in Program.cs) and has no filters: every check of it is written into the response by the handler itself.
sealed class EventStreamEndpoints(GatewayOptions gateway, MessagingSettings messaging, KeeperSettings keeper, Plant plant, EventHub hub, SseStreams sse) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/events", (HttpContext c) => Events(c));

    async Task Events(HttpContext ctx)
    {
        var ownerMode = gateway.OwnerMode;
        var who = ctx.Caller().Who;
        var name = ctx.Request.Query["name"].ToString();
        var lastEventId = ctx.Request.Headers["Last-Event-ID"].ToString();
        long after = long.TryParse(lastEventId, out var l) ? l : -1;
        if (name != "")
        {   // a worker registers by opening its stream
            if (!ownerMode) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"not-owner-mode\",\"reason\":\"the gateway is not running in owner mode\",\"exit\":30}"); return; }
            if (!Auth.ValidName(name)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"usage\",\"reason\":\"the worker name may use letters, digits, - and _ only\",\"exit\":2}"); return; }
            // with messaging or the supervisor on the id is "<epoch>.<seq>"; another epoch, a bare number or no header is no valid cursor and replays all (3.5)
            if (messaging.On) { after = ParseCursor(lastEventId, plant.Epoch); if ((who.Kind is "worker" or "manager") && who.Name == name) plant.NoteStaleEpoch(name, lastEventId); }
            if (messaging.OperatorEnabled && ctx.Request.Query["operator"].ToString() == "1") { await sse.OperatorStream(ctx, who, name, after); return; }
            if (keeper.Enabled && ctx.Request.Query["keeper"].ToString() == "1") { await sse.KeeperStream(ctx, who, name, after); return; }
            if (who.Kind == "keeper") { await Refuse(ctx, 403, "forbidden", "the backlog keeper opens only its own stream (keeper=1)"); return; }
            if (messaging.ManagerEnabled && ctx.Request.Query["supervisor"].ToString() == "1") { await sse.SupervisorStream(ctx, who, name, after); return; }
            if (who.Kind == "manager") { await Refuse(ctx, 403, "forbidden", "a supervisor session does not open a worker stream"); return; }
            if (!(who.Kind == "admin" || (who.Kind == "worker" && who.Name == name))) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"register with the credential issued for this worker name\",\"exit\":30}"); return; }
            if (messaging.Reserved.Contains(name)) { await Refuse(ctx, 409, "name-reserved", "the name is reserved for the operator, the gateway or the supervisor", 10); return; }
            var caps = ctx.Request.Query["capabilities"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var roles = ctx.Request.Query["roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var type = string.IsNullOrEmpty(ctx.Request.Query["type"]) ? "persistent-local" : ctx.Request.Query["type"].ToString();
            var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
            // the runtime is the credential's (cloud-worker-runtimes.md 4.1): a w2 names it, a w1 or the admin token is local; a claim that differs is refused
            var runtime = who.Kind == "worker" && who.Runtime != "" ? who.Runtime : "local";
            var claimedRuntime = ctx.Request.Query["runtime"].ToString();
            if (claimedRuntime != "" && claimedRuntime != runtime) { await Refuse(ctx, 403, "runtime-not-trusted", $"this credential registers runtime '{runtime}' only: a runtime is taken from the credential the gateway minted, never from the claim"); return; }
            if (!Auth.ValidRegistration(type, roles, caps, model, effort))
            { await Refuse(ctx, 400, "usage", "type, roles, capabilities (10 at most), model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
            ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
            var (w, ch, replay) = plant.Register(name, type, caps, int.TryParse(ctx.Request.Query["capacity"], out var cap) ? cap : 1, roles, after,
                int.TryParse(ctx.Request.Query["qc_capacity"], out var qcap) ? qcap : 1, ctx.Request.Query["qc_address"].ToString(), ctx.Request.Query["qc_profile"].ToString() is "1" or "true",
                messagingClient: ctx.Request.Query["messaging"].ToString() == "1", model: model, effort: effort,
                // a stream opened without a cursor is a new session of this name (a reconnect of the same client sends its last id): one item per session
                newSession: lastEventId == "",
                // a client that reconnects on stream.replaced with reconnect (issue 94); an older one is ended without it, and reconnects as after any drop
                handover: ctx.Request.Query["handover"].ToString() == "1", runtime: runtime);
            plant.SetHost(name, ctx.Request.Query["host"].ToString(), effort);
            // a new session can take a work item that waited as session-spent: plan now instead of at the next sweep (issue 72)
            if (lastEventId == "") hub.RequestSweep();
            var registered = new JsonObject { ["provider"] = "gateway", ["event"] = "worker.registered", ["name"] = name };
            if (messaging.On) { registered["epoch"] = plant.Epoch; registered["messaging"] = messaging.Enabled; }
            // one peers.snapshot per registration (3.2), only for a client that registered with messaging=1 under an allow-listed name
            var snapshot = messaging.Enabled && ctx.Request.Query["messaging"].ToString() == "1" && messaging.WorkerAllowed(name) ? plant.PeersSnapshot(name) : null;
            try { await sse.Pump(ctx, ch, replay, registered.ToJsonString(), snapshot); }
            catch (OperationCanceledException) { }
            finally { plant.Disconnect(w, ch); }
            return;
        }
        if (who.Kind != "admin") { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"the broadcast stream is for the operator\",\"exit\":30}"); return; }
        ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
        var (sub, legacyReplay, pending) = hub.Subscribe(after);
        try
        {
            foreach (var (id, json) in legacyReplay) await ctx.Response.WriteAsync($"id: {id}\ndata: {json}\n\n");
            foreach (var json in pending) await ctx.Response.WriteAsync($"data: {json}\n\n");
            await ctx.Response.Body.FlushAsync();
            var lastPing = DateTime.UtcNow;
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted); cts.CancelAfter(sse.PingDue(lastPing));
                try { var (id, json) = await sub.Reader.ReadAsync(cts.Token); await ctx.Response.WriteAsync($"id: {id}\ndata: {json}\n\n"); }
                catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { }
                if (sse.PingDue(lastPing) == TimeSpan.Zero) { await ctx.Response.WriteAsync(": ping\n\n"); lastPing = DateTime.UtcNow; }
                await ctx.Response.Body.FlushAsync();
            }
        }
        catch (OperationCanceledException) { }
        finally { hub.Unsubscribe(sub); }
    }
}
