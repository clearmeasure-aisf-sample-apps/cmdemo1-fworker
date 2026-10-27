// The Wolverine host's registration (task W2.2, docs/design/wolverine-host.md). Nothing is registered without a connection string (FACTORY_DB_CONNECTION):
// no service, no hosted service, no database call, and the gateway is v1 exactly as before. With one, the services below join the graph and
// the hosted service is added by AddGatewayBackground (the only place hosted services are registered), after LeadershipService (the node leaves with the lease, so it starts and stops beside it).
static class GatewayWolverineExtensions
{
    public static IServiceCollection AddGatewayWolverine(this IServiceCollection services, GatewayComposition c)
    {
        if (!c.Database.Configured) return services;
        services.AddSingleton(c.Database);
        services.AddSingleton(WolverineHostOptions.From(c.Instance));
        // built after LeadershipService, whose hand-over callbacks it wraps (the order of the callbacks is the order of the stop: hand-over, then the node leaves)
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<LeadershipService>();
            return new WolverineHostService(sp.GetRequiredService<DatabaseOptions>(), sp.GetRequiredService<WolverineHostOptions>(), sp.GetRequiredService<GatewayLeader>());
        });
        return services;
    }
}
