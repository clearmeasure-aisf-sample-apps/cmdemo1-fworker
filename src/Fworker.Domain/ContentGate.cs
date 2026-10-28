using System.Text.RegularExpressions;

namespace Fworker.Domain;

// The content gate of the automatic promotion (D3), ported from plugin/scripts/lib/Verbs.Promote.ps1 (Get-ReleaseContentRead,
// Get-ItemPromotionGapsRead, Find-CommitItemUncached's parsing) and Verbs.Delivery.ps1 (Get-ReleaseCommit) as pure values: no GitHub, no Octopus, no
// clock, no state store. v1 keeps calling its own functions; the parity tests (tests/Fworker.Domain.Tests/ReleaseChoiceParityTests.cs) pin this port
// against what they answer. The gap texts are v1's, character for character, because they reach the journal and the Sign-off reasons.

/// <summary>One master commit of a compare range: its sha, its parents' shas (first parent first) and its message (null: not carried).</summary>
public sealed record RangeCommit(string Sha, IReadOnlyList<string> ParentShas, string? Message);

/// <summary>
/// The answer of GitHub's compare. <see cref="Status"/> is its status text, <see cref="Commits"/> the commits it listed and
/// <see cref="TotalCommits"/> its total_commits (null: not given, then the listed count).
/// </summary>
public sealed record CompareRange(string? Status, IReadOnlyList<RangeCommit> Commits, int? TotalCommits);

/// <summary>What Find-CommitItem answered for a commit that is no queued item's: the item (0: none) and a waiting reason ('' when settled).</summary>
public readonly record struct CommitItemLookup(int Item, string Gap);

/// <summary>@{ skip; gaps; hard; items; deferred } of Get-ReleaseContentRead. Hard gaps are the failures a Sign-off aborts on; the rest only wait.</summary>
public sealed record ContentJudgment(bool Skip, IReadOnlyList<string> Gaps, IReadOnlyList<string> Hard, IReadOnlyList<int> Items, bool Deferred = false)
{
    public static ContentJudgment Waiting(string gap) => new(false, new[] { gap }, Array.Empty<string>(), Array.Empty<int>());
}

public static class ContentGate
{
    static readonly Regex Sha40 = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    static readonly Regex Sha40Anywhere = new(@"\b[0-9a-fA-F]{40}\b", RegexOptions.CultureInvariant);

    static string Short(string? sha) => sha is null ? "" : sha.Length <= 7 ? sha : sha[..7];

