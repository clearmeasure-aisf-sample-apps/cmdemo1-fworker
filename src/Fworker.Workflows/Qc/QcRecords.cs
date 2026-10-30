using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Domain;

namespace Fworker.Workflows.Qc;

/// <summary>
/// The QC record of a pull request and its moves (Qc.ps1: the state names, the failure codes and teardown reasons, QcMoves, Set-QcState, Add-QcHistory, New-QcRecord, ConvertTo-QcText). The record is the
/// JSON object v1 keeps under <c>state.qc["&lt;pr&gt;"]</c>; every change of it raises <c>rev</c> and appends the state to its history (the last 20).
/// </summary>
public static class QcRecords
{
    public static readonly IReadOnlyList<string> LiveStates = ["requested", "reset", "assigned", "building", "running", "ready", "stale"];
    public static readonly IReadOnlyList<string> FailCodes = ["build-failed", "start-failed", "health-timeout", "no-reachable-url", "unreachable-url", "no-dotnet", "port-busy", "no-memory", "profile-incomplete", "trust-refused", "app-down", "no-worker"];
    public static readonly IReadOnlyList<string> TeardownReasons = ["merged", "closed", "head-changed", "idle", "lifetime", "person", "left-validation", "label-changed", "worker-stopped", "drafted"];

    // the states a record may come from, by the state it goes to
    static readonly Dictionary<string, string[]> Moves = new(StringComparer.Ordinal)
    {
        ["assigned"] = ["requested", "reset", "assigned"],
        ["building"] = ["assigned"],
        ["running"] = ["building"],
        ["ready"] = ["building", "running", "ready"],
        ["failed"] = ["requested", "reset", "assigned", "building", "running", "ready", "stale"],
        ["stale"] = ["assigned", "building", "running", "ready"],
        ["torn-down"] = ["requested", "reset", "assigned", "building", "running", "ready", "stale", "failed"],
    };

    const int HistoryKept = 20;

    public static bool IsLive(JsonObject? rec) => rec is not null && LiveStates.Contains(QcJson.Str(rec, "state"));

    /// <summary>Test-QcMove: the record may go from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool CanMove(string from, string to) => Moves.TryGetValue(to, out var f) && f.Contains(from, StringComparer.Ordinal);

    /// <summary>Get-QcRec: the record of a pull request in the item's state, or null.</summary>
    public static JsonObject? Of(JsonObject state, int pr) => QcJson.Obj(QcJson.Obj(state, "qc"), pr.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static void AddHistory(JsonObject rec, string state, DateTime now)
    {
        var h = new JsonArray();
        if (QcJson.Has(rec, "history") && rec["history"] is JsonArray old) foreach (var x in old) if (x is not null) h.Add(x.DeepClone());
        h.Add(new JsonObject { ["state"] = state, ["at"] = QcJson.Iso(now) });
        while (h.Count > HistoryKept) h.RemoveAt(0);
        rec["history"] = h;
    }

    /// <summary>Set-QcState: the new state, its history entry, and one more revision.</summary>
    public static void SetState(JsonObject rec, string to, DateTime now)
    {
        rec["state"] = to; AddHistory(rec, to, now);
        rec["rev"] = 1 + (QcJson.Has(rec, "rev") ? QcJson.Int(rec, "rev") : 0);
    }

    /// <summary>One more revision without a change of state (an accept, a changed probe, a teardown that waits for the worker).</summary>
    public static void BumpRev(JsonObject rec) => rec["rev"] = 1 + QcJson.Int(rec, "rev");

    /// <summary>New-QcRecord: a record for the pull request at its current head, in <paramref name="state"/> (requested or reset).</summary>
    public static JsonObject New(int item, JsonObject pr, int attempt, string state, DateTime now, IReadOnlyList<string> secretPatterns)
    {
        var head = QcJson.Obj(pr, "head");
        var sha = QcJson.Str(head, "sha");
        var number = QcJson.Int(pr, "number");
        var rec = new JsonObject
        {
            ["state"] = state, ["sha"] = sha, ["item"] = item, ["pr"] = number,
            ["author"] = Text(QcJson.Str(QcJson.Obj(pr, "user"), "login"), 64, secretPatterns),
            ["requested_at"] = QcJson.Iso(now), ["attempt"] = attempt,
            ["env_id"] = $"qc-{number}-{sha[..Math.Min(7, sha.Length)]}-{attempt}",
            ["assignment"] = "", ["worker"] = "", ["history"] = new JsonArray(), ["rev"] = 0, ["old_assignments"] = new JsonArray(),
        };
        SetState(rec, state, now);
        return rec;
    }

    /// <summary>The assignments a record used to have, at most the last five (Select-Object -Last 5).</summary>
    public static JsonArray WithOld(JsonObject rec, string? add)
    {
        var all = new List<string>();
        if (rec["old_assignments"] is JsonArray old) foreach (var x in old) if (x is not null) all.Add(QcJson.Text(x));
        if (!string.IsNullOrEmpty(add)) all.Add(add);
        return QcJson.ArrayOf(all.TakeLast(5));
    }

    static readonly Regex Control = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.CultureInvariant);

    /// <summary>
    /// ConvertTo-QcText: every string a worker supplies passes it before it is persisted. A backtick would end the code fence the state is read from, so it becomes an apostrophe; control characters are dropped,
    /// secrets redacted, and the text cut to <paramref name="max"/>.
    /// </summary>
    public static string Text(string? text, int max, IReadOnlyList<string> secretPatterns)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Replace('`', '\'');
        t = Control.Replace(t, "");
        t = JournalText.Protect(t, secretPatterns);
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>ConvertTo-QcJournalText: for a journal comment, where no @mention can fire.</summary>
    public static string JournalLine(string? text, int max, IReadOnlyList<string> secretPatterns) => Text(text, max, secretPatterns).Replace("@", "@​");
}
