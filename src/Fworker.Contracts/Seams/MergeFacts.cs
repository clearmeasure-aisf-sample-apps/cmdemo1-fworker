using Fworker.Contracts.Ids;

namespace Fworker.Contracts.Seams;

// W5.0: what the derivation of ItemMerged reads from GitHub. The webhook event names a pull request or a merge commit, the board reconciler an item; the rest
// (title, branch, body, labels, the item's state comment) is read through this port. The implementation is Fworker.Integrations.GitHub.HttpMergeFactsReader.

/// <summary>A pull request as the merge derivation needs it. <see cref="MergeCommit"/> is lower case, null when GitHub names none. <see cref="MergedAt"/> is when it merged (null when GitHub names no time): a reopened item starts a new generation, and only a merge since its start is that generation's.</summary>
public sealed record MergedPullRequest(RepoId Repository, int Number, bool Merged, string? BaseRef, string? HeadRef, string? Title, string? Body, string? MergeCommit, DateTimeOffset? MergedAt = null);

/// <summary>An item as the gates read it: whether it carries the factory's state comment, its labels and body, and what the state comment says of the verdict and the merge.</summary>
public sealed record ItemMergeFacts(ItemId Item, bool IsFactoryItem, IReadOnlyList<string> Labels, string? Body, bool VerdictPassed, int? VerdictPr, int? MergePr, string? MergeBy, bool DemoVideoPublished);

/// <summary>W9.10h: an issue as the saga's producers need it: whether it is closed and its label names. One REST read of the issue; no comments are read.</summary>
public sealed record ItemIssue(ItemId Item, bool Closed, IReadOnlyList<string> Labels);

/// <summary>A check run of a commit as the settle of a merge needs it: its name, status and conclusion (null while it has none).</summary>
public sealed record CommitCheck(string Name, string? Status, string? Conclusion);

/// <summary>
/// W9.10h: the reads beside <see cref="IMergeFactsReader"/> that the producers of the saga's inputs make: the issue's state and labels (the dependencies of an item in the intake, the hold labels of an item whose card or labels
/// changed), the check runs of a commit (the merge commit's own Build) and the files of a pull request. Implemented by Fworker.Integrations.GitHub.HttpMergeFactsReader. A 404 is "not there" (null); any other failure throws.
/// </summary>
public interface IItemIssueReader
{
    Task<ItemIssue?> ReadIssue(ItemId item, CancellationToken ct = default);

    /// <summary>The check runs of the commit (up to the first two hundred), or null when GitHub does not know the commit.</summary>
    Task<IReadOnlyList<CommitCheck>?> ReadCheckRuns(RepoId repository, string commit, CancellationToken ct = default);

    /// <summary>The paths the pull request changes (up to three hundred), or null when it does not exist.</summary>
    Task<IReadOnlyList<string>?> ReadPullRequestFiles(RepoId repository, int number, CancellationToken ct = default);
}

public interface IMergeFactsReader
{
    /// <summary>The pull request, or null when it does not exist.</summary>
    Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default);

    /// <summary>The merged pull request that closed the item (its newest), or null when none is found.</summary>
    Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default);

    /// <summary>The item's facts, or null when the issue does not exist. The demo video is read only for an item that carries the video label.</summary>
    Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default);
}
