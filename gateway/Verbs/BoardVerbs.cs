using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.Transcripts;

// W9.8 (docs/design/fworker-wolverine-implementation.md W9): the verbs of the board group on v2, answered in the shape of the rule script that they replace (plugin/scripts/factory.ps1 server mode: the fields of the
// result, then "verb", "ok" and "exit"; a refusal is {ok:false, verb, code, reason, ...} with the exit code of its Reject).
//   ingest-event     the translation of a GitHub webhook payload into the factory's events (WebhookEventTranslator): the same events as Verb_ingest_event, no rule script
//   journal          the journal ledger (JournalLedger, the C# port of Add-Journal) answers the verb as Verb_journal does, and the line is recorded in ItemHistory (JournalHistory)
//   transcript-*     the sessions' transcripts in factory.Transcripts (SqlTranscriptRepository); the verbs keep the sealed form of the ref store ("fwt1:") on the wire
//   sync-board       the sweep is v1's until W9.11 (the hybrid plant reads its offers, owners and parks): the answer is the rule script's, and one pass of the board reconciler rides along
// What differs from the scripts is in docs/design/fworker-wolverine-implementation.md, "W9.8 as built".

/// <summary>What the board verbs read: the parts that are there where the engine acts. A part that is not there (no GitHub token, no database) makes its verb answer the refusal the script would have given.</summary>
sealed class BoardVerbParts
{
    public ITranscriptRepository? Transcripts { get; init; }
    public TranscriptSeal Seal { get; init; } = new("");
    public RepoId? Home { get; init; }
    public Func<bool> Ready { get; init; } = () => true;
    public WebhookEventTranslator? Translator { get; init; }
    public Func<JournalLedger?> Ledger { get; init; } = () => null;
    public JournalHistory? History { get; init; }
    public Func<BoardReconciler?> Reconciler { get; init; } = () => null;
    public Func<string, string, TimeSpan, Task<(int Code, string Output)>>? RuleScript { get; init; }
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
}

static class BoardVerbAnswer
{
    public static VerbAnswer Ok(string verb, JsonObject result)
    {
        result["verb"] = verb;
        if (!result.ContainsKey("ok")) result["ok"] = true;
        result["exit"] = 0;
        return new VerbAnswer(0, result.ToJsonString());
    }

    public static VerbAnswer Rejected(string verb, int exit, string code, string reason, JsonObject? extra = null)
    {
        var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = code, ["reason"] = reason };
        if (extra != null) foreach (var (k, v) in extra.ToList()) { extra.Remove(k); o[k] = v; }
        o["exit"] = exit;
        return new VerbAnswer(exit, o.ToJsonString());
    }

    public static VerbAnswer Usage(string verb, string reason) => Rejected(verb, 2, "usage", reason);
    // the script's internal error (an exception that is no Reject): exit 20, to be tried again
    public static VerbAnswer Internal(string verb, string reason) => Rejected(verb, 20, "internal-error", reason, new JsonObject { ["retryAfterSeconds"] = 30 });
}

