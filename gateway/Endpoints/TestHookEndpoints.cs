using System.Globalization;
using System.Text.Json.Nodes;
using static HttpResultsX;

// Test seam of the Tier 1 suites (task C7c; messaging.testHooks, operator token only): puts a message into a name's inbox, or a trigger into its Buffer,
// answers a question, and sets a messaging mode, so the store, the replay merge, the envelope and the question transport can be tested before the
// supervisor's and the operator's endpoints exist (steps 5 and 6). Not mapped unless the config asks for it: Map maps nothing unless messaging is
// enabled and messaging.testHooks is on, read when the routes are mapped, as Program.cs read them. Every route's guard is Admin with a bare 403.
sealed class TestHookEndpoints(GatewayOptions gateway, MessagingSettings messaging, TimingOptions timing, Plant plant, Messenger messenger, RuntimeRegistry runtimes,
    Revocations revocations, RuntimeNonces runtimeNonces) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        if (!(messaging.Enabled && messaging.TestHooks)) return;
        // the octopus runtime's start of a worker with an explicit role (the overflow rule; the plant starts own-item workers only)
        routes.MapPost("/api/v1/_test/octopus-provision", async (HttpContext c) => { return await OctopusProvision(c); }).Admin(CallerView.Who);
        // a runtime worker's nonce, as the adapter mints one at provisioning: {"name", "runtime", "tokenSeconds", "ttlSeconds"}
        routes.MapPost("/api/v1/_test/runtime-nonce", async (HttpContext c) => { return await RuntimeNonce(c); }).Admin(CallerView.Who);
        // an r1 for a test (agents-roles-launch.md 4): only the adapters mint one in a deployed gateway
        routes.MapPost("/api/v1/_test/role-token", async (HttpContext c) => { return await RoleToken(c); }).Admin(CallerView.Who);
        routes.MapPost("/api/v1/_test/deliver", async (HttpContext c) => { return await Deliver(c); }).Admin(CallerView.Who);
        // the answer to an open question, as the supervisor or the operator would give it (step 5 adds the real endpoints on the same plant method)
        routes.MapPost("/api/v1/_test/answer", async (HttpContext c) => { return await Answer(c); }).Admin(CallerView.Who);
        // one sweep's `owned` and the items it must keep (not read this time), as the Sweeper hands them to Plan
        routes.MapPost("/api/v1/_test/plan", async (HttpContext c) => { return await Plan(c); }).Admin(CallerView.Who);
        // one sweep's `release_queue`, as the Sweeper hands it to the plant: { "queue": [{ "item": N, "release": "1.0.N", "since": "<iso>" }] }
        routes.MapPost("/api/v1/_test/release-queue", async (HttpContext c) => { return await ReleaseQueue(c); }).Admin(CallerView.Who);
        // a gate refusal of one code on one item, as the /verbs handler records one of a verb that answered exit 10 or 11 (the digest's refusals)
        routes.MapPost("/api/v1/_test/refusal", async (HttpContext c) => { return await Refusal(c); }).Admin(CallerView.Who);
        // a fact of the reported conditions (3.9), as the verbs handler records it: a failed verb, a recorded review round, a refused merge
        routes.MapPost("/api/v1/_test/fact", async (HttpContext c) => { return await Fact(c); }).Admin(CallerView.Who);
        // the plant data the digest will be computed from (3.9, step 5a): owners as the sweep lists them, activity, waiting codes, refusals, parks
        routes.MapGet("/api/v1/_test/plant-data", (HttpContext c) => Results.Text(plant.PlantData().ToJsonString(), "application/json")).Admin(CallerView.Who);
        // issue 105: the plant snapshot as a graceful stop writes it, and its restore as the next start runs it (the digest's memory is part of it)
        routes.MapGet("/api/v1/_test/plant-snapshot", (HttpContext c) => Results.Text(plant.Snapshot().ToJsonString(), "application/json")).Admin(CallerView.Who);
        routes.MapPost("/api/v1/_test/plant-restore", async (HttpContext c) => { return await PlantRestore(c); }).Admin(CallerView.Who);
        // how many entries each bounded store holds, with its cap (the stress test and the limit scenarios read it); counts only, never a text or a name
        routes.MapGet("/api/v1/_test/stores", (HttpContext c) => Stores()).Admin(CallerView.Who);
        // who owns, inspects and runs the QC of an item, as accepted assignments would record it (independence, broadcast and thread scenarios)
        routes.MapPost("/api/v1/_test/roles", async (HttpContext c) => { return await Roles(c); }).Admin(CallerView.Who);
        // a verb call of an item token that has not answered yet, as the middleware counts one (the operator session's quiet-plant check): start
        // returns a ticket, end takes it back
        routes.MapPost("/api/v1/_test/inflight", async (HttpContext c) => { return await Inflight(c); }).Admin(CallerView.Who);
        routes.MapPost("/api/v1/_test/mode", async (HttpContext c) => { return await Mode(c); }).Admin(CallerView.Who);
    }

    // the body of a seam call (the Admin filter has refused any caller but the operator token): a JSON object of at most 64 KB
    static async Task<(JsonObject? Body, IResult? Error)> SeamBody(HttpContext ctx, string shape)
    {
        var (text, tooLarge) = await ReadBody(ctx, 64 * 1024);
        if (tooLarge) return (null, Fail(413, "too-large", "the request body is over 64 KB", 2));
        try { return (Messenger.ParseObject(text), null); }
        catch { return (null, Fail(400, "usage", shape, 2)); }
    }

    async Task<IResult> OctopusProvision(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with item, phase and role");
        if (b == null) return err!;
        var octopusManager = runtimes.Octopus;
        if (octopusManager == null) return Fail(404, "no-runtime", "the octopus runtime is not switched on", 12);
        var t = octopusManager.Begin(int.TryParse(b["item"]?.ToString(), out var ti) ? ti : 0, b["phase"]?.ToString() ?? "", "", b["role"]?.ToString() ?? "");
        await octopusManager.Provision(t);
        return Results.Json(new { ok = true, name = t.Name, execution = octopusManager.ExecutionOf(t.Name) });
    }

    async Task<IResult> RuntimeNonce(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name and runtime");
        if (b == null) return err!;
        var nm = b["name"]?.ToString() ?? ""; var rk = b["runtime"]?.ToString() ?? "";
        if (!Auth.ValidName(nm) || !Auth.ValidRuntime(rk)) return Fail(400, "usage", "a valid worker name and a runtime other than local", 2);
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)(double.TryParse(b["tokenSeconds"]?.ToString(), out var ts) ? ts : 3600);
        return Results.Json(new { ok = true, nonce = runtimeNonces.Mint(nm, rk, exp, TimeSpan.FromSeconds(double.TryParse(b["ttlSeconds"]?.ToString(), out var tt) ? tt : 600)) });
    }

    async Task<IResult> RoleToken(HttpContext ctx)
    {
        var tokenSecret = gateway.TokenSecret;
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name, role and mode");
        if (b == null) return err!;
        var nm = b["name"]?.ToString() ?? ""; var rl = b["role"]?.ToString() ?? ""; var md = b["mode"]?.ToString() ?? "manual";
        if (!Auth.ValidName(nm) || !Auth.Roles.Contains(rl) || !Auth.Modes.Contains(md) || tokenSecret.Length == 0) return Fail(400, "usage", "a valid name, a known role and mode, and a token secret", 2);
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)(double.TryParse(b["tokenSeconds"]?.ToString(), out var ts) && double.IsFinite(ts) ? Math.Clamp(ts, -86400, 86400) : 3600);
        var claims = new RoleClaims(nm, rl, md, b["agent"]?.ToString() ?? "", b["env"]?.ToString() ?? Auth.Environment, exp,
            int.TryParse(b["item"]?.ToString(), out var ri) ? ri : 0, b["assignment"]?.ToString() ?? "", b["phase"]?.ToString() ?? "");
        return Results.Json(new { ok = true, token = Auth.RoleToken(tokenSecret, claims, revocations.Of(nm)) });
    }

    async Task<IResult> Deliver(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with to and text, trigger or assigned");
        if (b == null) return err!;
        // an item trigger as the webhooks and the sweep route it: into the item's mailbox (what GET /wait reads) and to the owner's stream if there is one
        if (b["routeItem"] is JsonNode ri && int.TryParse(ri.ToString(), out var routeItem))
        {   // with park: an item.parked trigger with that reason, as the sweep routes one
            var routed = new JsonObject { ["provider"] = "gateway", ["event"] = b["park"] != null ? "item.parked" : b["event"]?.ToString() is { Length: > 0 } routedName ? routedName : "test.item-trigger", ["item"] = routeItem, ["note"] = b["note"]?.ToString() ?? "" };
            if (b["park"] != null) routed["reason"] = b["park"]!.ToString();
            plant.Route(routeItem, routed); return Results.Json(new { ok = true, exit = 0 });
        }
        var to = b["to"]?.ToString() ?? "";
        if (!Auth.ValidName(to)) return Fail(400, "usage", "to is a session name", 2);
        if (b["trigger"] is JsonNode tg) { plant.SendTo(to, new JsonObject { ["provider"] = "gateway", ["event"] = "test.trigger", ["note"] = tg.ToString() }); return Results.Json(new { ok = true, exit = 0 }); }
        // an item.assigned in the Buffer, as a replay would show it (no real assignment, no real token): for the client's replayed-event rule
        // With job (bot-job or qc-env) the event carries a real item token of that role, so a scenario can see which credential a spawned process gets.
        if (b["assigned"] is JsonNode asg && int.TryParse(asg.ToString(), out var asgItem))
        {
            var job = b["job"]?.ToString() ?? "own-item";
            if (job is not ("own-item" or "bot-job" or "qc-env")) return Fail(400, "usage", "job is own-item, bot-job or qc-env", 2);
            var jobRole = job == "bot-job" ? "inspector" : job == "qc-env" ? "qc" : "owner";
            var asgEvt = new JsonObject { ["provider"] = "gateway", ["event"] = "item.assigned", ["item"] = asgItem, ["job"] = job, ["role"] = jobRole, ["mode"] = "first", ["assignment"] = "a-test",
                ["token"] = b["job"] == null || b["tokenless"]?.ToString() == "true" ? "test-placeholder" : Auth.MintItemToken(gateway.TokenSecret, asgItem, jobRole, "a-test", to, TimeSpan.FromHours(1), revocations.Of(to)) };
            if (job == "qc-env") { asgEvt["pr"] = 0; asgEvt["sha"] = "0000000"; asgEvt["env_id"] = "qc-test"; }
            plant.SendTo(to, asgEvt); return Results.Json(new { ok = true, exit = 0 });
        }
        // a message as the gateway would deliver it; the optional fields let a scenario play the supervisor or a peer (the real send endpoint cannot)
        var fromKind = b["fromKind"]?.ToString() ?? "operator";
        if (fromKind is not ("operator" or "supervisor" or "worker")) return Fail(400, "usage", "fromKind is operator, supervisor or worker", 2);
        var from = b["from"]?.ToString() ?? (fromKind == "operator" ? "operator" : fromKind == "supervisor" ? "supervisor" : "peer");
        if (!Auth.ValidName(from)) return Fail(400, "usage", "from is a session name", 2);
        var evt = new JsonObject
        {
            ["provider"] = "peer", ["relay"] = "gateway", ["event"] = "message.received", ["type"] = b["type"]?.ToString() ?? (fromKind == "operator" ? "operator-note" : "note"),
            ["from"] = from, ["fromKind"] = fromKind, ["to"] = to, ["kind"] = "direct", ["thread"] = null,
            ["item"] = b["item"] is JsonValue iv && iv.TryGetValue<int>(out var itemN) ? itemN : null, ["inReplyTo"] = b["inReplyTo"]?.ToString(),
            ["subject"] = b["subject"]?.ToString() ?? "", ["text"] = b["text"]?.ToString() ?? "", ["sentAt"] = DateTime.UtcNow.ToString("o"),
            ["flags"] = b["flags"] is JsonArray fl ? new JsonArray(fl.Select(f => (JsonNode)f!.ToString()).ToArray()) : new JsonArray()
        };
        if (b["fromModel"]?.ToString() is { Length: > 0 } fm) evt["fromModel"] = fm;
        var (id, seq, pushed) = plant.Deliver(to, evt);
        return Results.Json(new { ok = true, id, seq, pushed, exit = 0 });
    }

    async Task<IResult> Answer(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with id and text");
        if (b == null) return err!;
        var fromKind = b["fromKind"]?.ToString() ?? "supervisor";
        if (fromKind is not ("operator" or "supervisor")) return Fail(400, "usage", "fromKind is operator or supervisor", 2);
        var evt = new JsonObject
        {
            ["provider"] = "peer", ["relay"] = "gateway", ["event"] = "message.received", ["type"] = fromKind == "operator" ? "operator-note" : "answer",
            ["from"] = fromKind == "operator" ? "operator" : "supervisor", ["fromKind"] = fromKind, ["kind"] = "direct", ["thread"] = null, ["subject"] = "",
            ["text"] = b["text"]?.ToString() ?? "", ["sentAt"] = DateTime.UtcNow.ToString("o"), ["flags"] = new JsonArray()
        };
        if (b["model"]?.ToString() is { Length: > 0 } model) evt["fromModel"] = model;
        var (ok, code, reason, answerId, _, _, _) = plant.AnswerQuestion(b["id"]?.ToString() ?? "", evt);
        return ok ? Results.Json(new { ok = true, id = answerId, exit = 0 }) : Fail(409, code, reason, 10);
    }

    async Task<IResult> Plan(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with owned and keep");
        if (b == null) return err!;
        plant.Plan(b["offers"]?.DeepClone() as JsonArray ?? new JsonArray(), b["owners"]?.DeepClone() as JsonObject, b["pins"]?.DeepClone() as JsonObject, b["owned"]?.DeepClone() as JsonObject ?? new JsonObject(), (b["keep"] as JsonArray ?? new JsonArray()).Select(x => x!.GetValue<int>()).ToList(), b["priorities"]?.DeepClone() as JsonObject,
            DateTime.TryParse(b["sweepStarted"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var seamStart) ? seamStart : null);
        return Results.Json(new { ok = true, exit = 0 });
    }

    async Task<IResult> ReleaseQueue(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with queue");
        if (b == null) return err!;
        plant.SetReleaseQueue(b["queue"]?.DeepClone() as JsonArray ?? new JsonArray());
        return Results.Json(new { ok = true, exit = 0 });
    }

    async Task<IResult> Refusal(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name, item and code");
        if (b == null) return err!;
        var rn = b["name"]?.ToString() ?? "";
        if (!Auth.ValidName(rn) || !(b["item"] is JsonValue fv && fv.TryGetValue<int>(out var fItem))) return Fail(400, "usage", "name is a session name and item a work item number", 2);
        plant.NoteRefusal(rn, fItem, b["code"]?.ToString() ?? "");
        return Results.Json(new { ok = true, exit = 0 });
    }

    async Task<IResult> Fact(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with fact (verb-error, review or merge-refused) and item");
        if (b == null) return err!;
        if (!(b["item"] is JsonValue fv && fv.TryGetValue<int>(out var fItem) && fItem >= 1)) return Fail(400, "usage", "item is a work item number", 2);
        int Num(string k, int d) => b[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : d;
        switch (b["fact"]?.ToString())
        {
            case "verb-error" when Auth.ValidName(b["name"]?.ToString() ?? ""): plant.NoteVerbError(b["name"]!.ToString(), fItem, b["code"]?.ToString() ?? "", Num("exit", 20)); break;
            case "review": plant.NoteReview(fItem, b["column"]?.ToString() ?? "", b["verdict"]?.ToString() ?? "FINDINGS", Num("round", 1), Num("max", 3)); break;
            case "merge-refused": plant.NoteMergeRefused(fItem, Num("roundTrip", 1)); break;
            default: return Fail(400, "usage", "fact is verb-error (with name), review or merge-refused", 2);
        }
        return Results.Json(new { ok = true, exit = 0 });
    }

    async Task<IResult> PlantRestore(HttpContext ctx)
    {
        string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
        JsonNode? snap; try { snap = JsonNode.Parse(text); } catch { return Fail(400, "bad-json", "the body is a plant snapshot", 2); }
        var (na, no, skipped) = plant.Restore(snap, timing.PlantSnapshotMaxAgeMinutes);
        return Results.Json(new { ok = true, assignments = na, owners = no, skipped, digest = plant.DigestReportedCount() });
    }

    IResult Stores()
    {
        var o = plant.StoreStats(); messenger.AddStoreStats(o);
        return Results.Text(o.ToJsonString(), "application/json");
    }

    async Task<IResult> Roles(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with item and owner, inspector or qc");
        if (b == null) return err!;
        if (!(b["item"] is JsonValue rv && rv.TryGetValue<int>(out var rItem) && rItem >= 1)) return Fail(400, "usage", "item is a work item number", 2);
        string? Name(string k) => b.ContainsKey(k) ? b[k]?.ToString() ?? "" : null;
        var (ro, ri, rq) = (Name("owner"), Name("inspector"), Name("qc"));
        foreach (var n in new[] { ro, ri, rq }) if (n is { Length: > 0 } && !Auth.ValidName(n)) return Fail(400, "usage", "owner, inspector and qc are session names", 2);
        plant.TestSetRoles(rItem, ro, ri, rq, b["sweepable"]?.ToString() == "true");
        return Results.Json(new { ok = true, exit = 0 });
    }

    async Task<IResult> Inflight(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name, item and verb, or with end");
        if (b == null) return err!;
        if (b["end"] is JsonValue ev && ev.TryGetValue<long>(out var ticket)) { plant.VerbEnded(ticket); return Results.Json(new { ok = true, exit = 0 }); }
        var fn = b["name"]?.ToString() ?? "";
        if (!Auth.ValidName(fn) || !(b["item"] is JsonValue fi && fi.TryGetValue<int>(out var fItem2))) return Fail(400, "usage", "name is a session name and item a work item number", 2);
        return Results.Json(new { ok = true, ticket = plant.VerbStarted(fn, fItem2, b["verb"]?.ToString() ?? ""), exit = 0 });
    }

    async Task<IResult> Mode(HttpContext ctx)
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name and mode");
        if (b == null) return err!;
        var name = b["name"]?.ToString() ?? "";
        return Auth.ValidName(name) && plant.SetMessagingMode(name, b["mode"]?.ToString() ?? "") ? Results.Json(new { ok = true, name, mode = plant.MessagingMode(name), exit = 0 }) : Fail(400, "usage", "name is a session name and mode is on, mute, inbound-off or off", 2);
    }
}
