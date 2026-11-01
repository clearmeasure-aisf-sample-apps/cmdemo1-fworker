using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.Transcripts;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.8 (gateway/Verbs/BoardVerbs.cs): the verbs of the board group on v2, in the shape of the rule scripts: the fields of the result, "verb", "ok" and "exit"; a refusal is {ok:false, verb, code, reason} with the exit code of
/// its Reject. IngestEventParityTests runs the real scripts on the same inputs for the cases that need no network.
/// </summary>
public sealed class BoardVerbsTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly DateTimeOffset T0 = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Time = TimeSpan.FromSeconds(30);

    sealed class Lookups : IWebhookLookups
    {
        public Exception? Fails;
        public Task<WebhookItemFacts?> ReadItem(int item, CancellationToken ct = default) => Task.FromResult<WebhookItemFacts?>(new(["factory worker"], "Development"));
        public Task<WebhookIssueNode?> ReadIssueNode(string nodeId, CancellationToken ct = default) => Fails is null ? Task.FromResult<WebhookIssueNode?>(new(5, "acme/orders", ["factory worker"])) : Task.FromException<WebhookIssueNode?>(Fails);
    }

    sealed class MemoryTranscripts : ITranscriptRepository
    {
        public readonly Dictionary<string, TranscriptDoc> Rows = new();
        public Exception? Fails; public int? Pruned;
        public Task Save(TranscriptDoc doc, CancellationToken ct = default) { if (Fails != null) throw Fails; Rows[doc.Name] = doc; return Task.CompletedTask; }
        public Task<TranscriptDoc?> Load(string name, CancellationToken ct = default) { if (Fails != null) throw Fails; return Task.FromResult(Rows.GetValueOrDefault(name)); }
        public Task<int> Prune(int days, CancellationToken ct = default) { Pruned = days; return Task.FromResult(2); }
        public Task<IReadOnlyList<string>> SessionsOf(string itemKey, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Rows.Values.Where(r => r.ItemKey == itemKey).Select(r => r.Name).ToList());
    }

    sealed class MemoryJournal : IJournalStore
    {
        public readonly Dictionary<int, JsonObject> States = new(); public readonly List<(int Item, string Body)> Posts = new();
        public bool Down, ShowPosts; long id = 100;
        // ShowPosts: the issue's comments are the posts, as GitHub's are (the ledger finds a comment an earlier try posted by its marker)
        public Task<ItemState> Read(int item, CancellationToken ct) { if (Down) throw new HttpRequestException("GitHub is down"); return Task.FromResult(new ItemState { State = (JsonObject)(States.GetValueOrDefault(item)?.DeepClone() ?? new JsonObject()), Rev = 0, Comments = ShowPosts ? Posts.Where(p => p.Item == item).Select((p, i) => new IssueComment(i + 1, "bot", p.Body)).ToList() : [] }); }
        public Task Save(int item, ItemState holder, CancellationToken ct) { States[item] = (JsonObject)holder.State.DeepClone(); return Task.CompletedTask; }
        public Task<long?> Post(int item, string body, CancellationToken ct) { Posts.Add((item, body)); return Task.FromResult<long?>(++id); }
        public Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct) => Task.FromResult(PatchOutcome.Ok);
    }

    sealed class Rig
    {
        public readonly MemoryTranscripts Transcripts = new();
        public readonly MemoryJournal Journal = new();
        public readonly Lookups Lookups = new();
        public bool Ready = true;
        public readonly List<string> Sweeps = [];
        public (int Code, string Output) SweepAnswer = (0, """{"ok":true,"steps":{},"offers":[],"actions":[],"errors":[],"incremental":false,"exit":0}""");
        public BoardReconciler? Reconciler;
        public IItemLock? Locks;
        public readonly List<string> Published = [];
        public TranscriptSeal Seal = new("board-verbs-tests-secret");
        public BoardVerbs Verbs() => new(new BoardVerbParts
        {
            Transcripts = Transcripts, Seal = Seal, Home = Home, Ready = () => Ready,
            Translator = new WebhookEventTranslator(new WebhookTranslatorConfig("acme", "orders", "factory-worker", "master", "factory worker", ""), Lookups, () => T0),
            Ledger = () => new JournalLedger(Journal, new JournalSettings(30), () => T0.UtcDateTime, _ => { }, Locks),
            Reconciler = () => Reconciler, Now = () => T0,
            RuleScript = (verb, stdin, _) => { Sweeps.Add(stdin); return Task.FromResult(SweepAnswer); },
        });
    }

    static JsonObject Parse(VerbAnswer a) => (JsonObject)JsonNode.Parse(a.Output)!;

    // ---- ingest-event ----
    [Fact]
    public async Task IngestEvent_AnswersTheScriptsShape()
    {
        var a = await new Rig().Verbs().IngestEvent("""{"event":"pull_request","delivery":"d-7","payload":{"action":"opened","sender":{"login":"alice"},"pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h1","repo":{"full_name":"acme/orders"}},"base":{"ref":"master"}}}}""", Time);
        Assert.Equal(0, a.Code);
        var o = Parse(a);
        Assert.Equal((true, "ingest-event", 0), ((bool)o["ok"]!, o["verb"]!.ToString(), (int)o["exit"]!));
        var e = Assert.Single(o["events"]!.AsArray())!;
        Assert.Equal(("pr.opened", 12, "d-7", "2026-10-29T09:00:00.000Z"), (e["event"]!.ToString(), (int)e["item"]!, e["ref"]!.ToString(), e["at"]!.ToString()));
    }

    [Fact]
    public async Task IngestEvent_ADroppedDeliveryAnswersEmptyEvents_AndWhy()
    {
        var o = Parse(await new Rig().Verbs().IngestEvent("""{"event":"issues","payload":{"repository":{"full_name":"acme/other"},"action":"closed"}}""", Time));
        Assert.Equal((true, "other-repository", 0), ((bool)o["ok"]!, o["dropped"]!.ToString(), o["events"]!.AsArray().Count));
    }

    [Theory]
    [InlineData("""{"payload":{}}""", "missing argument: event")]
    [InlineData("""{"event":""}""", "missing argument: event")]
    [InlineData("""{"event":null}""", "missing argument: event")]
    public async Task IngestEvent_WithoutAnEvent_IsAUsageError(string body, string reason)
    {
        var a = await new Rig().Verbs().IngestEvent(body, Time);
        Assert.Equal(2, a.Code);
        var o = Parse(a);
        Assert.Equal((false, "ingest-event", "usage", reason, 2), ((bool)o["ok"]!, o["verb"]!.ToString(), o["code"]!.ToString(), o["reason"]!.ToString(), (int)o["exit"]!));
    }

    [Fact]
    public async Task IngestEvent_AFailedRead_IsTheScriptsRejectWithItsExitAndRetry()
    {
        var rig = new Rig { };
        rig.Lookups.Fails = new WebhookLookupException(20, "github-rate-limited", "API rate limit exceeded", 60);
        var a = await rig.Verbs().IngestEvent("""{"event":"projects_v2_item","payload":{"action":"edited","projects_v2_item":{"content_node_id":"I_5"},"changes":{"field_value":{"field_name":"Status","to":{"name":"Done"}}}}}""", Time);
        Assert.Equal(20, a.Code);
        var o = Parse(a);
        Assert.Equal(("github-rate-limited", 60, 20), (o["code"]!.ToString(), (int)o["retryAfterSeconds"]!, (int)o["exit"]!));
    }

    [Fact]
    public async Task AVerbBodyThatIsNotJson_IsTheScriptsInternalError()
    {
        var a = await new Rig().Verbs().IngestEvent("not json", Time);
        Assert.Equal(20, a.Code); Assert.Equal("internal-error", Parse(a)["code"]!.ToString());
    }

    // ---- journal ----
    [Fact]
    public async Task Journal_PostsALine_AndAnswersIdAndPosted()
    {
        var rig = new Rig();
        var o = Parse(await rig.Verbs().Journal("""{"item":7,"event":"tier","key":"t1","fields":{"name":"opus","reasons":"big"}}""", Time));
        Assert.Equal((true, 7, true, "journal", 0), ((bool)o["ok"]!, (int)o["id"]!, (bool)o["posted"]!, o["verb"]!.ToString(), (int)o["exit"]!));
        Assert.Contains("model tier raised to **opus** (big).", Assert.Single(rig.Journal.Posts).Body);
        Assert.Null(o["reason"]);
        // the same key again: the ledger has it
        Assert.False((bool)Parse(await rig.Verbs().Journal("""{"item":7,"event":"tier","key":"t1","fields":{"name":"opus","reasons":"big"}}""", Time))["posted"]!);
        Assert.Single(rig.Journal.Posts);
    }

    [Fact]
    public async Task Journal_AWaitingNoteInsideItsWindow_SaysRateLimited()
    {
        var rig = new Rig();
        Assert.True((bool)Parse(await rig.Verbs().Journal("""{"item":7,"event":"waiting","fields":{"reason":"a"}}""", Time))["posted"]!);
        var o = Parse(await rig.Verbs().Journal("""{"item":7,"event":"waiting","fields":{"reason":"b"}}""", Time));
        Assert.Equal((false, "rate-limited"), ((bool)o["posted"]!, o["reason"]!.ToString()));
    }

    [Fact]
    public async Task Journal_ARoutineRelease_IsRecordedNotPosted()
    {
        var rig = new Rig();
        var first = Parse(await rig.Verbs().Journal("""{"item":7,"event":"ci-released","key":"r1","fields":{"run":1,"pr":2,"sha7":"abc","branch":"b"}}""", Time));
        Assert.Equal((false, true), ((bool)first["posted"]!, (bool)first["recorded"]!));
        Assert.Empty(rig.Journal.Posts);
        var again = Parse(await rig.Verbs().Journal("""{"item":7,"event":"ci-released","key":"r1","fields":{"run":1,"pr":2,"sha7":"abc","branch":"b"}}""", Time));
        Assert.False((bool)again["recorded"]!);
    }

    [Theory]
    [InlineData("""{"event":"tier"}""", 2, "missing argument: item")]
    [InlineData("""{"item":7}""", 2, "missing argument: event")]
    [InlineData("""{"item":7,"event":"no-such"}""", 2, "unknown journal event 'no-such'")]
    public async Task Journal_Refusals_AreTheScriptsUsageErrors(string body, int exit, string reason)
    {
        var a = await new Rig().Verbs().Journal(body, Time);
        Assert.Equal(exit, a.Code);
        var o = Parse(a);
        Assert.Equal((false, "usage", reason), ((bool)o["ok"]!, o["code"]!.ToString(), o["reason"]!.ToString()));
    }

    sealed class BusyLock : IItemLock
    {
        public Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct) => throw new ItemBusyException(item);
    }

    [Fact]
    public async Task Journal_AnItemLockThatStaysBusy_IsTheScriptsItemBusy_AndTheRetryPostsNothingTwice()
    {
        var rig = new Rig { Locks = new BusyLock() }; rig.Journal.ShowPosts = true;
        const string body = """{"item":7,"event":"question-escalated","key":"q|m-1","fields":{"worker":"alice","minutes":10,"supervisor":"absent"}}""";
        // the lock is held around the save only, so the comment is posted and the save is refused: the answer is the script's Reject 20 item-busy, which the callers retry on (not posted:false, which ends their retries)
        var busy = await rig.Verbs().Journal(body, Time);
        Assert.Equal(20, busy.Code);
        var o = Parse(busy);
        Assert.Equal((false, "journal", "item-busy", 5, 20), ((bool)o["ok"]!, o["verb"]!.ToString(), o["code"]!.ToString(), (int)o["retryAfterSeconds"]!, (int)o["exit"]!));
        Assert.Single(rig.Journal.Posts);
        // the lock is free at the retry: the comment of the first try is found by its marker, the state is saved with the key, and the answer is posted:true
        rig.Locks = null;
        var again = Parse(await rig.Verbs().Journal(body, Time));
        Assert.Equal((true, 0), ((bool)again["posted"]!, (int)again["exit"]!));
        Assert.Single(rig.Journal.Posts);
        Assert.Contains("question-escalated|q|m-1", rig.Journal.States[7].ToJsonString());
    }

    [Fact]
    public async Task Journal_AGitHubThatFails_IsBestEffort_PostedFalse_NotAnError()
    {
        var rig = new Rig(); rig.Journal.Down = true;
        var a = await rig.Verbs().Journal("""{"item":7,"event":"tier","key":"t1","fields":{"name":"opus","reasons":"big"}}""", Time);
        Assert.Equal(0, a.Code);
        Assert.False((bool)Parse(a)["posted"]!);
    }

    [Fact]
    public async Task Journal_AndIngestEvent_WithoutWhatV2NeedsHere_AreRefusedWithTheReason()
    {
        var seen = new List<string>();
        var parts = new BoardVerbParts { RuleScript = (verb, stdin, _) => { seen.Add(verb + " " + stdin); return Task.FromResult((0, "{\"ok\":true,\"by\":\"script\"}")); } };   // no ledger, no translator: no rule script answers for them any more
        var verbs = new BoardVerbs(parts);
        var journal = await verbs.Journal("""{"item":7,"event":"tier","fields":{}}""", Time);
        Assert.Equal((30, "config-missing"), (journal.Code, Parse(journal)["code"]!.ToString()));
        var ingest = await verbs.IngestEvent("""{"event":"push","payload":{}}""", Time);
        Assert.Equal((30, "config-missing"), (ingest.Code, Parse(ingest)["code"]!.ToString()));
        Assert.Empty(seen);
        // the usage refusals of a malformed call come first
        Assert.Equal(2, (await verbs.Journal("""{"item":7,"event":"no-such"}""", Time)).Code);
    }

    // ---- transcripts ----
    static (string Name, string Data) Sealed(Rig rig, string name = "cae-1.dev", int item = 12, params string[] lines)
    {
        var doc = new JsonObject
        {
            ["v"] = 1, ["name"] = name, ["item"] = item, ["phase"] = "develop", ["runtime"] = "octopus", ["day"] = "20261029", ["started"] = "2026-10-29T08:00:00.0000000Z",
            ["ended"] = null, ["lastLine"] = "2026-10-29T08:05:00.0000000Z", ["dropped"] = 0, ["total"] = lines.Length, ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()),
        };
        return (name, rig.Seal.Seal(doc));
    }

    [Fact]
    public async Task TranscriptSave_StoresTheOpenedSession_AndLoadSealsItAgain()
    {
        var rig = new Rig(); var v = rig.Verbs();
        var (name, data) = Sealed(rig, lines: ["one", "two"]);
        var saved = Parse(await v.TranscriptSave(new JsonObject { ["name"] = name, ["day"] = "20261029", ["data"] = data }.ToJsonString(), Time));
        Assert.Equal((true, "transcript-save", 0), ((bool)saved["ok"]!, saved["verb"]!.ToString(), (int)saved["exit"]!));
        var row = rig.Transcripts.Rows[name];
        Assert.Equal(("acme/orders#12", "octopus", "develop", 2L), (row.ItemKey, row.Runtime, row.Phase, row.Total));
        Assert.Equal(["one", "two"], row.Lines);
        var loaded = Parse(await v.TranscriptLoad($$"""{"name":"{{name}}"}""", Time));
        Assert.True((bool)loaded["found"]!);
        var doc = rig.Seal.Open(loaded["data"]!.ToString())!;
        Assert.Equal((12, "develop", "20261029"), ((int)doc["item"]!, doc["phase"]!.ToString(), doc["day"]!.ToString()));
        Assert.Equal(["one", "two"], doc["lines"]!.AsArray().Select(x => x!.ToString()).ToArray());
        Assert.False((bool)Parse(await v.TranscriptLoad("""{"name":"nobody"}""", Time))["found"]!);
    }

    [Theory]
    [InlineData("""{"day":"20261029","data":"fwt1:AAAA"}""", "missing argument: name")]
    [InlineData("""{"name":"a","data":"fwt1:AAAA"}""", "missing argument: day")]
    [InlineData("""{"name":"a","day":"20261029"}""", "missing argument: data")]
    [InlineData("""{"name":"-bad","day":"20261029","data":"fwt1:AAAA"}""", "name is a session name, day is yyyymmdd")]
    [InlineData("""{"name":"ok","day":"2026-10-29","data":"fwt1:AAAA"}""", "name is a session name, day is yyyymmdd")]
    [InlineData("""{"name":"ok","day":"20261029","data":"plain text"}""", "data is the encrypted transcript (fwt1:<base64>)")]
    public async Task TranscriptSave_Refusals_AreTheScriptsUsageErrors(string body, string reason)
    {
        var rig = new Rig();
        var a = await rig.Verbs().TranscriptSave(body, Time);
        Assert.Equal(2, a.Code);
        var o = Parse(a);
        Assert.Equal(("usage", reason), (o["code"]!.ToString(), o["reason"]!.ToString()));
        Assert.Empty(rig.Transcripts.Rows);
    }

    [Fact]
    public async Task TranscriptSave_TooLarge_IsRefusedWithItsLength()
    {
        var data = "fwt1:" + new string('A', 900000);
        var a = await new Rig().Verbs().TranscriptSave(new JsonObject { ["name"] = "a", ["day"] = "20261029", ["data"] = data }.ToJsonString(), Time);
        Assert.Equal(2, a.Code);
        Assert.Equal(("too-large", $"the transcript is {data.Length} characters; at most 900000"), (Parse(a)["code"]!.ToString(), Parse(a)["reason"]!.ToString()));
    }

    [Fact]
    public async Task TranscriptSave_ASessionSealedWithAnotherSecret_OrOfAnotherName_IsRefused()
    {
        var rig = new Rig();
        var (name, data) = Sealed(new Rig { Seal = new TranscriptSeal("another secret") }, lines: ["x"]);
        Assert.Equal(2, (await rig.Verbs().TranscriptSave(new JsonObject { ["name"] = name, ["day"] = "20261029", ["data"] = data }.ToJsonString(), Time)).Code);
        var (_, mine) = Sealed(rig, "cae-1.dev", 12, "x");
        Assert.Equal(2, (await rig.Verbs().TranscriptSave(new JsonObject { ["name"] = "other-name", ["day"] = "20261029", ["data"] = mine }.ToJsonString(), Time)).Code);
        Assert.Empty(rig.Transcripts.Rows);
    }

    [Fact]
    public async Task TheTranscriptVerbs_AnswerRetryable_WhileTheSchemaIsNotApplied_AndWhenTheDatabaseFails()
    {
        var rig = new Rig { Ready = false };
        var (name, data) = Sealed(rig, lines: ["x"]);
        var a = await rig.Verbs().TranscriptSave(new JsonObject { ["name"] = name, ["day"] = "20261029", ["data"] = data }.ToJsonString(), Time);
        Assert.Equal((20, "database-not-ready", 30), (a.Code, Parse(a)["code"]!.ToString(), (int)Parse(a)["retryAfterSeconds"]!));
        Assert.Equal(20, (await rig.Verbs().TranscriptLoad("""{"name":"a"}""", Time)).Code);
        Assert.Equal(20, (await rig.Verbs().TranscriptPrune("{}", Time)).Code);
        rig.Ready = true; rig.Transcripts.Fails = new InvalidOperationException("boom");
        var b = await rig.Verbs().TranscriptSave(new JsonObject { ["name"] = name, ["day"] = "20261029", ["data"] = data }.ToJsonString(), Time);
        Assert.Equal((20, "database-error"), (b.Code, Parse(b)["code"]!.ToString()));
        Assert.DoesNotContain("boom", b.Output);
    }

    [Theory]
    [InlineData("{}", 14)]
    [InlineData("""{"days":3}""", 3)]
    [InlineData("""{"days":0}""", 1)]
    [InlineData("""{"days":9999}""", 365)]
    public async Task TranscriptPrune_ClampsTheDays_AsTheScriptDoes(string body, int expected)
    {
        var rig = new Rig();
        var o = Parse(await rig.Verbs().TranscriptPrune(body, Time));
        Assert.Equal((true, 2, expected), ((bool)o["ok"]!, (int)o["removed"]!, rig.Transcripts.Pruned));
    }

    // ---- sync-board ----
    static BoardReconciler ReconcilerOver(Rig rig, params BoardCard[] cards)
    {
        var snap = new CatalogSnapshot(CatalogSource.SystemJson, T0, "acme", Home, new BoardRef("acme", 1), [new RepositoryEntry(Home, [])], []);
        return new BoardReconciler(new Board(cards), () => snap, j => { rig.Published.Add(j); return Task.FromResult(true); }, () => true);
    }

    sealed class Board(BoardCard[] cards) : IBoardReader
    {
        public Task<IReadOnlyList<BoardCard>> Read(BoardRef b, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BoardCard>>(cards);
    }

    [Fact]
    public async Task SyncBoard_IsTheScriptsSweep_WithTheReconcilersPassAdded()
    {
        var rig = new Rig(); rig.Reconciler = ReconcilerOver(rig, new BoardCard(Home, 5, "Doing", true, "a"), new BoardCard(Home, 6, "Todo", true, "b"));
        var a = await rig.Verbs().SyncBoard("""{"items":[5]}""", Time);
        Assert.Equal(0, a.Code);
        Assert.Equal("""{"items":[5]}""", Assert.Single(rig.Sweeps));            // the script got what the caller sent
        var o = Parse(a);
        Assert.Equal((true, 0), ((bool)o["ok"]!, (int)o["exit"]!));
        Assert.NotNull(o["steps"]); Assert.False((bool)o["incremental"]!);     // and its answer, untouched
        var r = o["reconciler"]!;
        Assert.Equal(("ok", 2, 1, false), (r["outcome"]!.ToString(), (int)r["cards"]!, (int)r["published"]!, (bool)r["dry"]!));
        Assert.Single(rig.Published);
        // a dry sweep: counted, not published
        var dry = Parse(await rig.Verbs().SyncBoard("""{"dry":true}""", Time))["reconciler"]!;
        Assert.Equal((1, true), ((int)dry["published"]!, (bool)dry["dry"]!));   // card 6 is still news
        Assert.Single(rig.Published);
    }

    [Fact]
    public async Task SyncBoard_AFailedSweep_IsReturnedAsItIs_AndNoReconcilerPassFollows()
    {
        var rig = new Rig { SweepAnswer = (20, """{"ok":false,"code":"timeout","exit":20}""") };
        rig.Reconciler = ReconcilerOver(rig, new BoardCard(Home, 5, "Doing", true, "a"));
        var a = await rig.Verbs().SyncBoard("{}", Time);
        Assert.Equal((20, """{"ok":false,"code":"timeout","exit":20}"""), (a.Code, a.Output));
        Assert.Empty(rig.Published);
    }

    [Fact]
    public async Task SyncBoard_WithoutAReconciler_ReturnsTheScriptsAnswerUnchanged()
    {
        var rig = new Rig();
        var a = await rig.Verbs().SyncBoard("{}", Time);
        Assert.Equal(rig.SweepAnswer.Output, a.Output);
    }

    [Fact]
    public async Task SyncBoard_AReconcilerThatThrows_NeverChangesTheSweepsAnswer()
    {
        var rig = new Rig();
        rig.Reconciler = new BoardReconciler(new ThrowingBoard(), () => new CatalogSnapshot(CatalogSource.SystemJson, T0, "acme", Home, new BoardRef("acme", 1), [new RepositoryEntry(Home, [])], []), _ => Task.FromResult(true), () => true);
        var a = await rig.Verbs().SyncBoard("{}", Time);
        Assert.Equal(0, a.Code);
        var o = Parse(a);
        Assert.True((bool)o["ok"]!); Assert.True(o.ContainsKey("reconciler")); Assert.Null(o["reconciler"]);
    }

    sealed class ThrowingBoard : IBoardReader
    {
        public Task<IReadOnlyList<BoardCard>> Read(BoardRef b, CancellationToken ct = default) => throw new InvalidOperationException("no board");
    }
}
