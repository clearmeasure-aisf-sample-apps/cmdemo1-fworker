using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

/// <summary>What <c>Resolve-OwnerPr</c> found: the owner task's pull request as it is now and what the resolution did to it.</summary>
public sealed class OwnerPrFound
{
    public JsonNode Pr { get; set; } = new JsonObject();
    public bool Changed { get; set; }
    public bool Retargeted { get; set; }
    public int Superseded { get; set; }
    public string Refused { get; set; } = "";
}

/// <summary>
/// W9.10q: the pull request side of the Copilot cloud agent for the item verbs (Verbs.Pr.ps1 <c>Set-PrItemTitle</c>, <c>Set-PrReady</c>, <c>Use-CopilotDraft</c>, <c>Close-PrWithBranch</c>, <c>Close-StrayCopilotDrafts</c> and <c>Resolve-OwnerPr</c>),
/// which <c>open-pr</c> for a <c>copilot/*</c> branch and <c>item-pr</c> for an owner task make. The item's pull request is always the open pull request of the owner task's current branch; a stack is retargeted to the default branch or
/// consolidated, a draft is marked ready. The same rules run for the delivery verbs on their own ports (Delivery.Verbs.OwnerPrs); these are the item verbs', on the item verbs' wire and state.
/// </summary>
public sealed class ItemOwnerPrs(ItemGitHub gh, ItemPulls pulls, ItemBoard board, IItemStateStore store, IItemClock clock)
{
    ItemVerbConfig Cfg => gh.Config;
    const string ReadyMutation = "mutation($id:ID!){ markPullRequestReadyForReview(input:{pullRequestId:$id}){ pullRequest{ number } } }";

