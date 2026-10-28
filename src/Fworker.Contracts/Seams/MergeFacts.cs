using Fworker.Contracts.Ids;

namespace Fworker.Contracts.Seams;

// W5.0: what the derivation of ItemMerged reads from GitHub. The webhook event names a pull request or a merge commit, the board reconciler an item; the rest
// (title, branch, body, labels, the item's state comment) is read through this port. The implementation is Fworker.Integrations.GitHub.HttpMergeFactsReader.

/// <summary>A pull request as the merge derivation needs it. <see cref="MergeCommit"/> is lower case, null when GitHub names none.</summary>
public sealed record MergedPullRequest(RepoId Repository, int Number, bool Merged, string? BaseRef, string? HeadRef, string? Title, string? Body, string? MergeCommit);

/// <summary>An item as the gates read it: whether it carries the factory's state comment, its labels and body, and what the state comment says of the verdict and the merge.</summary>
public sealed record ItemMergeFacts(ItemId Item, bool IsFactoryItem, IReadOnlyList<string> Labels, string? Body, bool VerdictPassed, int? VerdictPr, int? MergePr, string? MergeBy, bool DemoVideoPublished);

public interface IMergeFactsReader
{
    /// <summary>The pull request, or null when it does not exist.</summary>
    Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default);

    /// <summary>The merged pull request that closed the item (its newest), or null when none is found.</summary>
    Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default);

    /// <summary>The item's facts, or null when the issue does not exist. The demo video is read only for an item that carries the video label.</summary>
    Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default);
}
