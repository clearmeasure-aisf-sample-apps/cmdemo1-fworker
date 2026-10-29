using System.Text.RegularExpressions;

namespace Fworker.Domain;

// The docs-only test (D4): which changes are documentation only. Ports four v1 rules, pure, over plain records (no IO, no config loading, no HTTP):
//   Test-DocsOnlyFiles   (plugin/scripts/lib/Prs.ps1)            -> DocsOnly.FilesAreDocumentation
//   Test-ItemDocsOnly    (plugin/scripts/lib/Prs.ps1)            -> DocsOnly.ItemIsDocsOnly
//   Get-BuildOwed        (plugin/scripts/lib/Prs.ps1)            -> DocsOnly.BuildOwed
//   Get-MergeDocsOnly    (plugin/scripts/lib/Verbs.Delivery.ps1) -> DocsOnly.MergeIsDocsOnly
// v1 keeps its own functions until the cut-over; DocsOnlyParityTests pins this port against what they decide. PowerShell semantics that the port
// keeps on purpose: -match, -ieq and -ne on strings ignore case, and a hashtable lookup of a check name or a column ignores case too, so every
// comparison here is case-insensitive except the body marker (String.Contains, ordinal).

/// <summary>
/// The configuration the rules read, as plain values. The caller (the gateway adapter, later) fills it from config/instance.json:
/// <paramref name="Paths"/> = docsOnlyPaths (regexes), <paramref name="Marker"/> = docsOnlyMarker, <paramref name="LegacyBefore"/> =
/// timing.docsOnlyLegacyBefore (null: no cutoff), <paramref name="DesignColumns"/> = the keys of requiredBodySection, <paramref name="VideoLabel"/> =
/// videoLabel, <paramref name="MasterCodeChecks"/> = masterCodeChecks.
/// </summary>
public sealed record DocsOnlySettings(
    IReadOnlyList<string> Paths,
    string Marker,
    DateTimeOffset? LegacyBefore,
    IReadOnlyList<string> DesignColumns,
    string VideoLabel,
    IReadOnlyList<string> MasterCodeChecks);

/// <summary>The parts of an item that the rules read (v1's $Info): its column, body, creation time (null: unknown or unparsable) and labels.</summary>
public sealed record DocsOnlyItem(string? Column, string? Body, DateTimeOffset? CreatedAt, IReadOnlyList<string> Labels);

/// <summary>What Get-BuildOwed answers. <see cref="NotDocumentation"/> is set only for a marked item whose files are not all documentation (null otherwise, as v1's absent key).</summary>
public sealed record BuildOwedDecision(bool BuildOwed, bool DocsOnlyFiles, string Verification, string BuildReason, IReadOnlyList<string>? NotDocumentation = null);

/// <summary>A check run of a commit: its status ('completed', ...) and conclusion ('skipped', 'success', ...; null while it has none).</summary>
public sealed record DocsCheckRun(string? Status, string? Conclusion);

/// <summary>What Get-MergeDocsOnly answers: whether the merge is docs-only and from what ('build' or 'pr-files').</summary>
public sealed record MergeDocsOnlyVerdict(bool DocsOnly, string From);

public static class DocsOnly
{
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;   // PowerShell's -match

    /// <summary>
    /// Test-DocsOnlyFiles: every changed file matches one of docsOnlyPaths. An empty list is false (a pull request with no files proves nothing).
    /// </summary>
    public static bool FilesAreDocumentation(IReadOnlyList<string> files, IReadOnlyList<string> paths)
    {
        if (files.Count == 0) return false;
        foreach (var f in files)
            if (!paths.Any(p => Regex.IsMatch(f, p, Options, RegexTimeout))) return false;
        return true;
    }

    /// <summary>
    /// Test-ItemDocsOnly: what the design step recorded (<paramref name="designDocsOnly"/> = state.design.docs_only; null: the state has no field) comes
    /// first and, once it exists, the body text is ignored. Without the field the body marker counts only when the item is known (<paramref name="item"/>
    /// not null), is already past its design column, and was created before timing.docsOnlyLegacyBefore (strictly before: equal is not legacy).
    /// A newer item without the field is not docs-only, whatever its body says.
    /// </summary>
    public static bool ItemIsDocsOnly(bool? designDocsOnly, DocsOnlyItem? item, DocsOnlySettings s)
    {
        if (designDocsOnly is { } decided) return decided;
        if (item is null || s.DesignColumns.Any(c => string.Equals(c, item.Column ?? "", StringComparison.OrdinalIgnoreCase))) return false;
        if (s.LegacyBefore is not { } cut || item.CreatedAt is not { } made || made >= cut) return false;
        return (item.Body ?? "").Contains(s.Marker, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decision of Get-BuildOwed. <paramref name="files"/> are the pull request's changed files, null when they could not be read (the build is owed).
    /// The private build is not owed only when BOTH hold: the design recorded the item as docs-only and every changed file is documentation. Then
    /// Functional Testing reads the diff ('diff') unless the item has the video label ('app'). Everything else owes the build and is verified in the app.
    /// </summary>
    public static BuildOwedDecision BuildOwed(bool? designDocsOnly, DocsOnlyItem? item, IReadOnlyList<string>? files, DocsOnlySettings s)
    {
        var marker = ItemIsDocsOnly(designDocsOnly, item, s);
        if (files is null)
            return new(true, false, "app", "the files of the pull request could not be read; the private build is owed");
        var docs = FilesAreDocumentation(files, s.Paths);
        var other = files.Where(f => !FilesAreDocumentation(new[] { f }, s.Paths)).Take(5).ToList();
        if (marker && docs)
        {
            var video = item is not null && item.Labels.Any(l => string.Equals(l, s.VideoLabel, StringComparison.OrdinalIgnoreCase));
            return new(false, true, video ? "app" : "diff",
                "every changed file is documentation and the technical design recorded the item as docs-only: no private build and no SDK are owed; the checks of the pull request are the gate");
        }
        if (marker)
            return new(true, false, "app",
                $"the technical design recorded the item as docs-only but the pull request changes files that are not documentation ({string.Join(", ", other)}): the private build is owed before complete",
                other);
        return new(true, docs, "app", "the technical design did not record the item as docs-only: the private build is owed");
    }

    /// <summary>
    /// The rule of Get-MergeDocsOnly: the merge commit's own Build first. The checks named in masterCodeChecks run only for a code change, so when at
    /// least one of them has completed and all of those that completed were skipped, no code changed (from 'build'); one that ran for real says the
    /// merge is not docs-only. Without any completed code check, decide from the pull request's files (from 'pr-files'; an empty list is not docs-only).
    /// Null when neither can be read: <paramref name="buildRuns"/> has none of the code checks completed and <paramref name="prFiles"/> is null (no
    /// pull request recorded, or its files could not be read). Never the release check, which belongs to another commit's workflow.
    /// </summary>
    public static MergeDocsOnlyVerdict? MergeIsDocsOnly(IReadOnlyDictionary<string, DocsCheckRun> buildRuns, IReadOnlyList<string>? prFiles, DocsOnlySettings s)
    {
        var seen = new List<DocsCheckRun>();
        foreach (var name in s.MasterCodeChecks)
            foreach (var (key, run) in buildRuns)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    seen.Add(run);
        if (seen.Count > 0)
            return new(seen.All(r => string.Equals(r.Conclusion ?? "", "skipped", StringComparison.OrdinalIgnoreCase)), "build");
        if (prFiles is not null) return new(FilesAreDocumentation(prFiles, s.Paths), "pr-files");
        return null;
    }
}
