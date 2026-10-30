using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

// W9.4: ci-release (Verbs.Pr.ps1 Verb_ci_release; docs/design/copilot-cloud-agent.md, "CI release"). GitHub holds the workflow runs of a Copilot cloud agent's push in action_required, and POST .../approve
// refuses them; a re-run by an identity with write access starts them. Called by the gateway itself (never a session) with the items whose runtime is copilot-cloud (items) and the run ids it already handled
// (skip). A run is released only when its head branch is copilot/*, its item is open, in this gateway's env scope, carries no other runtime: label, and the item's pull request has that branch and the run's head_sha
// as its head. Released with the gateway's own identity (the author App); a 403 is reported, never retried with another credential.
public static class CiReleaseVerb
{
    public const string Verb = "ci-release";

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static async Task<JsonObject> Run(RuleKit kit, JsonObject a, Func<string, string?> env, CancellationToken ct)
    {
        var items = J.Items(a["items"]).Select(J.S).Where(x => Regex.IsMatch(x, @"^\d{1,7}$")).Select(int.Parse).Distinct().ToList();
        var skip = J.Items(a["skip"]).Select(J.S).ToList();
        var all = J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/actions/runs?status=action_required&per_page=100", ct)).Body?["workflow_runs"]).Where(x => x is not null).Select(x => x!).ToList();
        var runs = all.Where(r => J.S(r, "head_branch").StartsWith("copilot/", StringComparison.OrdinalIgnoreCase) && !skip.Contains(J.S(r, "id"), StringComparer.OrdinalIgnoreCase)).ToList();
        var @out = new JsonArray();
        // the App's Actions permission, read once per process by the gateway (probe): the installation as the App sees it (its JWT)
        string? perm = null;
        var probe = J.IsTrue(a["probe"]);
        if (probe && env("FACTORY_AUTHOR_APP") == "1" && string.IsNullOrEmpty(env("FACTORY_FAKE_APPS")))
        {
            try { perm = await kit.Gh.AppActionsPermission(ct); } catch (Exception e) when (e is VerbReject or HttpRequestException or InvalidOperationException) { perm = ""; }
        }
        else if (probe && !string.IsNullOrEmpty(env("FACTORY_FAKE_APPS"))) perm = env("FACTORY_FAKE_APP_ACTIONS") ?? "";
        // routeAll (the gateway routes every phase of an unlabelled item to copilot-cloud): the items of the held runs' pull requests are candidates too; the recorded pull request below still has to name the branch
        var routeAll = J.IsTrue(a["routeAll"]);
        if (routeAll && runs.Count > 0)
        {
            var open = J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/pulls?state=open&per_page=100", ct)).Body).Where(x => x is not null).ToList();
            foreach (var r in runs)
                foreach (var p in open.Where(p => Eq(J.S(p!["head"], "ref"), J.S(r, "head_branch"))))
                    if (Regex.Match(J.S(p, "title"), @"#(\d{1,7})\b") is { Success: true } m) items.Add(int.Parse(m.Groups[1].Value));
            items = items.Distinct().ToList();
        }
        var given = J.Items(a["items"]).Select(J.S).ToList();
        JsonObject Done() => J.Obj(("ok", true), ("pending", runs.Count), ("runs", @out), ("actions", perm));
        if (runs.Count == 0 || items.Count == 0) return Done();
        var heads = new Dictionary<string, (int Item, int Pr, string Sha)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in items)
        {
            ItemInfo info;
            try { info = await kit.GetItemInfo(n, ct); }
            catch (VerbReject e) { @out.Add(J.Obj(("item", n), ("result", "skipped"), ("reason", e.Code))); continue; }
            if (!info.Open) continue;
            var rtl = info.Labels.Where(l => l.StartsWith("runtime:", StringComparison.OrdinalIgnoreCase)).ToList();
            if (!(rtl.Count == 0 || (rtl.Count == 1 && Eq(rtl[0], "runtime:copilot-cloud")))) { @out.Add(J.Obj(("item", n), ("result", "skipped"), ("reason", "other-runtime"))); continue; }
            if (rtl.Count == 0 && !routeAll && !given.Contains(n.ToString())) { @out.Add(J.Obj(("item", n), ("result", "skipped"), ("reason", "not-copilot"))); continue; }
            // only the pull request pr_open recorded for the item (number and copilot/ branch), never a branch-name match
            JsonObject? sp = null;
            try { sp = (await kit.GetState(n, ct)).State; } catch (Exception e) when (e is VerbReject or HttpRequestException) { }
            if (!(sp is not null && J.Has(sp, "pr") && J.Has(sp["pr"], "branch") && J.Has(sp["pr"], "number") && J.S(sp["pr"], "branch").StartsWith("copilot/", StringComparison.OrdinalIgnoreCase))) continue;
            JsonNode? pr = null;
            try { pr = await kit.GetPr(J.I(sp["pr"], "number"), ct); } catch (Exception e) when (e is VerbReject or HttpRequestException) { }
            if (pr is null || !Eq(J.S(pr, "state"), "open") || !Eq(J.S(pr["head"], "ref"), J.S(sp["pr"], "branch"))) continue;
            var hb = J.S(pr["head"], "ref");
            if (heads.ContainsKey(hb)) { heads[hb] = (0, 0, ""); continue; }   // two items name one branch: nobody's
            heads[hb] = (n, J.I(pr, "number"), J.S(pr["head"], "sha"));
        }
        foreach (var r in runs)
        {
            var o = J.Obj(("run", J.L(r["id"])), ("branch", J.S(r, "head_branch")), ("sha", J.S(r, "head_sha")));
            if (!heads.TryGetValue(J.S(r, "head_branch"), out var h) || h.Item == 0) { o["result"] = "skipped"; o["reason"] = "no-item"; @out.Add(o); continue; }
            if (J.I(r["run_attempt"]) > 1) { o["result"] = "skipped"; o["reason"] = "already-rerun"; @out.Add(o); continue; }   // re-run before (by this gateway before a restart, or by someone else)
            o["item"] = h.Item; o["pr"] = h.Pr;
            if (!Eq(h.Sha, J.S(r, "head_sha"))) { o["result"] = "skipped"; o["reason"] = "head-mismatch"; @out.Add(o); continue; }
            var x = await kit.Gh.Send(HttpMethod.Post, $"{kit.RepoPath}/actions/runs/{J.L(r["id"])}/rerun", null, ct, null, 1);
            o["status"] = x.Status;
            if (x.Ok) o["result"] = "released";
            else
            {
                var msg = x.Body is JsonObject bo && bo.ContainsKey("message") ? J.S(bo, "message") : "";
                if (msg.Length > 300) msg = msg[..300];
                o["result"] = x.Status == 403 && GhApi.RateLimitWait(403, x.Headers, msg) < 0 ? "refused" : "failed";
                o["message"] = msg;
            }
            @out.Add(o);
        }
        return Done();
    }
}
