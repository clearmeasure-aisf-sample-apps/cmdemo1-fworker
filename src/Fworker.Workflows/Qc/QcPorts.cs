using System.Text.Json.Nodes;

namespace Fworker.Workflows.Qc;

// W9.1: what the QC verbs need of the outside. Workflows reaches GitHub only through ports (it never references Integrations); the gateway's QcPort.cs makes them over the GitHub clients of the journal store,
// the journal ledger and the rule script of the inspector. Each port is a function of Qc.ps1's helpers (Get-ItemInfo, Get-State, Save-State, Add-Journal, Get-Pr, Get-PrFiles, Get-Comments ...), with the same
// reads and writes, so that a test double can say what GitHub says without a server.

/// <summary>One comment of an issue or pull request.</summary>
public sealed record QcComment(long Id, string Login, string Body);

/// <summary>Get-ItemInfo, the part QC reads: the issue's labels and state, and the Status column of its card.</summary>
public sealed record QcItemInfo(int Number, bool Open, string Title, IReadOnlyList<string> Labels, string? Column);

/// <summary>One card of Get-BoardList (the cards of this repository and this environment).</summary>
public sealed record QcBoardItem(int Number, bool Open, string? Column, IReadOnlyList<string> Labels);

/// <summary>What Get-State returns: the state, the comments of the issue it was read with, and the adapter's handle for the save (the comment id and the revision it was read at).</summary>
public sealed class QcItemState
{
    public JsonObject State { get; set; } = new();
    public IReadOnlyList<QcComment> Comments { get; init; } = [];
    public object? Handle { get; init; }
}

/// <summary>The work item: its issue and card, its state comment, its journal and the board.</summary>
public interface IQcItems
{
    /// <summary>Get-ItemInfo. <see cref="QcReject"/> 12 'item-not-found' for no such issue, 10 'out-of-scope' for another environment's.</summary>
    Task<QcItemInfo> GetInfo(int item, CancellationToken ct);

    /// <summary>Get-State: the gateway's own state comment of the item (an empty state when there is none) with the comments of the issue. A missing issue is <see cref="QcReject"/> 12 'github-not-found'.</summary>
    Task<QcItemState> ReadState(int item, CancellationToken ct);

    /// <summary>Save-State: refused with <see cref="QcStateConflictException"/> when the comment changed since it was read.</summary>
    Task SaveState(int item, QcItemState state, CancellationToken ct);

    /// <summary>Add-Journal: one line of the item's journal, best effort (a repeat of its key, the cap, a failure of GitHub: nothing is thrown).</summary>
    Task Journal(int item, string ev, JsonObject fields, string key, CancellationToken ct);

    /// <summary>Get-BoardList.</summary>
    Task<IReadOnlyList<QcBoardItem>> Board(CancellationToken ct);
}

/// <summary>The repository's pull requests and comments, and the gateway's own login.</summary>
public interface IQcGitHub
{
    /// <summary>Get-Pr; null for a pull request that does not exist.</summary>
    Task<JsonObject?> GetPull(int number, CancellationToken ct);
    /// <summary>The first 100 open pull requests (pulls?state=open&amp;per_page=100).</summary>
    Task<IReadOnlyList<JsonObject>> OpenPulls(CancellationToken ct);
    /// <summary>Get-PrFiles: the file names of the pull request.</summary>
    Task<IReadOnlyList<string>> PullFiles(int number, CancellationToken ct);
    /// <summary>The reviews of the pull request in the order GitHub lists them.</summary>
    Task<IReadOnlyList<JsonObject>> Reviews(int number, CancellationToken ct);
    /// <summary>Get-Comments of an issue or pull request.</summary>
    Task<IReadOnlyList<QcComment>> Comments(int number, CancellationToken ct);
    Task PostComment(int number, string body, CancellationToken ct);
    Task PatchComment(long commentId, string body, CancellationToken ct);
    /// <summary>Set-PrReady: a draft is marked ready for review; the pull request as it is afterwards (the draft itself when GitHub refuses).</summary>
    Task<JsonObject> MarkReady(JsonObject pull, CancellationToken ct);
    /// <summary>Get-ServerLogin.</summary>
    Task<string> ServerLogin(CancellationToken ct);
}

/// <summary>The inspector's facts of a pull request (Get-InspectFacts): the codes of the reasons that need a person, from which the trust of a run is decided.</summary>
public interface IQcFacts
{
    Task<IReadOnlyList<string>> MandatoryCodes(int item, int pr, CancellationToken ct);
}

/// <summary>The index of the items that have a live record, so the sweep reaches an item that left Functional Testing (qc-items.json of the state directory in v1).</summary>
public interface IQcIndex
{
    IReadOnlyList<int> Items();
    void Update(int item, bool live);
}

/// <summary>An index kept in memory (a test, or a process that keeps none).</summary>
public sealed class InMemoryQcIndex : IQcIndex
{
    readonly HashSet<int> items = [];
    public IReadOnlyList<int> Items() { lock (items) return items.OrderBy(x => x).ToList(); }
    public void Update(int item, bool live) { lock (items) { if (live) items.Add(item); else items.Remove(item); } }
}
