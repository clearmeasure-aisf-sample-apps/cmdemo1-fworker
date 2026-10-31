using System.Text.Json.Nodes;

namespace Fworker.Workflows.Adoption;

/// <summary>
/// W9.9b: what one run of the adoption did, for the v2 block of /api/v1/status ("adoption"). Counts and codes only (no text a person wrote): the reasons an item was skipped are the mapping's codes without their detail (a column's
/// name is not in them), the errors are the types of the exceptions.
/// </summary>
public sealed class AdoptionReport
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>"running", "done", "partial" (some item or part failed; the run is tried again) or "aborted" (the lease was lost).</summary>
    public string Outcome { get; set; } = "running";
    /// <summary>W9.10n: v1 may have acted since the engine last did, and why.</summary>
    public bool AfterBreak { get; init; }
    public string BreakWhy { get; init; } = "";

    /// <summary>The open cards of the home repository on the board, and the items the run read (a card in a column of no step is not read).</summary>
    public int Cards { get; set; }
    /// <summary>The pass over the items finished (the board and the saga's rows were read and every item was tried; an item whose read failed is an error, not the end of the pass). Not in the status JSON.</summary>
    public bool ItemsCompleted { get; set; }
    /// <summary>The items whose adoption failed in this run (a read, a conflict, a write): after a break these are the items that are not fenced. Not in the status JSON (the count of errors is).</summary>
    public SortedSet<int> FailedItems { get; } = [];
    public int Read { get; set; }

    public int Adopted { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Kept { get; set; }
    public int Closed { get; set; }
    public int Reopened { get; set; }
    public int Skipped { get; set; }
    public SortedDictionary<string, int> SkippedBy { get; } = new(StringComparer.Ordinal);

    /// <summary>The rows of the saga the run wrote because the state comment said something the row did not: adopted, updated, reopened and closed.</summary>
    public int Upserted => Adopted + Updated + Reopened + Closed;

    public int Conflicts { get; set; }
    public int PublishFailed { get; set; }
    public int Errors { get; set; }
    public SortedDictionary<string, int> ErrorKinds { get; } = new(StringComparer.Ordinal);

    public int QcRows { get; set; }
    public int MergedRows { get; set; }
    public int MergedPublished { get; set; }
    public int MergedFromPulls { get; set; }
    public int DeliveredToWritten { get; set; }
    public int SignOffs { get; set; }
    public int CandidatesRemoved { get; set; }
    public int MergeLineRemoved { get; set; }

    public void Error(Exception e)
    {
        Errors++;
        var kind = e.GetType().Name;
        lock (Gate) ErrorKinds[kind] = ErrorKinds.GetValueOrDefault(kind) + 1;
    }

    /// <summary>The two dictionaries are written by the run while the status reads the report (<see cref="ToJson"/>): both sides hold this.</summary>
    public object Gate { get; } = new();

    /// <summary>The mapping's reason without its detail: "column-of-no-step: Todo" is "column-of-no-step".</summary>
    public static string Code(string reason)
    {
        var i = reason.IndexOf(':');
        return i < 0 ? reason : reason[..i];
    }

    public JsonObject ToJson()
    {
        KeyValuePair<string, int>[] skippedBy, errorKinds;
        lock (Gate) { skippedBy = SkippedBy.ToArray(); errorKinds = ErrorKinds.ToArray(); }
        return Json(skippedBy, errorKinds);
    }

    JsonObject Json(KeyValuePair<string, int>[] skippedBy, KeyValuePair<string, int>[] errorKinds) => new()
    {
        ["outcome"] = Outcome, ["startedAt"] = StartedAt.ToString("o"), ["finishedAt"] = FinishedAt?.ToString("o"),
        ["afterBreak"] = AfterBreak, ["breakWhy"] = BreakWhy,
        ["cards"] = Cards, ["read"] = Read,
        ["adopted"] = Adopted, ["upserted"] = Upserted, ["updated"] = Updated, ["unchanged"] = Unchanged, ["kept"] = Kept, ["closed"] = Closed, ["reopened"] = Reopened,
        ["skipped"] = Skipped, ["skippedBy"] = new JsonObject(skippedBy.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["conflicts"] = Conflicts, ["publishFailed"] = PublishFailed, ["errors"] = Errors, ["errorKinds"] = new JsonObject(errorKinds.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["qcRows"] = QcRows, ["mergedRows"] = MergedRows, ["mergedPublished"] = MergedPublished, ["mergedFromPulls"] = MergedFromPulls,
        ["deliveredToWritten"] = DeliveredToWritten, ["signOffs"] = SignOffs, ["candidatesRemoved"] = CandidatesRemoved, ["mergeLineRemoved"] = MergeLineRemoved,
    };
}
