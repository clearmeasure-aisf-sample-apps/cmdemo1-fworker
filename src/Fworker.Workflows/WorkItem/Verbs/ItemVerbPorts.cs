using System.Text.Json.Nodes;

namespace Fworker.Workflows.WorkItem.Verbs;

// W9.2: what the item verbs need of the outside, as ports. The gateway implements them (gateway/Ports/ItemVerbsPort.cs) over the GitHub REST and GraphQL wire, the state comment and the journal of Fworker.Integrations,
// the lock file of the item and the refusals file of the rule scripts; the tests have scripted ones. Fworker.Workflows does not reference Fworker.Integrations (the architecture test), so none of that is named here.

/// <summary>One request to GitHub (<c>Invoke-GhRaw</c>): the path is relative to the API root (<c>repos/o/r/pulls/3</c>, <c>graphql</c>).</summary>
public sealed record ItemGhCall(string Method, string Path, JsonNode? Body = null, string Accept = "application/vnd.github+json");

/// <summary>What GitHub answered: the status, the JSON body (null when there is none) and the headers.</summary>
public sealed record ItemGhAnswer(int Status, JsonNode? Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>The wire to GitHub with the gateway's identity: the headers, the retries on a network fault or a 502 to 504, the rate-limit numbers for the gateway's budget. A fault that outlasts the tries is an <see cref="ItemRefusal"/> (20, github-unreachable).</summary>
public interface IItemWire
{
    Task<ItemGhAnswer> Send(ItemGhCall call, CancellationToken ct);
}

/// <summary>The state comment of an item as <c>Get-State</c> returns it: the comment's id (null when there is none), the state, and the revision it was read at (null for a holder that was not read).</summary>
public sealed class ItemStateHolder
{
    public long? CommentId { get; set; }
    public JsonObject State { get; set; } = Ps.NewObject();
    public int? Rev { get; set; }
    /// <summary>What the adapter keeps with the holder (its own read of the comments, for the journal).</summary>
    public object? Backing { get; set; }
}

/// <summary><c>Get-State</c>, <c>Save-State</c> and <c>Get-ServerLogin</c>. A save that lost to another writer is the refusal 11 state-conflict.</summary>
public interface IItemStateStore
{
    Task<ItemStateHolder> Read(int item, CancellationToken ct);
    Task Save(int item, ItemStateHolder holder, CancellationToken ct);
    /// <summary>The login the gateway writes as (the author of the item's comments and of its state comment).</summary>
    Task<string> Login(CancellationToken ct);
}

/// <summary><c>Add-Journal</c> into the holder in hand (the caller saves it): false when the line was a repeat, over the cap, or could not be written (the script logs and goes on).</summary>
public interface IItemJournal
{
    Task<bool> Add(int item, ItemStateHolder holder, string ev, JsonObject fields, string key, CancellationToken ct);
}

/// <summary>The refusals a session was given (Models.ps1: refusals.json of the state directory), the model ladder's input.</summary>
public interface IItemRefusals
{
    Task Add(int item, CancellationToken ct);
    Task Reset(int item, CancellationToken ct);
    Task<int> Count(int item, CancellationToken ct);
}

/// <summary>
/// The checks of a head, as the CI rule of the completion of Development reads them (W9.10q): <c>Get-ChecksFor</c> and <c>Get-BaseFailures</c>, over the checks read model of the gateway (<see cref="Fworker.Workflows.ReadModels.ChecksReadModel"/>).
/// A refusal of GitHub is an <see cref="ItemRefusal"/> as for every other read of the verbs.
/// </summary>
public interface IItemChecks
{
    Task<Fworker.Workflows.ReadModels.ChecksFacts> ChecksFor(string sha, CancellationToken ct);
    /// <summary>Which of these checks fail on the base branch now (<c>Get-BaseFailures</c>).</summary>
    Task<Fworker.Workflows.ReadModels.BaseFailures> BaseFailures(IReadOnlyCollection<string> names, CancellationToken ct);
}

/// <summary>
/// The accept of a QC environment (the <c>accept</c> verb with kind qc-env), which is the QC gate's (<c>Invoke-QcAccept</c>: <see cref="Fworker.Workflows.Qc.QcService.Accept"/>). It takes the item's lock itself, so the verb does not hold it.
/// A refusal of the QC gate is an <see cref="ItemRefusal"/>. Null in the composition where the gateway has no QC service: the verb then refuses (30, qc-unavailable).
/// </summary>
public interface IItemQc
{
    Task<JsonObject> Accept(int item, string assignment, string holder, CancellationToken ct);
}

/// <summary>The time, for the tests.</summary>
public interface IItemClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemItemClock : IItemClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
