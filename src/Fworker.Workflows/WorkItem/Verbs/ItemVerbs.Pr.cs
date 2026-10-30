using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

// The verbs on the item's pull request: open-pr and update-pr (Verbs.Pr.ps1 Verb_open_pr, Verb_update_pr). The branches of the Copilot cloud agent (copilot/*) are the script's still (W9.4: Use-CopilotDraft, Close-PrWithBranch).

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
        if (copilotHead) throw ToV1("the pull request of a Copilot branch is opened with the script's takeover of the draft (W9.4)");
        if (!Ps.Match(title, $"(^|[^0-9])#{n}([^0-9]|$)")) title = $"{title} (#{n})";
        var baseBranch = c.Has("base") ? c.Text("base") : Cfg.DefaultBranch;
        var body = c.Has("body") ? c.Text("body") : "";
        var existing = await pulls.FindItemPr(n, c.Ct);
        var gs = (await store.Read(n, c.Ct)).State;
        if (existing is not null && Ps.Eq(Ps.Text(existing["head"], "ref"), head))
        {
            await pulls.AssertPushGate(n, info, gs, Ps.Text(existing["head"], "sha"), Ps.Int(existing, "number"), c.Ct);
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
