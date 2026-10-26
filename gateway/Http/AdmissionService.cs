// The per-principal work of an admitted /api request (task C6, gateway-composition.md 3 I): GatewayAuthMiddleware runs it, and so does an MCP
// tool's in-process call of an endpoint (gateway/Mcp/InProcess.cs, which is handed Admit), so a tool counts exactly as the HTTP call it stands for.
// It never refuses: the guards are the middleware's and the endpoints' own.
sealed class AdmissionService(GatewayOptions gateway, MessagingSettings messaging, Plant plant)
{
    readonly bool ownerMode = gateway.OwnerMode;

    public async Task Admit(Principal p, string path, Func<Task> next)
    {
        // presence (worker-messaging.md 2.2, 4.6): every request of a session's own credential is proof of life; a stream write is not. The
        // credentials that are the session itself are listed: a worker, the supervisor, and the item tokens of an owner or an inspector. A QC item
        // token is pull request code's credential and never stamps presence, nor does any role this list does not name.
        // A supervisor credential stamps only while its name holds the slot (4.6: exactly one is active; registration stamps by itself), so a
        // replaced or never-registered supervisor name is never heard.
        if (messaging.Enabled && ownerMode && (p.Kind == "worker" || (p.Kind == "manager" && plant.ActiveSupervisor().Name == p.Name) || (p.Kind == "item" && p.Role is "owner" or "inspector"))) plant.Touch(p.Name);
        // the operator session (Operator.cs): heard while it holds the slot; and, for its quiet-plant check, every verb call of an item token is
        // counted from its arrival to its answer (the prefix ignores case, as routing does: /API/v1/verbs/x reaches the same verb, issue 409)
        if (p.Kind == "operator") plant.OperatorHeard(p.Name);
        if (p.Kind == "keeper") plant.KeeperHeard(p.Name);
        if (messaging.OperatorEnabled && p.Kind == "item" && path.StartsWith("/api/v1/verbs/", StringComparison.OrdinalIgnoreCase))
        {
            var flightVerb = path["/api/v1/verbs/".Length..];
            var flight = plant.VerbStarted(p.Name, p.Item, flightVerb.All(ch => char.IsAsciiLetterLower(ch) || ch == '-') ? flightVerb : "");
            try { await next(); } finally { plant.VerbEnded(flight); }
            return;
        }
        await next();
    }
}
