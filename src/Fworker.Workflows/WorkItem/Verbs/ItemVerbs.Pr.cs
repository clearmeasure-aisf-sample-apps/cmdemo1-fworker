using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

// The verbs on the item's pull request: open-pr and update-pr (Verbs.Pr.ps1 Verb_open_pr, Verb_update_pr). The branches of the Copilot cloud agent (copilot/*) are taken over as the script does (W9.10q: ItemOwnerPrs, Use-CopilotDraft and Close-PrWithBranch).

public sealed partial class ItemVerbs
{
    /// <summary><c>Get-PrBodyText</c>: the worker's text, then the reference to the item.</summary>
    public static string PrBodyText(string body, int item)
    {
        var t = body.TrimEnd(); var reference = $"Refs #{item}";
        if (Regex.IsMatch(t, $@"(^|\n){Regex.Escape(reference)}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return t;
        return $"{t}\n\n{reference}";
    }

    static JsonObject PullRequestAnswer(JsonNode pr, string branch) => new()
    {
        ["number"] = pr["number"]?.DeepClone(), ["url"] = pr["html_url"]?.DeepClone(), ["head_sha"] = Ps.Get(pr["head"], "sha")?.DeepClone(), ["branch"] = branch,
    };

    // Verb_open_pr
    async Task<JsonObject> OpenPr(ItemCall c)
    {
        var n = c.Item;
        var head = c.NeedText("head"); var title = c.NeedText("title");
        var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info);
        if (!Ps.Eq(info.ColumnText, Cfg.ColumnDevelopment)) throw new ItemRefusal(10, "wrong-column", $"pull requests are opened from '{Cfg.ColumnDevelopment}'; the item is in '{info.ColumnText}'");
        // the Copilot cloud agent pushes only copilot/* branches: such a branch is the item's when the item is the Copilot runtime's (no other runtime: label routes it elsewhere)
        var copilotHead = Ps.Match(head, "^copilot/[A-Za-z0-9._/-]{1,150}$") && !info.Labels.Any(l => Ps.Match(l, "^runtime:") && !Ps.Eq(l, "runtime:copilot-cloud"));
        if (!copilotHead && !Ps.Match(head, $"^{Regex.Escape(Cfg.Name)}/{n}-")) throw new ItemRefusal(10, "bad-branch-name", $"the branch must be named '{Cfg.Name}/{n}-<something>' so the pull request belongs to the item");
        if (!Ps.Match(title, $"(^|[^0-9])#{n}([^0-9]|$)")) title = $"{title} (#{n})";
        var baseBranch = c.Has("base") ? c.Text("base") : Cfg.DefaultBranch;
        var body = c.Has("body") ? c.Text("body") : "";
        var existing = await pulls.FindItemPr(n, c.Ct);
        // the item already has its pull request on another branch (fix-forward, rework): a Copilot task started from that branch and pushed its own (CopilotCloudRuntime.BaseRef); the new branch must hold the old one's commits, then it becomes
        // the item's pull request and the old one is closed as superseded, its branch deleted
        var prev = existing is not null && !Ps.Eq(Ps.Text(existing["head"], "ref"), head) ? existing : null;
        if (prev is not null && copilotHead)
        {
            var cmp = await gh.Allow("GET", $"{gh.RepoPath}/compare/{Ps.Text(prev["head"], "sha")}...{head}", [404], null, c.Ct);
            if (cmp.Allowed || !Ps.NameIn(Ps.Text(cmp.Body, "status"), ["ahead", "identical"]))
                throw new ItemRefusal(10, "not-on-item-branch", $"#{n} already has its pull request #{Ps.Text(prev, "number")} on '{Ps.Text(prev["head"], "ref")}'; '{head}' does not hold its commits. Work on a branch made from '{Ps.Text(prev["head"], "ref")}' (the task's own branch is) and pass that.");
        }
        if (copilotHead && !(existing is not null && Ps.Eq(Ps.Text(existing["head"], "ref"), head))) existing = await ownerPrs.UseCopilotDraft(n, head, title, prev is not null ? Ps.Int(prev, "number") : 0, c.Session, c.Ct);
        if (prev is not null && copilotHead && existing is null)
        {
            // no draft on the branch: the item's pull request is opened on it below, then the old one is superseded
            await pulls.AssertPushGate(n, info, (await store.Read(n, c.Ct)).State, "", null, c.Ct);
            var made = await gh.Allow("POST", $"{gh.RepoPath}/pulls", [422], new JsonObject { ["title"] = title, ["head"] = head, ["base"] = Cfg.DefaultBranch, ["body"] = PrBodyText(body, n), ["draft"] = false }, c.Ct);
            if (made.Allowed) throw new ItemRefusal(10, "pr-not-created", $"GitHub refused to create the pull request on '{head}': {Ps.Text(made.Body, "message")}");
            var madeState = await store.Read(n, c.Ct);
            madeState.State["pr"] = new JsonObject { ["number"] = made.Body!["number"]?.DeepClone(), ["branch"] = head, ["url"] = made.Body["html_url"]?.DeepClone() };
            await store.Save(n, madeState, c.Ct);
            existing = await pulls.GetPr(Ps.Int(made.Body, "number"), c.Ct);
        }
        if (prev is not null && copilotHead && existing is not null && Ps.Int(existing, "number") != Ps.Int(prev, "number"))
        {
            await pulls.AssertPushGate(n, info, (await store.Read(n, c.Ct)).State, Ps.Text(existing["head"], "sha"), Ps.Int(existing, "number"), c.Ct);
            await ownerPrs.CloseStrayDrafts(n, [Ps.Text(prev["head"], "ref"), head], Ps.Int(existing, "number"), c.Session, c.Ct);
            await ownerPrs.ClosePrWithBranch(prev, n, $"Superseded by #{Ps.Int(existing, "number")}, the pull request of #{n} now: it holds this branch's commits and the fix. Closed by the factory worker gateway.", c.Session, c.Ct);
        }
        var gs = (await store.Read(n, c.Ct)).State;
        if (existing is not null && Ps.Eq(Ps.Text(existing["head"], "ref"), head))
        {
            if (copilotHead) await ownerPrs.CloseStrayDrafts(n, [head], Ps.Int(existing, "number"), c.Session, c.Ct);
            await pulls.AssertPushGate(n, info, gs, Ps.Text(existing["head"], "sha"), Ps.Int(existing, "number"), c.Ct);
            // the gateway's own resolution (Resolve-OwnerPr) may have adopted this branch's pull request before this call: it still names the item
            if (copilotHead) existing = await ownerPrs.SetItemTitle(existing, n, title, true, c.Ct) ?? existing;
            var bo0 = await pulls.GetBuildOwed(info, Ps.Int(existing, "number"), gs, c.Ct);
            var o0 = new JsonObject { ["ok"] = true, ["id"] = n, ["created"] = false, ["pullRequest"] = PullRequestAnswer(existing, head) };
            ItemPulls.AddTo(o0, bo0);
            return o0;
        }
        await pulls.AssertPushGate(n, info, gs, "", null, c.Ct);
        var r = await gh.Allow("POST", $"{gh.RepoPath}/pulls", [422], new JsonObject { ["title"] = title, ["head"] = head, ["base"] = baseBranch, ["body"] = PrBodyText(body, n), ["draft"] = false }, c.Ct);
        if (r.Allowed)
        {
            var msg = (Ps.Get(r.Body, "errors") as JsonArray ?? []).Select(e => Ps.Text(e, "message"));
            var detail = string.Join("; ", msg);
            throw new ItemRefusal(10, "pr-not-created", $"GitHub refused to create the pull request: {Ps.Text(r.Body, "message")} {detail}. This usually means '{head}' has no commits ahead of '{baseBranch}' or was not pushed. Do not push an empty commit; if the item is already satisfied, report complete with succeeded=false.", new JsonObject { ["detail"] = detail });
        }
        var created = r.Body!;
        var st = await store.Read(n, c.Ct);
        st.State["pr"] = new JsonObject { ["number"] = created["number"]?.DeepClone(), ["branch"] = head, ["url"] = created["html_url"]?.DeepClone() };
        await store.Save(n, st, c.Ct);
        var bo = await pulls.GetBuildOwed(info, Ps.Int(created, "number"), st.State, c.Ct);
        var o = new JsonObject { ["ok"] = true, ["id"] = n, ["created"] = true, ["pullRequest"] = PullRequestAnswer(created, head) };
        ItemPulls.AddTo(o, bo);
        return o;
    }

    // Verb_update_pr: replaces the body of the item's own open pull request (a worker corrects what open-pr wrote; nothing else of the pull request changes)
    async Task<JsonObject> UpdatePr(ItemCall c)
    {
        var n = c.Item;
        var body = c.NeedText("body"); if (body.Trim() == "") throw new ItemRefusal(2, "usage", "missing argument: body");
        var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info);
        // only while the item is in Development, the owner's column: after that the pull request has been read and approved as it is
        if (!Ps.Eq(info.ColumnText, Cfg.ColumnDevelopment)) throw new ItemRefusal(10, "wrong-column", $"the body of the pull request is changed only while the item is in '{Cfg.ColumnDevelopment}'; it is in '{info.ColumnText}'");
        var own = await pulls.FindItemPr(n, c.Ct);
        // the item's own branch, or the copilot/* branch its state records
        if (own is not null)
        {
            var reference = Ps.Text(own["head"], "ref");
            if (!Ps.Match(reference, $"^{Regex.Escape(Cfg.Name)}/{n}-") && !reference.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase)) own = null;
        }
        if (c.Has("pr"))
        {
            var want = Ps.Int(c.Args, "pr");
            if (own is null || Ps.Int(own, "number") != want)
            {
                JsonNode? p = null; try { p = await pulls.GetPr(want, c.Ct); } catch (ItemRefusal) { }
                if (p is not null && Ps.Match(Ps.Text(p["head"], "ref"), $"^{Regex.Escape(Cfg.Name)}/{n}-"))
                    throw new ItemRefusal(10, "pr-not-open", $"pull request #{want} of this item is {(Ps.TruthyKey(p, "merged") ? "merged" : Ps.Text(p, "state"))}; only an open pull request is changed");
                throw new ItemRefusal(10, "pr-not-own", $"pull request #{want} is not the pull request of item #{n}");
            }
        }
        if (own is null)
        {
            var s0 = (await store.Read(n, c.Ct)).State;
            if (Ps.Has(s0, "pr") && Ps.Has(s0["pr"], "number")) throw new ItemRefusal(10, "pr-not-open", $"pull request #{Ps.Text(s0["pr"], "number")} of this item is no longer open; only an open pull request is changed");
            throw new ItemRefusal(10, "no-open-pr", "no open pull request linked to this work item");
        }
        await pulls.AssertPushGate(n, info, (await store.Read(n, c.Ct)).State, Ps.Text(own["head"], "sha"), Ps.Int(own, "number"), c.Ct);
        await gh.Json("PATCH", $"{gh.RepoPath}/pulls/{Ps.Int(own, "number")}", new JsonObject { ["body"] = PrBodyText(body, n) }, c.Ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["updated"] = true, ["pullRequest"] = PullRequestAnswer(own, Ps.Text(own["head"], "ref")) };
    }
}
