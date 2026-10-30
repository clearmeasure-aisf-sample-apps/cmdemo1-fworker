using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Domain;

namespace Fworker.Integrations.Commands;

// W3.2: the journal ledger in C#. A port of Add-Journal, Close-JournalFolds, Add-JournalKind and Verb_journal of plugin/scripts/lib/Journal.ps1: the
// same comment text and marker, the same ledger in the item's state comment (journal.keys, count, kinds, fold, waiting_at, ciReleased), the same folds,
// the same cap, and the same GitHub calls in the same order (the golden writes see no difference). It does not go through the `journal` verb.
// Differences, all in failure cases only: the line is written under a per-item lock of this process instead of the item lock file of the rule script
// (the commands of one item also run one after the other, see JournalCommandWiring); a state conflict reads the state again and repeats the line, and
// the repeat finds a comment that was posted before the conflict by its marker instead of posting it twice; a GitHub failure that is worth a retry is
// thrown, for the command base to classify, where the script logs it and drops the line.

/// <summary>The events the journal verb accepts (Verb_journal).</summary>
public static class JournalEvents
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "matched", "offered", "launched", "assigned", "routed", "reassigned", "tier", "waiting", "skipped", "released", "qc-offered", "qc-requested", "qc-assigned",
        "qc-ready", "qc-failed", "qc-reset", "qc-torn-down", "qc-waiting", "question-escalated", "base-updated", "branch-updated", "keeper-action", "manager-action",
        "pin-fallback", "ci-released", "ci-release-refused",
    };
}

/// <summary>What one journal line did: <see cref="Posted"/> (a comment was written, or an open fold edited), the <see cref="Reason"/> when not, and <see cref="Recorded"/> for a ci-released line.</summary>
public sealed record JournalOutcome(bool Posted, string Reason = "", bool Recorded = false);

/// <summary>The settings of the ledger that the configuration holds: timing.journalWaitingMinutes (30), and the secrets catalogue (patterns, then redactOnly).</summary>
public sealed record JournalSettings(double WaitingMinutes = 30, IReadOnlyList<string>? SecretPatterns = null)
{
    public const int Cap = 30;
    public const int FoldShow = 5;
    public const int MaxLine = 1500;
    public const int KeysKept = 200;
    public const int CiReleasedKept = 20;
}

public sealed class JournalLedger
{
    static readonly string[] FoldKinds = ["matched", "waiting", "reassigned"];
    static readonly string[] FoldNeutral = ["offered", "launched"];
    static readonly string[] ExtraKinds = ["qc-ready", "qc-failed", "question-escalated", "manager-action", "keeper-action", "promoted", "promotion-waiting", "promotion-signoff"];

    readonly IJournalStore store;
    readonly JournalSettings settings;
    readonly Func<DateTime> utcNow;
    readonly Action<string> log;
    readonly ConcurrentDictionary<int, ItemLock> locks = new();

    public JournalLedger(IJournalStore store, JournalSettings? settings = null, Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        this.store = store; this.settings = settings ?? new JournalSettings(); this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.log = log ?? (m => Console.Error.WriteLine(m));
    }

    /// <summary>
    /// Verb_journal: one line of an item. <paramref name="key"/> empty takes the ticks of now (the script's default; a waiting line always does).
    /// Throws for a failure that is worth a retry (the caller classifies it); the answers of the script, such as "rate-limited", are in the outcome.
    /// </summary>
    public async Task<JournalOutcome> Append(int item, string ev, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct = default)
    {
        if (!JournalEvents.Supported.Contains(ev)) throw new JournalRefusedException($"unsupported journal event '{ev}'");
        ev = JournalEvents.Supported.First(e => string.Equals(e, ev, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(key) || ev == "waiting") key = utcNow().Ticks.ToString(CultureInfo.InvariantCulture);
        using var held = await Enter(item, ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ev switch
                {
                    "waiting" => await AppendWaiting(item, fields, key, ct),
                    "ci-released" => await RecordCiReleased(item, fields, key, ct),
                    _ => new JournalOutcome(await AddJournal(item, null, ev, fields, key, save: true, ct)),
                };
            }
            catch (StateConflictException) when (attempt < 3) { /* the state changed since the read: read it again */ }
            catch (StateConflictException e) { throw new HttpRequestException($"journal {ev} for #{item}: {e.Message}"); }
        }
    }

    /// <summary>
    /// W9.6: Add-Journal with the caller's holder, the call the keeper and manager verbs make (Verbs.Keeper.ps1, Verbs.Manager.ps1): the line is posted and the ledger updated in <paramref name="holder"/>, which the caller saves
    /// itself together with the state it changed (no save here). False when the line was a repeat or over the cap. Throws for a failure that is worth a retry, as <see cref="Append"/> does; the caller decides what a lost line means.
    /// </summary>
    public async Task<bool> AddTo(int item, ItemState holder, string ev, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct = default)
    {
        if (!JournalEvents.Supported.Contains(ev)) throw new JournalRefusedException($"unsupported journal event '{ev}'");
        ev = JournalEvents.Supported.First(e => string.Equals(e, ev, StringComparison.OrdinalIgnoreCase));
        using var held = await Enter(item, ct);
        return await AddJournal(item, holder, ev, fields, key, save: false, ct);
    }

