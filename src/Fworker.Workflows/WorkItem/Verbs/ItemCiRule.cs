using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Workflows.ReadModels;

namespace Fworker.Workflows.WorkItem.Verbs;

/// <summary>
/// What the CI rule ruled for a red head (<c>New-Ruling</c>): <c>running</c> (a workflow run of the head is still going), <c>rerun</c> (the failed jobs were re-run just now, once per head), <c>rerun-wait</c>, <c>deferred</c> (the base branch's
/// checks could not be read), <c>master-defect</c> (every cause fails on the base branch too; <see cref="Fixed"/>: the base branch passes the reported defect's checks again; <see cref="Cached"/>: judged less than a check interval ago and nothing
/// was read) or <c>red</c> (still red for a reason of its own).
/// </summary>
public sealed record RedHeadRuling(string Ruling, IReadOnlyList<string> Causes, string Reason, IReadOnlyList<JsonObject> Runs, bool Fixed = false, bool Cached = false);

/// <summary>
/// W9.10q: the one CI rule for a red head (issue 102, Prs.ps1 <c>Get-RedHeadRuling</c>), which the completion of Development applies. A head whose gates failed is judged only once every workflow run of it has completed. Then, once per head, the failed
/// jobs are re-run when the failures that caused it are checks other than the gates (an aggregating gate like "Build result" fails because a job it needs failed), match masterFlakyChecks, or fail on the base branch too; the runs and attempts re-run
/// are recorded in pr.rerun[sha] and the head is judged again on the newer, completed attempt of each. A head still red after that whose causes all fail on the base branch as well is a master defect: it costs no fix-forward round and no demotion.
/// The ruling records a re-run in the state in hand; the caller saves it.
/// </summary>
public sealed class ItemCiRule(ItemGitHub gh, IItemChecks checks, IItemClock clock)
{
    ItemVerbConfig Cfg => gh.Config;

    static readonly StringComparer Sorted = StringComparer.InvariantCultureIgnoreCase;

    /// <summary><c>Get-RedCauses</c>: the checks that caused the red: the failed (or cancelled) checks that are not gates, and the failed gates that are not aggregating ones; when there are none of those, the failed gates themselves.</summary>
    public static IReadOnlyList<string> RedCauses(ChecksFacts chk, ItemVerbConfig cfg)
    {
        var agg = cfg.CiAggregateGates; var gates = cfg.CiGates;
        var own = chk.NongateFailed.Concat(chk.NongateCancelled).Concat(chk.FailedChecks.Where(f => gates.Any(g => Ps.Eq(g, f)) && !agg.Any(a => Ps.Eq(a, f))))
            .Where(x => x.Length > 0).OrderBy(x => x, Sorted).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (own.Count > 0) return own;
        return chk.FailedChecks.OrderBy(x => x, Sorted).ToList();
    }

    static RedHeadRuling New(string ruling, IEnumerable<string> causes, string reason, IEnumerable<JsonObject>? runs = null, bool @fixed = false, bool cached = false) =>
        new(ruling, causes.ToList(), reason, (runs ?? []).ToList(), @fixed, cached);

