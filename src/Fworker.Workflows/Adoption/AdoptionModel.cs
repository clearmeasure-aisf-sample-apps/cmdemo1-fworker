using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

// W9.10m (docs/design/fworker-wolverine-implementation.md, "W9.10m as built" has the mapping table): the types of the adoption of an item from v1's state into the WorkItemSaga. Pure data: the snapshot is what v1 knows of one item
// (its state comment, the card, the issue, the pull request, the rows of the ledger, the merge train and the delivery trains), the settings are what the mapping needs of the configuration, and the mapping is what the saga
// would have to be told. Nothing here reads or writes anything; the wiring that gathers the snapshots and applies the results at every start is W9.9b, the fencing of a re-adoption after the kill switch W9.10n.

/// <summary>The columns of the board as the mapping reads them: where an item stands in each step (the merge happens in the validation column), the hold columns and the done column.</summary>
public sealed record AdoptionColumns(string Design, string Develop, string Validation, string Done, IReadOnlyList<string> Hold)
{
    /// <summary>The step whose column that is (the validation column is Verify; an item on its Merge step stands there too); null for a column of no step (Todo, Done, a hold column).</summary>
    public StepKind? StepOf(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return null;
        if (Same(column, Design)) return StepKind.Design;
        if (Same(column, Develop)) return StepKind.Develop;
        if (Same(column, Validation)) return StepKind.Verify;
        return null;
    }

    public bool IsHold(string? column) => !string.IsNullOrWhiteSpace(column) && Hold.Any(h => Same(h, column));
    public bool IsDone(string? column) => !string.IsNullOrWhiteSpace(column) && Same(Done, column);
    public string ColumnOf(StepKind step) => step switch { StepKind.Design => Design, StepKind.Develop => Develop, _ => Validation };

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the mapping needs of the configuration: the saga's policy (leases, the settle window, the sweep's times, the budgets, the plans it carries), the columns, the docs-only rules and the gates of the merged-items row.</summary>
/// <param name="SlotRuntime">The runtime of a live owner's session when the ledger and the snapshot name none (<c>scheduler.runtime</c>).</param>
public sealed record AdoptionSettings(WorkItemPolicy Policy, AdoptionColumns Columns, DocsOnlySettings DocsOnly, ItemGateConfig Gates, string SlotRuntime = "")
{
    public SweepRules Sweep => Policy.Sweep ?? SweepRules.Default;
}

/// <summary>The item's pull request as the mapping reads it: its state ("open", "closed" or "merged"), its head, the merge commit when merged and its changed files when they were read (null: not read).</summary>
public sealed record AdoptionPullRequest(int Number, string State, string Head, string? MergeCommit = null, IReadOnlyList<string>? Files = null);

/// <summary>Everything v1 knows of one item at a start.</summary>
/// <param name="IssueOpen">Whether the issue is open.</param>
/// <param name="Column">The card's column (null: not on the board).</param>
/// <param name="State">The state comment's state (null: the item has none, the intake has not started it).</param>
/// <param name="HeldSlot">The slot the scheduler's ledger holds for the item already, if any.</param>
/// <param name="Trains">The delivery trains (one per environment) of the item's deployable, as they are now.</param>
/// <param name="DemoVideoPublished">For the gates of the merged-items row.</param>
public sealed record AdoptionSnapshot(ItemId Item, bool IssueOpen, string? Column, IReadOnlyList<string> Labels, string? Body, DateTimeOffset? CreatedAt, JsonObject? State,
    AdoptionPullRequest? PullRequest = null, LedgerSlot? HeldSlot = null, IReadOnlyList<TrainState>? Trains = null, bool DemoVideoPublished = false);

/// <summary>A record of the state comment's <c>qc</c> map, for the table of QC environments (the copy that v1 reads from the comment and the sagas read from the table).</summary>
public sealed record AdoptionQcRow(string Key, string Repository, string ItemKey, JsonObject Record);

/// <summary>A slot of the scheduler's ledger a live owner's session holds, and the place the item takes among the items in flight.</summary>
public sealed record AdoptionSlot(string SlotId, ItemId Item, int Generation, string Runtime, string? Host, string Phase, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt, IReadOnlyList<string> Footprint);

/// <summary>An item between Development and merged that holds no slot (its PASS waits for the merge): the scheduler keeps it among the items in flight for the file-overlap rule until it is cleared.</summary>
public sealed record AdoptionFlight(ItemId Item, IReadOnlyList<string> Footprint, DateTimeOffset Since, int Generation = 0);

public enum AdoptionKind
{
    /// <summary>The item has no saga row: the mapping makes the first one.</summary>
    Adopted,
    /// <summary>The saga row is open and the state comment's revision is newer: the comment won and the row changed.</summary>
    Updated,
    /// <summary>The state comment is newer but says what the saga row says: only the revision it was read at moves on.</summary>
    Unchanged,
    /// <summary>The saga row stays as it is (the comment is not newer, or the saga has decisions its projection has not written yet).</summary>
    Kept,
    /// <summary>W9.10n, after a break of the engine: v1 acted on the item and the comment says it is over (settled, merged docs, a card in Done, an issue closed before the saga merged it): the saga's open row is closed.</summary>
    Closed,
    /// <summary>W9.10n, after a break of the engine: the saga's instance had ended and v1 opened the item again (the comment shows it open and not merged): the next generation starts from the comment.</summary>
    Reopened,
    /// <summary>The item is not the saga's to hold (a closed issue, a settled item, a column of no step, a plan this engine does not carry): nothing is made; see the reason.</summary>
    Skipped,
}

/// <summary>What the pure mapping made of one snapshot: the saga state it stands for (null when the item is skipped), and the rows beside it.</summary>
public sealed record AdoptionMapping(string? SkipReason, WorkItemState? State, AdoptionSlot? Slot, AdoptionFlight? Flight, IReadOnlyList<AdoptionQcRow> QcRows, MergedItem? MergedRow, IReadOnlyList<string> Notes)
{
    public bool Skipped => SkipReason is not null;
}

/// <summary>The result of the reconciliation of one item: what to save (the state, with the decision, the timers and the commands the saga publishes after the save), and the rows beside it.</summary>
public sealed record AdoptionResult(AdoptionKind Kind, string Reason, WorkItemState? State, WorkItemDecided? Decision, IReadOnlyList<WorkItemTimer> Timers, IReadOnlyList<object> Commands,
    AdoptionSlot? Slot, AdoptionFlight? Flight, IReadOnlyList<AdoptionQcRow> QcRows, MergedItem? MergedRow, IReadOnlyList<string> Notes)
{
    /// <summary>Whether the state is to be saved: a new row, a changed one, or only the revision moved on.</summary>
    public bool Save => Kind is AdoptionKind.Adopted or AdoptionKind.Updated or AdoptionKind.Unchanged or AdoptionKind.Closed or AdoptionKind.Reopened;

    /// <summary>
    /// W9.10n: set when the result opened a new generation or closed the instance: the ledger is to be fenced for the item (<see cref="AdoptionLedger.Fence"/>, before the adopted slot is applied) and the merge line validated
    /// (<see cref="AdoptionTrainRows.ValidateMergeLine"/>). Null for the results that leave the instance as it is.
    /// </summary>
    public LedgerFence? Fence { get; init; }

    /// <summary>W9.10n: the generation the row had before this result opened a new one (<see cref="AdoptionKind.Updated"/> after a break, <see cref="AdoptionKind.Reopened"/>); null otherwise.</summary>
    public int? FencedFrom { get; init; }
}
