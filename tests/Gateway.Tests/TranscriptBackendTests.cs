using Fworker.Contracts.Seams;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Transcripts;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.8 (gateway/TranscriptBackends.cs): the transcript store saves to a backend. The database backend keeps the session as a row with the qualified item, and nothing else (V3.3, slice 6: the ref store is no longer its
/// fallback); the rule-script backend is what the store does where the engine does not act (sealed, capped, the tree of the last save sent along). TranscriptServiceTests pins the store's loop and its final flush.
/// </summary>
public sealed class TranscriptBackendTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly TimeSpan Time = TimeSpan.FromSeconds(30);

    sealed class MemoryBackend : ITranscriptBackend
    {
        public readonly Dictionary<string, JsonObject> Docs = new();
        public readonly List<string> Calls = [];
        public bool SaveOk = true; public int? Pruned;
        public Task<bool> Save(JsonObject doc) { Calls.Add("save " + doc["name"]); if (SaveOk) Docs[doc["name"]!.ToString()] = (JsonObject)doc.DeepClone(); return Task.FromResult(SaveOk); }
        public Task<JsonObject?> Load(string name) { Calls.Add("load " + name); return Task.FromResult(Docs.TryGetValue(name, out var d) ? (JsonObject)d.DeepClone() : null); }
        public Task Prune(int days) { Calls.Add("prune"); Pruned = days; return Task.CompletedTask; }
    }

    sealed class MemoryRepository : ITranscriptRepository
    {
        public readonly Dictionary<string, TranscriptDoc> Rows = new(); public int Pruned = -1;
        public Task Save(TranscriptDoc doc, CancellationToken ct = default) { Rows[doc.Name] = doc; return Task.CompletedTask; }
        public Task<TranscriptDoc?> Load(string name, CancellationToken ct = default) => Task.FromResult(Rows.GetValueOrDefault(name));
        public Task<int> Prune(int days, CancellationToken ct = default) { Pruned = days; return Task.FromResult(0); }
        public Task<IReadOnlyList<string>> SessionsOf(string itemKey, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    static TranscriptStore StoreOver(ITranscriptBackend backend)
    {
        var s = new TranscriptStore(null, "");
        s.UseBackend(backend);
        return s;
    }

    // ---- the store over a backend ----
    [Fact]
    public async Task AStoreWithoutABackend_IsNotDurable_AndSavesNothing()
    {
        var s = new TranscriptStore(null, "");
        Assert.False(s.Durable);
        await s.Append("w1", ["a"]);
        await s.Flush(true, Time);
        Assert.Equal(1, (int)s.Status()["dirty"]!);
        s.UseBackend(new MemoryBackend());
        Assert.True(s.Durable);
    }

    [Fact]
    public async Task AFlush_HandsTheBackendTheSessionsDocument_AndAFailedSaveStaysDirty()
    {
        var b = new MemoryBackend();
        var s = StoreOver(b);
        s.Started("w1", 12, "develop", "octopus");
        await s.Append("w1", ["a", "b", "c"]);
        b.SaveOk = false;
        await s.Flush(true, Time);
        Assert.Equal((1, 1L), ((int)s.Status()["dirty"]!, (long)s.Status()["saveFailures"]!));
        b.SaveOk = true;
        await s.Flush(true, Time);
        Assert.Equal((0, 1L), ((int)s.Status()["dirty"]!, (long)s.Status()["saves"]!));
        var d = b.Docs["w1"];
        Assert.Equal((1, "w1", 12, "develop", "octopus", 3L, 0L), ((int)d["v"]!, d["name"]!.ToString(), (int)d["item"]!, d["phase"]!.ToString(), d["runtime"]!.ToString(), (long)d["total"]!, (long)d["dropped"]!));
        Assert.Equal(["a", "b", "c"], d["lines"]!.AsArray().Select(x => x!.ToString()).ToArray());
        Assert.Equal(DateTime.UtcNow.ToString("yyyyMMdd"), d["day"]!.ToString());
    }

    [Fact]
    public async Task ASessionAFreshStoreDoesNotHold_IsReadBackFromTheBackend_AndGrowsFromThere()
    {
        var b = new MemoryBackend();
        var first = StoreOver(b);
        first.Started("w1", 12, "develop", "octopus");
        await first.Append("w1", ["a", "b"]);
        first.Ended("w1");
        await first.Flush(true, Time);
        var second = StoreOver(b);   // a restart
        var r = (await second.Read("w1", 0))!;
        Assert.Equal((12, "develop", "octopus", 2L, false), ((int)r["item"]!, r["phase"]!.ToString(), r["runtime"]!.ToString(), (long)r["total"]!, (bool)r["live"]!));
        Assert.NotNull(r["ended"]);
        await second.Append("w1", ["c"]);
        Assert.Equal(3L, (long)(await second.Read("w1", 2))!["total"]!);
        Assert.Null(await second.Read("nobody", 0));
    }

    [Fact]
    public async Task ThePrune_RunsOnceInTheFirstFlush_WithTheRetentionDays()
    {
        var b = new MemoryBackend();
        var s = StoreOver(b); s.Days = 9;
        await s.Flush(false, Time);
        Assert.Equal(9, b.Pruned);
        Assert.Single(b.Calls, "prune");
        await s.Flush(false, Time);
        Assert.Single(b.Calls, "prune");   // every six hours, not every flush
    }

    // ---- the database backend ----
    static JsonObject Doc(string name = "w1", int item = 12, bool ended = true) => new()
    {
        ["v"] = 1, ["name"] = name, ["item"] = item, ["phase"] = "develop", ["runtime"] = "octopus", ["day"] = "20261029", ["started"] = "2026-10-29T08:00:00.0000000Z",
        ["ended"] = ended ? "2026-10-29T08:10:00.0000000Z" : null, ["lastLine"] = "2026-10-29T08:09:00.0000000Z", ["dropped"] = 3, ["total"] = 5, ["lines"] = new JsonArray("x", "y"),
    };

    [Fact]
    public void TheRow_QualifiesTheItemWithTheHomeRepository_AndTheDocumentIsRebuiltFromIt()
    {
        var row = DatabaseTranscriptBackend.ToRow(Doc(), Home);
        Assert.Equal(("w1", "acme/orders#12", "octopus", "develop", 3L, 5L), (row.Name, row.ItemKey, row.Runtime, row.Phase, row.Dropped, row.Total));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 8, 10, 0, TimeSpan.Zero), row.Ended);
        Assert.Equal(["x", "y"], row.Lines);
        var back = DatabaseTranscriptBackend.ToDoc(row);
        Assert.Equal((12, "20261029", 3L, 5L), ((int)back["item"]!, back["day"]!.ToString(), (long)back["dropped"]!, (long)back["total"]!));
        Assert.Equal(row.Started, DateTimeOffset.Parse(back["started"]!.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Equal(["x", "y"], back["lines"]!.AsArray().Select(x => x!.ToString()).ToArray());
    }

    [Fact]
    public void ASessionOfNoItem_HasNoItemKey_AndAnOpenOneNoEnd()
    {
        var row = DatabaseTranscriptBackend.ToRow(Doc(item: 0, ended: false), Home);
        Assert.Null(row.ItemKey); Assert.Null(row.Ended);
        var back = DatabaseTranscriptBackend.ToDoc(row);
        Assert.Equal(0, (int)back["item"]!); Assert.Null(back["ended"]);
        Assert.Null(DatabaseTranscriptBackend.ToRow(Doc(), null).ItemKey);   // no home repository: the item cannot be qualified
    }

    [Fact]
    public async Task TheDatabaseBackend_SavesRows_AndASessionItDoesNotHave_IsAMiss()
    {
        var repo = new MemoryRepository();
        var b = new DatabaseTranscriptBackend(repo, Home, () => true);
        Assert.True(await b.Save(Doc("w1")));
        Assert.True(repo.Rows.ContainsKey("w1"));
        Assert.Equal("w1", (await b.Load("w1"))!["name"]!.ToString());
        Assert.Null(await b.Load("old"));   // a session of the ref store, written before the move: not read (V3.3, slice 6)
    }

    [Fact]
    public async Task TheDatabaseBackend_BeforeTheSchemaIsApplied_SavesNothing_ReadsNothing_AndPrunesNothing()
    {
        var repo = new MemoryRepository();
        await repo.Save(DatabaseTranscriptBackend.ToRow(Doc("w0"), Home));
        var b = new DatabaseTranscriptBackend(repo, Home, () => false);
        Assert.False(await b.Save(Doc("w1")));
        Assert.False(repo.Rows.ContainsKey("w1"));
        Assert.Null(await b.Load("w0"));
        await b.Prune(14);
        Assert.Equal(-1, repo.Pruned);
    }

    [Fact]
    public async Task ThePrune_ClearsTheDatabase_WithTheRetentionDays()
    {
        var repo = new MemoryRepository();
        await new DatabaseTranscriptBackend(repo, Home, () => true).Prune(7);
        Assert.Equal(7, repo.Pruned);
    }

    // ---- the rule-script backend ----
    [Fact]
    public async Task TheRuleScriptBackend_SealsTheDocument_SendsTheTreeBack_AndOpensWhatItLoads()
    {
        var seal = new TranscriptSeal("backend-tests-secret");
        var calls = new List<(string Verb, JsonObject Args)>();
        var stored = "";
        var b = new RuleScriptTranscriptBackend((verb, stdin, _) =>
        {
            var args = (JsonObject)JsonNode.Parse(stdin)!; calls.Add((verb, args));
            if (verb == "transcript-save") { stored = args["data"]!.ToString(); return Task.FromResult((0, """{"ok":true,"tree":"abc1234"}""")); }
            return Task.FromResult(verb == "transcript-load" ? (0, new JsonObject { ["ok"] = true, ["found"] = true, ["data"] = stored }.ToJsonString()) : (0, """{"ok":true}"""));
        }, seal);
        Assert.True(await b.Save(Doc()));
        Assert.True(await b.Save(Doc()));
        Assert.False(calls[0].Args.ContainsKey("tree")); Assert.Equal("abc1234", calls[1].Args["tree"]!.ToString());
        Assert.Equal(("w1", "20261029"), (calls[0].Args["name"]!.ToString(), calls[0].Args["day"]!.ToString()));
        Assert.StartsWith("fwt1:", stored);
        Assert.Equal("w1", (await b.Load("w1"))!["name"]!.ToString());
        await b.Prune(5);
        Assert.Equal(5, (int)calls[^1].Args["days"]!);
    }

    [Fact]
    public async Task TheRuleScriptBackend_AStoredCopyOverTheCap_LeavesOutTheOldestLines()
    {
        var seal = new TranscriptSeal("backend-tests-secret");
        string stored = "";
        var b = new RuleScriptTranscriptBackend((verb, stdin, _) => { stored = JsonNode.Parse(stdin)!["data"]!.ToString(); return Task.FromResult((0, "{}")); }, seal);
        var rnd = new Random(7);
        var lines = Enumerable.Range(0, 400).Select(i => Convert.ToBase64String(Enumerable.Range(0, 3000).Select(_ => (byte)rnd.Next(256)).ToArray())).ToArray();   // incompressible: more than 1 MB sealed
        var doc = Doc(); doc["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()); doc["total"] = lines.Length; doc["dropped"] = 0;
        Assert.True(await b.Save(doc));
        Assert.True(stored.Length <= 880000, "stored " + stored.Length);
        var kept = seal.Open(stored)!;
        var n = kept["lines"]!.AsArray().Count;
        Assert.True(n is > 0 and < 400);
        Assert.Equal(400 - n, (int)(long)kept["dropped"]!);                                                          // the ones left out are counted as dropped
        Assert.Equal(lines[^1], kept["lines"]!.AsArray()[^1]!.ToString());                                            // the newest are kept
    }

    [Fact]
    public async Task TheRuleScriptBackend_ALoadOfAnotherSecretsData_IsAMiss()
    {
        var other = new TranscriptSeal("another").Seal(Doc());
        var b = new RuleScriptTranscriptBackend((verb, _, _) => Task.FromResult((0, new JsonObject { ["found"] = true, ["data"] = other }.ToJsonString())), new TranscriptSeal("mine"));
        Assert.Null(await b.Load("w1"));
    }

    // ---- W9.9a: where the engine does not act, the ref store is the rule script, not a route ----

    [Fact]
    public void TheComposition_BuildsTheRuleScriptBackendOnTheScriptPath_NotOnTheRoutedOne()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "gateway", "Composition", "GatewayServiceCollectionExtensions.cs"));
        Assert.Contains("new TranscriptStore(transcriptsDurable ? runner.RunScriptAsync : null", text);
        Assert.DoesNotContain("new TranscriptStore(transcriptsDurable ? runner.RunAsync", text);
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
