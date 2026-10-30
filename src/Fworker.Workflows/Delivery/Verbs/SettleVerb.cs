using System.Globalization;
using System.Text.Json.Nodes;

namespace Fworker.Workflows.Delivery.Verbs;

// W9.4: settle (Verbs.Delivery.ps1 Verb_settle and Invoke-Settle): one idempotent step of the post-merge flow of one item, read from the item's state comment (merge.awaiting): master green (with the one re-run of the
// failed jobs of issue 79), the release candidate that carries the merge, the docs-only skip, the Release Queue and Done. The v1 sweep calls the same step for every item that waits (Invoke-SettleLocked, inside the
// script of sync-board); this verb is the admin's call of it, and is answered here for the environments where the engine acts.
public static class SettleVerb
{
    public const string Verb = "settle";

    static string N(double d) => d.ToString(CultureInfo.InvariantCulture);
    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static async Task<JsonObject> Run(RuleKit kit, JsonObject a, CancellationToken ct)
    {
        var n = VerbArgs.ItemNumber(a);
        var r = await Step(kit, n, ct);
        r["ok"] = true; r["id"] = n;
        return r;
    }

    /// <summary>Invoke-Settle.</summary>
    public static async Task<JsonObject> Step(RuleKit kit, int n, CancellationToken ct)
    {
        var info = await kit.GetItemInfo(n, ct); var st = await kit.GetState(n, ct); var s = st.State;
        if (!J.Has(s, "merge") || !J.Has(s["merge"], "awaiting") || !J.B(s["merge"], "awaiting")) return J.Obj(("outcome", "nothing-to-settle"));
        var m = (JsonObject)s["merge"]!;
        // A merged pull request that says "Closes #N" closes the item itself: that is not the end of the flow. Only an item closed without a merge stops here.
        if (!info.Open && !J.Has(m, "sha")) { m["awaiting"] = null; await kit.SaveState(n, st, ct); return J.Obj(("outcome", "closed")); }
        var sha = J.S(m, "sha"); var now = kit.UtcNow; var t = kit.Timing;
        var sha7 = VerbArgs.Short(sha);
        double Time(string key) => kit.TimingNumber(key);

        async Task Finish(string text)
        {
            await kit.AddComment(n, text, ct);
            await kit.MoveCard(info, await kit.LastColumn(ct), ct); await kit.CloseIssue(n, ct);
            m["awaiting"] = null; m["done_at"] = J.Iso(kit.UtcNow); await kit.SaveState(n, st, ct);
        }

        var awaiting = J.S(m, "awaiting");
        if (awaiting == "master-green") return await MasterGreen(kit, n, info, st, s, m, sha, now, ct);
        if (awaiting is "master-broken" or "master-timeout")
        {
            // issue 114: the wait stays through the park. A master-broken park is lifted only by auto-unpark (every check that failed passed on master, then judged on that head, issue 116) or by a person; while it
            // stands the item just waits. Once it is gone (a person released it, or it expired), and for a master-green-timeout park, the item goes on when master is green for the merge.
            var live = kit.GetPark(s);   // (an expired park no longer holds the item)
            if (awaiting == "master-broken" && live is not null && J.S(live, "reason") == "master-broken") return J.Obj(("outcome", "waiting"), ("awaiting", "master-broken"));
            var g = await FindGreenMaster(kit, sha, ct);
            if (g is null) return J.Obj(("outcome", "waiting"), ("awaiting", awaiting));
            var was = awaiting;
            m["green_at"] = J.Iso(now); m["green_via"] = g.Value.Sha;
            m["awaiting"] = "release-candidate"; m["rc_deadline"] = J.Iso(now.AddMinutes(Time("releaseCandidateTimeoutMinutes")));
            if (J.Has(s, "park") && J.S(s["park"], "reason") is "master-broken" or "master-green-timeout") s.Remove("park");   // (only an expired master-broken park reaches here)
            var contains = g.Value.Own ? "" : $", which contains `{sha7}`";
            await kit.AddComment(n, $"master is GREEN at `{VerbArgs.Short(g.Value.Sha)}`{contains} - waiting for the release candidate that carries it.", ct);
            await kit.SaveState(n, st, ct);
            return J.Obj(("outcome", "advanced"), ("awaiting", "release-candidate"), ("recovered", was));
        }
        if (awaiting is "release-candidate" or "late-rc")
        {
            var chk = await kit.GetChecksFor(sha, ct);
            chk.Runs.TryGetValue(J.S(kit.Cfg, "releaseCheckName"), out var rel);
            // docs-only is decided from the merge commit's own Build or the pull request's files, once, before the release check is read
            if (!J.Has(m, "docs_only"))
            {
                var dk = await MergeDocsOnly(kit, m, chk, ct);
                if (dk is not null) { m["docs_only"] = dk.DocsOnly; m["docs_only_from"] = dk.From; }
            }
            var docsOnly = J.Has(m, "docs_only") && J.B(m, "docs_only");
            var lastColumn = await kit.LastColumn(ct);
            if (docsOnly && rel is not null && J.S(rel, "status") == "completed" && J.S(rel, "conclusion") == "skipped")
            {
                await Finish($"the master build published no release candidate for `{sha7}` - its publish step was skipped because no code changed. A docs-only change has nothing to deploy, so it is moved straight to **{lastColumn}**.");
                return J.Obj(("outcome", "done"), ("path", "docs-only"));
            }
            // A docs-only change builds no release. If the Release workflow never produced a 'release' check, do not wait for one: finish once master has been green for the grace period.
            var since = J.Has(m, "green_at") ? J.FromIso(m, "green_at") : J.FromIso(m, "merged_at");
            if (docsOnly && rel is null && since is { } sn && (now - sn).TotalSeconds >= Time("docsOnlyReleaseGraceSeconds"))
            {
                await Finish($"the change is documentation only and no release was built for `{sha7}` ({N(Time("docsOnlyReleaseGraceSeconds"))} s after master went green). A docs-only change has nothing to deploy, so it is moved straight to **{lastColumn}**.");
                return J.Obj(("outcome", "done"), ("path", "docs-only-no-release"));
            }
            var d = await OctopusDelivery.Get(kit, sha, ct);
            if (d.Candidate is not null)
            {
                // issue 117: the card moves on only from Functional Testing; one that is elsewhere (already in the Release Queue, or moved by a person) stays
                string? next = null;
                if (Eq(info.Column ?? "", kit.Column("validation"))) next = await NextColumn(kit, info.Column!, ct);
                if (next is not null && !Eq(next, lastColumn)) await kit.MoveCard(info, next, ct);
                next ??= info.Column ?? "";
                var version = J.S(d.Candidate, "version");
                m["awaiting"] = "release-queue"; m["release"] = version; m["queued_at"] = J.Iso(now);
                await kit.AddComment(n, $"release candidate **{version}** carries `{sha7}` - waiting in **{next}** for the production deployment.", ct);
                await kit.SaveState(n, st, ct);
                return J.Obj(("outcome", "advanced"), ("awaiting", "release-queue"), ("release", version), ("queued_at", J.S(m, "queued_at")));
            }
            var deadline = J.FromIso(m, "rc_deadline");
            if (J.S(m, "awaiting") == "release-candidate" && deadline is { } dl && now > dl)
            {
                m["awaiting"] = "late-rc"; m["late_deadline"] = J.Iso(now.AddHours(Time("lateReleaseCandidateHours")));
                kit.SetPark(s, "fv:release-candidate-timeout", Time("lateReleaseCandidateHours") * 60);
                await kit.AddComment(n, $"master went green but no release candidate carrying it was published within {N(Time("releaseCandidateTimeoutMinutes"))} min - parked for a human. The worker keeps looking for {N(Time("lateReleaseCandidateHours"))} h.", ct);
                await kit.SaveState(n, st, ct);
                return J.Obj(("outcome", "parked"), ("reason", "release-candidate-timeout"), ("still_looking", true));
            }
            if (J.S(m, "awaiting") == "late-rc" && J.FromIso(m, "late_deadline") is { } late && late < now && !(J.Has(m, "late_reported") && J.B(m, "late_reported")))
            {
                // issue 114: the wait is not erased (the item would be offered to a worker again): it keeps looking, and says so once
                m["late_reported"] = J.Iso(now); kit.SetPark(s, "fv:release-candidate-timeout", Time("parkHours") * 60);
                await kit.AddComment(n, $"still no release candidate carrying `{sha7}` after {N(Time("lateReleaseCandidateHours"))} h - parked for a human. The gateway keeps looking.", ct);
                await kit.SaveState(n, st, ct);
                return J.Obj(("outcome", "parked"), ("reason", "release-candidate-timeout"), ("still_looking", true));
            }
            return J.Obj(("outcome", "waiting"), ("awaiting", J.S(m, "awaiting")));
        }
        if (awaiting == "release-queue")
        {
            var d = await OctopusDelivery.Get(kit, sha, ct);
            var o = J.Obj(kit.Cfg, "octopus");
            var prodAt = J.Has(o, "prodUrl") && J.S(o, "prodUrl").Length > 0 ? $" Production: {J.S(o, "prodUrl")}" : "";
            if (d.ProdSuccess)
            {
                await Finish($"Delivered to **Prod** - moved to **{await kit.LastColumn(ct)}**. Release {J.S(m, "release")}.{prodAt}");
                return J.Obj(("outcome", "done"), ("path", "prod"));
            }
            var later = await OctopusDelivery.ProdContaining(kit, sha, ct);
            if (later is not null)
            {
                await Finish($"Delivered to **Prod** in release {later}, which contains this change - moved to **{await kit.LastColumn(ct)}**.{prodAt}");
                return J.Obj(("outcome", "done"), ("path", "prod-later-release"), ("release", later));
            }
            // a failed production deployment of the item's release: said once per deployment on the item, and the owner's status shows prod-failed
            JsonNode? pd = null;
            var prodName = J.S(J.Obj(o, "environments"), "prod");
            foreach (var kv in d.Deployments) if (Eq(kv.Key, prodName)) pd = kv.Value;
            if (pd is not null && J.B(pd, "completed") && J.S(pd, "state") is "Failed" or "TimedOut" && J.S(m, "prod_failed") != J.S(pd, "deployment"))
            {
                m["prod_failed"] = J.S(pd, "deployment"); await kit.SaveState(n, st, ct);
                await kit.AddComment(n, $"**Prod deployment FAILED** for release {J.S(m, "release")} ({J.S(pd, "deployment")}, {J.S(pd, "state")}). The item stays in the Release Queue for the operator: an infrastructure failure is deployed again; a code failure sends the item back to Development, where its owner fixes forward.", ct);
            }
            // since when it waits (the Release Queue age of the digest): an item queued before queued_at was recorded counts from master green, or the merge
            var queued = J.Has(m, "queued_at") ? J.S(m, "queued_at") : J.Has(m, "green_at") ? J.S(m, "green_at") : J.S(m, "merged_at");
            return J.Obj(("outcome", "waiting"), ("awaiting", "release-queue"), ("deployments", d.Deployments), ("release", J.S(m, "release")), ("queued_at", queued));
        }
        return J.Obj(("outcome", "unknown-state"), ("awaiting", awaiting));
    }

