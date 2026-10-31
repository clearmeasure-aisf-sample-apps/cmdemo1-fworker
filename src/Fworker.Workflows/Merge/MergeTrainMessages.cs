using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Merge;

// W7.2 (#339): the messages of the merge train, one train per repository (docs/design/wolverine-factory.md 3.2: ReadyToMerge, Merged, Dequeued). The work item saga already asks for a merge with MergeRequested and
// decides on the answer (MergeAttempted); the train sits between the two and changes nothing of that contract. What it adds is the order: with the train strategy only the pull request first in line is
// let through to the merge, the others are answered "blocked: merge-train" (a wait the saga asks again after, as it does for a blocked check), so a branch is updated at most once per merge ahead of it.
//
//   saga --MergeRequested--> router --ReadyToMerge--> train --MergeGranted--> merge port (GitHub) --MergeTrainAnswered--> train --MergeAttempted--> saga
//
// Everything that reaches a train is a MergeTrainInput addressed by the repository ("owner/repo", lower case). What the train publishes: MergeGranted (the turn: the gateway's merge port runs the C# direct merge of
// W6.2), MergeAttempted (to the saga: the answer of the port, or the wait of the line) and MergeTrainDecided (the history). Delivery is at least once and the train is a function of its saved state, so an input
// applied twice gives the same answers.

/// <summary>Any input of a merge train.</summary>
public abstract record MergeTrainInput(string Key) : IMessage;

/// <summary>The pull request of an item is ready to be merged: the saga's request, as it was sent. Asking again (a later attempt of the same arm) keeps the place in the line; a new arm (the item was validated again) takes the last one.</summary>
[MessageIdentity("merge.train.ready")]
public sealed record ReadyToMerge(string Key, MergeRequested Request) : MergeTrainInput(Key);

/// <summary>The merge port's answer to a <see cref="MergeGranted"/>: what the merge call did. The train settles its line by it and hands the answer on to the saga.</summary>
[MessageIdentity("merge.train.answered")]
public sealed record MergeTrainAnswered(string Key, MergeRequested Request, MergeResult Result) : MergeTrainInput(Key);

/// <summary>An item no longer waits for its turn (merged by a person or the queue, closed, taken elsewhere): its place in the line is released.</summary>
[MessageIdentity("merge.train.left")]
public sealed record MergeTrainLeft(string Key, ItemId Item, string Reason, int Generation = 0) : MergeTrainInput(Key);

/// <summary>
/// The turn: the train lets this request through to the merge. <paramref name="FirstTurn"/> is true for the first time the item gets the turn (the merge port then brings a branch that is behind up to date, once per
/// verdict, as the script does). Sending it again is safe: the merge command is idempotent on its key.
/// </summary>
[MessageIdentity("merge.train.granted")]
public sealed record MergeGranted(string Key, MergeRequested Request, bool FirstTurn) : ICommand;

/// <summary>What the train decided, once per change of its line, for the item history. <paramref name="Action"/> is one of <see cref="MergeTrainActions"/>.</summary>
[MessageIdentity("merge.train.decided")]
public sealed record MergeTrainDecided(string Key, long Sequence, string Action, ItemId Item, int Generation, IReadOnlyList<string> Reasons, DateTimeOffset At) : IEvent;

/// <summary>The actions of <see cref="MergeTrainDecided"/>.</summary>
public static class MergeTrainActions
{
    /// <summary>The item took a place in the line.</summary>
    public const string Enqueued = "enqueued";
    /// <summary>The item got the turn: its merge call was let through.</summary>
    public const string Granted = "granted";
    /// <summary>The merge went through (<c>Merged(commit)</c> of the design).</summary>
    public const string Merged = "merged";
    /// <summary>The item went to the back of the line: its merge is blocked for a reason of its own (no label, a hold, a draft) or it held the first place too long.</summary>
    public const string Rotated = "rotated";
    /// <summary>The item left the line without a merge (<c>Dequeued(reason)</c> of the design): refused for good, its head moved, merged elsewhere, or it stopped asking.</summary>
    public const string Dequeued = "dequeued";
}
