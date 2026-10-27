namespace Fworker.Contracts.Seams;

// The three seams of docs/design/fworker-wolverine-refactor.md 4, as empty interfaces (task W1.0). Their members arrive with W1.4 (S1), W1.5 (S2) and
// W1.6 (S3); nothing implements or calls them yet, so v1's behaviour does not change.

/// <summary>S1, the verb router: the one engine interface behind VerbPipeline (members: W1.4).</summary>
public interface IVerbRouter { }

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
