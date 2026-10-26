using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static HttpResultsX;

// The operator's administration of worker names and the plant's switches (task C7b): the gateway's own back-merges, the promotion request, a name's
// credential and its revoke, a runtime's health, and a worker's capacity, messaging mode, drain and resume. Credentials and revoke are a protected
// area (CLAUDE.md): the refusals and their order are pinned by tests/Gateway.Tests (caller-matrix.json, c7b-endpoints.json).
// mcpSessions: the worker sessions at /mcp, made by Program.cs after the routes are mapped (GatewayComposition.McpSessions), read at the revoke.
sealed class WorkerAdminEndpoints(GatewayOptions gateway, MessagingSettings messaging, KeeperSettings keeper, Plant plant, BaseWatch baseWatch,
    Revocations revocations, RevocationService revocationService, RuntimeRegistry runtimes, Func<WorkerSessions?> mcpSessions) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/base-update", (HttpContext c) => BaseUpdate(c)).Admin(CallerView.Who);
        routes.MapPost("/api/v1/release-queue/promotion-request", async (HttpContext c) => { return await PromotionRequest(c); })
            .OwnerMode()
            .Admin(CallerView.Who, "only the operator asks for a promotion");
        routes.MapPost("/api/v1/workers/{name}/credential", (string name, HttpContext c) => Credential(name, c)).Admin(CallerView.Who);
        routes.MapPost("/api/v1/workers/{name}/revoke", async (string name, HttpContext c) => { return await Revoke(name); })
            .Admin(CallerView.Who, "only the operator revokes a name's credentials");
        routes.MapPost("/api/v1/runtimes/{kind}/reset", (string kind, HttpContext c) => ResetRuntime(kind)).Admin(CallerView.Who, "only the operator resets a runtime");
        routes.MapPost("/api/v1/workers/{name}/{action}", (string name, string action, HttpContext c) => WorkerAction(name, action, c));
    }

    // the switch of the gateway's own back-merges (baseUpdate.autoUpdateBranch) at run time, operator token only; until the next restart
    IResult BaseUpdate(HttpContext c)
    {
        if (bool.TryParse(c.Request.Query["autoUpdateBranch"].ToString(), out var on)) baseWatch.AutoUpdateBranch = on;
        return Results.Text(baseWatch.Status().ToJsonString(), "application/json");
    }

    // POST /release-queue/promotion-request { "release": "1.2.3" } (enhancement 9): the dashboard's button. Operator token only. It records the request
    // and announces it on the operator stream; it never promotes: uat and prod belong to the owner and the cmdemo1 session.
    async Task<IResult> PromotionRequest(HttpContext c)
    {
        var (text, tooLarge) = await ReadBody(c, 4096);
        JsonObject body; try { body = tooLarge || string.IsNullOrWhiteSpace(text) ? new JsonObject() : Messenger.ParseObject(text); } catch { return Fail(400, "usage", "the body is { \"release\": \"<version>\" } or empty", 2); }
        var (status, res) = plant.RequestPromotion(body["release"]?.ToString(), "operator");
        if (status == 200 && res["already"]?.GetValue<bool>() == false)
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "promotion-requested", ["release"] = res["release"]!.DeepClone(), ["environment"] = "uat", ["by"] = c.Caller().By }.ToJsonString());
        return Results.Text(res.ToJsonString(), "application/json", Encoding.UTF8, status);
    }

    // A worker credential is issued here once per session name; the operator pastes it into the connect prompt. It is never stored by the gateway.
    // role=manager issues the supervisor's credential (worker-messaging.md 4.6): only while manager.enabled, and only for a name in manager.allowedNames.
    IResult Credential(string name, HttpContext c)
    {
        var tokenSecret = gateway.TokenSecret;
        var role = c.Request.Query["role"].ToString() is { Length: > 0 } r ? r : "worker";
        if (!Auth.ValidName(name) || tokenSecret.Length == 0 || role is not ("worker" or "manager" or "operator" or "keeper"))
            return Results.Json(new { ok = false, code = "usage", reason = "a valid name, a role of worker, manager or operator and FACTORY_TOKEN_SECRET are required", exit = 2 }, statusCode: 400);
        if (role == "keeper")
        {   // the backlog keeper's credential (Keeper.cs): only while keeper.enabled, and only for a name in keeper.allowedNames
            if (!keeper.Enabled) return Results.Json(new { ok = false, code = "keeper-disabled", reason = "the backlog keeper is not enabled on this gateway (keeper.enabled)", exit = 30 }, statusCode: 403);
            if (!keeper.Names.Contains(name)) return Results.Json(new { ok = false, code = "name-not-allowed", reason = "the name is not in keeper.allowedNames", exit = 10 }, statusCode: 409);
            return Results.Json(new { ok = true, name, token = Auth.KeeperToken(tokenSecret, name, keeper.TokenGeneration, revocations.Of(name)) });
        }
        if (role == "operator")
        {   // the factory operator session's credential: only while operator.enabled, and only for a name in operator.allowedNames
            if (!messaging.OperatorEnabled) return Results.Json(new { ok = false, code = "operator-disabled", reason = "the operator session is not enabled on this gateway (operator.enabled)", exit = 30 }, statusCode: 403);
            if (!messaging.OperatorNames.Contains(name)) return Results.Json(new { ok = false, code = "name-not-allowed", reason = "the name is not in operator.allowedNames", exit = 10 }, statusCode: 409);
            return Results.Json(new { ok = true, name, token = Auth.OperatorToken(tokenSecret, name, messaging.OperatorTokenGeneration, revocations.Of(name)) });
        }
        if (role == "manager")
        {
            if (!messaging.ManagerEnabled) return Results.Json(new { ok = false, code = "manager-disabled", reason = "the supervisor is not enabled on this gateway (manager.enabled)", exit = 30 }, statusCode: 403);
            if (!messaging.ManagerNames.Contains(name)) return Results.Json(new { ok = false, code = "name-not-allowed", reason = "the name is not in manager.allowedNames", exit = 10 }, statusCode: 409);
            return Results.Json(new { ok = true, name, token = Auth.ManagerToken(tokenSecret, name, messaging.TokenGeneration, revocations.Of(name)) });
        }
        if (messaging.Reserved.Contains(name)) return Results.Json(new { ok = false, code = "name-reserved", reason = "the name is reserved for the operator, the gateway or the supervisor", exit = 10 }, statusCode: 409);
        return Results.Json(new { ok = true, name, token = Auth.WorkerToken(tokenSecret, name, revocations.Of(name)) });
    }

    // Revoke (operator only): every credential minted so far for this name (worker, supervisor, item tokens) is refused from now on, on every endpoint;
    // its open stream ends; a credential minted afterwards works. The new generation is written to GitHub before it takes effect here, so it holds
    // across a restart. A name that never connected is revoked all the same (a credential may have been minted for it).
    async Task<IResult> Revoke(string name)
    {
        if (!gateway.OwnerMode || gateway.TokenSecret.Length == 0) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
        if (!Auth.ValidName(name)) return Fail(400, "usage", "the name may use letters, digits, - and _ only (40 at most)", 2);
        if (revocationService.Off) return Fail(403, "revocations-off", "this gateway runs with FACTORY_TEST_SKIP_REVOCATIONS: it cannot revoke", 30);
        if (revocationService.SkipReason != "") return Fail(409, "revocations-unavailable", $"revocations are not read on this gateway ({revocationService.SkipReason}): it cannot revoke", 30);
        if (!revocationService.IsLoaded) return Results.Json(new { ok = false, code = "starting", reason = "the gateway is still reading the revoked credentials; try again", retryAfterSeconds = 5, exit = 20 }, statusCode: 503);
        var (rok, gen, closed) = await revocationService.RevokeAsync(name, "operator");
        if (!rok) return Results.Json(new { ok = false, code = "not-recorded", reason = "the revocation could not be written to GitHub, so nothing was revoked; try again", retryAfterSeconds = 15, exit = 20 }, statusCode: 503);
        // the execution of a runtime worker is stopped with its name (cloud-worker-runtimes.md 5)
        var runtimeManager = runtimes.CaeJob; var cursorManager = runtimes.Cursor; var copilotManager = runtimes.Copilot; var octopusManager = runtimes.Octopus;
        if (runtimeManager != null && runtimeManager.IsRuntimeWorker(name)) _ = Task.Run(() => runtimeManager.Deprovision(name, "revoked"));
        if (cursorManager != null && cursorManager.IsRuntimeWorker(name)) _ = Task.Run(() => cursorManager.Deprovision(name, "revoked"));
        if (copilotManager != null && copilotManager.IsRuntimeWorker(name)) _ = Task.Run(() => copilotManager.Deprovision(name, "revoked"));
        if (octopusManager != null && octopusManager.IsRuntimeWorker(name)) _ = Task.Run(() => octopusManager.Deprovision(name, "revoked"));
        _ = Task.Run(() => mcpSessions()?.End(name, "revoked") ?? Task.CompletedTask);
        return Results.Json(new { ok = true, name, generation = gen, streamClosed = closed, exit = 0 });
    }

    // The operator marks a runtime healthy again after it was marked unhealthy (cloud-worker-runtimes.md 2.11): operator token only.
    IResult ResetRuntime(string kind)
    {
        var rk = runtimes.ByKind(kind);
        if (rk == null) return Fail(404, "no-runtime", "no such runtime is switched on", 12);
        rk.ResetHealth();
        return Results.Json(new { ok = true, runtime = kind, exit = 0 });
    }

    // A worker's capacity, messaging mode, drain and resume: the operator for any name, a worker for itself (drain and resume only)
    IResult WorkerAction(string name, string action, HttpContext ctx)
    {
        var caller = ctx.Caller();
        var who = caller.Who;
        // the operator session acts here as the operator does for exactly four actions: a session's messaging mode, its capacity, drain and resume
        // (owner decision 2026-10-14). It never revokes or mints here (those are routes of their own, admin token only).
        var opMode = action is "messaging" or "capacity" or "drain" or "resume" && caller.OperatorActive;
        var asOperator = who.Kind == "admin" || opMode;
        if (!(asOperator || (who.Kind == "worker" && who.Name == name))) return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"a worker may only change itself\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
        if (action == "capacity")
        {   // the operator (or the active operator session): how many items this worker name may own at once
            if (!asOperator) return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"only the operator sets a worker's capacity\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
            if (opMode && (!Auth.ValidName(name) || messaging.ManagerNames.Contains(name) || messaging.OperatorNames.Contains(name))) return Fail(400, "usage", "the name is a worker session's", 2);
            // a name that has not registered since the gateway started gets no override from the session (the overrides are kept per name, so they
            // must not grow without bound)
            if (opMode && !plant.IsWorker(name)) return Results.Text("{\"ok\":false,\"code\":\"not-found\",\"reason\":\"no such worker\",\"exit\":12}", "application/json", Encoding.UTF8, 404);
            if (!int.TryParse(ctx.Request.Query["value"], out var cv) || cv < 1 || cv > 20) return Results.Text("{\"ok\":false,\"code\":\"usage\",\"reason\":\"value must be 1 to 20\",\"exit\":2}", "application/json", Encoding.UTF8, 400);
            plant.SetCapacity(name, cv);
            if (opMode) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["worker"] = "capacity", ["name"] = name, ["capacity"] = Math.Clamp(cv, 1, 20), ["by"] = caller.By }.ToJsonString());
            return Results.Text(JsonSerializer.Serialize(new { ok = true, name, capacity = Math.Clamp(cv, 1, 20) }), "application/json");
        }
        if (action == "messaging" && messaging.Configured && gateway.OwnerMode)
        {   // operator only (worker-messaging.md 4.5): a worker acting on its own name is refused here, so a muted session cannot set itself back to on
            if (who.Kind != "admin" && !opMode) return Fail(403, "forbidden", "only the operator sets a session's messaging mode", 30);
            var mode = ctx.Request.Query["value"].ToString();
            if (!Auth.ValidName(name) || messaging.ManagerNames.Contains(name) || messaging.OperatorNames.Contains(name) || !plant.SetMessagingMode(name, mode)) return Fail(400, "usage", "the name is a worker session's and value is on, mute, inbound-off or off", 2);
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "mode", ["name"] = name, ["mode"] = mode, ["by"] = caller.By }.ToJsonString());
            return Results.Json(new { ok = true, name, messaging = mode, exit = 0 });
        }
        if (action is not ("drain" or "resume")) return Results.NotFound();
        if (opMode && (!Auth.ValidName(name) || messaging.ManagerNames.Contains(name) || messaging.OperatorNames.Contains(name))) return Fail(400, "usage", "the name is a worker session's", 2);
        // the operator session's drain is the operator's: it belongs to the name and holds across its reconnects and new sessions (issue 80)
        if (!plant.SetDraining(name, action == "drain", asOperator)) return Results.Text("{\"ok\":false,\"code\":\"not-found\",\"reason\":\"no such worker\",\"exit\":12}", "application/json", Encoding.UTF8, 404);
        if (opMode) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["worker"] = action, ["name"] = name, ["by"] = caller.By }.ToJsonString());
        return Results.Text(JsonSerializer.Serialize(new { ok = true, name, draining = plant.IsDraining(name) }), "application/json");
    }
}