    // a 'waiting' note is rate-limited: at most one per timing.journalWaitingMinutes per item; the state is read once, and saved here after the line
    async Task<JournalOutcome> AppendWaiting(int item, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct)
    {
        var st = await store.Read(item, ct);
        var last = st.State["journal"] is JsonObject jj && JournalJson.Has(jj, "waiting_at") ? JournalJson.FromIso(jj["waiting_at"]!.ToString()) : null;
        if (last is { } l && (utcNow() - l).TotalMinutes < settings.WaitingMinutes) return new JournalOutcome(false, "rate-limited");
        var posted = await AddJournal(item, st, "waiting", fields, key, save: false, ct);
        if (posted)
        {
            ((JsonObject)st.State["journal"]!)["waiting_at"] = JournalJson.Iso(utcNow());
            await store.Save(item, st, ct);
        }
        return new JournalOutcome(posted);
    }

    // a routine release is machine-read state only (journal.ciReleased, the last 20): no issue comment
    async Task<JournalOutcome> RecordCiReleased(int item, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct)
    {
        var f = With(fields, item);
        var line = JournalText.LimitLine(JournalText.Protect(JournalText.Format("ci-released", f), settings.SecretPatterns));
        var st = await store.Read(item, ct); var s = st.State;
        var j = Journal(s);
        var k = $"ci-released|{key}";
        if (JournalJson.Strings(j, "keys").Contains(k, StringComparer.Ordinal)) return new JournalOutcome(false, Recorded: false);
        AddKey(j, k);
        j["count"] = JournalJson.Int(j, "count") + 1;
        var prev = JournalJson.Strings(j, "ciReleased").ToList();
        prev.Add($"{JournalText.Stamp(utcNow())} {line}");
        j["ciReleased"] = Strings(prev.TakeLast(JournalSettings.CiReleasedKept));
        AddKind(j, "ci-released", JournalJson.Iso(utcNow()));
        await store.Save(item, st, ct);
        return new JournalOutcome(false, Recorded: true);
    }

    /// <summary>Add-Journal: posts one comment, or folds a repeat into the comment of its kind. <paramref name="st"/> is the caller's holder (null: read one and save it).</summary>
    async Task<bool> AddJournal(int item, ItemState? st, string ev, IReadOnlyDictionary<string, object?> fields, string key, bool save, CancellationToken ct)
    {
        var own = false;
        if (st == null) { st = await store.Read(item, ct); own = true; }
        var j = Journal(st.State);
        var k = $"{ev}|{key}";
        if (JournalJson.Strings(j, "keys").Contains(k, StringComparer.Ordinal)) return false;
        var f = With(fields, item);
        var now = utcNow(); var nowIso = JournalJson.Iso(now);
        var line = JournalText.Protect(JournalText.Format(ev, f), settings.SecretPatterns);
        if (line.Length > JournalSettings.MaxLine) line = line[..JournalSettings.MaxLine] + " [truncated]";
        if (!JournalJson.Has(j, "fold")) j["fold"] = new JsonObject();
        // a line of another kind ends every open fold: what comes after it is new in the journal
        CloseFolds(j, ev);
        var fold = (JsonObject)j["fold"]!;
        var folded = false;
        if (FoldKinds.Contains(ev) && fold[ev] is JsonObject open && JournalJson.Has(open, "id"))
        {
            open["count"] = JournalJson.Int(open, "count") + 1; open["last"] = nowIso;
            var lines = JournalJson.Strings(open, "lines").ToList();
            lines.Add($"{JournalText.Stamp(now)} {JournalText.LimitLine(line)}");
            open["lines"] = Strings(lines.TakeLast(JournalSettings.FoldShow));
            var body = FoldBody(ev, open, open["key"]?.ToString() ?? "");
            // a comment that is gone (deleted by a person) or cannot be edited any more is replaced by a new one; a rate limit fails the line as before
            var outcome = await store.Patch(open["id"]!.GetValue<long>(), body, ct);
            if (outcome != PatchOutcome.Ok) fold.Remove(ev); else folded = true;
        }
        if (!folded)
        {
            // qc-ready, qc-failed and question-escalated may take a few slots beyond the cap: they are the lines a person looks for (and manager-action,
            // keeper-action and the promotion lines: every action of the plant manager is journaled)
            var extra = ExtraKinds.Contains(ev);
            // the start of a runtime session that carries the link to its transcript is posted whatever the count (every session worked on the item is reachable from its comments)
            var always = ev == "launched" && f.TryGetValue("transcript", out var tr) && Truthy(tr);
            var count = JournalJson.Int(j, "count");
            if (!always && count >= JournalSettings.Cap + (extra ? 4 : 1)) return false;
            if (always) extra = true;
            var full = count == JournalSettings.Cap && !extra;
            var text = full ? $"journal full ({JournalSettings.Cap} entries); later events are not posted here, see the gateway log." : line;
            var marker = $"<!-- plant event={ev} key={key} -->";
            var comment = $"\U0001F3ED Plant manager: {text}\n\n{marker}";
            // a comment that an earlier try posted before its state was saved: found by its marker, never posted twice
            var cid = FindPosted(st, ev, key)?.Id ?? await store.Post(item, comment, ct);
            j["count"] = count + 1;
            if (FoldKinds.Contains(ev) && !full && cid != null)
                fold[ev] = new JsonObject { ["id"] = cid, ["key"] = key, ["count"] = 1, ["first"] = nowIso, ["last"] = nowIso, ["lines"] = Strings([$"{JournalText.Stamp(now)} {JournalText.LimitLine(line)}"]) };
        }
        AddKey(j, k);
        AddKind(j, ev, nowIso);
        if (save || own) await store.Save(item, st, ct);
        return true;
    }

