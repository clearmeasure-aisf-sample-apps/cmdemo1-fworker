using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;

namespace Fworker.Workflows.Qc;

/// <summary>
/// W9.1: the 14 qc-* verbs of Qc.ps1 on v2, a saga per (repository, pull request) whose state is the QC record. Each verb answers as the rule script does for the same input (the same fields, exit codes and
/// reasons) and does the same to GitHub: the offer comments on the pull request and the item, the journal lines, the record under <c>state.qc</c> of the item's state comment. That comment stays the home of the record
/// in the hybrid composition (v1's sweep, its merge gate and a rollback read it); every save of it is followed by the copy in factory.QcEnvironments, which the lists and the index read, and which a verb adopts when the
/// comment holds no record. The gateway's half of an environment (the lease, the URL, the probe, the worker) is QcHub's, in memory, as before.
/// </summary>
public sealed partial class QcService
{
    /// <summary>The verbs this service answers (the 14 of the inventory whose owner is QcService).</summary>
    public static readonly IReadOnlyList<string> Verbs =
    [
        "qc-sync", "qc-link", "qc-view", "qc-request", "qc-reset", "qc-teardown", "qc-list", "qc-job", "qc-progress", "qc-report", "qc-fail", "qc-stopped", "qc-transition", "qc-approval",
    ];

    // the verbs the rule script runs without the item lock (NoLockVerbs of factory.ps1)
    public static readonly IReadOnlySet<string> NoLockVerbs = new HashSet<string>(["qc-sync", "qc-list", "qc-view", "qc-job"]);

    readonly QcConfig cfg;
    readonly IQcItems items;
    readonly IQcGitHub github;
    readonly IQcFacts facts;
    readonly IQcIndex index;
    readonly IQcEnvironmentStore? table;
    readonly Func<DateTime> utcNow;
    readonly Func<string>? nonce;
    readonly Action<string> log;
    readonly QcLocks locks = new();

