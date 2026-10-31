using System.Text.Json.Nodes;

namespace Fworker.Workflows.Keeper;

// W9.6 (#358): what the keeper and manager commands need of the outside, as ports. The gateway implements them over GitHub's REST and GraphQL API, the state comment and the journal ledger (gateway/Ports/KeeperPort.cs);
// the tests implement them in memory. A port does what the helper of the rule scripts of the same name does (Get-ItemInfo, Move-Card, Get-State, Invoke-Gh...), and refuses the way it does: with a <see cref="VerbRejection"/>.

/// <summary><c>Get-ItemInfo</c>: everything one verb needs to know about an issue and its card.</summary>
public sealed record ItemInfo(int Number, string NodeId, bool Open, string Title, string Body, IReadOnlyList<string> Labels, string? CardId, string? Column)
{
    /// <summary><c>Test-Label</c>: the item carries the label (names compare without case).</summary>
    public bool HasLabel(string name) => Ps.NameIn(name, Labels);
}

/// <summary>One card of <c>Get-BoardList</c>: the factory's items on the board with their labels and column.</summary>
public sealed record BoardEntry(int Number, bool Open, string Title, string? Column, IReadOnlyList<string> Labels);

/// <summary>An item's state comment as read (<c>Get-State</c>): the state is edited in place and written back with <see cref="IItemStateStore.Save"/>. The gateway's holder carries the revision and the comment's id.</summary>
public abstract class StateHolder
{
    public abstract JsonObject State { get; }
}

/// <summary>The state comment of an item and the journal that lives beside it.</summary>
public interface IItemStateStore
{
    /// <summary><c>Get-State</c>.</summary>
    Task<StateHolder> Read(int item);

    /// <summary><c>Save-State</c>: refused with the 'state-conflict' rejection (exit 11) when the comment changed since it was read.</summary>
    Task Save(int item, StateHolder holder);

    /// <summary>
    /// <c>Add-Journal</c> (best effort: a line that cannot be written is false, never an error): one line of the plant manager's journal on the item. With a holder, the ledger is updated in it and the caller saves it;
    /// without one, the line reads the state and saves it itself.
    /// </summary>
    Task<bool> Journal(int item, StateHolder? holder, string ev, JsonObject fields, string key);
}

/// <summary>The GitHub calls of the keeper and manager verbs, one for each helper of the scripts (Board.ps1, Prs.ps1, Verbs.Item.ps1) that they call.</summary>
public interface IKeeperGitHub
{
    /// <summary><c>Get-ItemInfo</c>: refuses 'item-not-found' (12) for an issue that is not there and 'out-of-scope' (10) for another environment's.</summary>
    Task<ItemInfo> GetItemInfo(int item);

    /// <summary><c>Get-BoardList</c>, with the scope and repository filters of the script.</summary>
    Task<IReadOnlyList<BoardEntry>> ListBoard();

    /// <summary><c>Invoke-Gh issues/N</c>: the issue as GitHub gives it (an item that is not there is the 'github-not-found' refusal).</summary>
    Task<JsonNode> GetIssue(int item);

    /// <summary><c>Set-IssueBody</c>.</summary>
    Task SetBody(int item, string body);

    /// <summary>POST issues: the number of the new issue.</summary>
    Task<int> CreateIssue(string title, string body, IReadOnlyList<string> labels);

    /// <summary><c>Add-ToBoard</c>: the card's id.</summary>
    Task<string?> AddToBoard(string issueNodeId);

    /// <summary><c>Move-Card</c>: refuses 'unknown-column' and 'not-on-board' (10).</summary>
    Task MoveCard(ItemInfo info, string column);

    /// <summary><c>Add-ItemLabels</c>.</summary>
    Task AddLabels(int item, IReadOnlyList<string> names);

    /// <summary><c>Remove-ItemLabel</c> (a label the issue does not carry is no failure).</summary>
    Task RemoveLabel(int item, string name);

    /// <summary>POST issues/N/comments with exactly this body (a pull request is an issue too).</summary>
    Task PostComment(int number, string body);

    /// <summary><c>Close-Issue</c> with a state reason (completed, not_planned).</summary>
    Task CloseIssue(int item, string reason);

    /// <summary>GET pulls?state=open&amp;per_page=100 (the first page, as <c>Find-ItemPr</c> reads it).</summary>
    Task<IReadOnlyList<JsonObject>> OpenPulls();

    /// <summary><c>Get-Pr</c>.</summary>
    Task<JsonObject> GetPull(int number);

    /// <summary>PATCH pulls/N state closed.</summary>
    Task ClosePull(int number);

    /// <summary>GET actions/runs?head_sha=&lt;sha&gt;&amp;per_page=50: the workflow runs.</summary>
    Task<IReadOnlyList<JsonObject>> WorkflowRuns(string headSha);

    /// <summary>POST actions/runs/N/rerun-failed-jobs: the status GitHub answered (no refusal is thrown for it).</summary>
    Task<int> RerunFailedJobs(long runId);

    /// <summary><c>Remove-ClaimRef</c>: DELETE the item's claim ref (a ref that is not there is no failure).</summary>
    Task RemoveClaimRef(int item);
}

/// <summary>The locks of the scripts: <c>Use-ItemLock</c> (one verb at a time on an item) and <c>Use-IntakeLock</c> (one work item at a time put on the board). A wait that runs out is the 'item-busy' or 'intake-busy' refusal (20).</summary>
public interface IVerbLocks
{
    Task<IAsyncDisposable> Item(int item);
    Task<IAsyncDisposable> Intake();

    /// <summary>W9.10g: <c>Use-ItemLock $n {...} $seconds</c> with a wait of its own (the claim-ref reap takes it without waiting, the guard's journal line waits 3 seconds). A port that knows no other wait uses its own.</summary>
    Task<IAsyncDisposable> Item(int item, TimeSpan wait) => Item(item);

    /// <summary>W9.10g: <c>Use-IntakeLock {...} $seconds</c> (the board guard takes it without waiting).</summary>
    Task<IAsyncDisposable> Intake(TimeSpan wait) => Intake();
}

/// <summary>What the WorkItemSaga must be told when a keeper or manager verb changes an item it owns. Nothing is said about an item no saga owns.</summary>
public interface IKeeperSaga
{
    /// <summary>The item is owned by a saga (an item of the docs or the feature plan that v2 decides).</summary>
    bool Owns(int item);

    /// <summary>The park of the item was released (manager-action unpark, amend-criteria --unpark): the saga takes up its step again.</summary>
    Task Resumed(int item, string key);

    /// <summary>The item was split and closed: the saga stops its lease and takes no step message until a person resumes it.</summary>
    Task Closed(int item, string reason, string key);

    /// <summary>W9.10k: the plant manager ended the lease of the item's owner (manager-action release-owner): the saga records it and asks the gateway to give the claim up.</summary>
    Task OwnerReleased(int item, string reason, string key);

    /// <summary>W9.10k: the plant manager put the item back to be offered (manager-action requeue): the saga records it.</summary>
    Task Requeued(int item, string reason, string key);
}

/// <summary>No saga owns anything.</summary>
public sealed class NoSaga : IKeeperSaga
{
    public bool Owns(int item) => false;
    public Task Resumed(int item, string key) => Task.CompletedTask;
    public Task Closed(int item, string reason, string key) => Task.CompletedTask;
    public Task OwnerReleased(int item, string reason, string key) => Task.CompletedTask;
    public Task Requeued(int item, string reason, string key) => Task.CompletedTask;
}
