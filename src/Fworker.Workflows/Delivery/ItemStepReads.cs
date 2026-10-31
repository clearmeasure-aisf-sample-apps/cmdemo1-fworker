using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery.Verbs;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Delivery;

// W9.10f: what the saga's steps read of GitHub for the sweep's item rules (Fworker.Workflows.WorkItem.SweepFlow), as the same rules the sweep applied in the rule scripts, over the kit of the delivery verbs (the same GitHub calls, the
// same configuration): master's CI for the merge commit (Verbs.Delivery.ps1, the master-green wait of Invoke-Settle and Find-GreenMaster), the state of a pull request, and a human's approval of its head (Verbs.Inspect.ps1,
// Find-HumanApproval). The gateway's watcher (Ports/ItemStepsPort.cs) calls them and tells the saga; nothing here writes the saga or the state comment. The one write is the re-run of the failed jobs (issue 79), which the
// settle made too, once.

/// <summary>What master's CI says of a merge commit: the verdict, the checks that failed (red), the commit that made it green (green; a later one for a wait that was parked), and the runs re-run just now (pending).</summary>
public sealed record MasterReading(MasterVerdict Verdict, IReadOnlyList<string> Failed, string Via = "", IReadOnlyList<RerunRun>? Rerun = null);

/// <summary>The part of the saga's state a reading of master needs.</summary>
public sealed record MasterWait(string Commit, string State, DateTimeOffset? Since, DateTimeOffset? RerunAt, IReadOnlyList<RerunRun> RerunRuns)
{
    public static MasterWait Of(WorkItemState s) => new(s.MergeCommit, s.MasterState, s.MasterSince, s.MasterRerunAt, s.MasterRerunRuns);
}

public interface IMasterChecks
{
    /// <summary>Reads master's CI for the item's merge commit. A read that cannot be made throws; it is never answered as a state.</summary>
    Task<MasterReading> Read(WorkItemState state, CancellationToken ct);
}

/// <summary>A pull request as the watcher needs it.</summary>
public sealed record PullRequestFacts(int Number, string State, bool Merged, string Head, string Author)
{
    public bool ClosedWithoutMerge => !Merged && string.Equals(State, "closed", StringComparison.OrdinalIgnoreCase);
}

public interface IItemPullRequests
{
    /// <summary>The pull request now, or null when GitHub does not know it.</summary>
    Task<PullRequestFacts?> State(int pullRequest, CancellationToken ct);

    /// <summary>The human approver (humanApprovers) who approved the head, other than the pull request's author; null when none did (Find-HumanApproval).</summary>
    Task<string?> Approver(int pullRequest, string head, CancellationToken ct);
}

/// <summary>Where the reads get their kit: one per call (the gateway makes it over its GitHub identity and the leader lease).</summary>
public interface IRuleKitSource
{
    Task<T> With<T>(Func<RuleKit, Task<T>> body, CancellationToken ct);
}

public sealed class ItemStepReads(IRuleKitSource kits) : IMasterChecks, IItemPullRequests
{
    public Task<MasterReading> Read(WorkItemState state, CancellationToken ct)
    {
        var wait = MasterWait.Of(state);
        return kits.With(kit => MasterCheckRules.Read(kit, wait, ct), ct);
    }

    public Task<PullRequestFacts?> State(int pullRequest, CancellationToken ct) =>
        kits.With<PullRequestFacts?>(async kit =>
        {
            var r = await GhApi.Get(kit.Gh, $"{kit.RepoPath}/pulls/{pullRequest}", ct, 404);
            if (!r.Ok || r.Body is not JsonObject pr) return null;
            return new PullRequestFacts(pullRequest, J.S(pr, "state"), J.Has(pr, "merged") && J.B(pr, "merged") || J.Has(pr, "merged_at"), J.S(pr["head"], "sha").ToLowerInvariant(), J.S(pr["user"], "login"));
        }, ct);

