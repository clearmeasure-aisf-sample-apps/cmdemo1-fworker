using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

// W6.2: the direct merge strategy of plugin/scripts/lib/Verbs.Merge.ps1 (Verb_merge under the gateway: one step per call, never a long wait) as an
// integration command. The policy around it stays with the caller (the work-item saga): the Auto Merge label, a hold, the verdict and its head, the
// check status of the head, the migration collision, the state comment. This command is the GitHub side: the merge call itself with the PASS head, the
// mergeability wait, the update of a branch that is behind, the refusals that mean out of date, and the merge queue limit.
// v1 keeps Verbs.Merge.ps1 until W9.11; nothing registers this yet.

/// <summary>
/// Squash-merges <see cref="PullRequest"/> of <see cref="Repository"/> with <c>sha = <see cref="PassHeadSha"/></c>, the head the PASS verdict covers: GitHub itself
/// refuses the merge when the branch moved. The caller decides that the merge may happen (label, hold, verdict, green checks); this command does not read them.
/// </summary>
/// <param name="MergeQueueDepth">The limit of merges waiting for master CI or their release candidate (config mergeQueueDepth); 0 is off.</param>
/// <param name="QueueBusy">How many other merged items still wait there (the caller counts them from the item states); a documentation-only change never counts.</param>
/// <param name="DocsOnly">The change is documentation only: it builds no release, so the queue limit does not apply to it.</param>
/// <param name="UpdateWhenBehind">Bring a branch that is behind its base up to date before the merge (baseUpdate.autoUpdateBranch under mergePolicy auto). The caller sets it once per verdict, and only when the checks of the head are green, as the script does.</param>
/// <param name="UpdatedFrom">An earlier call updated the branch from this head (the verdict head): the head the branch has now is accepted when it is GitHub's own update of it.</param>
/// <param name="UpdatedTo">The commit that update named, when GitHub named one: the accepted head must be exactly that one.</param>
/// <param name="UpdatesSoFar">How many updates of this verdict were made (timing.outOfDateMergeAttempts bounds them): the count of the item state, so the calls add up to the same limit as the script's loop.</param>
/// <param name="MaxUpdates">timing.outOfDateMergeAttempts.</param>
public sealed record MergePullRequest(
    string IdempotencyKey, RepoId Repository, int PullRequest, string PassHeadSha,
    int MergeQueueDepth = 0, int QueueBusy = 0, bool DocsOnly = false,
    bool UpdateWhenBehind = false, string UpdatedFrom = "", string UpdatedTo = "", int UpdatesSoFar = 0, int MaxUpdates = 4) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "MergePullRequest";
    public override CommandTarget Target => CommandTarget.GitHub;

    /// <summary>The key of one merge attempt: the same pull request, PASS head and update count is the same side effect, so a replay merges once.</summary>
    public static string KeyFor(RepoId repository, int pullRequest, string passHeadSha, int updatesSoFar = 0) =>
        IdempotencyKeys.For("MergePullRequest", repository, pullRequest, passHeadSha, updatesSoFar);
}

/// <summary>How a merge call ended.</summary>
public enum MergeStatus
{
    /// <summary>The pull request is merged (or was already): <see cref="MergeOutcome.Sha"/> is the merge commit.</summary>
    Merged,
    /// <summary>The branch carries a head the PASS does not cover: nothing was merged, the verdict no longer applies.</summary>
    HeadMoved,
    /// <summary>It cannot be merged yet and nothing is wrong: the caller asks again later (the reason is the script's wait code).</summary>
    Blocked,
    /// <summary>GitHub refused it, or the call cannot work as asked: the reason says why. <see cref="MergeOutcome.Kind"/> says whether a retry can help.</summary>
    Failed,
}

/// <summary>
/// The answer of <see cref="MergeDirect"/>. <see cref="Reason"/> is a stable code: for Blocked the script's wait codes (merge-queue, mergeability-unknown, checks-pending,
/// branch-updated, merge-async), for HeadMoved stale-head, for Failed not-mergeable, pr-not-found, pr-not-open, github-forbidden, merge-refused, merge-no-sha,
/// update-refused, merge-out-of-date. <see cref="NewHead"/> is the head of GitHub's own update of the branch (branch-updated), which the caller records as the head the PASS covers.
/// </summary>
public sealed record MergeOutcome(MergeStatus Status, string Reason, string Detail = "", string Sha = "", string NewHead = "", FailureKind Kind = FailureKind.None, TimeSpan? RetryAfter = null)
{
    public bool Merged => Status == MergeStatus.Merged;
    public static MergeOutcome MergedAs(string sha, string reason = "merged") => new(MergeStatus.Merged, reason, "", sha);
    public static MergeOutcome HeadMoved(string detail) => new(MergeStatus.HeadMoved, "stale-head", detail, Kind: FailureKind.Permanent);
    public static MergeOutcome Blocked(string reason, string detail = "", string newHead = "") => new(MergeStatus.Blocked, reason, detail, NewHead: newHead, Kind: FailureKind.Transient);
    public static MergeOutcome Failed(string reason, string detail, FailureKind kind = FailureKind.Permanent, TimeSpan? retryAfter = null) => new(MergeStatus.Failed, reason, detail, Kind: kind, RetryAfter: retryAfter);

    // The ledger and the outbox store a CommandResult; the outcome travels in it: Execution = merge commit (or the new head of a blocked one),
    // Error = "merge/<status>/<reason>: <detail>". A permanent outcome (merged, head moved, refused) is recorded; a blocked one runs again with the same key.
    const string Prefix = "merge/";

    internal CommandResult ToResult() => Status == MergeStatus.Merged
        ? CommandResult.Success(Sha)
        : new CommandResult(false, Kind, $"{Prefix}{Status}/{Reason}: {Detail}", NewHead);

    internal static MergeOutcome From(CommandResult r)
    {
        if (r.Ok) return MergedAs(r.Execution, "merged");
        if (r.Error.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var rest = r.Error[Prefix.Length..];
            var slash = rest.IndexOf('/');
            var split = rest.IndexOf(": ", StringComparison.Ordinal);
            if (slash > 0 && split > slash && Enum.TryParse<MergeStatus>(rest[..slash], out var status))
                return new MergeOutcome(status, rest[(slash + 1)..split], rest[(split + 2)..], "", r.Execution, r.Failure);
        }
        // not made by MergeDirect: no idempotency key, a transport failure classified by the base handler
        return new MergeOutcome(MergeStatus.Failed, "command-failed", r.Error, Kind: r.Failure);
    }
}

/// <summary>The waits of the script (config timing.*), in the gateway's short form: no step sleeps longer than the caller can afford.</summary>
public sealed record MergeTiming(
    TimeSpan MergeabilityWait, TimeSpan MergeabilityPoll, TimeSpan UpdatedHeadWait, TimeSpan UpdatedHeadPoll, TimeSpan AsyncMergeWait, TimeSpan AsyncMergePoll)
{
    /// <summary>mergeabilityWaitSeconds 90 and mergeabilityPollSeconds 4 (config/instance.json), the update wait capped at 12 seconds and the stack merge wait at 30, as the script does under -Gateway.</summary>
    public static MergeTiming Default { get; } = new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3));
}
