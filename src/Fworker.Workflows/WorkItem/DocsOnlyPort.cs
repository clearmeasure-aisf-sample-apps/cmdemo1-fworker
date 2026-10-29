using Fworker.Domain;

namespace Fworker.Workflows.WorkItem;

// W6.1: the saga's view of the docs-only test. D4 (Fworker.Domain.DocsOnly) ports v1's four rules as pure functions over plain records; this is the small interface the saga needs of them, so a test can say what
// the files and the merge are without a path list, and the adapter below is the one place that knows D4's signatures.

public interface IDocsOnlyPort
{
    /// <summary>Whether the item is docs-only, from what its design recorded (<paramref name="designMarker"/> is whether the latest design section carries the marker). D4: <c>ItemIsDocsOnly(designMarker, ...)</c>.</summary>
    bool ItemIsDocsOnly(bool designMarker);

    /// <summary>Whether every changed file is documentation (an empty list is false), or null when this port cannot tell. D4: <c>FilesAreDocumentation(files, docsOnlyPaths)</c>, never null.</summary>
    bool? FilesAreDocumentation(IReadOnlyList<string> files);

    /// <summary>Whether the merge was docs-only and from what, or null when neither the build nor the files say. D4: <c>MergeIsDocsOnly(buildRuns, prFiles, settings)</c>.</summary>
    MergeDocsOnlyVerdict? MergeIsDocsOnly(IReadOnlyList<BuildCheck> checks, IReadOnlyList<string>? pullRequestFiles);
}

/// <summary>
/// D4 behind the port. <paramref name="settings"/> are what the gateway reads from config/instance.json (docsOnlyPaths, docsOnlyMarker, timing.docsOnlyLegacyBefore, requiredBodySection, videoLabel, masterCodeChecks).
/// The saga asks only what the design recorded, never the body text of the item: the design's marker decides, as Test-ItemDocsOnly does for every item created since docsOnlyLegacyBefore.
/// </summary>
public sealed class DomainDocsOnlyPort(DocsOnlySettings settings) : IDocsOnlyPort
{
    public bool ItemIsDocsOnly(bool designMarker) => DocsOnly.ItemIsDocsOnly(designMarker, null, settings);

    public bool? FilesAreDocumentation(IReadOnlyList<string> files) => DocsOnly.FilesAreDocumentation(files, settings.Paths);

    public MergeDocsOnlyVerdict? MergeIsDocsOnly(IReadOnlyList<BuildCheck> checks, IReadOnlyList<string>? pullRequestFiles)
    {
        var runs = new Dictionary<string, DocsCheckRun>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in checks) runs[c.Name] = new DocsCheckRun(c.Status, c.Conclusion);
        return DocsOnly.MergeIsDocsOnly(runs, pullRequestFiles, settings);
    }
}