    public QcService(QcConfig cfg, IQcItems items, IQcGitHub github, IQcFacts facts, IQcIndex index, IQcEnvironmentStore? table = null, Func<DateTime>? utcNow = null, Func<string>? nonce = null, Action<string>? log = null)
    {
        this.cfg = cfg; this.items = items; this.github = github; this.facts = facts; this.index = index; this.table = table;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow); this.nonce = nonce; this.log = log ?? (m => Console.Error.WriteLine(m));
    }

    public QcConfig Config => cfg;

    // ---- the verb call: the answer of factory.ps1 --server ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one verb. The answer is the script's: <c>{ ok, verb, ..., exit }</c> with exit 0, or a refusal <c>{ ok: false, verb, code, reason, ..., exit }</c>; a fault that is none of the verb's refusals is
    /// 'internal-error' (20, retry in 30 seconds). A verb with an item runs under the item's lock, except the four the script does not lock.
    /// </summary>
    public async Task<VerbAnswer> Run(string verb, string stdin, CancellationToken ct = default)
    {
        JsonObject a;
        try { a = string.IsNullOrWhiteSpace(stdin) ? new JsonObject() : JsonNode.Parse(stdin) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException e) { return Fault(verb, e); }
        // what the gateway adds for the script's own use (the session link, the test cause, the leader lease) is not an argument
        foreach (var k in new[] { "_session", "_cause", "_lease" }) a.Remove(k);
        try
        {
            if (!Verbs.Contains(verb)) throw new QcReject(2, "unknown-verb", $"unknown verb '{verb}'");
            JsonObject r;
            if (!NoLockVerbs.Contains(verb) && QcJson.Has(a, "item") && QcJson.TryInt(a["item"], out var item))
                r = await locks.Run(item, TimeSpan.FromSeconds(45), () => WithRetry(verb, a, ct), ct);
            else r = await WithRetry(verb, a, ct);
            r["verb"] = verb;
            if (!r.ContainsKey("ok")) r["ok"] = true;
            r["exit"] = 0;
            return new VerbAnswer(0, r.ToJsonString());
        }
        catch (QcReject e)
        {
            var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = e.Code, ["reason"] = e.Message };
            if (e.Extra is not null) foreach (var kv in e.Extra) o[kv.Key] = kv.Value?.DeepClone();
            o["exit"] = e.Exit;
            return new VerbAnswer(e.Exit, o.ToJsonString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return Fault(verb, e); }
    }

    VerbAnswer Fault(string verb, Exception e)
    {
        log($"internal error in {verb} : {e.Message}");
        return new VerbAnswer(20, new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "internal-error", ["reason"] = e.Message, ["retryAfterSeconds"] = 30, ["exit"] = 20 }.ToJsonString());
    }

    // a state comment that moved under the verb: read again and run again, then refuse as Save-State does (11 'state-conflict')
    async Task<JsonObject> WithRetry(string verb, JsonObject a, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await Dispatch(verb, a, ct); }
            catch (QcStateConflictException) when (attempt < 3) { }
            catch (QcStateConflictException e) { throw new QcReject(11, "state-conflict", e.Message); }
        }
    }

    Task<JsonObject> Dispatch(string verb, JsonObject a, CancellationToken ct) => verb switch
    {
        "qc-sync" => VerbSync(a, ct),
        "qc-link" => VerbLink(a, ct),
        "qc-view" => VerbView(a, ct),
        "qc-request" => VerbRequest(a, ct),
        "qc-reset" => VerbReset(a, ct),
        "qc-teardown" => VerbTeardown(a, ct),
        "qc-transition" => VerbTransition(a, ct),
        "qc-list" => VerbList(a, ct),
        "qc-job" => VerbJob(a, ct),
        "qc-progress" => VerbProgress(a, ct),
        "qc-report" => VerbReport(a, ct),
        "qc-fail" => VerbFail(a, ct),
        "qc-stopped" => VerbStopped(a, ct),
        "qc-approval" => VerbApproval(a, ct),
        _ => throw new QcReject(2, "unknown-verb", $"unknown verb '{verb}'"),
    };

    // ---- arguments (Need, Get-ItemNumber) -------------------------------------------------------------------------------------------------------------

    static string Need(JsonObject a, string key)
    {
        if (!QcJson.Has(a, key) || QcJson.Str(a, key) == "") throw new QcReject(2, "usage", $"missing argument: {key}");
        return QcJson.Str(a, key);
    }

    // [int](Need $a k): a value that is no number is a fault of the script (internal-error)
    static int NeedInt(JsonObject a, string key)
    {
        var text = Need(a, key);
        return QcJson.TryInt(a[key], out var i) ? i : throw new FormatException($"Cannot convert value \"{text}\" to type \"System.Int32\".");
    }

    static int ItemNumber(JsonObject a) => NeedInt(a, "item");

    static string Sha7(string sha) => sha[..Math.Min(7, sha.Length)];

    string Text(string? text, int max) => QcRecords.Text(text, max, cfg.SecretPatterns);

    string JournalText(string? text, int max) => QcRecords.JournalLine(text, max, cfg.SecretPatterns);

    // ---- the records ----------------------------------------------------------------------------------------------------------------------------------

    string KeyOf(int pr) => QcKeys.For(cfg.Owner, cfg.Repo, pr);

    /// <summary>Get-QcRec, and the copy in the table when the comment holds no record of the pull request (the copy goes into the state, so that the verb's save writes it back).</summary>
    async Task<JsonObject?> RecordOf(QcItemState st, int pr, CancellationToken ct)
    {
        var rec = QcRecords.Of(st.State, pr);
        if (rec is not null || table is null) return rec;
        try
        {
            if (await table.Load(KeyOf(pr), ct) is { } row && row.State.Count > 0)
            {
                if (st.State["qc"] is not JsonObject map) st.State["qc"] = map = new JsonObject();
                var adopted = (JsonObject)row.State.DeepClone();
                map[pr.ToString(System.Globalization.CultureInfo.InvariantCulture)] = adopted;
                return adopted;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { log($"qc: the table of environments could not be read for PR {pr}: {e.Message}"); }
        return null;
    }

    static List<JsonObject> LiveRecords(JsonObject s)
    {
        var live = new List<JsonObject>();
        if (QcJson.Obj(s, "qc") is { } map) foreach (var kv in map) if (kv.Value is JsonObject r && QcRecords.IsLive(r)) live.Add(r);
        return live;
    }

    /// <summary>Save-QcRecord: the record into the state, the state saved, the index of live items kept, and the copy in the table.</summary>
    async Task SaveRecord(int item, QcItemState st, JsonObject rec, CancellationToken ct)
    {
        var s = st.State;
        if (!QcJson.Has(s, "qc") || s["qc"] is not JsonObject map) s["qc"] = map = new JsonObject();
        var key = QcJson.Str(rec, "pr");
        if (!ReferenceEquals(map[key], rec)) map[key] = rec;
        await items.SaveState(item, st, ct);
        index.Update(item, map.Any(kv => kv.Value is JsonObject r && QcRecords.IsLive(r)));
        await Project(item, rec, ct);
    }

    async Task Project(int item, JsonObject rec, CancellationToken ct)
    {
        if (table is null) return;
        try { await table.Save(KeyOf(QcJson.Int(rec, "pr")), $"{cfg.Owner}/{cfg.Repo}".ToLowerInvariant(), QcKeys.ItemKey(cfg.Owner, cfg.Repo, item), rec, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { log($"qc: the copy of PR {QcJson.Int(rec, "pr")} in the table of environments was not written: {e.Message}"); }
    }

    // A journal line is best effort (Add-Journal logs a failure and goes on), but the state comment it is kept in is written by other loops too (the plant's lines, the sweep): a line that met a moved state
    // comment is tried again, which is safe, since the ledger finds the comment it posted before by its marker and never posts a line twice.
    async Task JournalLine(int item, string ev, JsonObject fields, string key, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await items.Journal(item, ev, fields, key, ct); return; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (attempt >= 3) { log($"qc journal {ev} for #{item}: {e.Message}"); return; }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
        }
    }

    async Task<JsonObject> GetPull(int number, CancellationToken ct) =>
        await github.GetPull(number, ct) ?? throw new QcReject(12, "no-such-pr", $"pull request #{number} does not exist");

    static string HeadSha(JsonObject pr) => QcJson.Str(QcJson.Obj(pr, "head"), "sha");

    static string Login(JsonObject pr) => QcJson.Str(QcJson.Obj(pr, "user"), "login");

    async Task<QcEligibility> EligibilityWithFiles(int item, QcItemInfo info, JsonObject s, JsonObject pr, CancellationToken ct) =>
        QcRules.Eligibility(cfg, item, info.Labels, info.Column, s, pr, await github.PullFiles(QcJson.Int(pr, "number"), ct));

    static QcReject NotEligible(int prNum, QcEligibility el) =>
        new(10, "not-eligible", $"PR #{prNum} is not eligible for a QC environment: {string.Join(", ", el.Reasons)}", new JsonObject { ["reasons"] = QcJson.ArrayOf(el.Reasons) });

    /// <summary>Get-QcTrust: the reasons a run needs the operator's confirmation.</summary>
    async Task<List<string>> Trust(int item, JsonObject pr, CancellationToken ct)
    {
        cfg.Validate();
        var login = await github.ServerLogin(ct);
        var codes = await facts.MandatoryCodes(item, ct);
        return QcRules.TrustReasons(cfg, Login(pr), login, codes);
    }

    /// <summary>Assert-QcAssignment: the token's assignment must be the record's. One this record used to have is stale (11); anything else is not yours (40).</summary>
    static void AssertAssignment(JsonObject? rec, string assignment)
    {
        if (rec is null) throw new QcReject(12, "no-environment", "no QC environment is recorded for this pull request");
        if (QcJson.Has(rec, "assignment") && string.Equals(QcJson.Str(rec, "assignment"), assignment, StringComparison.OrdinalIgnoreCase)) return;
        if (QcJson.Has(rec, "old_assignments") && rec["old_assignments"] is JsonArray old && old.Any(x => string.Equals(QcJson.Text(x), assignment, StringComparison.OrdinalIgnoreCase)))
            throw new QcReject(11, "stale-assignment", "this assignment was replaced by a reset or a newer attempt; stop and remove what it built");
        throw new QcReject(40, "claim-held", "this QC environment is not assigned to you");
    }

    static JsonObject WithRecord(JsonObject answer, JsonObject rec) { answer["record"] = rec.DeepClone(); return answer; }
}
