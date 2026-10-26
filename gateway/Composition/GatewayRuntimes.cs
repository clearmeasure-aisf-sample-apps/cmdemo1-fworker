// The runtime adapters and their managers as AddGatewayRuntimes makes them (null: that runtime is off in this process), and the prompt functions
// the Cursor and Copilot adapters call. The prompts are loaded after Build(), so Program.cs sets the functions there; until then each answers
// what Program.cs's late-bound locals answered (an empty prompt, no pull request).
//
// C5b replaces this with RuntimeRegistry (all configured managers, by kind) and the prompt builders as services (ICursorPromptBuilder,
// ICopilotPromptBuilder, IItemPrLookup), which removes the settable functions.
sealed class GatewayRuntimes
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

    public Func<int, string, string?, string> CursorPrompt { get; set; } = (_, _, _) => "";
    public Func<int, string, string, ItemPr?, string?, string> CopilotPrompt { get; set; } = (_, _, _, _, _) => "";
    public Func<int, CancellationToken, Task<ItemPr?>> CopilotItemPr { get; set; } = (_, _) => Task.FromResult<ItemPr?>(null);

    // every manager that is on, in the order their RuntimeLoops are registered
    public IEnumerable<RuntimeManager> Managers => new[] { CaeJob, Cursor, Copilot, Octopus }.OfType<RuntimeManager>();

    // a column's phase as a token claim and a prompt line ("Functional Testing" -> "functional-testing")
    public static string PhaseSlug(string column) => column.Trim().ToLowerInvariant().Replace(' ', '-');
}
