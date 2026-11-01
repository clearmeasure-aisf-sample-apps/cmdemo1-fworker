using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Domain;

namespace Fworker.Integrations.Commands;

// W3.2: the journal ledger in C#. A port of Add-Journal, Close-JournalFolds, Add-JournalKind and Verb_journal of plugin/scripts/lib/Journal.ps1: the
// same comment text and marker, the same ledger in the item's state comment (journal.keys, count, kinds, fold, waiting_at, ciReleased), the same folds,
// the same cap, and the same GitHub calls in the same order (the golden writes see no difference). It does not go through the `journal` verb.
// Differences, all in failure cases only: the lines of one item are written one after the other under a lock of this process (the ledger's own gate; the commands of one item also run one after the other, see JournalCommandWiring),
// and the shared IItemLock it is given (W9.10l: the gateway gives it the one lock of every port, the item lock file that the rule scripts take too) is held only around the Save of the state comment, never across the posts to GitHub:
// the Save is a compare-and-write on the revision, which the item verbs and the scripts do under that lock, so it must exclude them; a post that takes seconds must not hold a verb of the same item behind it (the accept of a worker
// waited for the offered and launched lines); a line that lost the race to a verb gets a state conflict, reads the state again and repeats, and
// the repeat finds a comment that was posted before the conflict by its marker instead of posting it twice; a GitHub failure that is worth a retry is
// thrown, for the command base to classify, where the script logs it and drops the line.

