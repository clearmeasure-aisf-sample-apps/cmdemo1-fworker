using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static HttpResultsX;

// What the runtime hosts and runtime workers call (task C7b): the reconcile loop of the Octopus runbook runtime, the redemption of a runtime worker's
// one-time nonce, and a host's private-build slot. The host token and the nonce are credentials (a protected area, CLAUDE.md): each endpoint checks
// its own, the middleware lets these paths through (gateway/Http/GatewayAuthMiddleware.cs).
// nonces: the runtime workers' one-time nonces, made by AddGatewayIdentity (GatewayComposition.RuntimeNonces).
sealed class RuntimeHostEndpoints(GatewayOptions gateway, Plant plant, Revocations revocations, RevocationService revocationService, RuntimeRegistry runtimes,
    RuntimeNonces nonces) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/runtime/octopus/desired", async (HttpContext c) => { return await OctopusDesired(c); });
        routes.MapPost("/api/v1/runtime/octopus/report", async (HttpContext c) => { return await OctopusReport(c); });
        routes.MapPost("/api/v1/runtime/redeem", async (HttpContext c) => { return await Redeem(c); });
        routes.MapPost("/api/v1/build-slot", async (HttpContext c) => { return await BuildSlot(c); }).OwnerMode();
    }

    // The reconcile loop of the Octopus runbook runtime (docs/design/octopus-runbook-runtime.md 6): the scheduled runbook "Reconcile hosts" asks, per host,
    // which workers should run there and reports the fw-* containers it found. Authenticated by the host token (FACTORY_OCTOPUS_HOST_TOKEN, compared in
    // constant time) only: never the operator token, never a worker credential. The answers name workers, never a credential.
    static string HostBearer(HttpContext c) { var a = c.Request.Headers.Authorization.ToString(); return a.StartsWith("Bearer ", StringComparison.Ordinal) ? a[7..].Trim() : ""; }

    async Task<IResult> OctopusDesired(HttpContext c)
    {
        var octopusRuntime = runtimes.OctopusRuntime;
        if (octopusRuntime == null) return Fail(404, "no-runtime", "the octopus runtime is not switched on", 12);
        if (!octopusRuntime.HostTokenOk(HostBearer(c))) return Fail(401, "unauthorized", "missing or wrong host token", 30);
        var host = c.Request.Query["host"].ToString().Trim();
        if (host.Length is 0 or > 200) return Fail(400, "usage", "?host=<machine id or name> is required", 2);
        JsonObject? d; try { d = await octopusRuntime.Desired(host, c.RequestAborted); } catch { return Fail(503, "not-ready", "the runtime has not read back its workers yet; remove nothing and try again", 20); }
        return d == null ? Fail(404, "unknown-host", "the host is not a deployment target of the runtime", 12) : Results.Text(d.ToJsonString(), "application/json");
    }

    async Task<IResult> OctopusReport(HttpContext c)
    {
        var octopusRuntime = runtimes.OctopusRuntime;
        if (octopusRuntime == null) return Fail(404, "no-runtime", "the octopus runtime is not switched on", 12);
        if (!octopusRuntime.HostTokenOk(HostBearer(c))) return Fail(401, "unauthorized", "missing or wrong host token", 30);
        var (text, tooLarge) = await ReadBody(c, 65536);
        JsonObject body; try { body = tooLarge ? throw new FormatException() : Messenger.ParseObject(text); } catch { return Fail(400, "usage", "the body is { \"host\": \"<machine>\", \"containers\": [{ \"name\", \"item\", \"state\", \"startedAt\" }] }", 2); }
        var host = body["host"]?.ToString().Trim() ?? "";
        if (host.Length is 0 or > 200 || body["containers"] is not JsonArray cs) return Fail(400, "usage", "host and containers are required", 2);
        var rr = await octopusRuntime.Report(host, cs, c.RequestAborted);
        return rr == null ? Fail(404, "unknown-host", "the host is not a deployment target of the runtime", 12) : Results.Text(rr.ToJsonString(), "application/json");
    }

    // A runtime worker's one-time nonce (cloud-worker-runtimes.md 2.6) is redeemed here for its w2, once. No Authorization header: the nonce is the
    // credential. Body {"nonce": "n1.<hex>"}. Every refusal is the same 401 for the caller (unknown, spent, expired); the log says which.
    async Task<IResult> Redeem(HttpContext ctx)
    {
        if (!revocationService.IsLoaded)
        {
            await Task.WhenAny(revocationService.Loaded, Task.Delay(TimeSpan.FromSeconds(20), ctx.RequestAborted));
            if (!revocationService.IsLoaded) return Results.Json(new { ok = false, code = "starting", reason = "the gateway is still reading the revoked credentials; try again", retryAfterSeconds = 5, exit = 20 }, statusCode: 503);
        }
        string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
        if (text.Length > 1000) return Results.Json(new { ok = false, code = "usage", reason = "the body is {\"nonce\": \"...\"}", exit = 2 }, statusCode: 400);
        string? nonce = null; try { nonce = (JsonNode.Parse(text) as JsonObject)?["nonce"]?.ToString(); } catch { }
        var (name, rt, expires, error) = nonces.Redeem(nonce);
        var tokenSecret = gateway.TokenSecret;
        if (error != null || tokenSecret.Length == 0)
        {
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "redeem-refused", ["code"] = error ?? "no-token-secret" }.ToJsonString());
            return Results.Json(new { ok = false, code = "unauthorized", reason = "unknown, spent or expired nonce", exit = 30 }, statusCode: 401);
        }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "redeemed", ["worker"] = name, ["kind"] = rt, ["expires"] = expires }.ToJsonString());
        return Results.Json(new { ok = true, name, runtime = rt, expires, token = Auth.WorkerToken2(tokenSecret, name, rt, expires, revocations.Of(name)), exit = 0 });
    }

    // POST /build-slot (gateway/Hosts.cs): a private-build slot of the caller's host, so the sessions of one computer do not build all at once.
    // Body { "action": "acquire" | "release", "ttlSeconds": S }. The caller is an owner's item token (the item is the token's) or a worker credential.
    // Exit 0 granted or released; exit 21 build-slot-busy with retryAfterSeconds (HTTP 429). Guard: owner mode (the filter), then the caller.
    async Task<IResult> BuildSlot(HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        if (!(who.Kind == "worker" || (who.Kind == "item" && who.Role == "owner"))) return Fail(403, "forbidden", "a build slot is asked for with an owner's item token or a worker credential", 30);
        var (text, tooLarge) = await ReadBody(ctx, 4 * 1024);
        if (tooLarge) return Fail(413, "too-large", "the request body is over 4 KB", 2);
        JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? throw new FormatException()); } catch { return Fail(400, "usage", "the body is a JSON object: { \"action\": \"acquire\" | \"release\", \"ttlSeconds\": S }", 2); }
        var action = body["action"]?.ToString() ?? "acquire";
        if (action is not ("acquire" or "release")) return Fail(400, "usage", "action is acquire or release", 2);
        double? ttl = null;
        if (body["ttlSeconds"] is JsonNode tn) { if (!double.TryParse(tn.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tv) || !double.IsFinite(tv) || tv <= 0) return Fail(400, "usage", "ttlSeconds is a positive number", 2); ttl = tv; }
        var item = who.Kind == "item" ? who.Item : (int.TryParse(body["item"]?.ToString(), out var bi) && bi > 0 ? bi : 0);
        var r = plant.BuildSlotRequest(who.Name, item, action, ttl);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["buildSlot"] = action, ["name"] = who.Name, ["item"] = item, ["ok"] = r["ok"]?.DeepClone() }.ToJsonString());
        return Results.Text(r.ToJsonString(), "application/json", Encoding.UTF8, r["ok"]?.GetValue<bool>() == true ? 200 : 429);
    }
}
