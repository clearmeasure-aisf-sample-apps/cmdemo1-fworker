// The gateway's endpoints as modules, one class per area (task C7a, gateway-composition.md 3 C and D). C7b to C7d add theirs the same way:
//
// 1. A sealed class implementing IEndpointModule in gateway/Endpoints/<Area>Endpoints.cs. Its collaborators come through its constructor (it is a
//    singleton made from the container when the routes are mapped); what an area owns (a cache, a gate) is a small class of its own beside it.
// 2. Map maps FULL patterns on the route builder it is given: no MapGroup prefix. InProcess (the MCP tools) finds endpoints by method and
//    RoutePattern.RawText, and tests/Gateway.Tests pins those patterns and every route's auth.
// 3. A handler is an instance method called from a lambda, `routes.MapGet("/x", (HttpContext c) => Handler(c))`, not a method group: a named
//    method group gives the endpoint a name (and other metadata) it did not have before. A handler that returns a Task is called from a block
//    body with return, `async (HttpContext c) => { return await Handler(c); }`: an expression body (`=> await Handler(c)` or `=> Handler(c)`) also
//    converts to RequestDelegate, which MapGet prefers, and the IResult would never be executed (the answer would be an empty 200).
// 4. The caller is c.Caller() (ICallerContext): Who, Admitted, OperatorActive, By. Not a handler parameter: the scoped CallerContext is bound to its
//    request by CallerContext.Of, which neither the middleware nor InProcess calls, so an injected one would be unbound.
// 5. Guards are endpoint filters (gateway/Http/Filters/EndpointFilters.cs), in the order the handler checked them before; the handler keeps only
//    its own rules. Never middleware: InProcess calls the endpoint's RequestDelegate, which carries its filters and nothing in front of them.
// 6. Register the module in AddGatewayEndpoints, at the place its routes were mapped among the others.
interface IEndpointModule
{
    void Map(IEndpointRouteBuilder routes);
}

static class EndpointModuleExtensions
{
    // The modules and what they own, as singletons. c: the composition, for what is not (yet) a service of its own (the sweep's minimum interval).
    public static IServiceCollection AddGatewayEndpoints(this IServiceCollection services, GatewayComposition c)
    {
        services.AddSingleton<BuildInfo>();
        services.AddSingleton<PluginArchive>();
        services.AddSingleton(sp => new StatusQuery(sp, c.SweepMinSeconds));
        services.AddSingleton<MetricsCache>();
        services.AddSingleton<WebhookDeduplicator>();
        // mapped in this order, the order Program.cs mapped these routes in
        services.AddSingleton<IEndpointModule, HealthEndpoints>();
        services.AddSingleton<IEndpointModule, StatusEndpoints>();
        services.AddSingleton<IEndpointModule, MetricsEndpoints>();
        services.AddSingleton<IEndpointModule, DashboardEndpoints>();
        services.AddSingleton<IEndpointModule, PluginEndpoints>();
        services.AddSingleton<IEndpointModule, OperatorReleaseEndpoints>();
        // C7c: wait, the event streams (their framing in SseStreams), presence, messaging, the supervisor, and the test hooks (which map nothing unless
        // messaging.testHooks is on); the runtime nonces are the composition's, not (yet) a service of their own
        services.AddSingleton<SseStreams>();
        services.AddSingleton<IEndpointModule, WaitEndpoints>();
        services.AddSingleton<IEndpointModule, EventStreamEndpoints>();
        services.AddSingleton<IEndpointModule, PresenceEndpoints>();
        services.AddSingleton<IEndpointModule, MessagingEndpoints>();
        services.AddSingleton<IEndpointModule, SupervisorEndpoints>();
        services.AddSingleton<IEndpointModule>(sp => ActivatorUtilities.CreateInstance<TestHookEndpoints>(sp, c.RuntimeNonces));
        // C7b: what is not a service of its own comes from the composition (the runtime nonces; the worker sessions at /mcp, made after the mapping)
        services.AddSingleton<IEndpointModule>(sp => new WorkerAdminEndpoints(sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<MessagingSettings>(),
            sp.GetRequiredService<KeeperSettings>(), sp.GetRequiredService<Plant>(), sp.GetRequiredService<BaseWatch>(), sp.GetRequiredService<Revocations>(),
            sp.GetRequiredService<RevocationService>(), sp.GetRequiredService<RuntimeRegistry>(), () => c.McpSessions));
        services.AddSingleton<IEndpointModule>(sp => new RuntimeHostEndpoints(sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<Plant>(),
            sp.GetRequiredService<Revocations>(), sp.GetRequiredService<RevocationService>(), sp.GetRequiredService<RuntimeRegistry>(), c.RuntimeNonces));
        services.AddSingleton<IEndpointModule, WebhookEndpoints>();
        services.AddSingleton<IEndpointModule, CopilotEndpoints>();
        services.AddSingleton<IEndpointModule, TranscriptEndpoints>();
        services.AddSingleton<IEndpointModule, ItemAdminEndpoints>();
        services.AddSingleton<IEndpointModule, DemoEndpoints>();
        // C7d: the verb route and its pipeline (seam S1); the GitHub budget is the composition's, not (yet) a service of its own
        services.AddSingleton(sp => new VerbPipeline(sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<MessagingSettings>(), sp.GetRequiredService<KeeperSettings>(),
            sp.GetRequiredService<Plant>(), sp.GetRequiredService<Messenger>(), sp.GetRequiredService<QcHub>(), sp.GetRequiredService<Runner>(), sp.GetRequiredService<EventHub>(),
            c.GitHubBudget, sp));
        services.AddSingleton<IEndpointModule, VerbEndpoints>();
        return services;
    }

    // Each registered module maps its routes. The modules (and BuildInfo's start time) are made here, where Program.cs mapped the routes.
    public static WebApplication MapGatewayEndpoints(this WebApplication app)
    {
        foreach (var module in app.Services.GetServices<IEndpointModule>()) module.Map(app);
        return app;
    }

    // The caller of the request (gateway/Http/CallerContext.cs), bound to it on the HTTP path and on InProcess's alike
    public static ICallerContext Caller(this HttpContext c) => CallerContext.Of(c);
}