/// <summary>
/// The events the journal verb accepts (Verb_journal, <see cref="Supported"/>) and the lines the ledger writes (<see cref="Writable"/>): Add-Journal takes any event the text knows, so the plant's own steps (the board guard's
/// board-added, the replenishment's replenish) write lines that no caller of the verb may send.
/// </summary>
public static class JournalEvents
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "matched", "offered", "launched", "assigned", "routed", "reassigned", "tier", "waiting", "skipped", "released", "qc-offered", "qc-requested", "qc-assigned",
        "qc-ready", "qc-failed", "qc-reset", "qc-torn-down", "qc-waiting", "question-escalated", "base-updated", "branch-updated", "keeper-action", "manager-action",
        "pin-fallback", "ci-released", "ci-release-refused",
    };

    /// <summary>
    /// V2.1: the lines of the automatic promotion (Verbs.Promote.ps1: promoted, promotion-waiting, promotion-signoff). The delivery train writes them through <see cref="JournalLedger.AppendPlant"/>; the journal verb and its command refuse them
    /// (they are neither <see cref="Supported"/> nor <see cref="Writable"/>), as before.
    /// </summary>
    public static readonly IReadOnlySet<string> Promotion = new HashSet<string>(["promoted", "promotion-waiting", "promotion-signoff"], StringComparer.OrdinalIgnoreCase);

    /// <summary>What the ledger writes: the verb's events and the lines of the plant's steps (Invoke-BoardGuard, Invoke-Replenish: W9.10g).</summary>
    public static readonly IReadOnlySet<string> Writable = new HashSet<string>(Supported.Concat(["board-added", "replenish"]), StringComparer.OrdinalIgnoreCase);
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
    readonly IItemLock itemLock;
    readonly ConcurrentDictionary<int, Gate> gates = new();

    public JournalLedger(IJournalStore store, JournalSettings? settings = null, Func<DateTime>? utcNow = null, Action<string>? log = null, IItemLock? itemLock = null)
    {
        this.itemLock = itemLock ?? new InProcessItemLock();
        this.store = store; this.settings = settings ?? new JournalSettings(); this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.log = log ?? (m => Console.Error.WriteLine(m));
    }

    /// <summary>
    /// Verb_journal: one line of an item. <paramref name="key"/> empty takes the ticks of now (the script's default; a waiting line always does).
    /// Throws for a failure that is worth a retry (the caller classifies it); the answers of the script, such as "rate-limited", are in the outcome.
    /// </summary>
    public Task<JournalOutcome> Append(int item, string ev, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct = default) => AppendFrom(item, ev, fields, key, plant: false, ct);

    /// <summary>
    /// <see cref="Append"/> for a line of the plant's own steps that the journal verb does not take from a caller: the promotion lines of the delivery train (<see cref="JournalEvents.Promotion"/>), V2.1.
    /// </summary>
    public Task<JournalOutcome> AppendPlant(int item, string ev, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct = default) => AppendFrom(item, ev, fields, key, plant: true, ct);

    async Task<JournalOutcome> AppendFrom(int item, string ev, IReadOnlyDictionary<string, object?> fields, string key, bool plant, CancellationToken ct)
    {
        (ev, key) = Prepare(ev, key, plant);
        // the lines of one item are written one after the other (the gate of this ledger); the shared item lock is taken only to save the state (see SaveState)
        using var turn = await Enter(item, ct);
        try { return await AppendLocked(item, ev, fields, key, held: false, ct); }
        catch (ItemBusyException e) { throw new TimeoutException($"journal {ev} for #{item}: {e.Message}", e); }   // worth a retry: the command base classifies a timeout as transient
    }

    /// <summary>
    /// <see cref="Append"/> for a caller that already holds the item's lock (W9.10l): the keeper and manager verbs run under it, and the lock is not reentrant, so taking it again would wait for the caller itself.
    /// </summary>
    public Task<JournalOutcome> AppendHeld(int item, string ev, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct = default)
    {
        (ev, key) = Prepare(ev, key);
        return AppendLocked(item, ev, fields, key, held: true, ct);
    }

    (string Event, string Key) Prepare(string ev, string key, bool plant = false)
    {
        var allowed = plant ? JournalEvents.Writable.Concat(JournalEvents.Promotion).ToList() : JournalEvents.Writable.ToList();
        if (!allowed.Contains(ev, StringComparer.OrdinalIgnoreCase)) throw new JournalRefusedException($"unsupported journal event '{ev}'");
        ev = allowed.First(e => string.Equals(e, ev, StringComparison.OrdinalIgnoreCase));
        // a line without a key takes the ticks (JournalKeys.OrTicks, the rule of Add-Journal too); a waiting line always does
        key = JournalKeys.OrTicks(ev == "waiting" ? "" : key, utcNow());
        return (ev, key);
    }

    async Task<JournalOutcome> AppendLocked(int item, string ev, IReadOnlyDictionary<string, object?> fields, string key, bool held, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ev switch
                {
                    "waiting" => await AppendWaiting(item, fields, key, held, ct),
                    "ci-released" => await RecordCiReleased(item, fields, key, held, ct),
                    _ => new JournalOutcome(await AddJournal(item, null, ev, fields, key, save: true, held, ct)),
                };
            }
            catch (StateConflictException) when (attempt < 3) { /* the state changed since the read: read it again */ }
            catch (StateConflictException e) { throw new HttpRequestException($"journal {ev} for #{item}: {e.Message}"); }
        }
    }

    /// <summary>
    /// W9.10c: whether the line with this event and key was handled already (its key is in the ledger of the item's state comment, which is what Add-Journal checks before it posts): the effect of a handling that was cut off
    /// before its result was recorded. A 'waiting' line takes the ticks of its write as its key, so it can never be looked up (false).
    /// </summary>
    public async Task<bool> Recorded(int item, string ev, string key, CancellationToken ct = default)
    {
        if (string.Equals(ev, "waiting", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(key)) return false;
        var supported = JournalEvents.Writable.FirstOrDefault(e => string.Equals(e, ev, StringComparison.OrdinalIgnoreCase));
        if (supported == null) return false;
        var st = await store.Read(item, ct);
        return JournalJson.Strings(Journal(st.State), "keys").Contains($"{supported}|{key}", StringComparer.Ordinal);
    }

    /// <summary>
    /// W9.6: Add-Journal with the caller's holder, the call the keeper and manager verbs make (Verbs.Keeper.ps1, Verbs.Manager.ps1): the line is posted and the ledger updated in <paramref name="holder"/>, which the caller saves
    /// itself together with the state it changed (no save here). False when the line was a repeat or over the cap. Throws for a failure that is worth a retry, as <see cref="Append"/> does; the caller decides what a lost line means.
    /// The caller holds the item's lock (W9.10l: the verbs run under it and it is not reentrant), so none is taken here.
    /// </summary>
    public Task<bool> AddTo(int item, ItemState holder, string ev, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct = default)
    {
        if (!JournalEvents.Writable.Contains(ev)) throw new JournalRefusedException($"unsupported journal event '{ev}'");
        ev = JournalEvents.Writable.First(e => string.Equals(e, ev, StringComparison.OrdinalIgnoreCase));
        return AddJournal(item, holder, ev, fields, key, save: false, held: true, ct);
    }

    /// <summary>
    /// W9.2: Add-Journal into a holder the caller has read and will save itself (the item verbs write their journal line and their state together, as the rule scripts do): the line is added to the holder's ledger and posted
    /// (or folded), nothing is saved here. False when the line was a repeat, over the cap, or already posted. Throws for a failure of GitHub, which the caller treats as the script does (it logs and goes on).
    /// </summary>
    public Task<bool> AddInHand(int item, ItemState holder, string ev, IReadOnlyDictionary<string, object?> fields, string key, CancellationToken ct = default) =>
        AddJournal(item, holder, ev, fields, key, save: false, held: true, ct);

    // a 'waiting' note is rate-limited: at most one per timing.journalWaitingMinutes per item; the state is read once, and saved here after the line
    async Task<JournalOutcome> AppendWaiting(int item, IReadOnlyDictionary<string, object?> fields, string key, bool held, CancellationToken ct)
    {
        var st = await store.Read(item, ct);
        var last = st.State["journal"] is JsonObject jj && JournalJson.Has(jj, "waiting_at") ? JournalJson.FromIso(jj["waiting_at"]!.ToString()) : null;
        if (last is { } l && (utcNow() - l).TotalMinutes < settings.WaitingMinutes) return new JournalOutcome(false, "rate-limited");
        var posted = await AddJournal(item, st, "waiting", fields, key, save: false, held, ct);
        if (posted)
        {
            ((JsonObject)st.State["journal"]!)["waiting_at"] = JournalJson.Iso(utcNow());
            await SaveState(item, st, held, ct);
        }
        return new JournalOutcome(posted);
    }

    // a routine release is machine-read state only (journal.ciReleased, the last 20): no issue comment
    async Task<JournalOutcome> RecordCiReleased(int item, IReadOnlyDictionary<string, object?> fields, string key, bool held, CancellationToken ct)
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
        await SaveState(item, st, held, ct);
        return new JournalOutcome(false, Recorded: true);
    }

    /// <summary>Add-Journal: posts one comment, or folds a repeat into the comment of its kind. <paramref name="st"/> is the caller's holder (null: read one and save it).</summary>
    async Task<bool> AddJournal(int item, ItemState? st, string ev, IReadOnlyDictionary<string, object?> fields, string key, bool save, bool held, CancellationToken ct)
    {
        var own = false;
        if (st == null) { st = await store.Read(item, ct); own = true; }
        var j = Journal(st.State);
        key = JournalKeys.OrTicks(key, utcNow());   // (AddTo and AddInHand come here without Prepare, as Add-Journal is called with whatever key the caller has)
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
        if (save || own) await SaveState(item, st, held, ct);
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

    // ---- the locks: the gate of an item's lines, and the shared item lock around the save only ------------------------------------------------------

    /// <summary>
    /// Save-State under the shared item lock (unless the caller holds it already): the save is a compare-and-write on the revision, which a verb or a script of the same item does under that lock too, so they must exclude one
    /// another here; everything slow (the posts and patches of the line) happens before, outside it. A save that lost the race throws <see cref="StateConflictException"/> and the line is repeated on a fresh read.
    /// </summary>
    Task SaveState(int item, ItemState st, bool held, CancellationToken ct) =>
        held ? store.Save(item, st, ct) : itemLock.Use(item, async () => { await store.Save(item, st, ct); return 0; }, IItemLock.DefaultWaitSeconds, ct);

    sealed class Gate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }

    async Task<IDisposable> Enter(int item, CancellationToken ct)
    {
        Gate g;
        lock (gates) { g = gates.GetOrAdd(item, _ => new Gate()); g.Users++; }
        try { await g.Semaphore.WaitAsync(ct); }
        catch { lock (gates) { if (--g.Users == 0) gates.TryRemove(item, out _); } throw; }
        return new Releaser(() => { g.Semaphore.Release(); lock (gates) { if (--g.Users == 0) gates.TryRemove(item, out _); } });
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
