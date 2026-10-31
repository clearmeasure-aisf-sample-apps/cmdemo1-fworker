using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static HttpResultsX;

// What a rule script's exit means over HTTP, for the endpoints that run one verb and answer its output (gateway-composition.md 3 D): 0 200, 2 400,
// 12 404, 10, 11 and 40 409, 20 503, anything else 500. An endpoint whose verb has another exit of its own maps that one before calling Status.
static class RuleScriptResult
{
    public static int Status(int exit) => exit switch { 0 => 200, 2 => 400, 12 => 404, 10 or 11 or 40 => 409, 20 => 503, _ => 500 };

    // the output, or the gateway's own refusal when the rule script wrote nothing
    public static string OrNoOutput(string output, int exit) =>
        string.IsNullOrWhiteSpace(output) ? JsonSerializer.Serialize(new { ok = false, code = "no-output", reason = "the rule script produced no output", exit = exit == 0 ? 20 : exit }) : output;
}

// The operator's hand on single items and on the plant manager (task C7b): the manager's actions and mode, releasing a park early, settling a merged
// item again.
// W9.10h: where the engine acts, the unpark also tells the saga of the item (ResumeWorkItem): it was parked by the script's verb alone, so V2Ownership went on owning the item without offering it.
sealed class ItemAdminEndpoints(MessagingSettings messaging, Plant plant, Runner runner, EventHub hub, GatewayOptions gateway, SagaInputs? saga = null) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/manager/actions", (HttpContext c) => ManagerActions(c)).OwnerMode();
        routes.MapPost("/api/v1/manager/mode", (HttpContext c) => ManagerMode(c))
            .OwnerMode()
            .Admin(CallerView.Who, "only the operator sets the plant manager's mode");
        routes.MapPost("/api/v1/items/{n:int}/unpark", async (int n, HttpContext c) => { return await Unpark(n, c); }).Admin(CallerView.Who, "only the operator releases a park");
        routes.MapPost("/api/v1/items/{n:int}/resettle", async (int n, HttpContext c) => { return await Resettle(n, c); }).Admin(CallerView.Who, "only the operator settles an item again");
    }

    // The plant manager's actions of the last 24 hours, its mode and its limits (Manager.cs): the operator, and the active supervisor (a step of its budget).
    IResult ManagerActions(HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        if (who.Kind == "manager")
        {
            if (!messaging.ManagerEnabled || plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
            if (plant.SupervisorStep(false) is { Ok: false } noStep) return Results.Json(new { ok = false, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }, statusCode: 429);
        }
        else if (who.Kind != "admin") return Fail(403, "forbidden", "the plant manager's actions are read by the supervisor and the operator", 30);
        var o = plant.ManagerActionsStatus(true); o["ok"] = true; o["exit"] = 0;
        return Results.Text(o.ToJsonString(), "application/json");
    }

    // The owner's switch of the plant manager (manager.mode): act, or advise (every action of manager-action is refused advise-mode). The operator token
    // only, never the operator session or the supervisor; in memory, and an advise survives a graceful restart in the plant snapshot (an act does not:
    // after a restart manager.mode of the configuration applies again).
    IResult ManagerMode(HttpContext ctx)
    {
        var value = ctx.Request.Query["value"].ToString();
        if (value is not ("act" or "advise")) return Fail(400, "usage", "value is act or advise", 2);
        plant.SetManagerMode(value);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerMode"] = value, ["by"] = "operator" }.ToJsonString());
        return Results.Json(new { ok = true, mode = value, exit = 0 });
    }

    // Release a park early (operator token only): after the owner relaxed or reworded a criterion, a person need not wait out timing.parkHours. The rule
    // verb unpark-item removes the park from the item's state comment with the gateway's own GitHub author and journals the reason; the sweep that
    // this call requests then offers the item again in its current column. Body: {"reason": "..."} (required, at most 500 characters).
    async Task<IResult> Unpark(int n, HttpContext ctx)
    {
        var ownerMode = gateway.OwnerMode;
        string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
        JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? new JsonObject()); } catch { return Fail(400, "bad-json", "the body is JSON: {\"reason\": \"...\"}", 2); }
        var reason = body["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs.Trim() : "";
        if (reason.Length == 0 || reason.Length > 500) return Fail(400, "usage", "a reason of 1 to 500 characters is required", 2);
        var args = new JsonObject { ["item"] = n, ["reason"] = reason, ["holder"] = "operator", ["by"] = "the operator" };
        var (code, output) = await runner.RunAsync("unpark-item", args.ToJsonString(), TimeSpan.FromSeconds(120));
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"unpark-item\",\"item\":\"{n}\",\"caller\":\"admin\",\"exit\":{code}}}");
        output = RuleScriptResult.OrNoOutput(output, code);
        if (code == 0)
        {   // no live claim: the session that held the item before the park is not its owner any more; the plant forgets that assignment (as for a
            // release), so the sweep this call requests offers the item afresh instead of first waiting to find that session gone
            try { if (ownerMode && JsonNode.Parse(output)?["reoffer"] is JsonValue ro && ro.TryGetValue<bool>(out var reoffer) && reoffer) plant.OnVerb("release", new JsonObject { ["item"] = n }, ctx.Caller().Who, new JsonObject()); }
            catch (Exception e) { Console.Error.WriteLine($"plant hook unpark-item: {e.Message}"); }
            if (ownerMode) plant.ClearParks(new[] { n });   // issue 82: the digest no longer reports a park that was released
            // the saga takes the step up again (budgets of the phase start afresh) and the next read of its table offers the item again; the sweep this call requests reads it
            if (saga is not null) await saga.Resumed(n, SagaKeys.ResumeKey(output));
            hub.RequestSweep(n);
        }
        else if (saga is not null && IsNotParked(output) && await saga.ResumeIfParked(n))
        {   // v1 has no park, the saga has one (its budget parked the item: a restart zeroes the runtime manager's counters but not the saga's, and a fix-forward round clears the manager's launches only): without this the item would stay
            // stranded, V2Ownership offers none that the saga parked. The answer says so.
            if (ownerMode) plant.ClearParks(new[] { n });
            hub.RequestSweep(n);
            output = new JsonObject { ["ok"] = true, ["id"] = n, ["unparked"] = true, ["exit"] = 0, ["saga"] = new JsonObject { ["resumed"] = true, ["note"] = "the park was the saga's only; v1 had none" } }.ToJsonString();
            code = 0;
        }
        return Results.Text(output, "application/json", Encoding.UTF8, RuleScriptResult.Status(code));
    }

    static bool IsNotParked(string output)
    {
        try { return JsonNode.Parse(output)?["code"]?.ToString() == "not-parked"; }
        catch (JsonException) { return false; }
    }

    // Settle a merged item again (operator token only, issue 114): for an item an older gateway finished or stranded without a release, after a person
    // reopened it and moved its card. The rule verb resettle-item sets the wait from the merge record and journals the reason; never a hand-off.
    async Task<IResult> Resettle(int n, HttpContext ctx)
    {
        string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
        JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? new JsonObject()); } catch { return Fail(400, "bad-json", "the body is JSON: {\"reason\": \"...\"}", 2); }
        var reason = body["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs.Trim() : "";
        if (reason.Length == 0 || reason.Length > 500) return Fail(400, "usage", "a reason of 1 to 500 characters is required", 2);
        var (code, output) = await runner.RunAsync("resettle-item", new JsonObject { ["item"] = n, ["reason"] = reason, ["holder"] = "operator", ["by"] = "the operator" }.ToJsonString(), TimeSpan.FromSeconds(120));
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"resettle-item\",\"item\":\"{n}\",\"caller\":\"admin\",\"exit\":{code}}}");
        output = RuleScriptResult.OrNoOutput(output, code);
        if (code == 0) hub.RequestSweep();
        return Results.Text(output, "application/json", Encoding.UTF8, RuleScriptResult.Status(code));
    }
}
