using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Merge;

// W9.3 (#355): `base-watch` and `base-update-branch` (plugin/scripts/lib/BaseUpdate.ps1) as C#, for the items the WorkItemSaga owns. The answers are the script's, field by field: base-watch gives the head of the default
// branch, whether it moved since the head the gateway knew, how many files master changed, and one row per item (pull request, branch, head, how far behind, the files both changed, why the item is skipped, and
// whether the pull request conflicts); base-update-branch gives an outcome (not-open, head-moved, skip, conflict, unknown, current, refused, updated). The reads of GitHub are the port's.

/// <summary>The reads and the one write of GitHub that the back-merge verbs make. A fault (not a 404 or 422 the script allows) throws; the verb then answers the script's internal error.</summary>
public interface IBaseUpdateGitHub
{
    /// <summary><c>Get-BaseHead</c>: the sha of the default branch.</summary>
    Task<string> BaseHead(CancellationToken ct);
    /// <summary><c>compare/{from}...{to}</c>: the file names the range changed (the first 300), or null when GitHub has no such range (404, 422).</summary>
    Task<IReadOnlyList<string>?> ChangedFiles(string from, string to, CancellationToken ct);
    /// <summary><c>pulls?state=open&amp;per_page=100</c>: the first hundred open pull requests.</summary>
    Task<IReadOnlyList<BasePull>> OpenPullRequests(CancellationToken ct);
    Task<BasePull> PullRequest(int number, CancellationToken ct);
    /// <summary><c>Get-PrFiles</c>: every file name the pull request changes (pages of a hundred).</summary>
    Task<IReadOnlyList<string>> PullRequestFiles(int number, CancellationToken ct);
    /// <summary><c>Get-BehindBy</c>: the commits the default branch has that the branch lacks; -1 when GitHub cannot tell (404, 422).</summary>
    Task<int> BehindBy(string branch, CancellationToken ct);
    /// <summary>GitHub's update branch with the expected head; the status GitHub answered (202 accepted; 409 and 422 are the script's refusals; anything else throws).</summary>
    Task<int> UpdateBranch(int pullRequest, string expectedHead, CancellationToken ct);
}

/// <summary>The saga's facts for an item, and the QC record of one of its pull requests (a QC run for a head makes the head stale to move).</summary>
public interface IBaseUpdateItems
{
    /// <summary>The facts of an item the saga owns, or null when it does not (the caller sends the item to the script).</summary>
    Task<BaseItemFacts?> FactsOf(int item, CancellationToken ct);
    /// <summary>The JSON of the QC record of the pull request, or null when there is none.</summary>
    Task<string?> QcRecord(int pullRequest, CancellationToken ct);
}

/// <summary>What the verbs read of the configuration: the name of the instance (its branches are <c>{name}/{item}-...</c>), and the three waits of the script.</summary>
public sealed record BackmergeSettings(string InstanceName, TimeSpan? MergeabilityPause = null, int MergeabilityLooks = 5, TimeSpan? ConflictPause = null)
{
    public TimeSpan Pause => MergeabilityPause ?? TimeSpan.FromSeconds(2);
    public TimeSpan ConflictRetryPause => ConflictPause ?? TimeSpan.FromSeconds(3);
}

public sealed class BaseUpdateService(IBaseUpdateGitHub github, IBaseUpdateItems items, BackmergeSettings settings, Func<TimeSpan, Task>? delay = null)
{
    readonly Func<TimeSpan, Task> pause = delay ?? Task.Delay;

    /// <summary>The numbers of <c>items</c> of a base-watch body (at most 50, as the script reads them).</summary>
    public static List<int> ItemsOf(JsonObject body) =>
        body["items"] is JsonArray a ? a.Select(x => x is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : 0).Take(50).ToList() : [];

