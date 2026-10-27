namespace Fworker.Contracts.Seams;

// The three seams of docs/design/fworker-wolverine-refactor.md 4. S1 has its members since W1.4; S2 is empty until W1.5; S3 has its members since W1.6. v1's behaviour does not change.

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

/// <summary>S2, the command port: one integration command per external side effect (members: W1.5).</summary>
public interface ICommandPort { }

/// <summary>An event that enters the gateway: a webhook event or a verb's request for a sweep. One typed record for each kind.</summary>
public abstract record IngressEvent;

/// <summary>An event the rule verb ingest-event returned for a webhook delivery: <paramref name="Json"/> is the event object as the script printed it.</summary>
public sealed record WebhookEventIngested(string Json) : IngressEvent;

/// <summary>A request for a sweep of the board: of one item (<paramref name="Item"/> above 0) or, with 0, a full sweep. The sweeper coalesces repeats.</summary>
public sealed record SweepRequested(int Item = 0) : IngressEvent;

/// <summary>S3, the event ingress: the one entry for webhook events and for the verbs' sweep requests. v1 publishes the event and asks the sweeper as before.</summary>
public interface IEventIngress
{
    /// <summary>Takes one event in. It returns when the event is handed on; it does not wait for a sweep.</summary>
    void Ingest(IngressEvent ev);
}
