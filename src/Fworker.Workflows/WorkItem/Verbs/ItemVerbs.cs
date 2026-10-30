using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;

namespace Fworker.Workflows.WorkItem.Verbs;

// W9.2 (#354): the eighteen item verbs of the WorkItemSaga's group (docs/design/verb-inventory.md), answered by v2 exactly as the rule scripts answer them: the same fields, the same exit codes, the same writes to GitHub, the
// board and the item's state comment, in the same order. Each handler is the port of its Verb_* function (Verbs.Item.ps1, Owner.ps1, Verbs.Pr.ps1); the comment above it names what it ports. The state comment stays in
// v1's format (Get-State, Save-State), so v1 can take every item back (FACTORY_V2_OFF). What the saga learns of a verb is told by the gateway's feed (V2VerbFeed) from the answer, as for v1 (hybrid composition, W6.6).
//
// Where a verb leans on a rule that another group of W9 owns, the call is the script's still: the accept of a QC environment (W9.1), the completion of Development (the red-head ruling and the migration check of the CI rule,
// W9.5), the pull requests of the Copilot owner task (W9.4), and the inline merge of a PASS (W9.3). IItemV1 runs the script for them; without a v1 the verb says so in a refusal (30, v1-unavailable).

/// <summary>The item verbs.</summary>
public sealed partial class ItemVerbs
{
    /// <summary>The verbs of this group.</summary>
    public static readonly IReadOnlyList<string> Verbs =
    [
        "get-item", "append-body", "comment", "claim-item", "accept", "release", "my-items", "record-review", "move-next", "complete",
        "validation-verdict", "open-pr", "update-pr", "item-pr", "park-item", "unpark-item", "resettle-item", "expire-claim",
    ];

    readonly ItemGitHub gh;
    readonly ItemBoard board;
    readonly ItemPulls pulls;
    readonly IItemStateStore store;
    readonly IItemJournal journal;
    readonly IItemRefusals refusals;
    readonly IItemV1? v1;
    readonly IItemClock clock;
    readonly ItemVerbRunner runner;
    readonly Dictionary<string, Func<ItemCall, Task<JsonObject>>> handlers;

    public ItemVerbs(ItemGitHub gh, ItemBoard board, ItemPulls pulls, IItemStateStore store, IItemJournal journal, IItemLock locks, IItemRefusals refusals, IItemV1? v1, IItemClock? clock = null)
    {
        this.gh = gh; this.board = board; this.pulls = pulls; this.store = store; this.journal = journal; this.refusals = refusals; this.v1 = v1; this.clock = clock ?? new SystemItemClock();
        runner = new ItemVerbRunner(locks, refusals, v1);
        handlers = new(StringComparer.Ordinal)
        {
            ["get-item"] = GetItem, ["append-body"] = AppendBody, ["comment"] = Comment, ["claim-item"] = ClaimItem, ["accept"] = Accept, ["release"] = Release, ["my-items"] = MyItems,
            ["record-review"] = RecordReview, ["move-next"] = MoveNext, ["complete"] = Complete, ["validation-verdict"] = ValidationVerdict, ["open-pr"] = OpenPr, ["update-pr"] = UpdatePr,
            ["item-pr"] = ItemPr, ["park-item"] = ParkItem, ["unpark-item"] = UnparkItem, ["resettle-item"] = ResettleItem, ["expire-claim"] = ExpireClaim,
        };
    }

    ItemVerbConfig Cfg => gh.Config;
    DateTime Now => clock.UtcNow;

    /// <summary>Runs one verb of the group the way <c>factory.ps1 --server</c> runs it: the answer is the fields of the script's JSON and its exit code.</summary>
    public async Task<ItemVerbAnswer> Run(string verb, string stdin, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!handlers.TryGetValue(verb, out var handler)) throw new ArgumentException($"'{verb}' is not an item verb", nameof(verb));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return await runner.Run(verb, stdin, handler, cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // as the runner answers a script that did not finish in time (Runner.RunProcessAsync)
            return new ItemVerbAnswer(20, new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "timeout", ["reason"] = "the rule script did not finish in time", ["retryAfterSeconds"] = 30, ["exit"] = 20 });
        }
    }

    // ---- shared by the verbs --------------------------------------------------------------------------------------------------------------------------

    /// <summary><c>Assert-Open</c>.</summary>
    static void AssertOpen(ItemInfo info) { if (!info.Open) throw new ItemRefusal(11, "item-closed", $"issue #{info.Number} is closed"); }

    /// <summary><c>Assert-ColumnMatches</c>: a completion for a column the item has left is stale.</summary>
    static void AssertColumnMatches(ItemInfo info, ItemCall c)
    {
        if (c.Has("column") && c.Text("column") != "" && !Ps.Eq(c.Text("column").Trim(), info.ColumnText))
            throw new ItemRefusal(11, "stale-column", $"stale completion: this agent was launched for '{c.Text("column")}' but the item is now in '{info.ColumnText}'. Another agent owns the current column; do nothing further and stop.");
    }

    /// <summary>The holder of the call: <c>holder</c> as text, "anonymous" when it is not there.</summary>
    static string HolderOf(ItemCall c) => c.Has("holder") ? c.Text("holder") : "anonymous";

    Task AddComment(int item, string text, ItemCall c) => board.AddComment(item, text, c.Session, c.Ct);

    /// <summary><c>Add-Journal</c> into the state in hand: the line is best effort, as in the script (it logs and goes on).</summary>
    async Task<bool> AddJournal(int item, ItemStateHolder st, string ev, JsonObject fields, string key, ItemCall c)
    {
        try { return await journal.Add(item, st, ev, fields, key, c.Ct); }
        catch (OperationCanceledException) when (c.Ct.IsCancellationRequested) { throw; }
        catch (Exception e) { Console.Error.WriteLine($"journal {ev} for #{item} failed: {e.Message}"); return false; }
    }

    /// <summary>The call is one this port leaves to the rule script (see <see cref="ItemDelegated"/>): nothing may have been written when this is thrown.</summary>
    static ItemDelegated ToV1(string reason) => new(reason);

}
