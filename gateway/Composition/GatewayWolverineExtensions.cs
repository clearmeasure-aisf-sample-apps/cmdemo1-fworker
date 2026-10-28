// The Wolverine host's registration (task W2.2, docs/design/wolverine-host.md). Nothing is registered without a connection string (FACTORY_DB_CONNECTION):
// no service, no hosted service, no database call, and the gateway is v1 exactly as before. With one, the services below join the graph and
// the hosted service is added by AddGatewayBackground (the only place hosted services are registered), after LeadershipService (the node leaves with the lease, so it starts and stops beside it).
static class GatewayWolverineExtensions
{
    public static IServiceCollection AddGatewayWolverine(this IServiceCollection services, GatewayComposition c)
    {
        if (!c.WolverineHost) return services;
        services.AddSingleton(c.Database);
        services.AddSingleton(WolverineHostOptions.From(c.Instance));
        // built after LeadershipService, whose hand-over callbacks it wraps (the order of the callbacks is the order of the stop: hand-over, then the node leaves)
        JournalCommandSink? journalSink = null;
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<LeadershipService>();
            var host = new WolverineHostService(sp.GetRequiredService<DatabaseOptions>(), sp.GetRequiredService<WolverineHostOptions>(), sp.GetRequiredService<GatewayLeader>());
            if (c.Engine.V2Acts)
            {
                RuntimeCommandWiring.Attach(host, sp.GetRequiredService<RuntimeRegistry>(), sp.GetRequiredService<Fworker.Contracts.Seams.ICommandPort>());
                journalSink = JournalCommandWiring.Attach(host, c);   // W3.2: the plant's journal lines become commands
            }
            return host;
        });
        // W3.2: the plant's journal delegate publishes through this (AddGatewayPlant); resolving it builds the host first, which makes the sink
        if (c.Engine.V2Acts) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return journalSink!; });
        // W2.3: shadow history, only where the environment is a shadow environment (v2 acting records its own history, later)
        if (c.Engine.ShadowRuns)
            services.AddSingleton<IShadowHistory>(sp => new ShadowHistory(r => sp.GetRequiredService<WolverineHostService>().PublishAsync(r), c.Instance.Home));
        return services;
    }
}