    static async Task<JsonObject> MasterGreen(RuleKit kit, int n, ItemInfo info, StateHolder st, JsonObject s, JsonObject m, string sha, DateTime now, CancellationToken ct)
    {
        double Time(string key) => kit.TimingNumber(key);
        var sha7 = VerbArgs.Short(sha);
        // judge_sha: an auto-unpark found master green on a later head while the merge commit's own run stayed red (issue 70): that head is judged
        var chk = await kit.GetChecksFor(J.Has(m, "judge_sha") && J.S(m, "judge_sha").Length > 0 ? J.S(m, "judge_sha") : sha, ct, kit.MasterIgnoredChecks());
        var mergedAt = J.FromIso(m, "merged_at");
        var rerunAt = J.Has(m, "rerun_at") ? J.FromIso(m, "rerun_at") : null;
        if (chk.Status == "failure")
        {
            // Issue 79: the failed jobs are re-run once before master is declared broken (a flaky acceptance shard must not park a merged item). GitHub refuses rerun-failed-jobs for a workflow run that is still in progress,
            // so the re-run waits until every run of the commit has completed; the runs and attempts re-run are recorded, and the verdict waits for a newer, completed attempt of each of them.
            var runs = J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/actions/runs?head_sha={sha}&per_page=50", ct)).Body?["workflow_runs"]).Where(x => x is not null).Select(x => x!).ToList();
            var running = runs.Where(r => J.S(r, "status") != "completed").ToList();
            if (!(J.Has(m, "rerun_attempted") && J.B(m, "rerun_attempted")))
            {
                if (running.Count > 0 || chk.PendingChecks.Count > 0)
                    if (!(mergedAt is { } ma && (now - ma).TotalMinutes > Time("masterGreenTimeoutMinutes")))
                        return J.Obj(("outcome", "waiting"), ("awaiting", "master-green"), ("reason", "failed-run-in-progress"), ("checks", chk.Public()));
                var flaky = await FlakyReason(kit, sha, chk.FailedChecks, ct);
                var started = new JsonArray(); var refused = new List<string>();
                foreach (var r in runs.Where(r => J.S(r, "status") == "completed" && J.S(r, "conclusion") is "failure" or "timed_out" or "startup_failure"))
                {
                    var res = await kit.Gh.Send(HttpMethod.Post, $"{kit.RepoPath}/actions/runs/{J.S(r, "id")}/rerun-failed-jobs", null, ct);
                    if (res.Status is 200 or 201) started.Add(J.Obj(("id", J.L(r["id"])), ("attempt", J.B(r, "run_attempt") ? J.I(r["run_attempt"]) : 1)));
                    else refused.Add($"{J.S(r, "id")}: {res.Status}");
                }
                m["rerun_attempted"] = true; m["rerun_at"] = J.Iso(now); m["rerun_runs"] = started.DeepClone();
                var failedList = string.Join(", ", chk.FailedChecks);
                if (started.Count > 0)
                {
                    await kit.AddComment(n, $"master checks failed at `{sha7}` ({failedList}) - {flaky}; re-running the failed jobs once (workflow run {string.Join(", ", started.Select(x => J.S(x, "id")))}), then judging the new attempt.", ct);
                    await kit.SaveState(n, st, ct);
                    return J.Obj(("outcome", "rerun"), ("failed", chk.FailedChecks), ("runs", started));
                }
                await kit.AddComment(n, $"master checks failed at `{sha7}` ({failedList}) - {flaky}; the failed jobs could not be re-run ({(refused.Count > 0 ? "GitHub refused: " + string.Join(", ", refused) : "no failed workflow run found")}).", ct);
            }
            else if (J.Items(m["rerun_runs"]).Count(x => x is not null) > 0)
            {
                // the old attempt's results stand until each re-run has a newer attempt that completed
                var inFlight = J.Items(m["rerun_runs"]).Where(rr =>
                {
                    var cur = runs.FirstOrDefault(x => J.L(x["id"]) == J.L(rr?["id"]));
                    return cur is null || J.I(cur["run_attempt"]) <= J.I(rr?["attempt"]) || J.S(cur, "status") != "completed";
                }).ToList();
                if (inFlight.Count > 0 && !(rerunAt is { } ra && (now - ra).TotalMinutes > Time("masterGreenTimeoutMinutes")))
                    return J.Obj(("outcome", "waiting"), ("awaiting", "master-green"), ("reason", "rerun-in-progress"), ("checks", chk.Public()));
            }
            if (chk.PendingChecks.Count == 0)
            {
                // issue 114: the wait stays (master-broken) and is polled; the item goes on once master is green for it, and is never offered meanwhile
                kit.SetPark(s, "master-broken", Time("parkHours") * 60);
                s["park"]!["failed"] = J.Node(chk.FailedChecks); s["park"]!["sha"] = sha; m["awaiting"] = "master-broken"; m["broken_at"] = J.Iso(now);
                await kit.SaveState(n, st, ct);
                var again = J.Has(m, "rerun_runs") && J.Items(m["rerun_runs"]).Count(x => x is not null) > 0 ? ", also after one re-run of the failed jobs" : "";
                await kit.AddComment(n, $"**master is BROKEN** at `{sha7}` after the merge ({string.Join(", ", chk.FailedChecks)}){again} - parked for a human. The gateway keeps watching: the park is lifted by itself once each check that failed has passed on a green master head, which is then judged. Auto-revert is out of scope.", ct);
                return J.Obj(("outcome", "parked"), ("reason", "master-broken"));
            }
        }
        if (chk.Status == "success")
        {
            m["green_at"] = J.Iso(now);
            m["awaiting"] = "release-candidate"; m["rc_deadline"] = J.Iso(now.AddMinutes(Time("releaseCandidateTimeoutMinutes")));
            await kit.AddComment(n, $"master is GREEN at `{sha7}` - waiting for the release candidate that carries it.", ct);
            await kit.SaveState(n, st, ct);
            return J.Obj(("outcome", "advanced"), ("awaiting", "release-candidate"));
        }
        // a re-run started counts the timeout again from the re-run (issue 79): an acceptance re-run takes as long as the first run
        var since = rerunAt is { } rr2 && mergedAt is { } mm && rr2 > mm ? rerunAt : mergedAt;
        if (since is { } sn && (now - sn).TotalMinutes > Time("masterGreenTimeoutMinutes"))
        {
            kit.SetPark(s, "master-green-timeout", Time("parkHours") * 60); m["awaiting"] = "master-timeout"; await kit.SaveState(n, st, ct);
            await kit.AddComment(n, $"master CI did not go green within {N(Time("masterGreenTimeoutMinutes"))} min of the merge - parked for a human. The gateway keeps watching: the item goes on by itself once this commit, or a later master commit that contains it, is green.", ct);
            return J.Obj(("outcome", "parked"), ("reason", "master-green-timeout"));
        }
        return J.Obj(("outcome", "waiting"), ("awaiting", "master-green"), ("checks", chk.Public()));
    }

