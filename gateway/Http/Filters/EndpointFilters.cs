// The cross-cutting guards of the gateway's endpoints as endpoint filters (task C7a, gateway-composition.md 2 rule 4 and 3 C and D).
//
// Why filters and nothing else: an MCP tool calls an endpoint in process (gateway/Mcp/InProcess.cs), which runs the endpoint's RequestDelegate directly.
// That delegate contains the endpoint's filters, so a guard written as a filter holds for an HTTP caller and for a tool alike. Middleware
// (GatewayAuthMiddleware) and ASP.NET authorization are never passed on that path, so a guard there would be skipped by every tool call.
//
// How to use them (C7b to C7d copy this): add them to the RouteHandlerBuilder a module's Map gets back, in the order the handler checked them before
// it moved. Filters run in the order they are added, the first added runs first, and the first that refuses answers; the handler runs only when every
// filter let the request through. An endpoint-specific rule that has to come before a shared guard is a filter of its module (MetricsEndpoints'
// MetricsAccessFilter before SupervisorBudget), never a reordering of the shared guard.
//
//   routes.MapGet("/api/v1/operator/release-check", (HttpContext c) => Check())
//       .OperatorOn()                                          // 403 operator-disabled, then not-owner-mode, messaging-disabled
//       .Admin(CallerView.Admitted, "this check belongs ...")  // 403 forbidden for any caller but the admin and the active operator session
//       .AddEndpointFilter<ReleasesLoadedFilter>();            // the module's own last guard, made from the container
//
// The filters read the settings and the caller at request time: messaging's Enabled and the operator slot change while the gateway runs.

// Which principal a guard looks at (gateway/Http/CallerContext.cs): Who, the credential as authenticated (the operator session is "operator", which
// no admin guard accepts), or Admitted, the principal it acts as (the active operator session counts as the admin).
enum CallerView { Who, Admitted }

static class EndpointFilters
{
    /// <summary>403 not-owner-mode unless the gateway runs in owner mode.</summary>
    public static RouteHandlerBuilder OwnerMode(this RouteHandlerBuilder b) => b.AddEndpointFilter<OwnerModeFilter>();

    /// <summary>Owner mode, then 403 messaging-disabled while peer messaging is switched off (the guard of every messaging endpoint).</summary>
    public static RouteHandlerBuilder MessagingOn(this RouteHandlerBuilder b) => b.AddEndpointFilter<MessagingOnFilter>();

    /// <summary>MessagingOn, then 403 manager-disabled unless the supervisor is enabled (manager.enabled).</summary>
    public static RouteHandlerBuilder SupervisorOn(this RouteHandlerBuilder b) => b.AddEndpointFilter<SupervisorOnFilter>();

    /// <summary>403 operator-disabled unless the operator session is enabled (operator.enabled), then MessagingOn.</summary>
    public static RouteHandlerBuilder OperatorOn(this RouteHandlerBuilder b) => b.AddEndpointFilter<OperatorOnFilter>();

    /// <summary>
    /// Only the admin, as <paramref name="view"/> sees the caller. Without <paramref name="reason"/> the refusal is a bare 403 (no body), as the
    /// read endpoints have always answered; with it, 403 {"ok":false,"code":"forbidden","reason":...,"exit":30}.
    /// </summary>
    public static RouteHandlerBuilder Admin(this RouteHandlerBuilder b, CallerView view, string? reason = null) => b.AddEndpointFilter(new AdminFilter(view, reason));

    /// <summary>
    /// The supervisor's hourly step budget (worker-messaging.md 3.9): in owner mode a supervisor credential's call is one step, refused 429
    /// step-budget once this hour's budget is spent. Any other caller passes. Add it after the endpoint's own caller rules: a refused call spends no step.
    /// </summary>
    public static RouteHandlerBuilder SupervisorBudget(this RouteHandlerBuilder b) => b.AddEndpointFilter<SupervisorBudgetFilter>();
}

sealed class OwnerModeFilter(GatewayOptions gateway) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        GatewayGuards.NotOwnerMode(gateway.OwnerMode) ?? await next(context);
}

sealed class MessagingOnFilter(GatewayOptions gateway, MessagingSettings messaging) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        GatewayGuards.MessagingOff(gateway.OwnerMode, messaging) ?? await next(context);
}

sealed class SupervisorOnFilter(GatewayOptions gateway, MessagingSettings messaging) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        GatewayGuards.SupervisorOff(gateway.OwnerMode, messaging) ?? await next(context);
}

sealed class OperatorOnFilter(GatewayOptions gateway, MessagingSettings messaging) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        GatewayGuards.OperatorOff(gateway.OwnerMode, messaging) ?? await next(context);
}

// callerOf: how the caller is found; the request's CallerContext (bound to the request on both paths), a stand-in in the unit tests
sealed class AdminFilter(CallerView view, string? reason, Func<HttpContext, ICallerContext>? callerOf = null) : IEndpointFilter
{
    readonly Func<HttpContext, ICallerContext> callerOf = callerOf ?? (c => CallerContext.Of(c));

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var caller = callerOf(context.HttpContext);
        var p = view == CallerView.Admitted ? caller.Admitted : caller.Who;
        if (p.Kind == "admin") return await next(context);
        return reason == null ? Results.StatusCode(403) : HttpResultsX.Fail(403, "forbidden", reason, 30);
    }
}

// step: Plant.SupervisorStep(false), a non-essential step (the essential ones, answers and patrol acknowledgements, are the messenger's); a stand-in in
// the unit tests
sealed class SupervisorBudgetFilter : IEndpointFilter
{
    readonly bool ownerMode;
    readonly Func<(bool Ok, int RetryAfter)> step;
    readonly Func<HttpContext, ICallerContext> callerOf;

    public SupervisorBudgetFilter(GatewayOptions gateway, Plant plant) : this(gateway.OwnerMode, () => plant.SupervisorStep(false), c => CallerContext.Of(c)) { }

    internal SupervisorBudgetFilter(bool ownerMode, Func<(bool Ok, int RetryAfter)> step, Func<HttpContext, ICallerContext> callerOf)
    { this.ownerMode = ownerMode; this.step = step; this.callerOf = callerOf; }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (ownerMode && callerOf(context.HttpContext).Who.Kind == "manager" && step() is { Ok: false } noStep)
            return Results.Json(new { ok = false, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }, statusCode: 429);
        return await next(context);
    }
}