sealed class BoardVerbs(BoardVerbParts p)
{
    static readonly Regex SessionName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);
    static readonly Regex Day = new(@"^\d{8}$", RegexOptions.CultureInvariant);
    static readonly Regex Sealed = new(@"^fwt1:[A-Za-z0-9+/=]+$", RegexOptions.CultureInvariant);

    // the body the script reads: blank is {}; anything that is not a JSON object is the script's internal error
    static bool TryBody(string verb, string stdin, out JsonObject body, out VerbAnswer refusal)
    {
        body = new JsonObject(); refusal = default;
        if (string.IsNullOrWhiteSpace(stdin)) return true;
        try { if (JsonNode.Parse(stdin) is JsonObject o) { body = o; return true; } }
        catch (System.Text.Json.JsonException) { }
        refusal = BoardVerbAnswer.Internal(verb, "the body is not a JSON object");
        return false;
    }

    // Need: a field that is there and not empty
    static bool Need(string verb, JsonObject a, string key, out string value, out VerbAnswer refusal)
    {
        value = ""; refusal = default;
        if (a.TryGetPropertyValue(key, out var v) && v is not null && Text(v) is { Length: > 0 } t) { value = t; return true; }
        refusal = BoardVerbAnswer.Usage(verb, $"missing argument: {key}");
        return false;
    }

    // "$($x)"
    static string Text(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonValue v => v.ToString(),
        _ => n.ToJsonString(),
    };

    // A verb that v2 cannot answer here (the ledger is made when the Wolverine host is built, the translator needs the instance file's owner and repository) is refused with the reason: there is no rule script behind it any more
    static VerbAnswer Unavailable(string verb, string why) => BoardVerbAnswer.Rejected(verb, 30, "config-missing", why);

    // ---- ingest-event ----------------------------------------------------------------------------------------------------------------------------
    public async Task<VerbAnswer> IngestEvent(string stdin, TimeSpan timeout)
    {
        const string verb = "ingest-event";
        if (!TryBody(verb, stdin, out var a, out var bad)) return bad;
        if (!Need(verb, a, "event", out var ev, out var refusal)) return refusal;
        if (p.Translator is null) return Unavailable(verb, "the instance configuration names no owner and repository");
        var delivery = a.TryGetPropertyValue("delivery", out var d) && d is not null ? Text(d) : "";
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var t = await p.Translator.Translate(ev, delivery, a["payload"], cts.Token);
            var o = new JsonObject { ["ok"] = true, ["events"] = new JsonArray(t.Events.Select(e => (JsonNode)e).ToArray()) };
            if (t.Dropped is not null) o["dropped"] = t.Dropped;
            return BoardVerbAnswer.Ok(verb, o);
        }
        catch (WebhookLookupException e) { return BoardVerbAnswer.Rejected(verb, e.Exit, e.Code, e.Message, e.RetryAfterSeconds is { } retry ? new JsonObject { ["retryAfterSeconds"] = retry } : null); }
        catch (OperationCanceledException) { return BoardVerbAnswer.Rejected(verb, 20, "timeout", "the translation did not finish in time", new JsonObject { ["retryAfterSeconds"] = 30 }); }
    }

    // ---- journal ----------------------------------------------------------------------------------------------------------------------------------
    public async Task<VerbAnswer> Journal(string stdin, TimeSpan timeout)
    {
        const string verb = "journal";
        if (!TryBody(verb, stdin, out var a, out var bad)) return bad;
        if (!Need(verb, a, "item", out var itemText, out var refusal)) return refusal;
        if (!int.TryParse(itemText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var item)) return BoardVerbAnswer.Internal(verb, $"item is not a number: {itemText}");
        if (!Need(verb, a, "event", out var ev, out refusal)) return refusal;
        if (!JournalEvents.Supported.Contains(ev)) return BoardVerbAnswer.Usage(verb, $"unknown journal event '{ev}'");
        if (p.Ledger() is not { } ledger) return Unavailable(verb, "GH_TOKEN is not set on the gateway");
        var fields = JournalLedger.FieldsOf(a["fields"] as JsonObject);
        // a line without a key takes the ticks of the write, as the script does (a waiting line always does, in the ledger)
        var key = a.TryGetPropertyValue("key", out var k) && k is not null ? Text(k) : "";
        if (key.Length == 0) key = p.Now().UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture);
        JournalOutcome outcome;
        try { outcome = await ledger.Append(item, ev, fields, key); }
        // The state could not be saved because the item's lock stayed busy for 45 s (an owner's merge holds it): the script's answer in that case is Reject 20 item-busy, and its callers retry on exactly that code (PlantTicker's
        // escalation comment, 3.8). The ledger posts the comment before it saves (the lock is held around the save only), so the retry finds the comment by its marker and posts nothing twice; answering posted:false here would
        // end the retries and mark the question's journal as failed although the comment is there.
        catch (TimeoutException e) when (e.InnerException is ItemBusyException busy)
        {
            return BoardVerbAnswer.Rejected(verb, 20, "item-busy", busy.Message, new JsonObject { ["retryAfterSeconds"] = 5 });
        }
        // best-effort, as Add-Journal is: a failed comment never blocks the caller; the script's answer is posted:false and its stderr says why
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"journal {ev} for #{item} failed: {e.Message}");
            return BoardVerbAnswer.Ok(verb, new JsonObject { ["ok"] = true, ["id"] = item, ["posted"] = false });
        }
        if (p.History is { } history && p.Home is { } home)
            await history.Record(new JournalHistoryLine(new ItemId(home, item).ToString(), ev, key, outcome.Posted, outcome.Reason, outcome.Recorded, p.Now()));
        var o = new JsonObject { ["ok"] = true, ["id"] = item, ["posted"] = outcome.Posted };
        // the answers of the script: a waiting line inside its window says so; a routine ci-released says whether it was recorded
        if (outcome.Reason == "rate-limited") o["reason"] = "rate-limited";
        if (ev.Equals("ci-released", StringComparison.OrdinalIgnoreCase)) o["recorded"] = outcome.Recorded;
        return BoardVerbAnswer.Ok(verb, o);
    }

    // ---- transcripts ------------------------------------------------------------------------------------------------------------------------------
    VerbAnswer? NoDatabase(string verb) =>
        p.Transcripts is null ? BoardVerbAnswer.Rejected(verb, 30, "config-missing", "the gateway has no database for the transcripts")
        : !p.Ready() ? BoardVerbAnswer.Rejected(verb, 20, "database-not-ready", "the database schema is not applied yet", new JsonObject { ["retryAfterSeconds"] = 30 })
        : null;

    public async Task<VerbAnswer> TranscriptSave(string stdin, TimeSpan timeout)
    {
        const string verb = "transcript-save";
        if (!TryBody(verb, stdin, out var a, out var bad)) return bad;
        if (!Need(verb, a, "name", out var name, out var refusal) || !Need(verb, a, "day", out var day, out refusal) || !Need(verb, a, "data", out var data, out refusal)) return refusal;
        if (!SessionName.IsMatch(name) || !Day.IsMatch(day)) return BoardVerbAnswer.Usage(verb, "name is a session name, day is yyyymmdd");
        if (!Sealed.IsMatch(data)) return BoardVerbAnswer.Usage(verb, "data is the encrypted transcript (fwt1:<base64>)");
        if (data.Length > 900000) return BoardVerbAnswer.Rejected(verb, 2, "too-large", $"the transcript is {data.Length} characters; at most 900000");
        if (NoDatabase(verb) is { } unavailable) return unavailable;
        // the database holds the session in the clear: the wire form is opened with this gateway's secret (a transcript sealed with another one is refused)
        if (p.Seal.Open(data) is not { } doc || doc["name"]?.ToString() != name) return BoardVerbAnswer.Usage(verb, "data is not a transcript of this session sealed by this gateway");
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await p.Transcripts!.Save(DatabaseTranscriptBackend.ToRow(doc, p.Home), cts.Token);
        }
        catch (Exception e)
        {
            return BoardVerbAnswer.Rejected(verb, 20, "database-error", "the database did not take the transcript: " + e.GetType().Name, new JsonObject { ["retryAfterSeconds"] = 30 });
        }
        return BoardVerbAnswer.Ok(verb, new JsonObject { ["ok"] = true, ["ref"] = "factory.Transcripts/" + name, ["store"] = "database" });
    }

    public async Task<VerbAnswer> TranscriptLoad(string stdin, TimeSpan timeout)
    {
        const string verb = "transcript-load";
        if (!TryBody(verb, stdin, out var a, out var bad)) return bad;
        if (!Need(verb, a, "name", out var name, out var refusal)) return refusal;
        if (!SessionName.IsMatch(name)) return BoardVerbAnswer.Usage(verb, "name is a session name");
        if (NoDatabase(verb) is { } unavailable) return unavailable;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            if (await p.Transcripts!.Load(name, cts.Token) is not { } row) return BoardVerbAnswer.Ok(verb, new JsonObject { ["ok"] = true, ["found"] = false });
            if (!p.Seal.Available) return BoardVerbAnswer.Rejected(verb, 30, "secret-missing", "the gateway has no token secret to seal the transcript with");
            return BoardVerbAnswer.Ok(verb, new JsonObject { ["ok"] = true, ["found"] = true, ["data"] = p.Seal.Seal(DatabaseTranscriptBackend.ToDoc(row)) });
        }
        catch (Exception e)
        {
            return BoardVerbAnswer.Rejected(verb, 20, "database-error", "the database did not answer: " + e.GetType().Name, new JsonObject { ["retryAfterSeconds"] = 30 });
        }
    }

    public async Task<VerbAnswer> TranscriptPrune(string stdin, TimeSpan timeout)
    {
        const string verb = "transcript-prune";
        if (!TryBody(verb, stdin, out var a, out var bad)) return bad;
        var days = 14;
        if (a.TryGetPropertyValue("days", out var d) && d is not null)
        {
            if (!int.TryParse(Text(d), NumberStyles.Integer, CultureInfo.InvariantCulture, out days)) return BoardVerbAnswer.Usage(verb, "days is a number of days");
            days = Math.Clamp(days, 1, 365);
        }
        if (NoDatabase(verb) is { } unavailable) return unavailable;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            return BoardVerbAnswer.Ok(verb, new JsonObject { ["ok"] = true, ["removed"] = await p.Transcripts!.Prune(days, cts.Token) });
        }
        catch (Exception e)
        {
            return BoardVerbAnswer.Rejected(verb, 20, "database-error", "the database did not answer: " + e.GetType().Name, new JsonObject { ["retryAfterSeconds"] = 30 });
        }
    }

    // ---- sync-board -------------------------------------------------------------------------------------------------------------------------------
    // The sweep stays the rule script's until the hybrid plant is gone (W9.11): its answer is returned as it is. The board reconciler's pass for the same scope (the items named, or the whole board; dry: counted, not published)
    // follows a sweep that succeeded and is added as "reconciler"; a reconciler that cannot run (no GitHub token, no board) adds null and never changes the exit code.
    public async Task<VerbAnswer> SyncBoard(string stdin, TimeSpan timeout)
    {
        const string verb = "sync-board";
        if (p.RuleScript is null) return BoardVerbAnswer.Rejected(verb, 30, "config-missing", "the rule script runner is not available");
        var (code, output) = await p.RuleScript(verb, stdin, timeout);
        if (code != 0 || p.Reconciler() is not { } reconciler) return new VerbAnswer(code, output);
        JsonObject? answer;
        try { answer = JsonNode.Parse(output) as JsonObject; } catch (System.Text.Json.JsonException) { answer = null; }
        if (answer is null) return new VerbAnswer(code, output);
        JsonObject body;
        try { body = string.IsNullOrWhiteSpace(stdin) ? new JsonObject() : JsonNode.Parse(stdin) as JsonObject ?? new JsonObject(); } catch (System.Text.Json.JsonException) { body = new JsonObject(); }
        HashSet<int>? only = null;
        if (body["items"] is JsonArray items)
        {
            only = [];
            foreach (var i in items) if (i is JsonValue v && v.TryGetValue<int>(out var n) && n > 0) only.Add(n);
        }
        var dry = body["dry"] is JsonValue dv && dv.TryGetValue<bool>(out var db) && db;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var r = await reconciler.Run(cts.Token, only, dry);
            answer["reconciler"] = new JsonObject { ["outcome"] = r.Outcome, ["cards"] = r.Cards, ["published"] = r.Published, ["dry"] = dry };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"sync-board reconciler pass: {e.GetType().Name}: {e.Message}");
            answer["reconciler"] = null;
        }
        return new VerbAnswer(code, answer.ToJsonString());
    }
}
