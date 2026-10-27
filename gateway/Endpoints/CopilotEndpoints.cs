using System.Text;
using System.Text.Json.Nodes;
using static HttpResultsX;

// The Copilot cloud agent's way in (task C7b, docs/design/copilot-cloud-agent.md part 3): the exchange of the bootstrap token and a task's one-time
// code for that task's credential, the operator's minting of the bootstrap token, and the stdio MCP proxy the agent runs. The bootstrap token and the
// codes are credentials (a protected area, CLAUDE.md): the exchange checks its own, the middleware lets the path through.
sealed class CopilotEndpoints(GatewayOptions gateway, CopilotCloudSettings copilotSettings, RuntimeRegistry runtimes) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/copilot/exchange", async (HttpContext c) => { return await Exchange(c); });
        routes.MapPost("/api/v1/copilot/bootstrap-token", (HttpContext c) => BootstrapToken()).Admin(CallerView.Who, "only the operator mints the Copilot bootstrap token");
        routes.MapGet("/fworker-mcp.mjs", () => McpProxy());
    }

    // The bootstrap token of the work-orders repository's Copilot MCP configuration (b1, api.copilot.bootstrapGeneration) and a task's one-time code
    // give that task's r1, once. Neither alone grants anything: the b1 authenticates no other endpoint, and a code is worth nothing without it.
    // Answers the same 401 for a bad token and a bad code.
    async Task<IResult> Exchange(HttpContext ctx)
    {
        if (!Auth.VerifyCopilotBootstrap(gateway.TokenSecret, ctx.Request.Headers.Authorization.ToString(), copilotSettings.BootstrapGeneration))
            return Results.Json(new { ok = false, code = "unauthorized", reason = "missing or void bootstrap token", exit = 30 }, statusCode: 401);
        var (text, tooLarge) = await ReadBody(ctx, 1000);
        string? code = null; if (!tooLarge) try { code = (JsonNode.Parse(text) as JsonObject)?["code"]?.ToString(); } catch { }
        if (runtimes.CopilotCodes.Redeem(code) is not { } got)
        {
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "exchange-refused" }.ToJsonString());
            return Results.Json(new { ok = false, code = "unauthorized", reason = "unknown, spent or expired code", exit = 30 }, statusCode: 401);
        }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "exchanged", ["worker"] = got.Name }.ToJsonString());
        return Results.Json(new { ok = true, name = got.Name, token = got.Token, exit = 0 });
    }

    // The bootstrap token itself (operator only), for the repository's Agents secret COPILOT_MCP_FWORKER_BOOTSTRAP_TOKEN; never stored here.
    IResult BootstrapToken()
    {
        if (!gateway.OwnerMode || gateway.TokenSecret.Length == 0) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
        return Results.Json(new { ok = true, generation = copilotSettings.BootstrapGeneration, token = Auth.CopilotBootstrapToken(gateway.TokenSecret, copilotSettings.BootstrapGeneration), exit = 0 });
    }

    // fworker-mcp (gateway/Mcp/fworker-mcp.mjs): the stdio MCP proxy the Copilot agent runs, served anonymously (it holds no secret; the repository is public)
    static IResult McpProxy()
    {
        using var st = typeof(CopilotCodes).Assembly.GetManifestResourceStream("fworker-mcp.mjs");
        if (st == null) return Results.NotFound();
        using var rd = new StreamReader(st);
        return Results.Text(rd.ReadToEnd(), "text/javascript", Encoding.UTF8);
    }
}
