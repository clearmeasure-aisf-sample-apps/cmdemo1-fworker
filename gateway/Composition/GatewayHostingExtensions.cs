using System.Diagnostics;
using System.Text.Json;

// The host around the gateway (gateway-composition.md 2 and 3 B, task C5a): what Program.cs set on the builder before its registrations, and what
// it ran once after Build() before mapping its routes. Behaviour as before C5a; only the place changed.
static class GatewayHostingExtensions
{
    // Kestrel's listeners, the log level, the forwarded headers of a trusted proxy, what a failing background loop does, and the process's crash lines.
    public static WebApplicationBuilder AddGatewayHosting(this WebApplicationBuilder builder, GatewayComposition c)
    {
        // Container mode (FACTORY_SINGLE_PORT, or ASPNETCORE_HTTP_PORTS from the image): one listener on every interface; the routes separate by path
        // (webhook: HMAC only; /api: bearer token; /health, /_build, /dashboard page: anonymous). Local mode keeps two loopback listeners.
        var gw = c.Gateway;
        if (gw.SinglePort > 0) builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(gw.SinglePort));
        else builder.WebHost.ConfigureKestrel(k => { k.ListenLocalhost(gw.ApiPort); k.ListenLocalhost(gw.HookPort); });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // The client address of a QC click comes from X-Forwarded-For only when qc.trustProxy is on, and then only through the proxies listed in
        // qc.knownProxies (the middleware itself is added in Program.cs, app.UseForwardedHeaders)
        var qc = c.Qc;
        if (qc.TrustProxy)
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
                o.ForwardLimit = null; o.KnownProxies.Clear(); o.KnownIPNetworks.Clear();
                foreach (var p in qc.KnownProxies) o.KnownProxies.Add(System.Net.IPAddress.Parse(p));
            });
        // A background loop that throws must not end the process (the default of .NET stops the host): the loops catch and go on, and this is the net under them.
        builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        // What ended a process is in its last lines: an exception nobody caught (a crash), or "stopping" (the platform asked: SIGTERM at a scale-in, a new
        // revision, a failed probe; UseGatewayLifetime). Neither line present means the process was killed outright (out of memory).
        AppDomain.CurrentDomain.UnhandledException += (_, ue) => { try { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"unhandled-exception\",\"terminating\":{(ue.IsTerminating ? "true" : "false")},\"type\":{JsonSerializer.Serialize(ue.ExceptionObject?.GetType().FullName ?? "")},\"message\":{JsonSerializer.Serialize((ue.ExceptionObject as Exception)?.Message ?? "")}}}"); } catch { } };
        TaskScheduler.UnobservedTaskException += (_, te) => { try { te.SetObserved(); Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"unobserved-task-exception\",\"type\":{JsonSerializer.Serialize(te.Exception.InnerException?.GetType().FullName ?? "")},\"message\":{JsonSerializer.Serialize(te.Exception.InnerException?.Message ?? "")}}}"); } catch { } };
        return builder;
    }

    // Once after Build(), before the pipeline: the stopping callbacks in their order, and the one wire the container cannot make yet.
    //
    // ApplicationStopping runs its callbacks last-registered first, so the order here is the reverse of the stop: the transcripts' final flush
    // (TranscriptService), then the "stopping" line, then the hand-over (LeadershipService: the final plant snapshot and the lease's release, or
    // without the lease the write of the plant). At the stop: hand-over, "stopping", flush. StopOrderTests pins it.
    //
    // The services are resolved in the order Program.cs resolved them before C5a, so each is constructed when it was.
    public static WebApplication UseGatewayLifetime(this WebApplication app)
    {
        var sp = app.Services;
        sp.GetRequiredService<TranscriptService>().RegisterFinalFlush();
        app.Lifetime.ApplicationStopping.Register(() => { try { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"stopping\",\"reason\":\"shutdown requested (signal or host)\",\"upSeconds\":{(long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds}}}"); } catch { } });
        sp.GetRequiredService<EventHub>();
        sp.GetRequiredService<Runner>();
        var plant = sp.GetRequiredService<Plant>();
        // the plant manager's mode and summary wait for the snapshot (Manager.cs). Still set here: the snapshot coordinator takes the plant in its
        // constructor, so the plant cannot take the coordinator's restore in its own (a restore signal owned by gateway/Leadership/ would allow it)
        plant.RestoredTask = sp.GetRequiredService<ISnapshotCoordinator>().Restored;
        sp.GetRequiredService<QcHub>();
        sp.GetRequiredService<Messenger>();
        // W9.9a: the table of the verbs v2 answers is built here, before the first call of the gateway itself: the Runner asks it (Runner.Routes, set by V2VerbRoutes.Build). BaseWatch used to resolve it
        sp.GetRequiredService<V2VerbRoutes>();
        sp.GetRequiredService<BaseWatch>();
        // Revocations (gateway/Identity/RevocationService.cs): resolved where the start used to set them up, so their start-up lines and the check of
        // the test switch keep their place; the read of the generations starts with the host. Until it succeeds no session credential is accepted.
        sp.GetRequiredService<RevocationService>();
        sp.GetRequiredService<LeadershipService>().RegisterStopping();
        // the prompt files (gateway/Mcp/Prompts.cs, agents-roles-launch.md 5) were loaded when AddGatewayRuntimes registered them; a broken one keeps /ready at 503
        foreach (var pe in sp.GetRequiredService<PromptStore>().Errors) Console.Error.WriteLine($"prompts: {pe}");
        // the revocation itself is RevocationService.RevokeAsync: a runtime revokes a failed execution's name through it
        var runtimes = sp.GetRequiredService<RuntimeRegistry>(); var revocation = sp.GetRequiredService<RevocationService>();
        if (runtimes.CaeJob is { } caeJob) caeJob.Revoke = async n => { await revocation.RevokeAsync(n, "runtime"); };
        if (runtimes.Cursor is { } cursor) cursor.Revoke = async n => { await revocation.RevokeAsync(n, "runtime"); };
        if (runtimes.Copilot is { } copilot) copilot.Revoke = async n => { runtimes.CopilotCodes.Forget(n); await revocation.RevokeAsync(n, "runtime"); };
        if (runtimes.Octopus is { } octopus) octopus.Revoke = async n => { await revocation.RevokeAsync(n, "runtime"); };
        // the plant manager on demand (api.copilot.manager): registered where it is enabled, ticked by ManagerCopilotService; resolved here so that its
        // start-up line (copilot-config) is written at the start, not at the first use
        sp.GetService<ManagerCopilot>();
        return app;
    }
}
