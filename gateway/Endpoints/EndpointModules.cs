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
        // mapped in this order, the order Program.cs mapped these routes in
        services.AddSingleton<IEndpointModule, HealthEndpoints>();
        services.AddSingleton<IEndpointModule, StatusEndpoints>();
        services.AddSingleton<IEndpointModule, MetricsEndpoints>();
        services.AddSingleton<IEndpointModule, DashboardEndpoints>();
        services.AddSingleton<IEndpointModule, PluginEndpoints>();
        services.AddSingleton<IEndpointModule, OperatorReleaseEndpoints>();
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