    public Task<string?> Approver(int pullRequest, string head, CancellationToken ct) =>
        kits.With<string?>(async kit =>
        {
            var humans = J.Strings(kit.Cfg["humanApprovers"]);
            if (humans.Count == 0) return null;
            var pr = await GhApi.Get(kit.Gh, $"{kit.RepoPath}/pulls/{pullRequest}", ct, 404);
            var author = pr.Ok ? J.S(pr.Body?["user"], "login") : "";
            // the latest review of each reviewer counts (Get-LatestReviews: the list is oldest first)
            var latest = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var review in J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/pulls/{pullRequest}/reviews?per_page=100", ct)).Body).Where(x => x is not null))
                latest[J.S(review!["user"], "login")] = review!;
            foreach (var (login, review) in latest)
                if (humans.Any(h => string.Equals(h, login, StringComparison.OrdinalIgnoreCase)) && login != author && J.S(review, "state") == "APPROVED" && string.Equals(J.S(review, "commit_id"), head, StringComparison.OrdinalIgnoreCase)) return login;
            return null;
        }, ct);
}

public static class MasterCheckRules
{
    /// <summary>
    /// The master-green wait of Invoke-Settle (SettleVerb.MasterGreen) without its writes. A wait that is open is judged on the merge commit's own checks: green is green; red is red only when nothing is pending and the one re-run of the failed
    /// jobs (issue 79) has been judged. The re-run is started here, once (the saga remembers the runs and the verdict waits for a newer, completed attempt of each); GitHub refuses it while a run of the commit is still going, so a failure
    /// that shows while runs are going is waited out first. A wait that is parked (broken or timed out) is read as Find-GreenMaster does: the merge commit's checks, else a later commit of the default branch that contains it.
    /// </summary>
    public static async Task<MasterReading> Read(RuleKit kit, MasterWait w, CancellationToken ct)
    {
        var sha = w.Commit; var now = kit.UtcNow; var timeout = kit.TimingNumber("masterGreenTimeoutMinutes");
        if (w.State is "broken" or "timeout")
        {
            var g = await SettleVerb.FindGreenMaster(kit, sha, ct);
            return g is null ? new MasterReading(MasterVerdict.Pending, []) : new MasterReading(MasterVerdict.Green, [], g.Value.Sha);
        }
        var chk = await kit.GetChecksFor(sha, ct, kit.MasterIgnoredChecks());
        if (chk.Status == "failure")
        {
            var runs = J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/actions/runs?head_sha={sha}&per_page=50", ct)).Body?["workflow_runs"]).Where(x => x is not null).Select(x => x!).ToList();
            var since = w.Since?.UtcDateTime;
            var rerun = w.RerunAt is not null || w.RerunRuns.Count > 0;
            if (!rerun)
            {
                var running = runs.Where(r => J.S(r, "status") != "completed").ToList();
                if ((running.Count > 0 || chk.PendingChecks.Count > 0) && !(since is { } s && (now - s).TotalMinutes > timeout)) return new MasterReading(MasterVerdict.Pending, []);
                var started = new List<RerunRun>();
                foreach (var r in runs.Where(r => J.S(r, "status") == "completed" && J.S(r, "conclusion") is "failure" or "timed_out" or "startup_failure"))
                {
                    var res = await kit.Gh.Send(HttpMethod.Post, $"{kit.RepoPath}/actions/runs/{J.S(r, "id")}/rerun-failed-jobs", null, ct);
                    if (res.Status is 200 or 201) started.Add(new RerunRun { Id = J.L(r["id"]), Attempt = J.Has(r, "run_attempt") ? J.I(r["run_attempt"]) : 1 });
                }
                if (started.Count > 0) return new MasterReading(MasterVerdict.Pending, [], Rerun: started);
            }
            else if (w.RerunRuns.Count > 0)
            {
                // the old attempt's results stand until each re-run has a newer attempt that completed
                var inFlight = w.RerunRuns.Where(rr =>
                {
                    var cur = runs.FirstOrDefault(x => J.L(x["id"]) == rr.Id);
                    return cur is null || J.I(cur["run_attempt"]) <= rr.Attempt || J.S(cur, "status") != "completed";
                }).ToList();
                if (inFlight.Count > 0 && !(w.RerunAt is { } ra && (now - ra.UtcDateTime).TotalMinutes > timeout)) return new MasterReading(MasterVerdict.Pending, []);
            }
            if (chk.PendingChecks.Count == 0) return new MasterReading(MasterVerdict.Red, chk.FailedChecks);
        }
        return chk.Status == "success" ? new MasterReading(MasterVerdict.Green, [], sha) : new MasterReading(MasterVerdict.Pending, []);
    }
}