    /// <summary>
    /// <c>Verb_base_watch</c>. <paramref name="only"/> names the items this call answers for (the others are the script's); every other field of the body is read as the script reads it. The row of an item is
    /// <c>skip</c> 'error' with the script's code when the saga has no such item after all.
    /// </summary>
    public async Task<VerbAnswer> Watch(JsonObject body, ISet<int> only, CancellationToken ct)
    {
        var known = body["known"]?.ToString() ?? "";
        var merged = new HashSet<int>();
        if (body["mergedPrs"] is JsonArray mp) foreach (var x in mp) if (int.TryParse(x?.ToString(), out var m)) merged.Add(m);
        var head = await github.BaseHead(ct);
        var changed = known != "" && head != known;
        // the files master changed since the last head the gateway saw (bounded: 300 files)
        IReadOnlyList<string> baseFiles = [];
        if (changed) baseFiles = await github.ChangedFiles(known, head, ct) ?? [];
        var rows = new JsonArray();
        // the conflict modes (issue 103): whether each open pull request conflicts with master
        var conflicts = body["conflicts"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb;
        var knownConflicts = body["conflictKnown"] as JsonObject;
        var wanted = ItemsOf(body).Where(only.Contains).ToList();
        if (wanted.Count > 0)
        {
            var open = await github.OpenPullRequests(ct);
            foreach (var n in wanted) rows.Add(await Row(n, open, merged, head, changed, baseFiles, conflicts, knownConflicts, ct));
        }
        return MergeVerbReplies.Ok("base-watch", new JsonObject { ["head"] = head, ["known"] = known, ["changed"] = changed, ["baseFiles"] = baseFiles.Count, ["rows"] = rows });
    }

    async Task<JsonObject> Row(int n, IReadOnlyList<BasePull> open, HashSet<int> merged, string head, bool changed, IReadOnlyList<string> baseFiles, bool conflicts, JsonObject? knownConflicts, CancellationToken ct)
    {
        var row = new JsonObject { ["item"] = n, ["pr"] = 0, ["branch"] = "", ["head"] = "", ["behind"] = 0, ["overlap"] = new JsonArray(), ["skip"] = "" };
        string Skip() => row["skip"]!.ToString();
        var facts = await items.FactsOf(n, ct);
        if (facts is null) { row["skip"] = "error"; row["code"] = "item-not-found"; return row; }
        // a PASS waiting for its merge, or a merge being settled: the gateway owns the next step, not a back-merge
        if (facts.PassUnmerged) row["skip"] = "pass-unmerged";
        else if (facts.Merged) row["skip"] = "merged";
        var pr = BaseUpdateRules.PullRequestOf(open, settings.InstanceName, n, facts.PullRequest);
        var prNumber = 0; var prHead = "";
        if (pr is not null)
        {
            prNumber = pr.Number; prHead = pr.HeadSha;
            row["pr"] = prNumber; row["branch"] = pr.HeadRef; row["head"] = prHead;
            if (merged.Contains(prNumber) && Skip() == "") row["skip"] = "merged";
        }
        else if (facts.PullRequest > 0) { if (Skip() == "") row["skip"] = "pr-closed"; }
        else if (facts.Branch.Length > 0) row["branch"] = facts.Branch;
        else if (Skip() == "") row["skip"] = "no-branch";
        if (Skip() == "") row["skip"] = BaseUpdateRules.Hold(facts, prNumber, prHead, prNumber > 0 ? await items.QcRecord(prNumber, ct) : null);
        if (Skip() == "" && conflicts && prNumber > 0)
        {
            if (knownConflicts?[n.ToString()] is JsonObject kc && kc["prHead"]?.ToString() == prHead && kc["head"]?.ToString() == head && kc["conflict"] is JsonValue kv && kv.TryGetValue<bool>(out var known))
                row["conflict"] = known;
            else
            {
                // GitHub answers null until it has computed it (the first read starts that): one more read 3 s later, then the next look
                var p1 = await github.PullRequest(prNumber, ct);
                if (p1.Mergeable is null) { await pause(settings.ConflictRetryPause); p1 = await github.PullRequest(prNumber, ct); }
                row["conflict"] = BaseUpdateRules.Conflict(p1);
            }
        }
        if (Skip() == "")
        {
            var branch = row["branch"]!.ToString();
            row["behind"] = await github.BehindBy(branch, ct);
            if (changed && prNumber > 0 && baseFiles.Count > 0)
            {
                var mine = (await github.PullRequestFiles(prNumber, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var overlap = new JsonArray();
                foreach (var f in baseFiles.Where(mine.Contains).Take(20)) overlap.Add(f);
                row["overlap"] = overlap;
            }
        }
        return row;
    }

    /// <summary>
    /// <c>Verb_base_update_branch</c> for an item the saga owns: the outcome is read fresh at the time of the call, because a verdict, a merge or a QC run may have started since the trigger. Null when the saga does not
    /// know the item (the script's).
    /// </summary>
    public async Task<VerbAnswer?> UpdateBranch(JsonObject body, CancellationToken ct)
    {
        if (!MergeVerbReplies.ItemNumber("base-update-branch", body, out var n, out var refusal)) return refusal;
        if (!MergeVerbReplies.Need("base-update-branch", body, "pr", out var prText, out refusal)) return refusal;
        if (!int.TryParse(prText, out var num)) return MergeVerbReplies.InternalError("base-update-branch", $"Cannot convert value \"{prText}\" to type \"System.Int32\".");
        if (!MergeVerbReplies.Need("base-update-branch", body, "expected", out var expected, out refusal)) return refusal;
        var facts = await items.FactsOf(n, ct);
        if (facts is null) return null;
        VerbAnswer Say(string outcome, JsonObject? more = null)
        {
            var o = new JsonObject { ["item"] = n, ["pr"] = num, ["outcome"] = outcome };
            if (more is not null) foreach (var (k, v) in more) o[k] = v?.DeepClone();
            return MergeVerbReplies.Ok("base-update-branch", o);
        }
        var pr = await github.PullRequest(num, ct);
        if (pr.State != "open") return Say("not-open");
        if (!string.Equals(pr.HeadSha, expected, StringComparison.Ordinal)) return Say("head-moved");
        // read fresh: a verdict, a merge or a QC run may have started since the trigger
        var hold = BaseUpdateRules.Hold(facts, num, expected, await items.QcRecord(num, ct));
        if (hold != "") return Say("skip", new JsonObject { ["reason"] = hold });
        // GitHub computes mergeability after a push to either side: null until then
        for (var i = 0; i < settings.MergeabilityLooks && pr.Mergeable is null; i++) { await pause(settings.Pause); pr = await github.PullRequest(num, ct); }
        if (pr.Mergeable == false || pr.MergeableState == "dirty") return Say("conflict");
        if (pr.Mergeable is null) return Say("unknown");
        var behind = await github.BehindBy(pr.HeadRef, ct);
        if (behind == 0) return Say("current");
        var status = await github.UpdateBranch(num, expected, ct);
        if (status is 409 or 422) return Say("refused", new JsonObject { ["status"] = status });
        return Say("updated", new JsonObject { ["behind"] = behind });
    }
}
