using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

// W9.4: copilot-owner (Verbs.Pr.ps1 Verb_copilot_owner): the item's owner task (docs/design/copilot-cloud-agent.md, "One task, resumed"). The gateway's own verb (refused over HTTP). Body: item, action get | set | close
// | status; set: task {id, url, branch, pr, phase, runtime}. The item state's copilot_task is the durable record:
//   get:    the record (null when the item has none, or its task is done)
//   set:    records the owner task; the first record of a task comments the ownership on the item; later sets update branch and pull request
//   close:  the owner is done (merged, master green): done_at, a comment with the task's link
//   status: what the owner waits on: ci (its checks), next-phase, parked:<reason>, master-green, master-broken (with the failed checks), done, closed
public static class CopilotOwnerVerb
{
    public const string Verb = "copilot-owner";

    public static async Task<JsonObject> Run(RuleKit kit, JsonObject a, CancellationToken ct)
    {
        var n = VerbArgs.ItemNumber(a);
        var action = J.S(VerbArgs.Need(a, "action"));
        if (action is not ("get" or "set" or "close" or "status")) throw new VerbReject(2, "usage", "action is get, set, close or status");
        var st = await kit.GetState(n, ct); var s = st.State;
        var own = J.Has(s, "copilot_task") ? s["copilot_task"] as JsonObject : null;
        var done = own is not null && J.Has(own, "done_at") && J.B(own, "done_at");
        if (action == "get") return J.Obj(("ok", true), ("id", n), ("task", own is not null && !done ? own : null));
        if (action == "set") return await Set(kit, a, n, st, s, own, ct);
        if (action == "close") return await Close(kit, a, n, st, s, own, ct);
        return await Status(kit, n, st, s, own, ct);
    }

    static async Task<JsonObject> Set(RuleKit kit, JsonObject a, int n, StateHolder st, JsonObject s, JsonObject? own, CancellationToken ct)
    {
        var t = VerbArgs.Need(a, "task") as JsonObject ?? new JsonObject();
        var id = J.S(t, "id");
        if (!Regex.IsMatch(id, "^[A-Za-z0-9-]{1,80}$")) throw new VerbReject(2, "usage", "task.id is the agent task id");
        var url = J.S(t, "url"); if (url.Length > 0 && !Regex.IsMatch(url, "^https://[A-Za-z0-9./_?=&%~-]{1,300}$")) url = "";
        var branch = J.S(t, "branch"); if (branch.Length > 0 && !Regex.IsMatch(branch, "^[A-Za-z0-9._/-]{1,200}$")) branch = "";
        var pr = Regex.IsMatch(J.S(t, "pr"), @"^\d{1,7}$") ? J.I(t, "pr") : 0;
        // the runtime of the owner: copilot-cloud (a Copilot task, the default) or cursor-cloud (a Cursor agent with follow-up runs)
        var rt = J.Has(t, "runtime") && J.S(t, "runtime") == "cursor-cloud" ? "cursor-cloud" : "copilot-cloud";
        var first = own is null || J.S(own, "id") != id;
        var phase = J.S(t, "phase");
        if (first)
        {
            var prev = new List<string>();
            if (own is not null && !(J.Has(own, "done_at") && J.B(own, "done_at")))
            {
                prev.AddRange(J.Strings(own["previous"]));
                prev.Add(J.S(own, "id"));
            }
            s["copilot_task"] = new JsonObject
            {
                ["id"] = id, ["url"] = url, ["branch"] = branch, ["pr"] = pr, ["phase"] = phase, ["since"] = J.Iso(kit.UtcNow),
                ["previous"] = J.Node(prev.Where(x => x.Length > 0).ToList()), ["runtime"] = rt,
            };
        }
        else
        {
            if (branch.Length > 0) own!["branch"] = branch;
            if (pr > 0) own!["pr"] = pr;
            if (url.Length > 0) own!["url"] = url;
            if (phase.Length > 0) own!["phase"] = phase;
        }
        await kit.SaveState(n, st, ct);
        if (first)
        {
            var link = url.Length > 0 ? $"[{id}]({url})" : $"`{id}`";
            var from = phase.Length > 0 ? phase : "now";
            if (rt == "cursor-cloud") await kit.AddComment(n, $"**Owner:** Cursor agent {link} owns #{n} from {from} until the change is in production. Between phases the gateway holds the item for this agent and continues it with a follow-up run; no other worker is offered the item.", ct);
            else await kit.AddComment(n, $"**Owner:** Copilot task {link} owns #{n} from {from} until its pull request is merged and master is green. Between phases the gateway holds the item for this task and resumes it with a follow-up session; no other worker is offered the item.", ct);
        }
        return J.Obj(("ok", true), ("id", n), ("task", s["copilot_task"]), ("first", first));
    }

