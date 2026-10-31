using System.Text.Json.Nodes;
using Fworker.Workflows.Merge;
using Fworker.Workflows.ReadModels;

namespace Fworker.Workflows.WorkItem.Verbs;

// W9.10q: the completion of Development (Verbs.Item.ps1 Verb_complete from "3. Development: a real pull request with green checks"): the item's open pull request, the empty and docs-only refusals, the migration-number check, and the one CI
// rule for the checks of its head (Prs.ps1 Get-RedHeadRuling: ItemCiRule) over the checks read model (IItemChecks). It runs after the checks every column has (the claim, the required section, the reviewer verdict) in ItemVerbs.Flow.cs.

public sealed partial class ItemVerbs
{
    /// <summary><c>Register-MasterDefect</c>: a master defect is reported once per set of checks (state master_defect): a comment on the item. True when this call reported it (the caller saves the state).</summary>
    async Task<bool> RegisterMasterDefect(int n, JsonObject s, int pr, string sha, IReadOnlyList<string> causes, ItemCall c)
    {
        var sorted = causes.OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase).ToList();
        var key = string.Join("|", sorted);
        var known = Ps.Has(s, "master_defect") && Ps.Eq(string.Join("|", Ps.Strings(Ps.Get(s["master_defect"], "checks"))), key);
        if (known) { ((JsonObject)s["master_defect"]!)["sha"] = sha; return false; }
        s["master_defect"] = new JsonObject { ["checks"] = Ps.Array(sorted), ["sha"] = sha, ["pr"] = pr, ["reported_at"] = Ps.Iso(Now) };
        var def = Cfg.DefaultBranch;
        await AddComment(n, $"PR #{pr} is red only on checks that fail on **{def}** as well ({string.Join(", ", causes)}): a defect of {def}, not of this change. It costs this item no fix-forward round and no demotion; the gateway waits, merges {def} into the branch once {def} passes those checks again, and judges the new head.", c);
        return true;
    }

    async Task<JsonObject> CompleteDevelopment(int n, ItemInfo info, ItemStateHolder st, string col, string comment, ItemCall c)
    {
        var s = st.State;
        var pr = await pulls.FindItemPr(n, c.Ct) ?? throw new ItemRefusal(10, "no-open-pr", "no open pull request linked to this work item");
        var number = Ps.Int(pr, "number");
        var prNumber = pr["number"]?.DeepClone();
        var prd = await pulls.GetPr(number, c.Ct) ?? new JsonObject();
        if (Ps.Int(prd, "changed_files") == 0) throw new ItemRefusal(10, "empty-pr", "pull request changes no files; an empty pull request is not a delivery", new JsonObject { ["prNumber"] = prNumber?.DeepClone() });
        var files = await pulls.GetPrFiles(number, c.Ct);
        var docsOnly = ItemRules.DocsOnlyFiles(ItemPulls.Names(files), Cfg);
        if (docsOnly && !ItemRules.ItemDocsOnly(s, info, Cfg))
            throw new ItemRefusal(10, "development-docs-only-pr", $"the pull request changes only documentation and the item's technical design did not record it as docs-only ({Cfg.DocsOnlyMarker} inside the design section)", new JsonObject { ["prNumber"] = prNumber?.DeepClone() });
        if (await pulls.MigrationCollisionOf(files, c.Ct) is { } mig)
            throw new ItemRefusal(10, "migration-number-taken", MigrationNumbers.CompleteReason(mig, Cfg.DefaultBranch), new JsonObject { ["prNumber"] = prNumber?.DeepClone(), ["script"] = mig.Script, ["taken_by"] = mig.TakenBy, ["next"] = mig.Next });
        var sha = Ps.Text(Ps.Get(prd, "head"), "sha");
        var chk = await checks.ChecksFor(sha, c.Ct);
        // the one CI rule (issue 102, Prs.ps1): a red head is re-run once, or waits for a run still going or for a defect of the base branch, before it is refused; the re-runs it made (pr.rerun) are kept with the pull request record
        JsonNode? rerun = Ps.Has(s, "pr") && Ps.Has(s["pr"], "rerun") && Ps.Int(s["pr"], "number") == number ? s["pr"]!["rerun"]!.DeepClone() : null;
        var ciWait = "";
        if (chk.Status == "failure")
        {
            if (rerun is null) s["pr"] = new JsonObject { ["number"] = prNumber?.DeepClone() }; else ((JsonObject)s["pr"]!)["number"] = number;
            var rule = await ciRule.Rule(s, sha, chk, c.Ct);
            rerun = Ps.Get(s["pr"], "rerun")?.DeepClone();
            if (rule.Ruling == "red")
            {
                s["pr"] = new JsonObject { ["number"] = number, ["branch"] = Ps.Text(Ps.Get(pr, "head"), "ref"), ["url"] = Ps.Text(pr, "html_url"), ["failed_checks"] = Ps.Array(chk.FailedChecks), ["rerun"] = rerun };
                ChecksReadModel.AddChecksRed(s, sha, Now);
                await store.Save(n, st, c.Ct);
                throw new ItemRefusal(10, "checks-not-green", $"pull request checks are not green ({rule.Reason})", new JsonObject { ["prNumber"] = prNumber?.DeepClone(), ["checks"] = chk.Public() });
            }
            if (rule.Ruling == "master-defect" && !rule.Cached)
            {
                await RegisterMasterDefect(n, s, number, sha, rule.Causes, c);
                ((JsonObject)s["master_defect"]!)["checked_at"] = Ps.Iso(Now);
            }
            ciWait = $"{rule.Ruling}: {rule.Reason}";
        }
        s["pr"] = new JsonObject { ["number"] = number, ["branch"] = Ps.Text(Ps.Get(pr, "head"), "ref"), ["url"] = Ps.Text(pr, "html_url") };
        if (rerun is not null) ((JsonObject)s["pr"]!)["rerun"] = rerun;
        // delivery-speed.md 5.1: Functional Testing starts at the push and CI runs alongside. The gateway asks for it (ftWithPendingChecks) for an item whose saga watches the checks (the feature plan of the v2 engine): checks that are still
        // running, or that the CI rule is still ruling on, do not park the item; it moves on to Functional Testing and the answer says so (continue, next_column, checks: pending). A head that is red for good is refused above, as always.
        var waiting = chk.Status == "pending" || ciWait.Length > 0;
        var alongside = waiting && c.Has("ftWithPendingChecks") && Ps.NameIn(c.Text("ftWithPendingChecks"), ["True", "true", "1"]);
        if (waiting && !alongside)
        {
            s["awaiting_checks"] = new JsonObject { ["pr"] = number, ["sha"] = sha, ["comment"] = comment, ["since"] = Ps.Iso(Now), ["column"] = col };
            ItemRules.SetPark(s, "development:awaiting-checks", Cfg.AwaitingChecksParkMinutes, Now);
            if (ItemRules.OwnedClaim(s)) await store.Save(n, st, c.Ct);
            else { s.Remove("claim"); await store.Save(n, st, c.Ct); await board.RemoveClaimRef(n, c.Ct); }
            var why = ciWait.Length > 0 ? $"checks failed and the gateway applies the CI rule ({ciWait}); it decides when that is over" : "checks are still running";
            await AddComment(n, $"PR #{number} {why}; the gateway is watching them and will advance the item when green. Stay connected while the checks run.", c);
            throw new ItemRefusal(21, "accepted-checks-pending", $"pull request {why}; the gateway is watching them and will advance the item when they are green. Stay connected while the checks run: do not end the session. The gateway advances the item when the checks are green.",
                new JsonObject { ["prNumber"] = prNumber?.DeepClone(), ["checks"] = chk.Public() });
        }
        var extra = new JsonObject { ["prNumber"] = prNumber?.DeepClone(), ["build_owed"] = !(docsOnly && ItemRules.ItemDocsOnly(s, info, Cfg)) };
        if (alongside) { extra["checks"] = "pending"; extra["ci"] = "alongside"; }
        else
        {
            s.Remove("fix_forward_rounds"); s.Remove("master_defect");
            if (Ps.Has(s, "pr") && Ps.Has(s["pr"], "rejection")) ((JsonObject)s["pr"]!).Remove("rejection");
        }
        return await FinishColumnExit(n, info, st, comment, c, extra);
    }
}
