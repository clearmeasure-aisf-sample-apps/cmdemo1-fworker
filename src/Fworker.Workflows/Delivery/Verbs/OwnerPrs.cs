using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

/// <summary>What Resolve-OwnerPr found: the owner task's pull request as it is now and what the resolution did to it.</summary>
public sealed class OwnerPrResolution
{
    public JsonNode Pr { get; set; } = new JsonObject();
    public bool Changed { get; set; }
    public bool Retargeted { get; set; }
    public int Superseded { get; set; }
    public string Refused { get; set; } = "";
}

// W9.4: the pull request side of the Copilot owner task (Verbs.Pr.ps1 Set-PrItemTitle, Set-PrReady, Close-PrWithBranch, Close-StrayCopilotDrafts and Resolve-OwnerPr): the item's pull request is always the open
// pull request of the owner task's current branch; a stack is retargeted to the default branch or consolidated, a draft is marked ready. Used by copilot-settle and the status of copilot-owner.
public static class OwnerPrs
{
    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Set-PrItemTitle: the pull request's title names #N (the CI release finds a held run's item from it). Soft: nothing throws, a refused write leaves the pull request as it was.</summary>
    public static async Task<JsonNode?> SetItemTitle(RuleKit kit, JsonNode? pr, int n, string title, bool soft, CancellationToken ct)
    {
        if (pr is null || Regex.IsMatch(J.S(pr, "title"), $"(^|[^0-9])#{n}([^0-9]|$)")) return pr;
        if (title.Length == 0)
        {
            var t = Regex.Replace(J.S(pr, "title"), @"^\s*\[WIP\]\s*", "", RegexOptions.IgnoreCase).Trim();
            title = t.Length == 0 ? $"#{n}" : Regex.IsMatch(t, @"#\d") ? $"#{n}: {t}" : $"{t} (#{n})";
        }
        else if (!Regex.IsMatch(title, $"(^|[^0-9])#{n}([^0-9]|$)")) title = $"{title} (#{n})";
        var number = J.I(pr, "number");
        try { await GhApi.Call(kit.Gh, HttpMethod.Patch, $"{kit.RepoPath}/pulls/{number}", new JsonObject { ["title"] = title }, ct); }
        catch (Exception e) when (soft && e is VerbReject or HttpRequestException) { return pr; }
        if (!soft) return await kit.GetPr(number, ct);
        // the write is done: a failed re-read returns the pull request as read before, with its new title
        try { return await kit.GetPr(number, ct); }
        catch (Exception e) when (e is VerbReject or HttpRequestException) { try { pr["title"] = title; } catch { } return pr; }
    }

    const string ReadyMutation = "mutation($id:ID!){ markPullRequestReadyForReview(input:{pullRequestId:$id}){ pullRequest{ number } } }";

    /// <summary>The mutation that takes a draft out of draft state; the caller decides what a failure means.</summary>
    public static Task MarkReady(RuleKit kit, JsonNode pr, CancellationToken ct) => GhApi.GraphQl(kit.Gh, ReadyMutation, new JsonObject { ["id"] = J.S(pr, "node_id") }, ct);

    /// <summary>Set-PrReady: an owner pull request still a draft is marked ready for review; a refusal leaves it as it was.</summary>
    public static async Task<JsonNode?> SetReady(RuleKit kit, JsonNode? pr, CancellationToken ct)
    {
        if (pr is null || !J.B(pr, "draft")) return pr;
        try { await MarkReady(kit, pr, ct); }
        catch (Exception e) when (e is VerbReject or HttpRequestException) { return pr; }
        return await kit.GetPr(J.I(pr, "number"), ct);
    }

    /// <summary>Close-PrWithBranch: a pull request closed with a comment and its branch deleted (a copilot/* branch or the factory's own branch of the item only).</summary>
    public static async Task ClosePrWithBranch(RuleKit kit, JsonNode pr, int n, string why, CancellationToken ct)
    {
        var number = J.I(pr, "number");
        await GhApi.Call(kit.Gh, HttpMethod.Post, $"{kit.RepoPath}/issues/{number}/comments", new JsonObject { ["body"] = kit.WithSessionFooter(why) }, ct, [404, 422]);
        await GhApi.Call(kit.Gh, HttpMethod.Patch, $"{kit.RepoPath}/pulls/{number}", new JsonObject { ["state"] = "closed" }, ct);
        var @ref = J.S(pr["head"], "ref");
        if (@ref.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(@ref, $"^{Regex.Escape(kit.Name)}/{n}-", RegexOptions.IgnoreCase))
            await GhApi.Call(kit.Gh, HttpMethod.Delete, $"{kit.RepoPath}/git/refs/heads/{@ref}", null, ct, [404, 422]);
    }

    /// <summary>Close-StrayCopilotDrafts: the extra drafts a task opened against the item's branches are closed, their branches deleted; never the default branch's, never <paramref name="keep"/>.</summary>
    public static async Task CloseStrayDrafts(RuleKit kit, int n, IReadOnlyList<string> bases, int keep, CancellationToken ct)
    {
        var def = kit.DefaultBranch;
        foreach (var p in J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/pulls?state=open&per_page=100", ct)).Body).ToList())
        {
            if (p is null || J.I(p, "number") == keep || !J.B(p, "draft") || !J.S(p["head"], "ref").StartsWith("copilot/", StringComparison.OrdinalIgnoreCase)) continue;
            var b = J.S(p["base"], "ref");
            if (b == def || !bases.Contains(b, StringComparer.OrdinalIgnoreCase)) continue;
            await ClosePrWithBranch(kit, p, n, $"Closed by the factory worker gateway: an extra draft of a task of #{n}; the item's pull request is #{keep}.", ct);
        }
    }

    /// <summary>
    /// Resolve-OwnerPr: the item's pull request of a Copilot owner task is always the open pull request of the owner task's current branch (copilot_task.branch). A recorded pull request that was closed or
    /// superseded is replaced by it in the state (pr, copilot_task.pr, awaiting_checks). Every owner pull request targets the default branch: a stacked one is retargeted once its base has no open pull
    /// request, or consolidated onto the owner's when its branch holds the base's commits. Null when the item has no owner branch with an open pull request.
    /// </summary>
    public static async Task<OwnerPrResolution?> Resolve(RuleKit kit, int n, CancellationToken ct)
    {
        var def = kit.DefaultBranch;
        var st = await kit.GetState(n, ct); var s = st.State;
        if (!J.Has(s, "copilot_task") || J.S(s["copilot_task"], "branch").Length == 0) return null;
        if (J.Has(s["copilot_task"], "done_at") && J.B(s["copilot_task"], "done_at")) return null;
        var br = J.S(s["copilot_task"], "branch");
        string Esc(string x) => Uri.EscapeDataString(x);
        async Task<List<JsonNode>> Open(string query) => J.Items((await GhApi.Get(kit.Gh, $"{kit.RepoPath}/pulls?state=open&{query}", ct)).Body).Where(x => x is not null).Select(x => x!).ToList();
        var pr = (await Open($"head={Esc($"{kit.Owner}:{br}")}&per_page=5")).FirstOrDefault(p => Eq(J.S(p["head"], "ref"), br));
        // a follow-up session may open its own draft on a new branch, stacked on the owner's: the owner's work goes on there (followed a few levels)
        for (var hop = 0; hop < 3; hop++)
        {
            var succ = (await Open($"base={Esc(br)}&per_page=20"))
                .Where(p => (J.S(p["head"], "ref").StartsWith("copilot/", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(J.S(p["head"], "ref"), $"^{Regex.Escape(kit.Name)}/{n}-", RegexOptions.IgnoreCase)) && Eq(J.S(p["base"], "ref"), br))
                .OrderBy(p => J.I(p, "number")).LastOrDefault();
            if (succ is null) break;
            br = J.S(succ["head"], "ref"); pr = succ;
        }
        // the item's recorded pull request, when open on a copilot/* branch and newer than the one found, is the owner's current one
        var recNo = J.Has(s, "pr") && J.Has(s["pr"], "number") ? J.I(s["pr"], "number") : 0;
        if (recNo > 0 && (pr is null || recNo > J.I(pr, "number")))
        {
            JsonNode? rp = null;
            try { rp = await kit.GetPr(recNo, ct); } catch (Exception e) when (e is VerbReject or HttpRequestException) { }
            if (rp is not null && Eq(J.S(rp, "state"), "open") && !J.B(rp, "merged") && J.S(rp["head"], "ref").StartsWith("copilot/", StringComparison.OrdinalIgnoreCase)) { pr = rp; br = J.S(rp["head"], "ref"); }
        }
        if (pr is null) return null;
        if (J.S(s["copilot_task"], "branch") != br) { s["copilot_task"]!["branch"] = br; await kit.SaveState(n, st, ct); }
        var o = new OwnerPrResolution { Pr = pr };
        if (!Eq(J.S(pr["base"], "ref"), def))
        {
            var baseRef = J.S(pr["base"], "ref");
            var basePr = (await Open($"head={Esc($"{kit.Owner}:{baseRef}")}&per_page=5")).FirstOrDefault(p => Eq(J.S(p["head"], "ref"), baseRef));
            var go = true;
            if (basePr is not null)
            {
                // the base's pull request is still open: consolidate only when the owner's branch holds its commits
                var cmp = await GhApi.Get(kit.Gh, $"{kit.RepoPath}/compare/{J.S(basePr["head"], "sha")}...{J.S(pr["head"], "sha")}", ct, 404);
                if (!cmp.Ok || !(J.S(cmp.Body, "status") is "ahead" or "identical")) { go = false; o.Refused = $"stacked on #{J.I(basePr, "number")}, whose commits '{br}' does not hold"; }
            }
            if (go)
            {
                var rb = await GhApi.Call(kit.Gh, HttpMethod.Patch, $"{kit.RepoPath}/pulls/{J.I(pr, "number")}", new JsonObject { ["base"] = def }, ct, [422]);
                if (!rb.Ok) o.Refused = $"GitHub refused to retarget #{J.I(pr, "number")} to {def}: {rb.Message}";
                else
                {
                    o.Retargeted = true; pr = await kit.GetPr(J.I(pr, "number"), ct); o.Pr = pr;
                    await kit.AddComment(n, $"PR #{J.I(pr, "number")}, the pull request of the owner task's branch `{br}`, was stacked on `{J.S(pr["base"], "ref")}`{(basePr is not null ? $" (#{J.I(basePr, "number")})" : "")}; the gateway retargeted it to **{def}**, where CI runs.", ct);
                    // the base's pull request only after the retarget: deleting its branch first would close the stacked one with it
                    if (basePr is not null)
                    {
                        await ClosePrWithBranch(kit, basePr, n, $"Superseded by #{J.I(pr, "number")}, the pull request of #{n} now: it holds this branch's commits and the owner task's work. Closed by the factory worker gateway.", ct);
                        o.Superseded = J.I(basePr, "number");
                    }
                }
            }
        }
        st = await kit.GetState(n, ct); s = st.State;
        var rec = J.Has(s, "pr") && J.Has(s["pr"], "number") ? J.I(s["pr"], "number") : 0;
        var prNumber = J.I(pr, "number");
        // only a recorded pull request is replaced: none recorded yet (a Technical Design draft) stays unrecorded until pr_open or the settle takes it
        if (rec > 0 && rec != prNumber)
        {
            // what the record says about the work (a rework asked for, the failed checks) stays; the re-run record was of the old pull request
            var sp = (JsonObject)s["pr"]!;
            sp["number"] = prNumber; sp["branch"] = br; sp["url"] = J.S(pr, "html_url"); if (J.Has(sp, "rerun")) sp.Remove("rerun");
            o.Changed = true;
        }
        if (J.Has(s, "awaiting_checks") && J.I(s["awaiting_checks"], "pr") != prNumber)
        {
            var ac = (JsonObject)s["awaiting_checks"]!;
            ac["pr"] = prNumber; ac["sha"] = J.S(pr["head"], "sha"); ac["since"] = J.Iso(kit.UtcNow); o.Changed = true;
        }
        if (J.I(s["copilot_task"], "pr") != prNumber) { s["copilot_task"]!["pr"] = prNumber; o.Changed = true; }
        // a recorded owner pull request (a successor adopted here included) or one retargeted to the default branch names the item in its title
        if (rec > 0 || o.Retargeted) { pr = (await SetItemTitle(kit, pr, n, "", true, ct))!; o.Pr = pr; }
        // a recorded owner pull request (a successor adopted after Development included) is never left a draft
        if (rec > 0 && J.B(pr, "draft")) { pr = (await SetReady(kit, pr, ct))!; o.Pr = pr; }
        if (o.Changed)
        {
            await kit.SaveState(n, st, ct);
            if (rec > 0 && rec != J.I(pr, "number"))
                await kit.AddComment(n, $"The item's pull request is now #{J.I(pr, "number")}, the open pull request of the owner task's branch `{br}` (the recorded #{rec} is closed or superseded).", ct);
        }
        return o;
    }
}
