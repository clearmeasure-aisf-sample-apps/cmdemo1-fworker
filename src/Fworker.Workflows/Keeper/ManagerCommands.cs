using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Keeper;

// W9.6 (#358): the plant manager's flow verb (docs/design/plant-manager.md 10e) on v2, the port of plugin/scripts/lib/Verbs.Manager.ps1: what the supervisor session may change on an item to keep the flow moving, and
// nothing that decides about the work itself. One verb, manager-action, with one action per call. The gateway's pipeline checks the mode (advise turns every action off) and the hourly limit before this runs, and the plant's
// part (the ticket, the hooks after it) is the pipeline's, unchanged; this is the part the rule script played.
// Enforced here, on the item:
//   - a label hold:owner on the item refuses every action: the owner's hold wins;
//   - at most manager.actions.perItemPerDay actions on one item in 24 hours, counted in the item state (a gateway restart does not reset it);
//   - a policy code from manager.actions.policies on every call, and one journal line per action that names it;
//   - never a merge, an approval, a verdict, clear-hold, a column move or a claim for a session: unpark refuses the parks that only those would end.
public sealed class ManagerCommands(VerbContext cx)
{
    public static readonly IReadOnlyList<string> ItemActions = ["unpark", "release-owner", "pin", "unpin", "requeue", "prioritize", "rerun-failed"];

    FactoryRules Rules => cx.Rules;

