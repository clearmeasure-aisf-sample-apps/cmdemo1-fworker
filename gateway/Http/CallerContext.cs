using System.Text.Json.Nodes;

// The caller of one request (task C6, gateway-composition.md 3 C and D, request helpers): who it is, whom it acts as, and the link to its AI session.
interface ICallerContext
{
    // the principal GatewayAuthMiddleware authenticated, or InProcess was given
    Principal Who { get; }
    bool OperatorActive { get; }
    Principal Admitted { get; }
    string By { get; }
    JsonObject? SessionLinkOf(Principal p);
}

// Scoped, bound to the HttpContext of its scope: over HTTP the request's scope, and for an MCP tool's in-process call the scope InProcess makes for
// that call (its RequestServices). Of(c) resolves it from c's scope and binds it to c, so a handler in either path gets its own caller. Every value
// is read when asked, never cached: the operator slot and messaging can change while a request runs, as before.
sealed class CallerContext(GatewayOptions gateway, MessagingSettings messaging, Plant plant, PublicBase publicBase, TranscriptStore transcripts, IServiceProvider services) : ICallerContext
{
    HttpContext? context;

    public static CallerContext Of(HttpContext c) => c.RequestServices.GetRequiredService<CallerContext>().Bind(c);

    CallerContext Bind(HttpContext c)
    {
        if (context == null) context = c;
        else if (!ReferenceEquals(context, c)) throw new InvalidOperationException("a caller context serves the one request of its scope");
        return this;
    }

    HttpContext Context => context ?? throw new InvalidOperationException("the caller context is not bound to a request (CallerContext.Of)");

    public Principal Who => (Principal)Context.Items["principal"]!;

    // The operator SESSION credential (o1., Auth.cs) is admitted at exactly the endpoints that call Admitted or OperatorActive, and nowhere else: there it
    // acts as the operator does (its notes are operator notes). It must hold the operator slot, and messaging must be switched on. Since 2026-10-14 that
    // includes a worker's capacity, drain and resume (POST /workers/{name}/...) and the read-only cycle-time metrics. Everywhere else its kind is
    // "operator", which no endpoint accepts: it cannot mint or revoke a credential, switch messaging, read the dashboard JSON or call a verb.
    public bool OperatorActive => Who is { Kind: "operator" } w && gateway.OwnerMode && messaging.OperatorEnabled && messaging.Enabled && plant.ActiveOperator() == w.Name;

    // The principal it acts as carries the session's NAME: the admin token's has none. What the session sends is stamped with that name and the kind
    // "operator-session" (Messaging.cs), never as the person's "operator"; log lines name it too (By).
    public Principal Admitted => OperatorActive ? new Principal("admin", Who.Name) : Who;

    public string By => Who is { Kind: "operator" } w ? w.Name : "operator";

    // {name, url} of the AI session behind a session credential, null for the operator, the gateway itself and a click. octopus and cae-job: the
    // gateway's transcript page; cursor-cloud and copilot-cloud: the agent's or task's page; the on-demand manager: its Copilot session; a connected
    // session: the transcript page when this gateway holds one, else the name only (url "")
    public JsonObject? SessionLinkOf(Principal p)
    {
        if (p.Kind is not ("item" or "worker" or "role" or "manager" or "keeper") || !TranscriptStore.ValidName(p.Name)) return null;
        var kind = plant.RuntimeKindOf(p.Name); if (kind == "") kind = p.Runtime;
        var lb = publicBase.LinkBase();
        var page = lb != "" ? $"{lb}/sessions/{Uri.EscapeDataString(p.Name)}" : "";
        // the on-demand manager is registered only where it is enabled (AddGatewayBackground); elsewhere null, as Program.cs's local was
        var managerCopilot = services.GetService<ManagerCopilot>();
        var url = kind switch
        {
            "octopus" or "cae-job" => page,
            "cursor-cloud" or "copilot-cloud" => plant.RuntimeUrlOf(p.Name),
            _ => p.Kind == "manager" && managerCopilot?.SessionUrl(p.Name) is { Length: > 0 } mu ? mu : transcripts.Has(p.Name) ? page : ""
        };
        if (url != "" && !(url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal)) || url.Contains('?') || url.Contains('#') || url.Any(char.IsWhiteSpace)) url = "";
        return new JsonObject { ["name"] = p.Name, ["url"] = url };
    }
}
