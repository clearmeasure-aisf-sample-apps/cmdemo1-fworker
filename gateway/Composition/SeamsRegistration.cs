/// <summary>
/// The registration of the seams S1 (verb router), S2 (command port) and S3 (event ingress) of docs/design/fworker-wolverine-refactor.md 4.
/// A stub (task W1.0): the interfaces are empty (Fworker.Contracts.Seams) and nothing is registered, so v1 behaves as before. W1.4 registers S1
/// here, W1.5 S2 and W1.6 S3.
/// </summary>
internal static class SeamsRegistration
{
    public static IServiceCollection AddGatewaySeams(this IServiceCollection services, GatewayComposition c) => services;
}
