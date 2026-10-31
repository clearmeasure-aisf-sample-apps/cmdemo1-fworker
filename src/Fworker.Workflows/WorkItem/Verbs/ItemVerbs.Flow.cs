using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

// The verbs that move an item through its columns and hold it: complete, validation-verdict, park-item, unpark-item and resettle-item (Verbs.Item.ps1, Verbs.Pr.ps1 Verb_validation_verdict and Invoke-Demote).

public sealed partial class ItemVerbs
{
    /// <summary><c>Finish-ColumnExit</c>: the comment, the card to the next column, and the claim: the owner keeps the item and carries on in the next column; any other session's claim ends with a hand-off.</summary>
    async Task<JsonObject> FinishColumnExit(int n, ItemInfo info, ItemStateHolder st, string commentText, ItemCall c, JsonObject? extra = null)
    {
        var s = st.State;
        if (commentText.Length > 0) await AddComment(n, commentText, c);
        var to = await MoveNextInternal(info, c.Ct);
        var owned = ItemRules.OwnedClaim(s);
        foreach (var k in new[] { "park", "awaiting_checks" }) s.Remove(k);
        if (owned) ItemRules.MoveOwnerTo(s, to, Cfg, Now);          // the owner keeps the item and carries on in the next column
        else { s.Remove("claim"); s["handoff"] = new JsonObject { ["column"] = to, ["at"] = Ps.Iso(Now) }; }
        await store.Save(n, st, c.Ct);
        if (!owned) await board.RemoveClaimRef(n, c.Ct);
        var r = new JsonObject { ["ok"] = true, ["id"] = n, ["completed"] = true, ["column"] = info.Column, ["moved_to"] = to, ["continue"] = owned, ["next_column"] = owned ? to : null };
        if (extra is not null) foreach (var kv in extra) r[kv.Key] = kv.Value?.DeepClone();
        return r;
    }

