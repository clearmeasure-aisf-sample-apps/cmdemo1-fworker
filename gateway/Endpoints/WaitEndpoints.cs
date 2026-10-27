using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Triggers for one item (task C7c): a long-poll that costs no tokens while idle, for sessions that cannot hold a stream open (headless and cloud
// sessions). Its caller rules come before messaging's guard (which applies to messages=1 only), so they stay in the handler.
sealed class WaitEndpoints(GatewayOptions gateway, MessagingSettings messaging, Plant plant) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/wait", async (HttpContext c) => { return await Wait(c); });

    async Task<IResult> Wait(HttpContext ctx)
    {
        var ownerMode = gateway.OwnerMode;
        var who = ctx.Caller().Who;
        if (!int.TryParse(ctx.Request.Query["item"], out var item)) return Results.Text("{\"ok\":false,\"code\":\"usage\",\"reason\":\"item is required\",\"exit\":2}", "application/json", Encoding.UTF8, 400);
        if (who.Kind == "item" && who.Item != item) return Results.Text(JsonSerializer.Serialize(new { ok = false, code = "forbidden", reason = $"this token is for item #{who.Item} only", exit = 30 }), "application/json", Encoding.UTF8, 403);
        if (who.Kind == "worker") return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"waiting for an item's triggers needs the item token\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
        if (who.Kind == "manager") return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"a supervisor session cannot wait for an item's triggers\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
        if (who.Kind == "operator") return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"an operator session cannot wait for an item's triggers\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
        // then an allow-list (after the kinds above, which keep their own reasons): the operator token and the item's own token (a role token waits through next_event at /mcp)
        if (who.Kind is not ("admin" or "item")) return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"waiting for an item's triggers needs the item token\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
        // an owner that waits in wait-event makes no verb call for hours: its poll is its activity on the item (worker-messaging.md 3.9)
        if (ownerMode && messaging.On && who.Kind == "item") plant.NoteActivity(who.Name, item);
        if (ownerMode && who.Kind == "item" && who.Role == "owner") plant.NoteItemCall(who.Name, item, "");
        long after = long.TryParse(ctx.Request.Query["after"], out var a) ? a : 0;
        var secs = Math.Clamp(double.TryParse(ctx.Request.Query["timeout"], out var t) ? t : 60, 1, 120);
        if (ctx.Request.Query["messages"].ToString() == "1")
        {   // the item's triggers and the caller's own new messages (worker-messaging.md 3.1, 3.5): wakes on either, and only for the item token of the session
            if (GatewayGuards.MessagingOff(ownerMode, messaging) is { } off) return off;
            if (who.Kind != "item" || who.Role is not ("owner" or "inspector")) return MessagingResults.AsResult(Messenger.Plain("forbidden", "waiting for messages needs the item token of an owner or inspector session"));
            if (!(messaging.WorkerAllowed(who.Name) || (messaging.ManagerEnabled && messaging.AskerAllowed(who.Name)))) return MessagingResults.AsResult(Messenger.Plain("not-allowed", "this name is not in messaging.allowedWorkers"));
            plant.NotePoll(who.Name);
            var afterMsg = long.TryParse(ctx.Request.Query["afterMsg"], out var am) && am >= 0 ? am : 0;
            var epochQ = ctx.Request.Query["epoch"].ToString();
            // a cursor of another epoch is void (3.5): the caller's seqs started again, so nothing may be skipped
            if (epochQ != "" && epochQ != plant.Epoch) { afterMsg = 0; plant.NoteStaleEpoch(who.Name, epochQ); }
            var (evs, msgs, lastMsg, noneArrived) = await plant.WaitAny(item, after, who.Name, afterMsg, TimeSpan.FromSeconds(secs), ctx.RequestAborted);
            var changed = epochQ != "" && epochQ != plant.Epoch;
            var both = new JsonObject
            {
                ["ok"] = true, ["item"] = item, ["timeout"] = noneArrived, ["last"] = evs.Count > 0 ? evs[^1].Item1 : after,
                ["events"] = new JsonArray(evs.Select(e => JsonNode.Parse(e.Item2)).ToArray()),
                ["messages"] = new JsonArray(msgs.ToArray()), ["lastMsg"] = lastMsg == 0 ? afterMsg : lastMsg, ["epoch"] = plant.Epoch, ["epochChanged"] = changed
            };
            return Results.Text(both.ToJsonString(), "application/json");
        }
        var (events, timedOut) = await plant.Wait(item, after, TimeSpan.FromSeconds(secs), ctx.RequestAborted);
        var res = new JsonObject { ["ok"] = true, ["item"] = item, ["timeout"] = timedOut, ["last"] = events.Count > 0 ? events[^1].Item1 : after, ["events"] = new JsonArray(events.Select(e => JsonNode.Parse(e.Item2)).ToArray()) };
        return Results.Text(res.ToJsonString(), "application/json");
    }
}
