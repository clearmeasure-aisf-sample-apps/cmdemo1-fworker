// W9.8 (#360): the board and the history: ingest-event (the webhook's translation, into the ingress), journal (the ledger, recorded in the item history), the transcripts (in the database, factory.Transcripts, script 013)
// and sync-board (the sweep stays v1's until W9.11; one pass of the board reconciler rides along). The verbs are answered by BoardVerbs (gateway/Verbs/BoardVerbs.cs) from the parts BoardWiring registers.
// Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged. Without the parts (no home repository in the instance file) nothing is registered and v1 answers.
static class BoardRoutes
{
    public const string Group = "board";

    public static readonly IReadOnlyList<string> Verbs = ["sync-board", "ingest-event", "journal", "transcript-save", "transcript-load", "transcript-prune"];

    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        if (services.GetService(typeof(BoardVerbParts)) is not BoardVerbParts parts) return;
        var verbs = new BoardVerbs(parts);
        routes.Add(Verbs, "sync-board", verbs.SyncBoard);
        routes.Add(Verbs, "ingest-event", verbs.IngestEvent);
        routes.Add(Verbs, "journal", verbs.Journal);
        routes.Add(Verbs, "transcript-save", verbs.TranscriptSave);
        routes.Add(Verbs, "transcript-load", verbs.TranscriptLoad);
        routes.Add(Verbs, "transcript-prune", verbs.TranscriptPrune);
        // the gateway's own calls of the verbs that no longer need a rule script (the journal lines of the ticker and the back-merge, the webhook's translation) take the same route as a verb call does
        if (services.GetService(typeof(Runner)) is Runner runner)
            runner.V2Own = async (verb, stdin, timeout) =>
            {
                if (!BoardWiring.OwnCalls.Contains(verb) || !routes.TryGet(verb, out var handler)) return null;
                var answer = await handler(stdin, timeout);
                return (answer.Code, answer.Output);
            };
    }
}
