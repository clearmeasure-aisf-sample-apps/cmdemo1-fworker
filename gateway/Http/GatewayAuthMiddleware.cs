// The gateway's authentication (task C6, gateway-composition.md 3 I), in this order: the port split, the replaced process, the bypasses (the nonce
// redemption and Copilot exchange, the Octopus runbook's calls), authentication with the wait for the revoked credentials, the 401, the base for
// links (the admin), the /mcp role gate, then admission (AdmissionService). Behaviour as it was inline in Program.cs; tests/Gateway.Tests pins it
// (the route-and-auth table, CallerMatrixTests, AuthMiddlewareOrderTests, RevocationWaitPinTests, PublicBasePinTests, AdmissionPinTests).
// It is middleware, so an MCP tool's in-process call (gateway/Mcp/InProcess.cs) never passes it: such a call is authenticated at /mcp and admitted
// by InProcess through the same AdmissionService. A guard an in-process call must meet therefore belongs in an endpoint filter, never here.
sealed class GatewayAuthMiddleware(RequestDelegate next, GatewayOptions gateway, MessagingSettings messaging, GatewayLeader leader, Revocations revocations,
    RevocationService revocationService, PublicBase publicBase, AdmissionService admission)
{
    // Container mode (FACTORY_SINGLE_PORT): one listener for the API and the webhook (GatewayHostingExtensions); the routes are told apart by path
    readonly int apiPort = gateway.SinglePort > 0 ? gateway.SinglePort : gateway.ApiPort;
    readonly int hookPort = gateway.SinglePort > 0 ? gateway.SinglePort : gateway.HookPort;
    readonly string apiToken = gateway.ApiToken;
    readonly string tokenSecret = gateway.TokenSecret;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var port = ctx.Connection.LocalPort;
        var path = ctx.Request.Path.Value ?? "";
        if (port == hookPort && hookPort != apiPort && !(path == "/webhooks/github" && ctx.Request.Method == "POST")) { ctx.Response.StatusCode = 404; return; }
        // a process that a newer one superseded (Leader.cs) serves nothing any more but its status to the operator: calls and new streams are answered
        // 503 gateway-replaced, so the client tries again and reaches the new process; a webhook too (the new process's sweep catches up)
        if (leader.Replaced && (path == "/webhooks/github" || path.StartsWith("/qc/") || ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) || (path.StartsWith("/api/") && !(path is "/api/v1/status" or "/api/v1/dashboard" && Auth.Authenticate(ctx.Request.Headers.Authorization.ToString(), apiToken, tokenSecret, messaging, revocations)?.Kind == "admin"))))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "gateway-replaced", reason = "this gateway process was replaced by a newer one; try again", retryAfterSeconds = 5, exit = 20 });
            return;
        }
        // the redemption of a runtime worker's nonce carries no credential: the nonce is the credential, checked by the endpoint itself
        if (port == apiPort && path is "/api/v1/runtime/redeem" or "/api/v1/copilot/exchange") { publicBase.NoteBase(ctx); await next(ctx); return; }
        // the Octopus reconcile runbook's calls carry the host token, checked by the endpoints themselves (never a gateway credential)
        if (port == apiPort && path is "/api/v1/runtime/octopus/desired" or "/api/v1/runtime/octopus/report") { await next(ctx); return; }
        var isMcp = port == apiPort && ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
        if (port == apiPort && (path.StartsWith("/api/") || isMcp))
        {
            var p = Auth.Authenticate(ctx.Request.Headers.Authorization.ToString(), apiToken, tokenSecret, messaging, revocations);
            if (p?.Kind != "admin" && !revocationService.IsLoaded)
            {
                await Task.WhenAny(revocationService.Loaded, Task.Delay(TimeSpan.FromSeconds(20), ctx.RequestAborted));
                if (!revocationService.IsLoaded) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "starting", reason = "the gateway is still reading the revoked credentials; try again", retryAfterSeconds = 5, exit = 20 }); return; }
                p = Auth.Authenticate(ctx.Request.Headers.Authorization.ToString(), apiToken, tokenSecret, messaging, revocations);
            }
            if (p == null) { await HttpResultsX.Refuse(ctx, 401, "unauthorized", "missing, malformed, forged or expired credential"); return; }
            ctx.Items["principal"] = p;
            if (p.Kind == "admin") publicBase.NoteBase(ctx);
            // /mcp (gateway/Mcp/): a credential whose kind plays no role there is refused before the MCP server reads the request; the tools admit
            // themselves (AdmissionService, through InProcess) when they call their endpoint
            if (isMcp)
            {
                if (RoleTools.RoleOf(p) == "") { await HttpResultsX.Refuse(ctx, 403, "forbidden", "this credential plays no role at /mcp"); return; }
                ctx.User = new System.Security.Claims.ClaimsPrincipal(new GatewayIdentity(p));
                await next(ctx); return;
            }
            await admission.Admit(p, path, () => next(ctx));
            return;
        }
        await next(ctx);
    }
}