    public async Task<JsonObject> Action(VerbArgs a)
    {
        var n = a.ItemNumber();
        var action = a.Need("action");
        if (!Ps.NameIn(action, ItemActions)) throw VerbRejection.Usage($"action is one of {string.Join(", ", ItemActions)}");
        var policy = a.Need("policy");
        if (!Ps.NameIn(policy, Rules.ManagerPolicies)) throw new VerbRejection(2, "unknown-policy", $"the policy code is one of manager.actions.policies: {string.Join(", ", Rules.ManagerPolicies)}");
        // one line, no HTML comment markers, no mention: the reason goes into a journal comment whose hidden marker is machine-read
        var reason = a.Has("reason") ? Regex.Replace(Regex.Replace(Regex.Replace(a.Str("reason"), "://", " ", Ps.Match), @"[<>@`\[\]!]", "", Ps.Match), @"\s+", " ", Ps.Match).Trim() : "";
        if (reason.Length > 300) throw VerbRejection.Usage("the reason is at most 300 characters");
        var by = a.Has("by") && Regex.IsMatch(a.Str("by"), "^[A-Za-z0-9_-]{1,40}$", Ps.Match) ? a.Str("by") : "operator";
        // the Copilot session of the on-demand manager (set by the gateway, never by the caller): a link in the journal line
        var session = a.Has("session") && Regex.IsMatch(a.Str("session"), "^https://[A-Za-z0-9._~:/?#=&%+@-]{1,300}$", Ps.Match) ? a.Str("session") : "";

        var info = await cx.GitHub.GetItemInfo(n);
        if (info.HasLabel(Rules.OwnerHoldLabel)) throw new VerbRejection(10, "owner-hold", $"item #{n} carries the label '{Rules.OwnerHoldLabel}': the owner holds it, and the plant manager changes nothing on it");
        var st = await cx.State.Read(n); var s = st.State;
        var now = cx.Now;
        var recent = new List<JsonNode>();
        if (s["manager_actions"] is JsonArray all)
            foreach (var x in all)
                if (x is JsonObject e && Ps.Has(e, "at") && (now - Ps.FromIso(e["at"])!.Value).TotalHours < 24) recent.Add(e);
        if (recent.Count >= Rules.ManagerPerItemPerDay)
        {
            var retry = 86400;
            if (recent.Count > 0)
            {
                var first = recent.Select(r => Ps.FromIso(r["at"])!.Value).Order().First();
                retry = (int)Math.Max(60, Math.Ceiling((first.AddHours(24) - now).TotalSeconds));
            }
            throw new VerbRejection(10, "item-action-limit", $"the plant manager acted on item #{n} {recent.Count} times in 24 hours (limit {Rules.ManagerPerItemPerDay}): leave it to the operator",
                new Dictionary<string, object?> { ["retryAfterSeconds"] = retry, ["actions_today"] = recent.Count, ["limit"] = Rules.ManagerPerItemPerDay });
        }
        var o = new JsonObject { ["ok"] = true, ["id"] = n, ["item"] = n, ["action"] = action, ["policy"] = policy, ["column"] = info.Column };
        string detail;
        var unparked = false;
        switch (action.ToLowerInvariant())
        {
            case "unpark":
            {
                if (!Ps.Has(s, "park") || s["park"] is not JsonObject park) throw new VerbRejection(10, "not-parked", $"item #{n} is not parked");
                var code = VerbContext.Text(park, "reason");
                if (code.Equals("development:awaiting-checks", StringComparison.OrdinalIgnoreCase)) throw new VerbRejection(10, "gateway-wait", "the item waits for its pull request checks, not for a person; the sweep ends that wait by itself");
                if (code.Equals("inspector-escalated", StringComparison.OrdinalIgnoreCase)) throw new VerbRejection(10, "needs-approval", "the inspector's hold clears only on a human approval of the head commit (clear-hold); the plant manager never clears it");
                if (Ps.Has(s, "verdict") && s["verdict"] is JsonObject verdict && Ps.Truthy(verdict["passed"])) throw new VerbRejection(10, "needs-merge", "a PASS verdict is on record: the item waits for a merge, and the plant manager never merges");
                var parkCopy = park.DeepClone();
                s.Remove("park");
                var offered = info.Open && !cx.ClaimLive(s) && Ps.NameIn(info.Column ?? "", Rules.AiColumns);
                if (offered) s["handoff"] = new JsonObject { ["column"] = info.Column, ["at"] = Ps.Iso(now) };
                o["park"] = parkCopy; o["reoffer"] = offered; o["closed"] = !info.Open;
                detail = $"released the park `{code}`{(offered ? $"; the next sweep offers it again in **{info.Column}**" : "")}";
                unparked = true;
                break;
            }
            case "release-owner":
            {
                if (!VerbContext.OwnedClaim(s) || !cx.ClaimLive(s)) throw new VerbRejection(10, "no-owner", $"item #{n} has no owner session with a live lease: the sweep offers it by itself");
                var claim = (JsonObject)s["claim"]!;
                var holder = VerbContext.Text(claim, "holder");
                // as expire-claim does (Owner.ps1): the claim stays and its lease ends now, marked taken_back, so the old owner cannot renew it and the next sweep offers the item in resume mode
                claim["lease_until"] = Ps.Iso(now.AddSeconds(-1)); claim["taken_back"] = "released-by-plant-manager";
                o["holder"] = holder;
                detail = $"ended the lease of **{holder}**; the item is offered again in resume mode";
                break;
            }
            case "pin":
            {
                VerbContext.AssertOpen(info);
                var w = a.Has("worker") ? a.Str("worker") : "";
                if (!Regex.IsMatch(w, "^[A-Za-z0-9_-]{1,40}$", Ps.Match) || Ps.NameIn(w, ["operator", "gateway", "all", "supervisor", "plant-manager"])) throw VerbRejection.Usage("worker is the name of a worker session");
                var old = info.Labels.Where(l => l.StartsWith("worker:", StringComparison.OrdinalIgnoreCase)).ToList();
                if (old.Count == 1 && string.Equals(old[0], $"worker:{w}", StringComparison.OrdinalIgnoreCase)) throw new VerbRejection(10, "already-pinned", $"item #{n} is pinned to {w} already");
                foreach (var l in old) if (!string.Equals(l, $"worker:{w}", StringComparison.OrdinalIgnoreCase)) await cx.GitHub.RemoveLabel(n, l);
                if (!old.Any(l => string.Equals(l, $"worker:{w}", StringComparison.OrdinalIgnoreCase))) await cx.GitHub.AddLabels(n, [$"worker:{w}"]);
                var was = old.Select(l => l.Substring(7)).Where(l => !string.Equals(l, w, StringComparison.OrdinalIgnoreCase)).ToList();
                o["worker"] = w; o["was"] = VerbContext.Strings(was);
                detail = $"pinned it to **{w}**{(was.Count > 0 ? $" (was {string.Join(", ", was)})" : "")}";
                break;
            }
            case "unpin":
            {
                var old = info.Labels.Where(l => l.StartsWith("worker:", StringComparison.OrdinalIgnoreCase)).ToList();
                if (old.Count == 0) throw new VerbRejection(10, "not-pinned", $"item #{n} is not pinned to a worker");
                foreach (var l in old) await cx.GitHub.RemoveLabel(n, l);
                var was = old.Select(l => l.Substring(7)).ToList();
                o["was"] = VerbContext.Strings(was);
                detail = $"removed the pin to {string.Join(", ", was)}";
                break;
            }
            case "requeue":
            {
                VerbContext.AssertOpen(info);
                if (!Ps.NameIn(info.Column ?? "", Rules.AiColumns)) throw new VerbRejection(10, "not-ai-column", $"item #{n} is in '{info.Column}', which no session works");
                if (cx.ClaimLive(s)) throw new VerbRejection(10, "owned", $"item #{n} has a live claim of {VerbContext.Text(s["claim"] as JsonObject, "holder")}: release the owner first (release-owner), or let it work");
                if (cx.Park(s) is { } parked) throw new VerbRejection(10, "parked", $"item #{n} is parked ({VerbContext.Text(parked, "reason")}): unpark it, which offers it again");
                if (Ps.Has(s, "verdict") && s["verdict"] is JsonObject vd && Ps.Truthy(vd["passed"]) && !Ps.Truthy(vd["processed"])) throw new VerbRejection(10, "gateway-merge", "a PASS verdict waits for the gateway's merge: there is nothing to offer");
                if (VerbContext.MergeAwaiting(s)) throw new VerbRejection(10, "settling", "the item is merged and its release is being settled: there is nothing to offer");
                s["handoff"] = new JsonObject { ["column"] = info.Column, ["at"] = Ps.Iso(now) };
                o["reoffer"] = true;
                detail = $"put it back to be offered in **{info.Column}** at once";
                break;
            }
            case "prioritize":
            {
                VerbContext.AssertOpen(info);
                var level = a.Has("level") ? a.Str("level") : "top";
                if (!Ps.NameIn(level, ["top", "high", "normal"])) throw VerbRejection.Usage("level is top, high or normal");
                var cur = Rules.PriorityOf(info.Labels);
                if (string.Equals(cur, level, StringComparison.OrdinalIgnoreCase) || (string.Equals(level, "normal", StringComparison.OrdinalIgnoreCase) && cur.Length == 0))
                    throw new VerbRejection(10, "already", $"item #{n} has priority {level} already");
                foreach (var k in new[] { "top", "high" })
                {
                    var sp = info.Labels.FirstOrDefault(l => string.Equals(l, Rules.PriorityLabel(k), StringComparison.OrdinalIgnoreCase));
                    if (sp != null && !string.Equals(k, level, StringComparison.OrdinalIgnoreCase)) await cx.GitHub.RemoveLabel(n, sp);
                }
                if (!string.Equals(level, "normal", StringComparison.OrdinalIgnoreCase) && !info.Labels.Any(l => string.Equals(l, Rules.PriorityLabel(level), StringComparison.OrdinalIgnoreCase)))
                    await cx.GitHub.AddLabels(n, [Rules.PriorityLabel(level)]);
                var was = cur.Length > 0 ? cur : "normal";
                o["level"] = level; o["was"] = was;
                detail = $"set its priority to **{level}** (was {was})";
                break;
            }
            case "rerun-failed":
            {
                VerbContext.AssertOpen(info);
                var prn = Ps.Has(s, "pr") && s["pr"] is JsonObject sp && Ps.Has(sp, "number") ? VerbContext.AsInt(sp["number"]) : 0;
                JsonObject? pr = null;
                if (prn > 0) { try { pr = await cx.GitHub.GetPull(prn); } catch (VerbRejection) { pr = null; } }
                if (pr == null || !string.Equals(VerbContext.Text(pr, "state"), "open", StringComparison.OrdinalIgnoreCase)) pr = await cx.FindItemPr(n);
                if (pr == null) throw new VerbRejection(10, "no-pr", $"item #{n} has no open pull request");
                var head = Ps.Str(pr["head"]?["sha"]);
                var done = s["manager_reruns"] as JsonObject ?? new JsonObject();
                if (done.Any(kv => string.Equals(kv.Key, head, StringComparison.OrdinalIgnoreCase)))
                    throw new VerbRejection(10, "already-rerun", $"the failed jobs of head {VerbContext.Sha7(head)} were re-run once already: a second failure is not a flake; report it instead");
                var runs = await cx.GitHub.WorkflowRuns(head);
                var failed = runs.Where(r => string.Equals(VerbContext.Text(r, "status"), "completed", StringComparison.OrdinalIgnoreCase)
                    && (!Ps.Has(r, "event") || string.Equals(VerbContext.Text(r, "event"), "pull_request", StringComparison.OrdinalIgnoreCase))
                    && Ps.NameIn(VerbContext.Text(r, "conclusion"), ["failure", "timed_out"])).ToList();
                if (failed.Count == 0)
                {
                    if (runs.Any(r => !string.Equals(VerbContext.Text(r, "status"), "completed", StringComparison.OrdinalIgnoreCase)))
                        throw VerbRejection.Retry(10, "run-in-progress", "a workflow run of the head is still running: GitHub re-runs only a completed run; ask again when it has finished", 120);
                    throw new VerbRejection(10, "nothing-failed", $"no failed workflow run on head {VerbContext.Sha7(head)}");
                }
                var started = new List<long>(); var refused = new List<string>();
                foreach (var r in failed)
                {
                    var id = r["id"]!.GetValue<long>();
                    var status = await cx.GitHub.RerunFailedJobs(id);
                    if (status is 200 or 201) started.Add(id); else refused.Add($"{id}: {status}");
                }
                if (started.Count == 0) throw VerbRejection.Retry(20, "rerun-refused", $"GitHub refused the re-run ({string.Join(", ", refused)})", 60);
                // once per head: a later failure of the same head is a real one; the last 10 heads are kept
                var entries = done.Select(kv => (kv.Key, Value: kv.Value?.DeepClone())).ToList();
                entries.RemoveAll(e => string.Equals(e.Key, head, StringComparison.OrdinalIgnoreCase));
                entries.Add((head, new JsonObject { ["at"] = Ps.Iso(now), ["runs"] = new JsonArray(started.Select(x => (JsonNode)x).ToArray()) }));
                var keep = entries.OrderBy(e => Ps.FromIso((e.Value as JsonObject)?["at"]) ?? DateTime.MinValue).TakeLast(10).ToList();
                var reruns = new JsonObject();
                foreach (var e in keep) reruns[e.Key] = e.Value;
                s["manager_reruns"] = reruns;
                o["pr"] = VerbContext.Number(pr); o["sha"] = head; o["runs"] = new JsonArray(started.Select(x => (JsonNode)x).ToArray()); o["refused"] = VerbContext.Strings(refused);
                detail = $"re-ran the failed jobs of PR #{VerbContext.Number(pr)} at `{VerbContext.Sha7(head)}` (workflow run {string.Join(", ", started)}), once for this head";
                break;
            }
            default:
                throw VerbRejection.Usage($"action is one of {string.Join(", ", ItemActions)}");
        }
        var kept = recent.Select(r => r.DeepClone()).ToList();
        kept.Add(new JsonObject { ["at"] = Ps.Iso(now), ["action"] = action, ["policy"] = policy, ["by"] = by });
        var max = Math.Max(10, Rules.ManagerPerItemPerDay);
        s["manager_actions"] = new JsonArray(kept.TakeLast(max).ToArray());
        o["journaled"] = await cx.Journal(n, st, "manager-action", new Dictionary<string, JsonNode?> { ["action"] = action, ["policy"] = policy, ["by"] = by, ["reason"] = reason, ["detail"] = detail, ["session"] = session },
            $"{action}|{now.Ticks}");
        await cx.State.Save(n, st);
        if (unparked && cx.Saga.Owns(n)) await cx.Saga.Resumed(n, $"manager-unpark|{now.Ticks}");
        o["actions_today"] = recent.Count + 1; o["limit"] = Rules.ManagerPerItemPerDay;
        return o;
    }
}
