// W9.0 stub, filled by W9.8 (#360): the board and the history (sync-board and ingest-event into the reconciler and the ingress, journal into history and projection) and the transcripts, which move to the database
// (factory.Transcripts, script 013). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged.
static class BoardRoutes
{
    public const string Group = "board";

    public static readonly IReadOnlyList<string> Verbs = ["sync-board", "ingest-event", "journal", "transcript-save", "transcript-load", "transcript-prune"];

    /// <summary>W9.8 registers each verb here with <c>routes.Add(Verbs, "journal", handler)</c>. Nothing is registered yet: every board verb still runs on v1.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services) { }
}
