using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery;

// W5.2: the report of the shadow run of the delivery train (docs/design/fworker-wolverine-implementation.md W5). GET /api/v1/delivery-shadow?limit=50&outcome=differs lists the newest stored
// comparisons of the train's decisions with v1's promotion pass (outcome: match, v2-only, v1-only or differs), the summary of all of them, the gate of the shadow run (met when
// 3 releases were compared by both sides and nothing live is left unexplained; summary.live and gate say what was compared, what differs, what only one side judged and which declared differences explain
// a one-sided row; the hours are information) and the commands the train would have sent (recorded, never executed). The operator's credential only; 404 where the gateway does not run the shadow.
// The same counts are in /api/v1/status as "deliveryShadow". Read only: it reads the Wolverine database and changes nothing.
sealed class AdminDeliveryShadowEndpoints(IServiceProvider services) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/delivery-shadow", async (HttpContext c) => { return await Report(c); }).Admin(CallerView.Who, "only the operator reads the delivery shadow report");

    async Task<IResult> Report(HttpContext ctx)
    {
        if (services.GetService<DeliveryShadowHolder>()?.Shadow is not { } shadow)
            return Results.Json(new { ok = false, code = "no-delivery-shadow", reason = "the delivery train does not run in shadow in this gateway", exit = 12 }, statusCode: 404);
        var outcome = ctx.Request.Query["outcome"].ToString();
        if (outcome.Length > 0 && !ShadowOutcomes.All.Contains(outcome))
            return Results.Json(new { ok = false, code = "usage", reason = "outcome is one of " + string.Join(", ", ShadowOutcomes.All), exit = 2 }, statusCode: 400);
        var limit = int.TryParse(ctx.Request.Query["limit"], out var n) ? Math.Clamp(n, 1, 500) : 50;
        try { return Results.Text((await shadow.Report(limit, outcome.Length > 0 ? outcome : null, ctx.RequestAborted)).ToJsonString(), "application/json"); }
        catch (Exception e) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { ok = false, code = "report-failed", reason = "the comparisons could not be read: " + e.GetType().Name, exit = 20 }, statusCode: 503);
        }
    }
}
