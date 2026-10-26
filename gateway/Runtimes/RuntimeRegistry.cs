// The runtimes of this process (task C5b, docs/design/gateway-composition.md 3 B): every configured manager by kind, and the adapters behind them
// (null: that runtime is off here). AddGatewayRuntimes makes it once and registers it as a singleton; the plant, the RuntimeLoops and the routes
// read it. The prompts the Cursor and Copilot adapters send are services of their own (Templates/RuntimePrompts.cs), given to their constructors.
sealed class RuntimeRegistry
{
    public RuntimeManager? CaeJob { get; init; }
    public CursorCloudRuntime? CursorRuntime { get; init; }
    public RuntimeManager? Cursor { get; init; }
    public CopilotCloudRuntime? CopilotRuntime { get; init; }
    public RuntimeManager? Copilot { get; init; }
    public OctopusRunbookRuntime? OctopusRuntime { get; init; }
    public RuntimeManager? Octopus { get; init; }
    // the CI release of the Copilot agent's held workflow runs, and the one-time codes in a Copilot task's prompt
    public required CopilotCiRelease CopilotCiRelease { get; init; }
    public required CopilotCodes CopilotCodes { get; init; }

    // every manager that is on, in the order their RuntimeLoops are registered
    public IEnumerable<RuntimeManager> Managers => new[] { CaeJob, Cursor, Copilot, Octopus }.OfType<RuntimeManager>();

    // the manager of a runtime kind as the API names it (POST /api/v1/runtimes/{kind}/reset), null when that runtime is off or there is no such kind
    public RuntimeManager? ByKind(string kind) => kind switch
    {
        "cae-job" => CaeJob,
        "cursor-cloud" => Cursor,
        "copilot-cloud" => Copilot,
        "octopus" => Octopus,
        _ => null,
    };

    // a column's phase as a token claim and a prompt line ("Functional Testing" -> "functional-testing")
    public static string PhaseSlug(string column) => column.Trim().ToLowerInvariant().Replace(' ', '-');
}