    static string Ref(JsonNode? p) => Ps.Text(Ps.Get(p, "head"), "ref");
    static string BaseRef(JsonNode? p) => Ps.Text(Ps.Get(p, "base"), "ref");
    static bool IsCopilot(string reference) => reference.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase);
    static string Esc(string s) => Uri.EscapeDataString(s);

    async Task<List<JsonNode>> Open(string query, CancellationToken ct) =>
        (await gh.Get($"{Cfg.RepoPath}/pulls?state=open&{query}", ct)) switch { JsonArray a => a.Where(x => x is not null).Select(x => x!).ToList(), null => [], var one => [one] };

    /// <summary><c>Set-PrItemTitle</c>: the pull request's title names #N (the CI release finds a held run's item from it). Soft: nothing throws, a refused write leaves the pull request as it was.</summary>
    public async Task<JsonNode?> SetItemTitle(JsonNode? pr, int n, string title, bool soft, CancellationToken ct)
    {
        if (pr is null || Ps.Match(Ps.Text(pr, "title"), $"(^|[^0-9])#{n}([^0-9]|$)")) return pr;
        if (title.Length == 0)
        {
            var t = Ps.Replace(Ps.Text(pr, "title"), @"^\s*\[WIP\]\s*", "").Trim();
            title = t.Length == 0 ? $"#{n}" : Ps.Match(t, @"#\d") ? $"#{n}: {t}" : $"{t} (#{n})";
        }
        else if (!Ps.Match(title, $"(^|[^0-9])#{n}([^0-9]|$)")) title = $"{title} (#{n})";
        var number = Ps.Int(pr, "number");
        try { await gh.Json("PATCH", $"{Cfg.RepoPath}/pulls/{number}", new JsonObject { ["title"] = title }, ct); }
        catch (Exception e) when (soft && e is not OperationCanceledException) { return pr; }
        if (!soft) return await pulls.GetPr(number, ct);
        // the write is done: a failed re-read returns the pull request as read before, with its new title
        try { return await pulls.GetPr(number, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { try { pr["title"] = title; } catch (InvalidOperationException) { } return pr; }
    }

    Task MarkReady(JsonNode pr, CancellationToken ct) => gh.GraphQL(ReadyMutation, new JsonObject { ["id"] = Ps.Text(pr, "node_id") }, ct);

    /// <summary><c>Set-PrReady</c>: an owner pull request still a draft is marked ready for review (GitHub merges no draft and QC is not offered for one); a refusal leaves it as it was.</summary>
    public async Task<JsonNode?> SetReady(JsonNode? pr, CancellationToken ct)
    {
        if (pr is null || !Ps.TruthyKey(pr, "draft")) return pr;
        try { await MarkReady(pr, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return pr; }
        return await pulls.GetPr(Ps.Int(pr, "number"), ct);
    }

    /// <summary><c>Close-PrWithBranch</c>: a pull request closed with a comment and its branch deleted (a <c>copilot/*</c> branch or the factory's own branch of the item only).</summary>
    public async Task ClosePrWithBranch(JsonNode pr, int n, string why, JsonObject? session, CancellationToken ct)
    {
        var number = Ps.Int(pr, "number");
        await gh.Allow("POST", $"{Cfg.RepoPath}/issues/{number}/comments", [404, 422], new JsonObject { ["body"] = ItemBoard.WithFooter(why, session) }, ct);
        await gh.Json("PATCH", $"{Cfg.RepoPath}/pulls/{number}", new JsonObject { ["state"] = "closed" }, ct);
        var reference = Ref(pr);
        if (IsCopilot(reference) || Ps.Match(reference, $"^{Regex.Escape(Cfg.Name)}/{n}-"))
            await gh.Allow("DELETE", $"{Cfg.RepoPath}/git/refs/heads/{reference}", [404, 422], null, ct);
    }

    /// <summary><c>Close-StrayCopilotDrafts</c>: the extra drafts a task opened against the item's branches (a task that started from the item's pull request and opened its own draft) are closed, their branches deleted; never the default branch's, never <paramref name="keep"/>.</summary>
    public async Task CloseStrayDrafts(int n, IReadOnlyList<string> bases, int keep, JsonObject? session, CancellationToken ct)
    {
        foreach (var p in await Open("per_page=100", ct))
        {
            if (Ps.Int(p, "number") == keep || !Ps.TruthyKey(p, "draft") || !IsCopilot(Ref(p))) continue;
            var b = BaseRef(p);
            if (Ps.Eq(b, Cfg.DefaultBranch) || !Ps.NameIn(b, bases)) continue;
            await ClosePrWithBranch(p, n, $"Closed by the factory worker gateway: an extra draft of a task of #{n}; the item's pull request is #{keep}.", session, ct);
        }
    }

    /// <summary>
    /// <c>Use-CopilotDraft</c>: the draft pull request the Copilot cloud agent opened on its branch, taken over for the item (title names #N, marked ready for review, recorded in the state as the item's pull request). Null when there is none, or
    /// when GitHub does not retarget a stacked draft (it is closed with a comment, its branch kept, and the caller opens the item's pull request on the branch itself).
    /// </summary>
    public async Task<JsonNode?> UseCopilotDraft(int n, string head, string title, int superseding, JsonObject? session, CancellationToken ct)
    {
        var pr = (await Open($"head={Esc($"{Cfg.Owner}:{head}")}&per_page=5", ct)).FirstOrDefault();
        if (pr is null) return null;
        // a draft that names another item, or that another item's state already holds, is not this item's to take
        var named = Regex.Matches($"{Ps.Text(pr, "title")} {Ps.Text(pr, "body")}", @"(?<![0-9A-Za-z])#(\d+)\b", RegexOptions.CultureInvariant).Select(m => int.Parse(m.Groups[1].Value)).Where(x => x != n && x != superseding).ToList();
        // a pull request it names is no other item: a Copilot follow-up opens its own draft "which merges into #<the item's pull request>" (#474 on #470)
        var others = new List<int>();
        foreach (var x in named) if ((await gh.Allow("GET", $"{Cfg.RepoPath}/pulls/{x}", [404], null, ct)).Allowed) others.Add(x);
        if (others.Count > 0) throw new ItemRefusal(10, "branch-of-other-item", $"the pull request on '{head}' names #{others[0]}, not #{n}");
        // a draft Copilot opened against the item's previous branch (a task started from it) goes to the default branch: it carries that branch's commits.
        // GitHub refuses that for a draft that is part of a stack (422 "part of a stack", #395/#398): the draft is closed, its branch kept, and the caller opens the item's pull request on the branch itself
        if (!Ps.Eq(BaseRef(pr), Cfg.DefaultBranch))
        {
            var rb = await gh.Allow("PATCH", $"{Cfg.RepoPath}/pulls/{Ps.Int(pr, "number")}", [422], new JsonObject { ["base"] = Cfg.DefaultBranch }, ct);
            if (rb.Allowed)
            {
                await gh.Allow("POST", $"{Cfg.RepoPath}/issues/{Ps.Int(pr, "number")}/comments", [404, 422], new JsonObject { ["body"] = $"Closed by the factory worker gateway: GitHub does not retarget this stacked draft to {Cfg.DefaultBranch}; the pull request of #{n} is opened on the same branch instead." }, ct);
                await gh.Json("PATCH", $"{Cfg.RepoPath}/pulls/{Ps.Int(pr, "number")}", new JsonObject { ["state"] = "closed" }, ct);
                return null;
            }
        }
        _ = await SetItemTitle(pr, n, title, false, ct);
        if (Ps.TruthyKey(pr, "draft")) await MarkReady(pr, ct);
        var st = await store.Read(n, ct);
        st.State["pr"] = new JsonObject { ["number"] = pr["number"]?.DeepClone(), ["branch"] = head, ["url"] = pr["html_url"]?.DeepClone() };
        await store.Save(n, st, ct);
        return await pulls.GetPr(Ps.Int(pr, "number"), ct);
    }

    /// <summary>
    /// <c>Resolve-OwnerPr</c>: the item's pull request of a Copilot owner task is always the open pull request of the owner task's current branch (copilot_task.branch). A recorded pull request that was closed or superseded (a migration, a takeover)
    /// is replaced by it in the state (pr, copilot_task.pr, awaiting_checks). Every owner pull request targets the default branch: one stacked on another branch is retargeted once that base has no open pull request (merged or closed); while the
    /// base's pull request is still open, the stack is consolidated onto the owner's pull request when its branch holds the base's commits (the commit-ancestry guard of #192), the base's pull request is closed as superseded. Null when the item has no
    /// owner branch with an open pull request.
    /// </summary>
    public async Task<OwnerPrFound?> Resolve(int n, JsonObject? session, CancellationToken ct)
    {
        var def = Cfg.DefaultBranch;
        var st = await store.Read(n, ct); var s = st.State;
        if (!Ps.Has(s, "copilot_task") || Ps.Text(s["copilot_task"], "branch").Length == 0) return null;
        if (Ps.Has(s["copilot_task"], "done_at") && Ps.TruthyKey(s["copilot_task"], "done_at")) return null;
        var br = Ps.Text(s["copilot_task"], "branch");
        var pr = (await Open($"head={Esc($"{Cfg.Owner}:{br}")}&per_page=5", ct)).FirstOrDefault(p => Ps.Eq(Ref(p), br));
        // a follow-up session may open its own draft on a new branch, stacked on the owner's ("merges into #<pr>", #468 on #466, #474 on #470): the owner's work goes on there, so its current branch is the newest such successor (followed a few levels)
        for (var hop = 0; hop < 3; hop++)
        {
            var current = br;
            var succ = (await Open($"base={Esc(br)}&per_page=20", ct))
                .Where(p => (IsCopilot(Ref(p)) || Ps.Match(Ref(p), $"^{Regex.Escape(Cfg.Name)}/{n}-")) && Ps.Eq(BaseRef(p), current)).OrderBy(p => Ps.Int(p, "number")).LastOrDefault();
            if (succ is null) break;
            br = Ref(succ); pr = succ;
        }
        // the item's recorded pull request, when open on a copilot/* branch and newer than the one found, is the owner's current one: a task's later branch that is not stacked on the owner's (based on the default branch) is found by no base
        // lookup, and the owner branch must not go back to the closed older pull request (#455: FT sessions were sent to the closed #470, never connected)
        var recNo = Ps.Has(s, "pr") && Ps.Has(s["pr"], "number") ? Ps.Int(s["pr"], "number") : 0;
        if (recNo > 0 && (pr is null || recNo > Ps.Int(pr, "number")))
        {
            JsonNode? rp = null;
            try { rp = await pulls.GetPr(recNo, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            if (rp is not null && Ps.Eq(Ps.Text(rp, "state"), "open") && !Ps.TruthyKey(rp, "merged") && IsCopilot(Ref(rp))) { pr = rp; br = Ref(rp); }
        }
        if (pr is null) return null;
        if (!Ps.Eq(Ps.Text(s["copilot_task"], "branch"), br)) { s["copilot_task"]!["branch"] = br; await store.Save(n, st, ct); }
        var o = new OwnerPrFound { Pr = pr };
        if (!Ps.Eq(BaseRef(pr), def))
        {
            var baseRef = BaseRef(pr);
            var basePr = (await Open($"head={Esc($"{Cfg.Owner}:{baseRef}")}&per_page=5", ct)).FirstOrDefault(p => Ps.Eq(Ref(p), baseRef));
            var go = true;
            if (basePr is not null)
            {
                // the base's pull request is still open: consolidate only when the owner's branch holds its commits
                var cmp = await gh.Allow("GET", $"{Cfg.RepoPath}/compare/{Ps.Text(Ps.Get(basePr, "head"), "sha")}...{Ps.Text(Ps.Get(pr, "head"), "sha")}", [404], null, ct);
                if (cmp.Allowed || cmp.Body is null || !Ps.NameIn(Ps.Text(cmp.Body, "status"), ["ahead", "identical"])) { go = false; o.Refused = $"stacked on #{Ps.Int(basePr, "number")}, whose commits '{br}' does not hold"; }
            }
            if (go)
            {
                var rb = await gh.Allow("PATCH", $"{Cfg.RepoPath}/pulls/{Ps.Int(pr, "number")}", [422], new JsonObject { ["base"] = def }, ct);
                if (rb.Allowed) o.Refused = $"GitHub refused to retarget #{Ps.Int(pr, "number")} to {def}: {Ps.Text(rb.Body, "message")}";
                else
                {
                    o.Retargeted = true; pr = (await pulls.GetPr(Ps.Int(pr, "number"), ct))!; o.Pr = pr;
                    await board.AddComment(n, $"PR #{Ps.Int(pr, "number")}, the pull request of the owner task's branch `{br}`, was stacked on `{BaseRef(pr)}`{(basePr is not null ? $" (#{Ps.Int(basePr, "number")})" : "")}; the gateway retargeted it to **{def}**, where CI runs.", session, ct);
                    // the base's pull request only after the retarget: deleting its branch first would close the stacked one with it
                    if (basePr is not null)
                    {
                        await ClosePrWithBranch(basePr, n, $"Superseded by #{Ps.Int(pr, "number")}, the pull request of #{n} now: it holds this branch's commits and the owner task's work. Closed by the factory worker gateway.", session, ct);
                        o.Superseded = Ps.Int(basePr, "number");
                    }
                }
            }
        }
        st = await store.Read(n, ct); s = st.State;
        var rec = Ps.Has(s, "pr") && Ps.Has(s["pr"], "number") ? Ps.Int(s["pr"], "number") : 0;
        var prNumber = Ps.Int(pr, "number");
        // only a recorded pull request is replaced: none recorded yet (a Technical Design draft) stays unrecorded until pr_open or the settle takes it
        if (rec > 0 && rec != prNumber)
        {
            // what the record says about the work (a rework asked for, the failed checks) stays; the re-run record was of the old pull request
            var sp = (JsonObject)s["pr"]!;
            sp["number"] = prNumber; sp["branch"] = br; sp["url"] = Ps.Text(pr, "html_url"); if (Ps.Has(sp, "rerun")) sp.Remove("rerun");
            o.Changed = true;
        }
        if (Ps.Has(s, "awaiting_checks") && Ps.Int(s["awaiting_checks"], "pr") != prNumber)
        {
            var ac = (JsonObject)s["awaiting_checks"]!;
            ac["pr"] = prNumber; ac["sha"] = Ps.Text(Ps.Get(pr, "head"), "sha"); ac["since"] = Ps.Iso(clock.UtcNow); o.Changed = true;
        }
        if (Ps.Int(s["copilot_task"], "pr") != prNumber) { s["copilot_task"]!["pr"] = prNumber; o.Changed = true; }
        // a recorded owner pull request (a successor adopted here included) or one retargeted to the default branch names the item in its title, as Use-CopilotDraft names it: this resolution may adopt a stacked successor before open-pr runs,
        // and open-pr then takes its same-branch return
        if (rec > 0 || o.Retargeted) { pr = (await SetItemTitle(pr, n, "", true, ct))!; o.Pr = pr; }
        // a recorded owner pull request (a successor adopted after Development included) is never left a draft
        if (rec > 0 && Ps.TruthyKey(pr, "draft")) { pr = (await SetReady(pr, ct))!; o.Pr = pr; }
        if (o.Changed)
        {
            await store.Save(n, st, ct);
            if (rec > 0 && rec != Ps.Int(pr, "number"))
                await board.AddComment(n, $"The item's pull request is now #{Ps.Int(pr, "number")}, the open pull request of the owner task's branch `{br}` (the recorded #{rec} is closed or superseded).", session, ct);
        }
        return o;
    }
}
