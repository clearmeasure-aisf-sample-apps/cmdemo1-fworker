namespace Fworker.Contracts.Seams;

// The three seams of docs/design/fworker-wolverine-refactor.md 4. S1 has its members since W1.4; S2 and S3 arrive with W1.5 and W1.6 and are empty
// until then; nothing but S1 is implemented or called, so v1's behaviour does not change.

/// <summary>What a verb call answered: the exit code and the JSON text the caller gets (the shape the rule script printed).</summary>
public readonly record struct VerbAnswer(int Code, string Output);

/// <summary>
/// S1, the verb router: the one engine interface behind VerbPipeline. The pipeline has already authorized and checked the call; the router runs
/// it on the engine the environment is composed with (v1: the rule scripts; v2, later: sagas by InvokeAsync or a published event).
/// </summary>
public interface IVerbRouter
{
    /// <summary>Runs one verb. <paramref name="stdin"/> is the JSON body the rule script reads; the answer is the script's exit code and output.</summary>
    Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout);
}

/// <summary>
/// S2, the command port: one integration command per external side effect (W1.5). v1's implementation delegates to today's code paths; the recording
/// sink only captures commands, for shadow mode and the golden comparison.
/// </summary>
public interface ICommandPort
{
    /// <summary>Executes (or, for the recording sink, records) one command. Never throws for a failed side effect: the failure is in the result.</summary>
    Task<Fworker.Contracts.Commands.CommandResult> SendAsync(Fworker.Contracts.Commands.IntegrationCommand command, CancellationToken ct = default);
}

/// <summary>S3, the event ingress: one entry for webhook and verb events (members: W1.6).</summary>
public interface IEventIngress { }
