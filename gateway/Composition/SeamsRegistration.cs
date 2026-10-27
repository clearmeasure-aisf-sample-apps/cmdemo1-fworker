using Fworker.Contracts.Catalog;
using Fworker.Integrations.Catalog;

/// <summary>
/// The registration of the seams S1 (verb router), S2 (command port) and S3 (event ingress) of docs/design/fworker-wolverine-refactor.md 4, and of the
/// system catalog (hook H2 of docs/design/system-gateway.md 3, task W1.2).
/// The seams are a stub (task W1.0): the interfaces are empty (Fworker.Contracts.Seams) and nothing is registered, so v1 behaves as before. W1.4
/// registers S1 here, W1.5 S2 and W1.6 S3.
/// The catalog: <see cref="ISystemCatalog"/> is the loader of Fworker.Integrations (W1.1) reading system.json of cmdemo1-system through
/// <see cref="GitHubSystemJson"/>; its first copy is the instance.json entry, so the home repository is what it was. Its refresh is a hosted service
/// of AddGatewayBackground, registered only where the catalog is switched on (<see cref="CatalogSettings"/>). Nothing but /api/v1/status reads it.
/// </summary>
internal static class SeamsRegistration
{
    public static IServiceCollection AddGatewaySeams(this IServiceCollection services, GatewayComposition c)
    {
        // a gateway without a readable instance.json (no owner and repo) has no fallback entry for the catalog: it composes none
        if (!c.Instance.CanCompose) return services;
        services.AddSingleton(c.Catalog);
        services.AddSingleton(sp =>
        {
            var source = new GitHubSystemJson(c.Catalog);
            return new SystemCatalogLoader(source.ReadAsync, c.Instance.Root!.ToJsonString());
        });
        services.AddSingleton<ISystemCatalog>(sp => sp.GetRequiredService<SystemCatalogLoader>());
        if (c.Catalog.Enabled) services.AddSingleton(sp => new CatalogRefreshService(sp.GetRequiredService<ISystemCatalog>(), c.Catalog));
        return services;
    }
}