    /// <summary>Get-NextColumn: the board column after <paramref name="column"/>, null at the end.</summary>
    static async Task<string?> NextColumn(RuleKit kit, string column, CancellationToken ct)
    {
        var cols = (await kit.ProjectMeta(ct)).Options;
        for (var i = 0; i < cols.Count; i++) if (Eq(cols[i].Name, column)) return i + 1 < cols.Count ? cols[i + 1].Name : null;
        return null;
    }

    /// <summary>Get-FlakyReason (issue 79): why a failure on master looks flaky, for the item's comment. Only the wording depends on it; the re-run happens either way. Never throws.</summary>
    static async Task<string> FlakyReason(RuleKit kit, string sha, IReadOnlyList<string> failed, CancellationToken ct)
    {
        var pats = J.Has(kit.Cfg, "masterFlakyChecks") ? J.Strings(kit.Cfg["masterFlakyChecks"]) : [];
        var known = failed.Where(f => pats.Any(p => System.Text.RegularExpressions.Regex.IsMatch(f, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase))).ToList();
        if (known.Count > 0) return $"known flaky: {string.Join(", ", known)}";
        try
        {
            var elsewhere = new List<string>();
            var commits = J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/commits?sha={kit.DefaultBranch}&per_page=6", ct)).Body).Where(c => c is not null && J.S(c, "sha") != sha).Take(5).ToList();
            foreach (var cm in commits)
            {
                var o = await kit.GetChecksFor(J.S(cm, "sha"), ct, kit.MasterIgnoredChecks());
                elsewhere.AddRange(failed.Where(f => o.FailedChecks.Contains(f, StringComparer.OrdinalIgnoreCase) && !elsewhere.Contains(f, StringComparer.OrdinalIgnoreCase)));
            }
            if (elsewhere.Count > 0) return $"also failed on a recent master commit: {string.Join(", ", elsewhere)}";
        }
        catch (Exception e) when (e is VerbReject or HttpRequestException) { }
        return "not known to be flaky";
    }

