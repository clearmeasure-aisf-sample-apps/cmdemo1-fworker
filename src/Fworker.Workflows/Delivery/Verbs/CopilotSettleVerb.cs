using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

/// <summary>Another verb of the gateway, run as a rule verb: the answer's exit code and json. The takeover of copilot-settle opens the item's pull request through it (open-pr is the item verbs' group).</summary>
public delegate Task<(int Exit, string Output)> VerbCall(string verb, JsonObject args, CancellationToken ct);

// W9.4: copilot-settle (Verbs.Pr.ps1 Verb_copilot_settle): the gateway owns the completion of a Copilot Development task (CopilotCloudRuntime). The coding agent's session ends before CI does, so item_complete
// may never succeed and the agent may even report a failure while its pull request is fine (#393 to #395). Admin only (the gateway's runner); never a worker's verb. Body: item, stage (launch | end), head (end: the
// branch of the task's artifacts).
//   launch: before a Development task is started. The item already has a pull request with changes and no fix-forward or rework is asked for (no pr.rejection): no task; the gateway waits for its checks instead
//           (action awaiting). A rework: the head it starts from is recorded (copilot_base), so its end can tell whether it pushed anything. Otherwise: action launch.
//   end:    after a Development task ended, whatever the agent reported. The task's branch (head) with changes is taken over as the item's pull request (open-pr: its draft, or a new pull request, superseding the
//           old one); then a pull request with changes and a head the task did not start from waits for its checks in the gateway: Development is done pending checks (awaiting_checks, the sweep moves the item
//           to the next column when they are green and hands it back to Development to fix forward when they are red). Otherwise action none (the relaunch cap decides).
// A park other than the agent's worker-failure is left alone; so is an item that is not in Development or already waits for its checks.
public static class CopilotSettleVerb
{
    public const string Verb = "copilot-settle";

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static async Task<JsonObject> Run(RuleKit kit, JsonObject a, VerbCall call, CancellationToken ct)
    {
        var n = VerbArgs.ItemNumber(a);
        var stage = J.S(VerbArgs.Need(a, "stage"));
        if (stage is not ("launch" or "end")) throw new VerbReject(2, "usage", "stage is launch or end");
        var head = J.Has(a, "head") ? J.S(a, "head") : "";
        var info = await kit.GetItemInfo(n, ct);
        JsonObject None(string why) => J.Obj(("ok", true), ("id", n), ("action", stage == "launch" ? "launch" : "none"), ("reason", why));
        if (!info.Open) return None("closed");
        if (!Eq(info.Column ?? "", kit.Column("development"))) return None("not-in-development");
        // an owner task's item: its pull request is the open one of the task's branch (a closed or superseded record is replaced first)
        try { await OwnerPrs.Resolve(kit, n, ct); } catch (Exception e) when (e is VerbReject or HttpRequestException) { }
        var s = (await kit.GetState(n, ct)).State;
        if (J.Has(s, "awaiting_checks")) return J.Obj(("ok", true), ("id", n), ("action", "awaiting"), ("already", true), ("pr", s["awaiting_checks"]?["pr"]));
        if (J.Has(s, "merge") && J.Has(s["merge"], "sha") && J.B(s["merge"], "sha")) return None("merged");
        var park = kit.GetPark(s);
        if (park is not null && J.S(park, "reason") != "worker-failure") return None($"parked {J.S(park, "reason")}");
        var rework = J.Has(s, "pr") && J.Has(s["pr"], "rejection");
        var pr = await kit.FindItemPr(n, ct);
        if (stage == "launch")
        {
            if (pr is null) return None("no-pr");
            if (rework)
            {
                var rs = await kit.GetState(n, ct); rs.State["copilot_base"] = J.S(pr["head"], "sha"); await kit.SaveState(n, rs, ct);
                return J.Obj(("ok", true), ("id", n), ("action", "launch"), ("reason", "rework"), ("pr", J.I(pr, "number")));
            }
        }
        else if (Regex.IsMatch(head, "^copilot/[A-Za-z0-9._/-]{1,150}$", RegexOptions.IgnoreCase) && !(pr is not null && Eq(J.S(pr["head"], "ref"), head)))
        {
            // the task's own branch: taken over when it carries changes beyond the item's pull request (or the default branch when it has none)
            var from = pr is not null ? J.S(pr["head"], "sha") : kit.DefaultBranch;
            var cmp = await GhApi.Get(kit.Gh, $"{kit.RepoPath}/compare/{Uri.EscapeDataString(from)}...{Uri.EscapeDataString(head)}", ct, 404);
            var ok = cmp.Ok;
            var files = ok && cmp.Body is JsonObject cb && cb.ContainsKey("files") ? J.Items(cb["files"]).Count(f => f is not null) : ok ? J.I(cmp.Body, "ahead_by") : 0;
            var ahead = ok && J.S(cmp.Body, "status") == "ahead" && J.I(cmp.Body, "ahead_by") > 0 && (pr is not null || files > 0);
            if (ahead)
            {
                var (exit, output) = await call("open-pr", J.Obj(("item", n), ("head", head), ("title", info.Title)), ct);
                if (exit != 0)
                {
                    var code = J.S(TryParse(output), "code");
                    // a refusal of the rule is the takeover refused; anything the rule script did not refuse itself is a failure of this verb
                    if (code is "" or "internal-error") throw new InvalidOperationException($"open-pr answered exit {exit} {code}");
                    return J.Obj(("ok", true), ("id", n), ("action", "none"), ("reason", $"takeover-refused {code}"));
                }
                pr = await kit.FindItemPr(n, ct);
            }
            else if (pr is not null) await OwnerPrs.CloseStrayDrafts(kit, n, [J.S(pr["head"], "ref")], J.I(pr, "number"), ct);
        }
        if (pr is null) return None("no-pr");
        var prd = await kit.GetPr(J.I(pr, "number"), ct);
        if (J.I(prd, "changed_files") == 0) return None("empty-pr");
        // the item's pull request is never left a draft: GitHub merges no draft, and nobody would mark it ready (#418, #421)
        if (J.B(prd, "draft")) await OwnerPrs.MarkReady(kit, prd, ct);
        var st = await kit.GetState(n, ct); s = st.State;
        if (stage == "end" && J.Has(s, "copilot_base") && J.S(s, "copilot_base") == J.S(prd["head"], "sha")) return None("no-new-commits");
        if (kit.FilesAreDocs(await kit.GetPrFiles(J.I(pr, "number"), ct)) && !kit.ItemDocsOnly(s, info)) return None("docs-only-pr");
        JsonNode? rerun = J.Has(s, "pr") && J.Has(s["pr"], "rerun") && J.I(s["pr"], "number") == J.I(pr, "number") ? s["pr"]!["rerun"]!.DeepClone() : null;
        s["pr"] = new JsonObject { ["number"] = J.I(pr, "number"), ["branch"] = J.S(pr["head"], "ref"), ["url"] = J.S(pr, "html_url") };
        if (rerun is not null) s["pr"]!["rerun"] = rerun;
        var why = stage == "launch" ? "it already has its pull request with changes; no new Development task is started" : "the Copilot task ended";
        var sha = J.S(prd["head"], "sha");
        s["awaiting_checks"] = new JsonObject { ["pr"] = J.I(pr, "number"), ["sha"] = sha, ["comment"] = $"Completed by the gateway: {why}.", ["since"] = J.Iso(kit.UtcNow), ["column"] = info.Column ?? "" };
        if (park is not null) s.Remove("park");
        kit.SetPark(s, "development:awaiting-checks", kit.TimingNumber("awaitingChecksParkMinutes"));
        s.Remove("copilot_base"); s.Remove("claim");
        await kit.SaveState(n, st, ct);
        await kit.RemoveClaimRef(n, ct);
        await kit.AddComment(n, $"Development is done pending checks: {why}, and PR #{J.I(pr, "number")} carries the change (head `{VerbArgs.Short(sha)}`). The gateway waits for its checks and moves the item on when they are green; red checks go back to Development to fix forward.", ct);
        return J.Obj(("ok", true), ("id", n), ("action", "awaiting"), ("pr", J.I(pr, "number")), ("sha", sha), ("overrode", park is not null ? J.S(park, "reason") : ""));
    }

    static JsonNode? TryParse(string text) { try { return JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { return null; } }
}
