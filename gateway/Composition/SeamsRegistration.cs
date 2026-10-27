/// <summary>
/// The registration of the seams S1 (verb router), S2 (command port) and S3 (event ingress) of docs/design/fworker-wolverine-refactor.md 4.
/// A stub (task W1.0): the interfaces are empty (Fworker.Contracts.Seams) and nothing is registered, so v1 behaves as before. W1.4 registers S1
/// here, W1.5 S2 and W1.6 S3.
/// </summary>
internal static class SeamsRegistration
{
    public static IServiceCollection AddGatewaySeams(this IServiceCollection services, GatewayComposition c)
    {
        // S2 (W1.5): v1's port delegates to the runtime adapters; the recording sink is registered by its own type. Nothing resolves either yet.
        services.AddSingleton<Fworker.Domain.Commands.RecordingCommandSink>();
        services.AddSingleton<Fworker.Contracts.Seams.ICommandPort>(sp =>
        {
            var rts = sp.GetRequiredService<RuntimeRegistry>();
            return new V1CommandPort(kind => kind switch
            {
                "cursor-cloud" => rts.CursorRuntime,
                "copilot-cloud" => rts.CopilotRuntime,
                "octopus" => rts.OctopusRuntime,
                _ => null,
            });
        });
        return services;
    }
}
