// The gateway's HTTP edge (task C6, gateway-composition.md 2 and 3 I): its services, and the middleware in the order Program.cs added them.
static class HttpServiceCollectionExtensions
{
    // PublicBase is made by Program.cs before the composition (the plant's session pages read its LinkBase), so it is registered as that instance.
    // AdmissionService is a singleton (the middleware and InProcess share it); CallerContext is scoped, one per request or in-process call.
    public static IServiceCollection AddGatewayHttp(this IServiceCollection services, PublicBase publicBase)
    {
        services.AddSingleton(publicBase);
        services.AddSingleton<AdmissionService>();
        services.AddScoped<CallerContext>();
        services.AddScoped<ICallerContext>(sp => sp.GetRequiredService<CallerContext>());
        return services;
    }

    // In this order, as before C6: the forwarded headers of a trusted proxy (qc.trustProxy, configured in AddGatewayHosting), the test-only cause of
    // a request (FACTORY_TEST_CAUSE_HEADER=1, tests/scenarios/golden/README.md), then the authentication.
    public static WebApplication UseGatewayPipeline(this WebApplication app)
    {
        if (app.Services.GetRequiredService<QcSettings>().TrustProxy) app.UseForwardedHeaders();
        // a request's X-Fake-Cause is held while the request is served, so every rule-script run started meanwhile carries it (TestCause.cs); cleared
        // at the end of the request
        if (TestCause.On)
            app.Use(async (ctx, next) =>
            {
                var cause = ctx.Request.Headers["X-Fake-Cause"].ToString();
                if (cause.Length == 0) { await next(); return; }
                var box = TestCause.Begin(cause);
                try { await next(); }
                finally { TestCause.End(box); }
            });
        app.UseMiddleware<GatewayAuthMiddleware>();
        return app;
    }
}