    /// <summary><c>Get-RedHeadRuling</c>: the ruling for a red head (<paramref name="chk"/> has status failure).</summary>
    public async Task<RedHeadRuling> Rule(JsonObject state, string sha, ChecksFacts chk, CancellationToken ct)
    {
        var def = Cfg.DefaultBranch;
        var causes = RedCauses(chk, Cfg);
        var md = Ps.Has(state, "master_defect") ? state["master_defect"] as JsonObject : null;
        if (md is not null && Ps.Eq(Ps.Text(md, "sha"), sha) && Ps.Has(md, "checked_at"))
        {
            if (Ps.FromIso(md["checked_at"]) is { } ca && (clock.UtcNow - ca).TotalMinutes < Cfg.MasterDefectCheckMinutes)
                return New("master-defect", Ps.Strings(md["checks"]), "looked at less than a check interval ago", cached: true);
        }
        // the branch was just brought up to date for the fixed defect: GitHub makes the new head a moment later (update branch answers 202 at once)
        if (md is not null && Ps.Has(md, "updated_for") && Ps.Eq(Ps.Text(md, "updated_for"), sha) && Ps.Has(md, "updated_at"))
        {
            if (Ps.FromIso(md["updated_at"]) is { } ua && (clock.UtcNow - ua).TotalMinutes < 3) return New("deferred", causes, "the branch was just brought up to date; its new head comes next");
        }
        var runsBody = await gh.Get($"{gh.RepoPath}/actions/runs?head_sha={sha}&per_page=50", ct);
        var runs = (Ps.Get(runsBody, "workflow_runs") as JsonArray ?? []).Where(r => r is not null).Select(r => r!).ToList();
        if (runs.Any(r => !Ps.Eq(Ps.Text(r, "status"), "completed"))) return New("running", causes, "a workflow run of the head is still in progress");
        if (!Ps.Has(state, "pr")) state["pr"] = new JsonObject();
        var pr = (JsonObject)state["pr"]!;
        if (!Ps.Has(pr, "rerun")) pr["rerun"] = new JsonObject();
        var rerun = (JsonObject)pr["rerun"]!;
        var rec = rerun.TryGetPropertyValue(sha, out var rn) ? rn as JsonObject : null;
        var baseFailures = await checks.BaseFailures(causes, ct);
        if (rec is null)
        {
            var flaky = causes.Where(f => Cfg.MasterFlakyChecks.Any(p => Regex.IsMatch(f, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToList();
            var why = new List<string>();
            var others = chk.NongateFailed.Concat(chk.NongateCancelled).ToList();
            if (others.Count > 0) why.Add($"checks other than the gates failed ({string.Join(", ", others)})");
            if (flaky.Count > 0) why.Add($"known flaky: {string.Join(", ", flaky)}");
            if (baseFailures.Preexisting.Count > 0) why.Add($"failing on {def} too: {string.Join(", ", baseFailures.Preexisting)}");
            if (why.Count > 0)
            {
                // (the record of the last 5 heads is kept: an older one can no longer be the head of this pull request)
                foreach (var old in rerun.OrderBy(kv => Ps.Text(kv.Value, "at"), StringComparer.InvariantCulture).Select(kv => kv.Key).SkipLast(4).ToList()) rerun.Remove(old);
                rec = new JsonObject { ["at"] = Ps.Iso(clock.UtcNow), ["runs"] = new JsonArray(), ["causes"] = Ps.Array(causes), ["reason"] = string.Join("; ", why), ["refused"] = new JsonArray() };
                rerun[sha] = rec;
                var started = (JsonArray)rec["runs"]!; var refused = (JsonArray)rec["refused"]!;
                foreach (var r in runs.Where(r => Ps.Text(r, "conclusion").ToLowerInvariant() is "failure" or "timed_out" or "startup_failure" or "cancelled"))
                {
                    int status;
                    try { status = (await gh.Raw(new ItemGhCall("POST", $"{gh.RepoPath}/actions/runs/{Ps.Text(r, "id")}/rerun-failed-jobs"), ct)).Status; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { status = 0; }
                    if (status is 200 or 201) started.Add(new JsonObject { ["id"] = long.Parse(Ps.Text(r, "id")), ["attempt"] = Ps.Has(r, "run_attempt") && Ps.Truthy(r["run_attempt"]) ? Ps.Int(r, "run_attempt") : 1 });
                    else refused.Add($"{Ps.Text(r, "id")}: {status}");
                }
                if (started.Count > 0) return New("rerun", causes, string.Join("; ", why), started.Select(x => (JsonObject)x!.DeepClone()));
            }
        }
        else if (rec["runs"] is JsonArray recRuns && recRuns.Count > 0)
        {
            // the first attempt's results stand until each re-run has a newer attempt that completed
            var inFlight = recRuns.Where(x => x is not null).Any(rr =>
            {
                var cur = runs.FirstOrDefault(r => Ps.Text(r, "id") == Ps.Text(rr, "id"));
                return cur is null || Ps.Int(cur, "run_attempt") <= Ps.Int(rr, "attempt") || !Ps.Eq(Ps.Text(cur, "status"), "completed");
            });
            if (inFlight) return New("rerun-wait", causes, "the re-run of the failed jobs has not finished");
        }
        if (!baseFailures.Available) return New("deferred", causes, $"the checks of {def} could not be read");
        if (causes.Count > 0 && baseFailures.Preexisting.Count == causes.Count)
            return New("master-defect", causes, $"{string.Join(", ", causes)} also fail{(causes.Count == 1 ? "s" : "")} on {def}");
        // the defect reported before (the same checks) is fixed on the base branch: the branch is brought up to date once for it, and every head after that is judged as any head
        if (md is not null && string.Join("|", Ps.Strings(md["checks"])) == string.Join("|", causes.OrderBy(x => x, Sorted)) && baseFailures.Green.Count == causes.Count && !Ps.Has(md, "updated_for"))
            return New("master-defect", causes, $"{string.Join(", ", causes)} pass on {def} again", @fixed: true);
        var again = rec is not null && rec["runs"] is JsonArray ra && ra.Count > 0;
        return New("red", causes, $"{(again ? "still red after one re-run of the failed jobs" : "red")}: {string.Join(", ", causes)}");
    }
}