    /// <summary>Get-MergeDocsOnly: the merge commit's own Build first, then the files of the pull request; null when neither can be read.</summary>
    static async Task<Fworker.Domain.MergeDocsOnlyVerdict?> MergeDocsOnly(RuleKit kit, JsonObject m, ChecksFor chk, CancellationToken ct)
    {
        var runs = chk.Runs.ToDictionary(kv => kv.Key, kv => new Fworker.Domain.DocsCheckRun(J.S(kv.Value, "status"), J.Has(kv.Value, "conclusion") ? J.S(kv.Value, "conclusion") : null), StringComparer.OrdinalIgnoreCase);
        var settings = kit.DocsSettings();
        var v = Fworker.Domain.DocsOnly.MergeIsDocsOnly(runs, null, settings);
        if (v is not null || !J.Has(m, "pr")) return v;
        try { return Fworker.Domain.DocsOnly.MergeIsDocsOnly(runs, (await kit.GetPrFiles(J.I(m["pr"]), ct)).Select(f => J.S(f, "filename")).ToList(), settings); }
        catch (Exception e) when (e is VerbReject or HttpRequestException) { return null; }
    }

    /// <summary>Find-GreenMaster (issue 114): master is green for a merge when the merge commit's latest attempt is green, or when a later commit of the default branch that contains it is (the newest first, at most masterContainsLookback after the merge commit).</summary>
    static async Task<(string Sha, bool Own)?> FindGreenMaster(RuleKit kit, string sha, CancellationToken ct)
    {
        var ign = kit.MasterIgnoredChecks();
        if ((await kit.GetChecksFor(sha, ct, ign)).Status == "success") return (sha, true);
        var max = J.Has(kit.Timing, "masterContainsLookback") ? Math.Max(1, J.I(kit.Timing, "masterContainsLookback")) : 10;
        var commits = J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/commits?sha={kit.DefaultBranch}&per_page={Math.Min(100, max + 1)}", ct)).Body).Where(c => c is not null).ToList();
        foreach (var cm in commits)
        {
            var later = J.S(cm, "sha");
            if (later == sha) break;   // the merge commit itself: the commits after it in the list are older and cannot contain it
            if ((await kit.GetChecksFor(later, ct, ign)).Status != "success") continue;
            var cmp = await GhApi.Get(kit.Gh, $"{kit.RepoPath}/compare/{sha}...{later}?per_page=1", ct, 404, 422);
            if (cmp.Ok && J.S(cmp.Body, "status") is "ahead" or "identical") return (later, false);
        }
        return null;
    }
}
