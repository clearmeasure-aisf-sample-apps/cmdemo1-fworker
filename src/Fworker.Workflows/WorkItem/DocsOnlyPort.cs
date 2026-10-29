namespace Fworker.Workflows.WorkItem;

// W6.1: the saga's view of the docs-only test. D4 (src/Fworker.Domain/DocsOnly.cs, branch cm-ai-ops-bot/d4-docs-only-domain) ports v1's four rules as pure functions over plain records; this is the small interface the
// saga needs of them, so the saga does not depend on the branch. When D4 is on master the adapter is one class that calls DocsOnly.ItemIsDocsOnly, DocsOnly.FilesAreDocumentation and DocsOnly.MergeIsDocsOnly with
// the DocsOnlySettings the gateway fills from config/instance.json (docsOnlyPaths, docsOnlyMarker, masterCodeChecks) and is registered in place of RecordedDesignDocsOnly (WorkItemWiring); nothing else changes.

/// <summary>What the merge commit's own Build and the pull request's files say about the merge (D4's MergeDocsOnlyVerdict): whether it is docs-only and from what, "build" or "pr-files".</summary>
public sealed record MergeDocsOnlyAnswer(bool DocsOnly, string From);

public interface IDocsOnlyPort
{
    /// <summary>Whether the item is docs-only, from what its design recorded (<paramref name="designMarker"/> is whether the latest design section carries the marker). D4: <c>ItemIsDocsOnly(designMarker, ...)</c>.</summary>
    bool ItemIsDocsOnly(bool designMarker);

    /// <summary>Whether every changed file is documentation (an empty list is false), or null when this port cannot tell. D4: <c>FilesAreDocumentation(files, docsOnlyPaths)</c>, never null.</summary>
    bool? FilesAreDocumentation(IReadOnlyList<string> files);

    /// <summary>Whether the merge was docs-only, or null when neither the build nor the files say. D4: <c>MergeIsDocsOnly(buildRuns, prFiles, settings)</c>.</summary>
    MergeDocsOnlyAnswer? MergeIsDocsOnly(IReadOnlyList<BuildCheck> checks, IReadOnlyList<string>? pullRequestFiles);
}

/// <summary>
/// The port until D4 is on master: the design's recorded marker decides (what v1 does for every item created since docsOnlyLegacyBefore, Test-ItemDocsOnly), and what only D4's path list can judge is not answered:
/// the files are neither documentation nor code (an item stays on the plan it has) and the merge is not known to be docs-only (the item goes on to look for its release).
/// </summary>
public sealed class RecordedDesignDocsOnly : IDocsOnlyPort
{
    public bool ItemIsDocsOnly(bool designMarker) => designMarker;
    public bool? FilesAreDocumentation(IReadOnlyList<string> files) => null;
    public MergeDocsOnlyAnswer? MergeIsDocsOnly(IReadOnlyList<BuildCheck> checks, IReadOnlyList<string>? pullRequestFiles) => null;
}