    static async Task<JsonObject> Close(RuleKit kit, JsonObject a, int n, StateHolder st, JsonObject s, JsonObject? own, CancellationToken ct)
    {
        if (own is null) return J.Obj(("ok", true), ("id", n), ("closed", false), ("reason", "no-owner"));
        if (J.Has(own, "done_at") && J.B(own, "done_at")) return J.Obj(("ok", true), ("id", n), ("closed", false), ("reason", "already"));
        own["done_at"] = J.Iso(kit.UtcNow);
        await kit.SaveState(n, st, ct);
        if (J.Has(a, "unmerged") && J.B(a, "unmerged"))
        {
            own["ended"] = "closed-unmerged";
            await kit.SaveState(n, st, ct);
            return J.Obj(("ok", true), ("id", n), ("closed", true), ("unmerged", true));
        }
        var sha = J.Has(s, "merge") && J.Has(s["merge"], "sha") ? J.S(s["merge"], "sha") : "";
        var link = J.S(own, "url").Length > 0 ? $"[{J.S(own, "id")}]({J.S(own, "url")})" : $"`{J.S(own, "id")}`";
        var merged = J.I(own, "pr") > 0 ? $": PR #{J.I(own, "pr")} merged" : "";
        if (sha.Length > 0) merged += " as `" + VerbArgs.Short(sha) + "`";
        var sessionLink = J.S(own, "url").Length > 0 ? J.S(own, "url") : J.S(own, "id");
        var rel = J.Has(s, "merge") && J.Has(s["merge"], "release") && J.B(s["merge"], "release") ? $" in release {J.S(s["merge"], "release")}" : "";
        var oc = J.Obj(kit.Cfg, "octopus");
        var prodAt = J.Has(oc, "prodUrl") && J.S(oc, "prodUrl").Length > 0 ? $" ({J.S(oc, "prodUrl")})" : "";
        var who = J.Has(own, "runtime") && J.S(own, "runtime") == "cursor-cloud" ? "Cursor agent" : "Copilot task";
        await kit.AddComment(n, $"**Owner done:** {who} {link} finished #{n}{merged}, master is green and the change is delivered to production{rel}{prodAt}. Session link: {sessionLink}", ct);
        return J.Obj(("ok", true), ("id", n), ("closed", true));
    }

    static async Task<JsonObject> Status(RuleKit kit, int n, StateHolder st, JsonObject s, JsonObject? own, CancellationToken ct)
    {
        var info = await kit.GetItemInfo(n, ct);
        // before the merge: the item's pull request is the open one of the owner task's branch (a closed or superseded record is replaced)
        if (info.Open && !(J.Has(s, "merge") && J.Has(s["merge"], "sha") && J.B(s["merge"], "sha")))
        {
            OwnerPrResolution? ow = null;
            try { ow = await OwnerPrs.Resolve(kit, n, ct); } catch (Exception e) when (e is VerbReject or HttpRequestException) { }
            if (ow is not null) { st = await kit.GetState(n, ct); s = st.State; }
        }
        var m = J.Has(s, "merge") ? s["merge"] as JsonObject : null;
        var park = kit.GetPark(s);
        var o = J.Obj(("ok", true), ("id", n), ("open", info.Open), ("column", info.Column ?? ""),
            ("branch", own is not null ? J.S(s["copilot_task"], "branch") : ""), ("pr", J.Has(s, "pr") && J.Has(s["pr"], "number") ? J.I(s["pr"], "number") : 0));
        var awaiting = m is not null ? J.S(m, "awaiting") : "";
        if (m is not null && J.Has(m, "sha") && J.B(m, "sha"))
        {
            o["merge_sha"] = J.S(m, "sha");
            // the owner's work ends in production: delivered (done_at, the Release Queue saw the prod deployment); a merged item closed by any other route ends the ownership too
            if ((J.Has(m, "done_at") && J.B(m, "done_at")) || (!info.Open && !(J.Has(m, "awaiting") && J.B(m, "awaiting"))))
            {
                o["waiting"] = "done"; if (J.Has(m, "release")) o["release"] = J.S(m, "release");
            }
            else if (awaiting == "release-queue") o["waiting"] = J.Has(m, "prod_failed") && J.B(m, "prod_failed") ? "prod-failed" : "prod-deploy";
            else if (awaiting is "release-candidate" or "late-rc") o["waiting"] = "release-candidate";
            else if (awaiting == "master-broken")
            {
                o["waiting"] = "master-broken";
                o["failed"] = park is not null && J.Has(park, "failed") ? J.Node(J.Strings(park["failed"])) : new JsonArray();
            }
            else o["waiting"] = "master-green";
        }
        else if (!info.Open) o["waiting"] = "closed";
        else if (J.Has(s, "awaiting_checks")) o["waiting"] = "ci";
        else if (park is not null) o["waiting"] = $"parked:{J.S(park, "reason")}";
        else o["waiting"] = "next-phase";
        if (J.Has(s, "pr") && J.Has(s["pr"], "rejection")) { var rj = J.S(s["pr"], "rejection"); o["rework"] = rj[..Math.Min(600, rj.Length)]; }
        return o;
    }
}