    static IssueComment? FindPosted(ItemState st, string ev, string key)
    {
        var plain = $"<!-- plant event={ev} key={key} -->"; var folded = $"<!-- plant event={ev} key={key} fold=";
        return st.Comments.FirstOrDefault(c => c.Body.StartsWith("\U0001F3ED Plant manager:", StringComparison.Ordinal) && (c.Body.EndsWith(plain, StringComparison.Ordinal) || c.Body.Contains(folded, StringComparison.Ordinal)));
    }

    // the body of a folded comment (Format-JournalFold)
    static string FoldBody(string ev, JsonObject fold, string key) =>
        JournalText.FormatFold(ev, JournalJson.Int(fold, "count"), JournalJson.FromIso(fold["first"]?.ToString()) ?? default, JournalJson.FromIso(fold["last"]?.ToString()) ?? default, JournalJson.Strings(fold, "lines"), key);

    // Close-JournalFolds: another kind of line ends the open folds
    static void CloseFolds(JsonObject j, string ev) { if (!FoldKinds.Contains(ev) && !FoldNeutral.Contains(ev)) j["fold"] = new JsonObject(); }

    // Add-JournalKind: every line counts for its kind
    static void AddKind(JsonObject j, string ev, string at)
    {
        if (!JournalJson.Has(j, "kinds")) j["kinds"] = new JsonObject();
        var kinds = (JsonObject)j["kinds"]!;
        if (kinds[ev] is JsonObject kind && kinds.ContainsKey(ev)) { kind["count"] = JournalJson.Int(kind, "count") + 1; kind["last"] = at; }
        else kinds[ev] = new JsonObject { ["count"] = 1, ["first"] = at, ["last"] = at };
    }

    static void AddKey(JsonObject j, string k)
    {
        var keys = JournalJson.Strings(j, "keys").ToList(); keys.Add(k);
        j["keys"] = Strings(keys.TakeLast(JournalSettings.KeysKept));
    }

    static JsonObject Journal(JsonObject s)
    {
        if (!JournalJson.Has(s, "journal") || s["journal"] is not JsonObject) s["journal"] = new JsonObject { ["keys"] = new JsonArray(), ["count"] = 0 };
        return (JsonObject)s["journal"]!;
    }

    static JsonArray Strings(IEnumerable<string> items) { var a = new JsonArray(); foreach (var s in items) a.Add(s); return a; }

    // $Fields.item = $Item: the fields as the script's hashtable holds them (names without case)
    static Dictionary<string, object?> With(IReadOnlyDictionary<string, object?> fields, int item)
    {
        var d = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase) { ["item"] = item };
        return d;
    }

    static bool Truthy(object? v) => v switch { null => false, string s => s.Length > 0, bool b => b, long l => l != 0, int i => i != 0, double d => d != 0, _ => true };

    // ---- the lock of an item: lines of one item are written one after the other -------------------------------------------------------------------

    sealed class ItemLock { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }

    async Task<IDisposable> Enter(int item, CancellationToken ct)
    {
        ItemLock l;
        lock (locks) { l = locks.GetOrAdd(item, _ => new ItemLock()); l.Users++; }
        try { await l.Gate.WaitAsync(ct); }
        catch { lock (locks) { if (--l.Users == 0) locks.TryRemove(item, out _); } throw; }
        return new Releaser(() => { l.Gate.Release(); lock (locks) { if (--l.Users == 0) locks.TryRemove(item, out _); } });
    }

    sealed class Releaser(Action release) : IDisposable { int done; public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) release(); } }

    /// <summary>The fields of a command (json) as the ledger reads them: text, true/false, numbers, null; anything else as its json.</summary>
    public static Dictionary<string, object?> FieldsOf(JsonObject? fields)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (fields == null) return d;
        foreach (var kv in fields)
        {
            d[kv.Key] = kv.Value switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when v.TryGetValue<bool>(out var b) => b,
                JsonValue v when v.TryGetValue<long>(out var l) => l,
                JsonValue v when v.TryGetValue<double>(out var x) => x,
                var other => other.ToJsonString(),
            };
        }
        return d;
    }
}