    /// <summary>
    /// Get-ReleaseCommit: the first build-information VcsCommitNumber that is 40 hex characters, else the first 40-hex word of the release notes,
    /// lower case; null when there is none.
    /// </summary>
    public static string? ReleaseCommit(string? releaseNotes, IEnumerable<string?>? buildInformationCommits)
    {
        if (buildInformationCommits is not null)
            foreach (var c in buildInformationCommits)
                if (c is not null && Sha40.IsMatch(c)) return c.ToLowerInvariant();
        var m = Sha40Anywhere.Match(releaseNotes ?? "");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>The pull request a squash message names on its first line: "... (#P)" at its end (Find-CommitItemUncached); 0 when it names none.</summary>
    public static int SquashPullRequest(string? message)
    {
        var first = (message ?? "").Split('\n')[0];
        var m = Regex.Match(first, @"\(#(\d+)\)\s*$", RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>
    /// The candidate items of a merged pull request, in v1's order and without repeats: the number after a slash in the branch
    /// (name/N-...), each "#N" of the title, each "Closes/Fixes/Resolves #N" of the body; never the pull request itself.
    /// </summary>
    public static IReadOnlyList<int> CandidateItems(int pullRequest, string? headRef, string? title, string? body)
    {
        var c = new List<int>();
        var bm = Regex.Match(headRef ?? "", @"/(\d+)-", RegexOptions.CultureInvariant);
        if (bm.Success) c.Add(int.Parse(bm.Groups[1].Value));
        foreach (Match x in Regex.Matches(title ?? "", @"(?<![0-9])#(\d+)(?![0-9])", RegexOptions.CultureInvariant)) c.Add(int.Parse(x.Groups[1].Value));
        foreach (Match x in Regex.Matches(body ?? "", @"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            c.Add(int.Parse(x.Groups[1].Value));
        return c.Where(n => n > 0 && n != pullRequest).Distinct().ToList();
    }

    /// <summary>
    /// Get-ReleaseContentRead after the commit running in the target is known. <paramref name="releaseCommit"/> is the release's commit (null or
    /// empty: it names none), <paramref name="deployedCommit"/> and <paramref name="deployedVersion"/> what runs in the target (null commit: unknown),
    /// <paramref name="compare"/> GitHub's compare of deployed...release (null: GitHub cannot compare them, a 404 or 422), <paramref name="queuedBySha"/>
    /// the merge sha (lower case) of every Release Queue item, <paramref name="lookup"/> v1's Find-CommitItem for a commit that is none of them, and
    /// <paramref name="isOver"/> the sweep's budget (true: stop and say deferred).
    /// </summary>
    public static ContentJudgment Judge(string? releaseCommit, string targetName, string? deployedCommit, string? deployedVersion, CompareRange? compare,
        IReadOnlyDictionary<string, int> queuedBySha, Func<RangeCommit, CommitItemLookup> lookup, Func<bool>? isOver = null)
    {
        if (string.IsNullOrEmpty(releaseCommit)) return ContentJudgment.Waiting("the release names no commit (its build information or notes)");
        if (string.IsNullOrEmpty(deployedCommit))
            return ContentJudgment.Waiting($"the commit running in {targetName} is not known (no successful deployment of a release with a commit)");
        var skip = new ContentJudgment(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<int>());
        if (string.Equals(deployedCommit, releaseCommit, StringComparison.OrdinalIgnoreCase)) return skip;
        if (compare is null)
            return ContentJudgment.Waiting($"GitHub cannot compare the commit running in {targetName} ({Short(deployedCommit)}, release {deployedVersion}) with the release's");
        if (compare.Status is "behind" or "identical") return skip;
        var cs = compare.Commits;
        var total = compare.TotalCommits ?? cs.Count;
        if (compare.Status != "ahead" || cs.Count == 0 || total > cs.Count)
            return ContentJudgment.Waiting($"the commits between {targetName} ({Short(deployedCommit)}) and the release cannot all be read (compare: {compare.Status}, {cs.Count} of {total})");
        // first parent: the commits of master itself, walked back from the release's commit
        var by = new Dictionary<string, RangeCommit>(StringComparer.Ordinal);
        foreach (var x in cs) by[x.Sha.ToLowerInvariant()] = x;
        var chain = new List<string>();
        var cur = releaseCommit.ToLowerInvariant();
        while (by.TryGetValue(cur, out var node))
        {
            chain.Add(cur);
            cur = node.ParentShas.Count > 0 && !string.IsNullOrEmpty(node.ParentShas[0]) ? node.ParentShas[0].ToLowerInvariant() : "";
        }
        if (cur != deployedCommit.ToLowerInvariant())
            return ContentJudgment.Waiting($"the first-parent history from the release back to {targetName} ({Short(deployedCommit)}) cannot be followed");
        var gaps = new List<string>(); var hard = new List<string>(); var items = new List<int>();
        foreach (var sha in chain)
        {
            if (isOver?.Invoke() == true) return new(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<int>(), true);
            if (queuedBySha.TryGetValue(sha, out var q)) { items.Add(q); continue; }
            var f = lookup(by[sha]);
            if (f.Item > 0) { items.Add(f.Item); if (!string.IsNullOrEmpty(f.Gap)) gaps.Add(f.Gap); }
            else
            {
                var g = $"commit {Short(sha)} in the release is not the merge commit of a factory work item";
                gaps.Add(g); hard.Add(g);
            }
        }
        return new(false, gaps, hard, items.Distinct().OrderBy(i => i).ToList());
    }
}

/// <summary>The facts about one item that its promotion gates read (labels, the item's state comment, the demo video).</summary>
public sealed record ItemGateFacts(
    int Item, IReadOnlyList<string> Labels, bool VerdictPassed, int? VerdictPr, int? MergePr, string? MergeBy, bool DemoVideoPublished);

/// <summary>The labels the item gates are configured with (autoMergeLabel, videoLabel, holdLabels; holdLabels default is hold:owner).</summary>
public sealed record ItemGateConfig(string AutoMergeLabel, string VideoLabel, IReadOnlyList<string>? HoldLabels = null);

public static class ItemGates
{
    static bool NameIn(string name, IEnumerable<string> list) => list.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Get-HoldLabel: the first configured hold label the item carries, as configured; '' when none.</summary>
    public static string HoldLabel(IReadOnlyList<string> labels, IReadOnlyList<string>? holdLabels)
    {
        foreach (var h in holdLabels ?? new[] { "hold:owner" })
            if (!string.IsNullOrEmpty(h) && NameIn(h, labels)) return h;
        return "";
    }

    /// <summary>Get-ItemPromotionGapsRead: why the item does not qualify for promotion; empty when it does.</summary>
    public static IReadOnlyList<string> Gaps(ItemGateFacts f, ItemGateConfig c)
    {
        var n = f.Item; var gaps = new List<string>();
        if (!NameIn(c.AutoMergeLabel, f.Labels)) gaps.Add($"#{n} has no '{c.AutoMergeLabel}' label");
        var hold = NameIn("Hold", f.Labels) ? "Hold" : HoldLabel(f.Labels, c.HoldLabels);
        if (hold != "") gaps.Add($"#{n} carries the '{hold}' label");
        var passMerged = f.VerdictPassed && f.VerdictPr is not null && f.MergePr is > 0 && f.MergePr == f.VerdictPr && !string.Equals(f.MergeBy, "person", StringComparison.OrdinalIgnoreCase);
        if (!passMerged) gaps.Add($"#{n} has no pull request merged after a Functional Testing PASS verdict");
        if (NameIn(c.VideoLabel, f.Labels) && !f.DemoVideoPublished)
            gaps.Add($"#{n} has the '{c.VideoLabel}' label and no demo video published (release demo-{n}, asset demo-{n}.mp4)");
        return gaps;
    }
}
