using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

// W6.2: the direct merge strategy of plugin/scripts/lib/Verbs.Merge.ps1 (Verb_merge under the gateway: one step per call, never a long wait) as an
// integration command. The policy around it stays with the caller (the work-item saga): the Auto Merge label, a hold, the verdict and its head, the
// check status of the head, the migration collision, the state comment. This command is the GitHub side: the merge call itself with the PASS head, the
// mergeability wait, the update of a branch that is behind, the refusals that mean out of date, and the merge queue limit.
// v1 keeps Verbs.Merge.ps1 until W9.11; nothing registers this yet.
//
// Declared scope limit: only mergePolicy 'auto' (the policy of config/instance.json and of the docs plan, W6.6) is implemented. Under 'auto' the PASS covers one head and the merge
// sends exactly that head. 'peer-approved' merges the CURRENT head after the approval of that head (and, via the QC gate, a QC run and a human approval) and updates that head as it
// moves; none of that is here, so this command is NOT a replacement of Verb_merge for peer-approved items: they stay on v1 (the script) until a plan needs them.
// Also not here, on purpose: the pull request being ready is the caller's (a draft comes back Blocked 'pr-draft'; marking it ready is a GraphQL mutation of its own command), and the
// belongs-to-the-item check is done when the caller names the item (<see cref="MergePullRequest.Item"/>).

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
/// <param name="Item">The work item the pull request must belong to (Test-PrBelongs of the script); 0 leaves that check to the caller.</param>
/// <param name="RecordedPullRequest">The pull request number the item state records, which makes a copilot/* branch belong to the item as in the script.</param>
public sealed record MergePullRequest(
    string IdempotencyKey, RepoId Repository, int PullRequest, string PassHeadSha,
    int MergeQueueDepth = 0, int QueueBusy = 0, bool DocsOnly = false,
    bool UpdateWhenBehind = false, string UpdatedFrom = "", string UpdatedTo = "", int UpdatesSoFar = 0, int MaxUpdates = 4,
    int Item = 0, int RecordedPullRequest = 0) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "MergePullRequest";
    public override CommandTarget Target => CommandTarget.GitHub;

    /// <summary>The key of one merge attempt: the same pull request, PASS head, update count and arm is the same side effect, so a replay merges once. The arm (the saga's ArmId) changes whenever the item enters a step
    /// anew, which a resume of a parked item does, so what the ledger stored before an operator acted can never answer the resumed merge. An empty arm keeps the key of the callers that have none.</summary>
    public static string KeyFor(RepoId repository, int pullRequest, string passHeadSha, int updatesSoFar = 0, string armId = "") =>
        armId.Length == 0 ? IdempotencyKeys.For("MergePullRequest", repository, pullRequest, passHeadSha, updatesSoFar)
                          : IdempotencyKeys.For("MergePullRequest", repository, pullRequest, passHeadSha, updatesSoFar, armId);
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
/// The answer of <see cref="MergeDirect"/>. <see cref="Reason"/> is a stable code: for Merged merged or already-merged, for Blocked the script's wait codes (merge-queue, mergeability-unknown,
/// checks-pending, branch-updated, merge-async) and pr-draft, for HeadMoved stale-head, for Failed not-mergeable, pr-not-found, pr-not-open, pr-mismatch, github-forbidden, merge-refused,
/// merge-no-sha, update-refused, merge-out-of-date, rate-limited. <see cref="NewHead"/> is the head of GitHub's own update of the branch (branch-updated), which the caller records as the head the PASS covers.
/// <see cref="Updated"/> says that THIS call had GitHub update the branch (not that it recognised an update an earlier call made); <see cref="UpdatesAdded"/> is how much the caller adds to the
/// update count of the verdict (item state <c>verdict.updates</c>): like the script, only an update made after a refusal of the merge counts, the one before the merge does not.
/// </summary>
public sealed record MergeOutcome(
    MergeStatus Status, string Reason, string Detail = "", string Sha = "", string NewHead = "", FailureKind Kind = FailureKind.None, TimeSpan? RetryAfter = null,
    bool Updated = false, int UpdatesAdded = 0)
{
    public bool Merged => Status == MergeStatus.Merged;
    public static MergeOutcome MergedAs(string sha, string reason = "merged") => new(MergeStatus.Merged, reason, "", sha);
    public static MergeOutcome HeadMoved(string detail) => new(MergeStatus.HeadMoved, "stale-head", detail, Kind: FailureKind.Permanent);
    public static MergeOutcome Blocked(string reason, string detail = "", string newHead = "") => new(MergeStatus.Blocked, reason, detail, NewHead: newHead, Kind: FailureKind.Transient);
    public static MergeOutcome Failed(string reason, string detail, FailureKind kind = FailureKind.Permanent, TimeSpan? retryAfter = null) => new(MergeStatus.Failed, reason, detail, Kind: kind, RetryAfter: retryAfter);

    // The ledger and the outbox store a CommandResult, which has no field for most of the outcome. So the whole outcome travels in Error as "merge/" and one JSON object (an unambiguous
    // format: a detail may be empty or contain any text), and Execution holds the merge commit (Merged) or the new head (Blocked), as a reader of the plain result expects.
    // A permanent outcome (merged, head moved, a refusal that stays) is recorded; a blocked, transient or rate-limited one runs again with the same key.
    const string Prefix = "merge/";

    sealed record Wire(string Status, string Reason, string Detail, string Sha, string NewHead, double? RetryAfterSeconds, bool Updated, int UpdatesAdded);

    public CommandResult ToResult()
    {
        var wire = Prefix + System.Text.Json.JsonSerializer.Serialize(new Wire(Status.ToString(), Reason, Detail, Sha, NewHead, RetryAfter?.TotalSeconds, Updated, UpdatesAdded));
        return Status == MergeStatus.Merged ? new CommandResult(true, FailureKind.None, wire, Sha) : new CommandResult(false, Kind, wire, NewHead);
    }

    public static MergeOutcome From(CommandResult r)
    {
        if (r.Error.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                var w = System.Text.Json.JsonSerializer.Deserialize<Wire>(r.Error[Prefix.Length..]);
                if (w is not null && Enum.TryParse<MergeStatus>(w.Status, out var status))
                    return new MergeOutcome(status, w.Reason, w.Detail ?? "", w.Sha ?? "", w.NewHead ?? "", r.Failure,
                        w.RetryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null, w.Updated, w.UpdatesAdded);
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (r.Ok) return MergedAs(r.Execution, "merged");
        // not made by MergeDirect: no idempotency key, a transport failure classified by the base handler
        return new MergeOutcome(MergeStatus.Failed, "command-failed", r.Error, Kind: r.Failure);
    }
}

/// <summary>The waits of the script (config timing.*), in the gateway's short form: no step sleeps longer than the caller can afford. The mergeability wait polls at a fixed interval, as the script does.</summary>
public sealed record MergeTiming(
    TimeSpan MergeabilityWait, TimeSpan MergeabilityPoll, TimeSpan UpdatedHeadWait, TimeSpan UpdatedHeadPoll, TimeSpan AsyncMergeWait, TimeSpan AsyncMergePoll)
{
    /// <summary>mergeabilityWaitSeconds (90 in config/instance.json) capped at 12 and mergeabilityPollSeconds 4, the update wait capped at 12 seconds and the stack merge wait at 30, as the script does under -Gateway (Math.Min(..., 12)).</summary>
    public static MergeTiming Default { get; } = new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3));
}
