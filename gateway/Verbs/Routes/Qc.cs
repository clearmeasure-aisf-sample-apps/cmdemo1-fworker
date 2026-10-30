using System.Text.Json;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Qc;

// W9.1 (#353): the QC verbs on v2, owned by the QcService (Fworker.Workflows.Qc): a saga per (repository, pull request). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build), with the
// service that QcWiring makes (EndpointModules); without a GitHub identity there is no service and the verbs stay on v1. The QC verbs are in no verb group of W9.0 (V2VerbRoutes.Groups): they are registered
// with QC itself, and no flag moves them or the items they serve (revision of 2026-10-26).
static class QcRoutes
{
    public const string Group = "qc";

    public static readonly IReadOnlyList<string> Verbs = QcService.Verbs;

    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        if (services.GetService(typeof(QcService)) is not QcService qc) return;
        foreach (var verb in Verbs)
            routes.Add(Verbs, verb, async (stdin, timeout) =>
            {
                using var cts = new CancellationTokenSource(timeout);
                try { return await qc.Run(verb, stdin, cts.Token); }
                catch (OperationCanceledException)
                {
                    return new VerbAnswer(20, JsonSerializer.Serialize(new { ok = false, verb, code = "timeout", reason = "the verb did not finish in time", retryAfterSeconds = 30, exit = 20 }));
                }
            });
    }
}
