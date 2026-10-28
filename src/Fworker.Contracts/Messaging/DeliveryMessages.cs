using Fworker.Contracts.Ids;

namespace Fworker.Contracts.Messaging;

// W5.0 (docs/design/fworker-wolverine-implementation.md W5): the messages of the delivery train. ItemMerged goes in, DeliveredTo and Undelivered come out
// (docs/design/wolverine-factory.md 3.2). v1 emits none of them: ItemMerged is derived by the gateway from the merge webhook and from the board reconciler.

/// <summary>
/// The promotion gates of one merged item, read from the item when it merged: the Auto Merge label, a hold label, a pull request merged by the gateway after a
/// Functional Testing PASS, and the demo video. <see cref="Gaps"/> are v1's gap texts (Get-ItemPromotionGapsRead), empty when the item qualifies.
/// </summary>
public sealed record ItemMergedGates(bool AutoMerge, string Hold, bool PassMerged, bool VideoRequired, bool DemoVideoPublished, IReadOnlyList<string> Gaps)
{
    public bool Qualifies => Gaps.Count == 0;
}

/// <summary>
/// An item's pull request was squash merged into the default branch as <paramref name="Commit"/> (lower case, 40 hex characters). <paramref name="Dependencies"/> are the
/// qualified items the item "Depends on" (the cross-repository ones included, hook H5 of system-gateway.md). <paramref name="Source"/> is "webhook" or "poll".
/// A merge commit that closes several items gives one message per item.
/// </summary>
[MessageIdentity("delivery.item-merged")]
public sealed record ItemMerged(ItemId Item, string Commit, int PullRequest, ItemMergedGates Gates, IReadOnlyList<ItemId> Dependencies, string Source) : ItemEvent(Item);

/// <summary>The item reached <paramref name="Environment"/> (tdd, uat or prod) in <paramref name="Release"/>: the delivery train's output, kept in the delivered-to table.</summary>
[MessageIdentity("delivery.delivered-to")]
public sealed record DeliveredTo(ItemId Item, string Environment, string Release) : ItemEvent(Item);

/// <summary>The item is no longer in <paramref name="Environment"/> (a rollback or a failed deployment): its delivered-to row goes.</summary>
[MessageIdentity("delivery.undelivered")]
public sealed record Undelivered(ItemId Item, string Environment) : ItemEvent(Item);
