using Fworker.Contracts.Seams;
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
        // mapped in this order, the order Program.cs mapped these routes in; the QC routes first, in owner mode only (QcHub is the module, Qc.cs)
        if (c.Gateway.OwnerMode) services.AddSingleton<IEndpointModule>(sp => sp.GetRequiredService<QcHub>());
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
        services.AddSingleton<IEndpointModule>(sp => new OctopusWebhookEndpoints(sp));   // W4.2: answers 404 unless the Octopus watcher is on (v2 acting)
        services.AddSingleton<IEndpointModule>(sp => ActivatorUtilities.CreateInstance<TestHookEndpoints>(sp, c.RuntimeNonces));
        // C7b: what is not a service of its own comes from the composition (the runtime nonces; the worker sessions at /mcp, made after the mapping)
        services.AddSingleton<IEndpointModule>(sp => new WorkerAdminEndpoints(sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<MessagingSettings>(),
            sp.GetRequiredService<KeeperSettings>(), sp.GetRequiredService<Plant>(), sp.GetRequiredService<BaseWatch>(), sp.GetRequiredService<Revocations>(),
            sp.GetRequiredService<RevocationService>(), sp.GetRequiredService<RuntimeRegistry>(), () => sp.GetRequiredService<WorkerSessions>()));
        services.AddSingleton<IEndpointModule>(sp => new RuntimeHostEndpoints(sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<Plant>(),
            sp.GetRequiredService<Revocations>(), sp.GetRequiredService<RevocationService>(), sp.GetRequiredService<RuntimeRegistry>(), c.RuntimeNonces));
        // S3 (W1.6): the one entry for webhook events and the verbs' sweep requests; v1 only
        services.AddSingleton<IEventIngress>(sp =>
        {
            var v1 = new V1EventIngress(sp.GetRequiredService<EventHub>());
            if (sp.GetService<IngressInboxPort>() is { } inbox)
            {
                // W4.1: where the engine acts, events also go to the durable inbox. W5.2: in the shadow run they do too, the Octopus watcher's events go to the train, and the history is kept
                IEventIngress withInbox = new InboxEventIngress(v1, inbox);
                if (sp.GetService<ShadowOctopusPort>() is not { } octopus) return withInbox;
                withInbox = new OctopusShadowIngress(withInbox, octopus);
                return sp.GetService<IShadowHistory>() is { } history ? new ShadowEventIngress(withInbox, history) : withInbox;
            }
            return sp.GetService<IShadowHistory>() is { } shadow ? new ShadowEventIngress(v1, shadow) : v1;   // W2.3: shadow history records after v1 has done its part
        });
        services.AddSingleton<IEndpointModule, WebhookEndpoints>();
        services.AddSingleton<IEndpointModule, CopilotEndpoints>();
        services.AddSingleton<IEndpointModule, TranscriptEndpoints>();
        services.AddSingleton<IEndpointModule, ItemAdminEndpoints>();
        services.AddSingleton<IEndpointModule>(sp => new AdminDeliveryShadowEndpoints(sp));   // W5.2: 404 where the delivery train does not run in shadow
        services.AddSingleton<IEndpointModule, DemoEndpoints>();
        // C7d: the verb route and its pipeline (seam S1); the GitHub budget is the composition's, not (yet) a service of its own
        // S1 (W1.4): the engine the environment is composed with; v1 is the only one
        // W9.0: the verbs that v2 answers (one route file per verb group, gateway/Verbs/Routes); empty, and Register not called, unless the engine acts
        services.AddSingleton(sp => V2VerbRoutes.Build(c.Engine, sp));
        services.AddSingleton<IVerbRouter>(sp =>
        {
            IVerbRouter router = VerbRouterFactory.For(sp.GetRequiredService<EngineOptions>(), sp.GetRequiredService<Runner>());
            // W6.6: where the engine acts, the decision verbs of the items the saga owns also tell the saga what the script did (V2VerbFeed); every other call is v1's alone
            if (c.Engine.V2Acts && c.Instance.Home is { } home && sp.GetService<WorkItemParts>() is { Ownership: { } ownership } parts)
            {
                var host = sp.GetRequiredService<WolverineHostService>();
                router = new V2VerbFeed(router, ownership, parts.Store, home, parts.Docs.Settings, m => host.PublishAsync(m), parts.Docs.Heads);
            }
            // W9.0: the verbs a route file registered are answered by v2 and never reach v1; with none registered the router is what it was
            return c.Engine.V2Acts && sp.GetRequiredService<V2VerbRoutes>() is { Count: > 0 } routes ? new V2VerbRouter(router, routes) : router;
        });
        services.AddSingleton(sp => new VerbPipeline(sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<MessagingSettings>(), sp.GetRequiredService<KeeperSettings>(),
            sp.GetRequiredService<Plant>(), sp.GetRequiredService<Messenger>(), sp.GetRequiredService<QcHub>(), sp.GetRequiredService<IVerbRouter>(), sp.GetRequiredService<IEventIngress>(),
            c.GitHubBudget, sp));
        services.AddSingleton<IEndpointModule, VerbEndpoints>();
        // the MCP server's route, last as it was mapped (the tools it serves are McpTools, AddGatewayMcp)
        services.AddSingleton<IEndpointModule, McpEndpoints>();
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