    // Verb_complete. The completion of Development (a real pull request with green checks: the CI rule, the migration check) is CompleteDevelopment (ItemVerbs.Development.cs).
    async Task<JsonObject> Complete(ItemCall c)
    {
        var n = c.Item;
        var holder = HolderOf(c);
        var succeeded = !(c.Has("succeeded") && Ps.Eq(c.Text("succeeded"), "false"));
        var comment = c.Has("comment") ? c.Text("comment") : "";
        var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info);
        AssertColumnMatches(info, c);
        var col = info.ColumnText;
        if (Ps.Eq(col, Cfg.ColumnValidation)) throw new ItemRefusal(10, "use-validation-verdict", $"completion in '{col}' is a validation verdict, not a completion");
        if (Ps.NameIn(col, Cfg.ColumnsHold)) throw new ItemRefusal(10, "hold-column", $"'{col}' is a hold column");
        if (!Ps.NameIn(col, Cfg.ColumnsAi)) throw new ItemRefusal(10, "not-ai-column", $"'{col}' is not a column this worker completes");
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (Ps.Has(s, "claim") && ItemRules.ClaimLive(s, Now) && !Ps.Eq(Ps.Text(s["claim"], "holder"), holder)) throw new ItemRefusal(40, "claim-held", $"the claim is held by {Ps.Text(s["claim"], "holder")}");
        if (!succeeded)
        {
            await AddComment(n, $"worker reported **FAILURE** for **{col}**; item parked for review. {comment}", c);
            ItemRules.SetPark(s, "worker-failure", Cfg.ParkMinutes, Now); s.Remove("claim"); await store.Save(n, st, c.Ct); await board.RemoveClaimRef(n, c.Ct);
            return new JsonObject { ["ok"] = true, ["id"] = n, ["completed"] = false, ["parked"] = true, ["reason"] = "worker-failure" };
        }
        // 1. required section, written after this dispatch
        if (Cfg.SectionOf(col) is { } sec)
        {
            var dispatched = Ps.Has(s, "dispatched_at") && Ps.CiGet(s["dispatched_at"] as JsonObject, col) is { } dj ? Ps.FromIso(dj) : null;
            var lastAppend = Ps.Has(s, "last_append") && Ps.CiGet(s["last_append"] as JsonObject, col) is { } lj ? Ps.FromIso(lj) : null;
            var hasSection = info.Body.IndexOf(sec, StringComparison.OrdinalIgnoreCase) >= 0;
            var fresh = dispatched is null || (lastAppend is not null && lastAppend > dispatched);
            if (!hasSection || !fresh)
            {
                var detail = fresh ? "" : $"(no body append landed after dispatch: dispatched={(dispatched is { } d ? Ps.Iso(d) : "none")} last_append={(lastAppend is { } l ? Ps.Iso(l) : "none")})";
                await AddComment(n, $"completion for **{col}** rejected: missing required section `{sec}` {detail}. A fresh append is needed.", c);
                throw new ItemRefusal(10, "missing-required-section", $"missing required section '{sec}'. Append it with append-body, verify appended is true, then call complete again.");
            }
        }
        // 2. reviewer verdict
        if (Ps.NameIn(col, Cfg.ReviewVerdictColumns))
        {
            if (comment.IndexOf("REVIEW: COMPLETE", StringComparison.OrdinalIgnoreCase) < 0)
                throw new ItemRefusal(10, "reviewer-verdict-missing", $"reviewer verdict missing: a successful {col} completion must carry 'REVIEW: COMPLETE' in its comment. Play the reviewer role, then call complete again, or report succeeded=false.");
            var reference = c.Has("review_ref") ? c.Text("review_ref") : "";
            var rv = Ps.Has(s, "reviews") ? Ps.CiGet(s["reviews"] as JsonObject, col) as JsonObject : null;
            if (rv is null || !Ps.Has(rv, "ref") || !Ps.Truthy(rv["ref"]) || !Ps.Eq(Ps.Text(rv, "ref"), reference)) throw new ItemRefusal(10, "review-ref-invalid", "no recorded REVIEW: COMPLETE matches review_ref; call record-review first");
        }
        if (!Ps.Eq(col, Cfg.ColumnDevelopment))
        {
            // leaving the design column always leaves an explicit decision behind: no design append recorded one, so it is not docs-only
            if (Cfg.HasSection(col) && !(Ps.Has(s, "design") && Ps.Has(s["design"], "docs_only"))) s["design"] = new JsonObject { ["docs_only"] = false, ["at"] = Ps.Iso(Now) };
            return await FinishColumnExit(n, info, st, comment, c);
        }
        // 3. Development: a real pull request with green checks
        return await CompleteDevelopment(n, info, st, col, comment, c);
    }

    /// <summary><c>Invoke-Demote</c>: a failed validation or merge sends the item back to Development, up to the demotion limit, then parks it for a human.</summary>
    async Task<(string Outcome, JsonObject Detail)> Demote(int n, ItemInfo info, ItemStateHolder st, string reason, string cause, ItemCall c)
    {
        var s = st.State;
        var d = Ps.Has(s, "demotions") ? Ps.Int(s, "demotions") : 0;
        s["demotions"] = d + 1;
        var demotions = d + 1;
        var owned = ItemRules.OwnedClaim(s);
        s.Remove("verdict"); s.Remove("bot");
        if (!owned || demotions > Cfg.MaxDemotions) { s.Remove("claim"); await board.RemoveClaimRef(n, c.Ct); }
        if (demotions > Cfg.MaxDemotions)
        {
            ItemRules.SetPark(s, cause == "verdict" ? "verdict-failed" : "demotion-limit", Cfg.ParkMinutes, Now);
            await store.Save(n, st, c.Ct);
            await AddComment(n, $"failed {demotions} time(s), over the demotion limit of {Cfg.MaxDemotions}; parked for human review: {reason}", c);
            return ("parked", new JsonObject { ["outcome"] = "parked", ["reason"] = s["park"]!["reason"]!.DeepClone() });
        }
        await board.MoveCard(info, Cfg.ColumnDevelopment, c.Ct);
        if (!Ps.Has(s, "pr")) s["pr"] = new JsonObject();
        ((JsonObject)s["pr"]!)["rejection"] = reason;
        if (owned) ItemRules.MoveOwnerTo(s, Cfg.ColumnDevelopment, Cfg, Now); else s["handoff"] = new JsonObject { ["column"] = Cfg.ColumnDevelopment, ["at"] = Ps.Iso(Now) };
        await store.Save(n, st, c.Ct);
        await AddComment(n, $"{(cause == "verdict" ? "validation FAILED" : "merge failed")}: {reason} - demoting to **Development** (round-trip {demotions} of {Cfg.MaxDemotions}).", c);
        return ("demoted", new JsonObject { ["outcome"] = "demoted", ["reason"] = reason, ["round_trip"] = demotions });
    }

    // Verb_validation_verdict. A PASS that the gateway would merge in this call (the inline merge of Verb_merge -Gateway) is merged by the sweep and the saga's merge request right after, which a verdict always asks for (W9.3).
    async Task<JsonObject> ValidationVerdict(ItemCall c)
    {
        var n = c.Item;
        if (!c.Has("passed") || !c.Has("summary") || c.Text("summary").Trim() == "")
            throw new ItemRefusal(2, "verdict-fields", "the body must hold \"passed\": true or false and \"summary\": a one-paragraph verdict. Nothing was recorded.");
        var passed = Ps.Eq(c.Text("passed"), "true");
        var summary = c.Text("summary").Trim();
        var pr = c.Has("pr") ? Ps.Int(c.Args, "pr") : 0;
        var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info);
        if (!Ps.Eq(info.ColumnText, Cfg.ColumnValidation)) throw new ItemRefusal(10, "wrong-column", $"item is in '{info.ColumnText}'");
        if (passed && pr <= 0) throw new ItemRefusal(2, "pass-requires-prNumber", "a PASS verdict requires a valid prNumber");
        var st = await store.Read(n, c.Ct); var s = st.State;
        var holder = HolderOf(c);
        if (Ps.Has(s, "claim") && ItemRules.ClaimLive(s, Now) && !Ps.Eq(Ps.Text(s["claim"], "holder"), holder)) throw new ItemRefusal(40, "claim-held", $"the claim is held by {Ps.Text(s["claim"], "holder")}");
        if (passed)
        {
            var reference = c.Has("review_ref") ? c.Text("review_ref") : "";
            var rv = Ps.Has(s, "reviews") ? Ps.CiGet(s["reviews"] as JsonObject, info.ColumnText) as JsonObject : null;
            if (rv is null || !Ps.Has(rv, "ref") || !Ps.Truthy(rv["ref"]) || !Ps.Eq(Ps.Text(rv, "ref"), reference)) throw new ItemRefusal(10, "review-ref-invalid", "a PASS needs a recorded REVIEW: COMPLETE (record-review) and its review_ref");
        }
        var downgraded = false;
        if (passed && info.HasLabel(Cfg.VideoLabel))
        {
            var rel = await gh.Allow("GET", $"{gh.RepoPath}/releases/tags/demo-{n}", [404], null, c.Ct);
            var hasMp4 = !rel.Allowed && (Ps.Get(rel.Body, "assets") as JsonArray ?? []).Any(a => Ps.Text(a, "name").EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            if (!hasMp4)
            {
                passed = false; downgraded = true;
                summary += $" FACTORY GATE: this item carries the '{Cfg.VideoLabel}' label, but no MP4 is published on the 'demo-{n}' release. PASS downgraded to FAIL.";
            }
        }
        var headSha = "";
        if (pr > 0) { try { headSha = Ps.Text(Ps.Get(await pulls.GetPr(pr, c.Ct), "head"), "sha"); } catch (ItemRefusal) { } }
        // the head the validator tested (--head-sha): a PASS for another head than the current one is refused; without it the head read here is recorded
        if (passed && c.Has("head_sha") && c.Text("head_sha").Length > 0)
        {
            var given = c.Text("head_sha");
            if (headSha.Length == 0 || given != headSha)
                throw new ItemRefusal(10, "head-mismatch", $"the PASS was given for {given[..Math.Min(7, given.Length)]}, but the head of PR #{pr} is {(headSha.Length > 0 ? headSha[..7] : "unknown")}; test the current head. Nothing was recorded.");
        }
        var verdict = new JsonObject
        {
            ["passed"] = passed, ["pr"] = pr, ["head_sha"] = headSha, ["summary"] = summary, ["at"] = Ps.Iso(Now),
            ["round"] = Ps.Has(s, "demotions") ? Ps.Int(s, "demotions") : 0, ["processed"] = false,
        };
        s["verdict"] = verdict;
        await AddComment(n, $"validation verdict for **{info.ColumnText}**: {(passed ? "PASS" : "FAIL")} - {summary}", c);
        var res = new JsonObject { ["ok"] = true, ["id"] = n, ["recorded"] = true, ["passed"] = passed, ["downgraded"] = downgraded, ["column"] = info.Column };
        if (!passed)
        {
            verdict["processed"] = true;
            // escalate earlier: two consecutive FAIL verdicts that cite the same acceptance criterion park the item with a question
            var repeated = ItemRules.RepeatedCriteria(s, "verdict", ItemRules.CitedCriteria(info, summary, Cfg));
            if (repeated.Count > 0)
            {
                await ParkRepeatedCriterion(n, st, "FAIL verdicts", repeated, c);
                res["next"] = "parked"; res["reason"] = "same-criterion-repeated"; res["criteria"] = Ps.Array(repeated);
                return res;
            }
            if (pr > 0)
            {
                JsonNode? prd = null; try { prd = await pulls.GetPr(pr, c.Ct); } catch (ItemRefusal) { }
                var merged = prd is not null && Ps.TruthyKey(prd, "merged");
                var cause = merged ? $"PR #{pr} already merged as {Ps.Text(prd, "merge_commit_sha")} - residual gaps only (open a NEW branch from master): {summary}" : summary;
                // (a merge that a FAIL verdict judged is not picked up by the sweep as a finished change: the gaps are still to be closed)
                if (merged) { if (!Ps.Has(s, "pr")) s["pr"] = new JsonObject(); ((JsonObject)s["pr"]!)["residual"] = true; }
                var (outcome, detail) = await Demote(n, info, st, cause, "verdict", c);
                // auto-unpark: the head this park judged, so the sweep can lift it once the pull request is green on a newer head
                if (outcome == "parked" && prd is not null && !merged && Ps.Has(s, "park"))
                {
                    var p = (JsonObject)s["park"]!; p["pr"] = pr; p["head"] = headSha.Length > 0 ? headSha : Ps.Text(Ps.Get(prd, "head"), "sha");
                    await store.Save(n, st, c.Ct);
                }
                res["next"] = outcome; res["detail"] = detail;
            }
            else
            {
                ItemRules.SetPark(s, "verdict-failed", Cfg.ParkMinutes, Now); s.Remove("claim"); await board.RemoveClaimRef(n, c.Ct); await store.Save(n, st, c.Ct);
                await AddComment(n, "validation FAILED with no pull request to fix; parked for human review.", c);
                res["next"] = "parked";
            }
            return res;
        }
        ItemRules.RepeatedCriteria(s, "verdict", [], clear: true);
        // Without the Auto Merge label a PASS is parked for a person to merge, except under the QC merge gate (peer-approved): there the item stays on the approval path and the merge verb enforces the QC run and the human approval.
        if (!info.HasLabel(Cfg.AutoMergeLabel) && !Cfg.QcGateActive)
        {
            verdict["processed"] = true;
            ItemRules.SetPark(s, "pass-without-auto-merge", Cfg.ParkMinutes, Now); s.Remove("claim"); await board.RemoveClaimRef(n, c.Ct); await store.Save(n, st, c.Ct);
            await AddComment(n, $"validation PASSED - awaiting human merge (no '{Cfg.AutoMergeLabel}' label; the worker will not merge).", c);
            res["next"] = "parked"; return res;
        }
        await store.Save(n, st, c.Ct);
        res["next"] = Ps.Eq(Cfg.MergePolicy, "auto") ? "merge" : "approve";
        return res;
    }

    // Verb_park_item: admin verb. The gateway parks an item itself (runtime-failed after the executions of one phase failed). Body: item, code (a park code), phase, by.
    async Task<JsonObject> ParkItem(ItemCall c)
    {
        var n = c.Item;
        var code = c.NeedText("code");
        if (!Ps.Match(code, "^[a-z0-9-]{1,40}$")) throw new ItemRefusal(2, "usage", "the park code is lowercase letters, digits and dashes");
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (ItemRules.GetPark(s, Now) is { } already) return new JsonObject { ["ok"] = true, ["id"] = n, ["parked"] = true, ["already"] = true, ["park"] = already.DeepClone() };
        ItemRules.SetPark(s, code, Cfg.ParkMinutes, Now);
        var phase = c.Has("phase") ? OneLine(c.Text("phase")) : "";
        var by = c.Has("by") ? OneLine(c.Text("by")) : "the gateway";
        var park = (JsonObject)s["park"]!;
        await AddJournal(n, st, "parked", new JsonObject { ["code"] = code, ["phase"] = phase, ["by"] = by }, Ps.Text(park, "at"), c);
        await store.Save(n, st, c.Ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["parked"] = true, ["park"] = park.DeepClone() };
    }

    /// <summary>One line, no HTML comment markers: the text goes into a journal comment whose hidden marker is machine-read.</summary>
    static string OneLine(string text) => Ps.Replace(Ps.Replace(text, "<!--|-->", ""), @"\s+", " ").Trim();

    // Verb_unpark_item: a person releases a park before its `until`. Admin verb, reached only through POST /api/v1/items/{n}/unpark.
    async Task<JsonObject> UnparkItem(ItemCall c)
    {
        var n = c.Item;
        var reason = OneLine(c.NeedText("reason"));
        if (reason.Length == 0) throw new ItemRefusal(2, "usage", "a reason is required: why the park is released early");
        if (reason.Length > 500) throw new ItemRefusal(2, "usage", "the reason is at most 500 characters");
        var by = c.Has("by") && c.Text("by").Trim().Length > 0 ? c.Text("by").Trim() : "the operator";
        // a closed item may be unparked too; only the park is removed (no hand-off, no new offer)
        var info = await board.GetItemInfo(n, c.Ct);
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (!Ps.Has(s, "park")) throw new ItemRefusal(10, "not-parked", $"item #{n} is not parked");
        var park = (JsonObject)s["park"]!; var code = Ps.Text(park, "reason");
        if (code == "development:awaiting-checks") throw new ItemRefusal(10, "gateway-wait", "the item waits for its pull request checks, not for a person; the sweep ends that wait by itself");
        if (code == "inspector-escalated") throw new ItemRefusal(10, "needs-approval", "the inspector's hold clears only on a human approval of the head commit (clear-hold), not by unpark");
        var expired = ItemRules.GetPark(s, Now) is null;
        // the item starts afresh, so the fix-forward rounds and demotions that parked it are counted from 0 again
        var reset = Ps.Has(s, "fix_forward_rounds") || Ps.Has(s, "demotions");
        s.Remove("park"); s.Remove("fix_forward_rounds"); s.Remove("demotions"); s.Remove("master_defect");
        var offered = false;
        // an item with a merge record is never handed to a worker: unpark re-arms its settle step when it has none (or leaves the one it has)
        var merged = Ps.Has(s, "merge") && Ps.Has(s["merge"], "sha") && Ps.Truthy(s["merge"]!["sha"]);
        var rearmed = merged ? ItemRules.RestoreSettle(s, Cfg, Now) : "";
        if (!merged && info.Open && !ItemRules.ClaimLive(s, Now) && Ps.NameIn(info.ColumnText, Cfg.ColumnsAi)) { s["handoff"] = new JsonObject { ["column"] = info.Column, ["at"] = Ps.Iso(Now) }; offered = true; }
        await AddJournal(n, st, "unparked", new JsonObject { ["column"] = info.Column, ["code"] = code, ["by"] = by, ["reason"] = reason, ["closed"] = !info.Open, ["merged"] = merged, ["reset"] = reset }, Ps.Text(park, "at"), c);
        await store.Save(n, st, c.Ct);
        var o = new JsonObject
        {
            ["ok"] = true, ["id"] = n, ["unparked"] = true, ["column"] = info.Column, ["park"] = park.DeepClone(), ["expired"] = expired, ["reoffer"] = offered, ["closed"] = !info.Open, ["countersReset"] = reset,
        };
        if (merged)
        {
            o["merged"] = true;
            o["awaiting"] = rearmed.Length > 0 ? rearmed : Ps.Has(s["merge"], "awaiting") && Ps.Truthy(s["merge"]!["awaiting"]) ? Ps.Text(s["merge"], "awaiting") : "";
            o["rearmed"] = rearmed.Length > 0;
        }
        return o;
    }

    // Verb_resettle_item: a person puts a merged item back into the settle step. Admin verb, reached only through POST /api/v1/items/{n}/resettle.
    async Task<JsonObject> ResettleItem(ItemCall c)
    {
        var n = c.Item;
        var reason = OneLine(c.NeedText("reason"));
        if (reason.Length == 0) throw new ItemRefusal(2, "usage", "a reason is required: why the item is settled again");
        if (reason.Length > 500) throw new ItemRefusal(2, "usage", "the reason is at most 500 characters");
        var by = c.Has("by") && c.Text("by").Trim().Length > 0 ? c.Text("by").Trim() : "the operator";
        var info = await board.GetItemInfo(n, c.Ct);
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (!(Ps.Has(s, "merge") && Ps.Has(s["merge"], "sha") && Ps.Truthy(s["merge"]!["sha"]))) throw new ItemRefusal(10, "not-merged", $"item #{n} has no merge record: it is not settled, it is worked");
        var merge = (JsonObject)s["merge"]!;
        if (Ps.Has(merge, "awaiting") && Ps.Truthy(merge["awaiting"])) throw new ItemRefusal(10, "already-settling", $"item #{n} is already in the settle step (awaiting {Ps.Text(merge, "awaiting")})");
        if (Ps.Eq(info.ColumnText, await board.LastColumn(c.Ct))) throw new ItemRefusal(10, "done-column", $"item #{n} is in '{info.ColumnText}', which the sweep does not read: move its card out of it first (the Release Queue, or Functional Testing)");
        var w = ItemRules.RestoreSettle(s, Cfg, Now, force: true);
        await AddJournal(n, st, "settle-rearmed", new JsonObject { ["awaiting"] = w, ["by"] = by, ["reason"] = reason }, $"resettle|{Ps.Text(merge, "rearmed_at")}", c);
        await store.Save(n, st, c.Ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["awaiting"] = w, ["column"] = info.Column, ["open"] = info.Open };
    }
}
