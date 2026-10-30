using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>
/// W3.2: the journal ledger in C# against a fake of the issue-comment endpoints of GitHub (the same paging, ids and 404s as tests/FakeGitHub). The expected values are the
/// ones plugin/scripts/lib/Journal.ps1 produces: the scenarios of tests/scenarios/journal-fold.ps1 are replayed step by step (same lines, same keys, same asserts), and
/// the order of the GitHub calls is pinned to the order of Add-Journal and Save-State (list the comments, post the line, read the state comment's revision, patch it).
/// </summary>
public sealed class JournalLedgerTests
{
    const string Login = "factory-bot";
    const string Name = "factory-worker";

    // ---- the fake: issue comments ------------------------------------------------------------------------------------------------------------

    sealed record Comment(long Id, string Login, string Body);

    sealed class FakeIssues : HttpMessageHandler
    {
        public readonly Dictionary<int, List<Comment>> Issues = new();
        public readonly List<string> Calls = new();
        long nextId = 1000;
        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept;
        public Action<string>? AfterListing;   // after a list of comments was answered: a write by another process can come here

        public List<Comment> Of(int n) { if (!Issues.TryGetValue(n, out var l)) Issues[n] = l = new(); return l; }
        public Comment Add(int n, string login, string body) { var c = new Comment(nextId++, login, body); Of(n).Add(c); return c; }
        public Comment? Find(long id) => Issues.Values.SelectMany(x => x).FirstOrDefault(c => c.Id == id);
        public void Replace(long id, string body) { foreach (var l in Issues.Values) { var i = l.FindIndex(c => c.Id == id); if (i >= 0) l[i] = l[i] with { Body = body }; } }
        public void Delete(long id) { foreach (var l in Issues.Values) l.RemoveAll(c => c.Id == id); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath; var q = req.RequestUri.Query;
            Calls.Add($"{req.Method} {path}{(q.Contains("page=") ? q : "")}");
            if (Intercept?.Invoke(req) is { } custom) return Task.FromResult(custom);
            var body = req.Content == null ? "" : req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var m = Regex.Match(path, @"^/repos/o/r/issues/(\d+)/comments$");
            if (m.Success)
            {
                var n = int.Parse(m.Groups[1].Value);
                if (req.Method == HttpMethod.Get)
                {
                    var page = int.Parse(Regex.Match(q, @"[?&]page=(\d+)").Groups[1].Value);
                    var slice = Of(n).Skip((page - 1) * 100).Take(100).Select(c => Json(c)).ToArray();
                    var r = Ok(new JsonArray(slice));
                    AfterListing?.Invoke(path);
                    return Task.FromResult(r);
                }
                var c = Add(n, Login, JsonNode.Parse(body)!["body"]!.ToString());
                return Task.FromResult(Ok(Json(c), HttpStatusCode.Created));
            }
            m = Regex.Match(path, @"^/repos/o/r/issues/comments/(\d+)$");
            if (m.Success)
            {
                var id = long.Parse(m.Groups[1].Value); var c = Find(id);
                if (c == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") });
                if (req.Method == HttpMethod.Patch) { Replace(id, JsonNode.Parse(body)!["body"]!.ToString()); c = Find(id)!; }
                return Task.FromResult(Ok(Json(c)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        static JsonObject Json(Comment c) => new() { ["id"] = c.Id, ["user"] = new JsonObject { ["login"] = c.Login }, ["body"] = c.Body };
        static HttpResponseMessage Ok(JsonNode n, HttpStatusCode s = HttpStatusCode.OK) => new(s) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    sealed class Rig
    {
        public readonly FakeIssues Api = new();
        public DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
        public readonly List<string> Log = new();
        public JournalLedger Ledger;
        // every read of the clock moves it by a tick, so two lines in the same instant still have the different ticks the script's keys (the ticks of the write) have
        DateTime Clock() { var t = Now; Now = Now.AddTicks(10); return t; }
        public Rig(double waitingMinutes = 30, IReadOnlyList<string>? patterns = null, IReadOnlyList<string>? stateAuthors = null, IItemLock? itemLock = null)
        {
            var http = new HttpClient(Api) { BaseAddress = new Uri("http://fake/") };
            var factory = new GitHubClientFactory(http, new StaticCredentialSource([], new GitHubCredential("t", "token")), new GitHubBudget());
            var client = factory.For(Contracts.Ids.RepoId.Parse("o/r")).GetAwaiter().GetResult();
            var store = new GitHubJournalStore(client, new JournalIdentity(Name, _ => Task.FromResult(Login), stateAuthors ?? []), Clock, Log.Add);
            Ledger = new JournalLedger(store, new JournalSettings(waitingMinutes, patterns), Clock, Log.Add, itemLock);
        }

        public Task<JournalOutcome> Line(int n, string ev, string key, params (string K, object? V)[] f) =>
            Ledger.Append(n, ev, f.ToDictionary(x => x.K, x => x.V), key);

        public IEnumerable<Comment> Plant(int n) => Api.Of(n).Where(c => c.Body.Contains("<!-- plant event=")).ToList();
        public List<string> Events(int n) => Plant(n).Select(c => Regex.Match(c.Body, "plant event=([a-z-]+)").Groups[1].Value).ToList();
        public JsonObject State(int n)
        {
            var c = Api.Of(n).Single(x => x.Login == Login && x.Body.StartsWith($"<!-- factory-state:{Name} v1 -->"));
            return ItemStateComment.Parse(c.Body)!;
        }
        public JsonObject J(int n) => (JsonObject)State(n)["journal"]!;
        public void SetCount(int n, int count)
        {
            var c = Api.Of(n).Single(x => x.Body.StartsWith($"<!-- factory-state:{Name} v1 -->"));
            var s = ItemStateComment.Parse(c.Body)!; ((JsonObject)s["journal"]!)["count"] = count;
            Api.Replace(c.Id, ItemStateComment.Body(Name, n, s));
        }
    }

    static readonly (string, object?)[] Matched = [("column", "Technical Design"), ("reason", "board-move"), ("trigger", "the sweep")];

    // ---- W9.10l: the lock of the item is the one the caller gives ---------------------------------------------------------------------------------

    sealed class RecordingLock(bool busy = false) : IItemLock
    {
        public readonly List<(int Item, int Wait)> Calls = [];
        public Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct)
        {
            Calls.Add((item, waitSeconds));
            return busy ? throw new ItemBusyException(item) : body();
        }
    }

    [Fact]
    public async Task ALineIsWrittenUnderTheItemLockItIsGiven_WithTheWaitOfUseItemLock()
    {
        var l = new RecordingLock(); var r = new Rig(itemLock: l);
        Assert.True((await r.Line(7, "matched", "m1", Matched)).Posted);
        Assert.Equal([(7, 45)], l.Calls);
        Assert.Single(r.Plant(7));
    }

    [Fact]
    public async Task ABusyItemLock_IsATimeoutTheCommandBaseRetries_AndNothingWasWritten()
    {
        var l = new RecordingLock(busy: true); var r = new Rig(itemLock: l);
        var e = await Assert.ThrowsAsync<TimeoutException>(() => r.Line(7, "matched", "m1", Matched));
        Assert.Contains("another operation on item 7 is running", e.Message);
        Assert.Empty(r.Api.Calls);
        Assert.Equal(Fworker.Integrations.Commands.ErrorAction.Retry, ErrorClassifier.Classify(e).Action);
    }

    [Fact]
    public async Task ACallerThatHoldsTheItemLock_WritesWithoutTakingItAgain_TheLockIsNotReentrant()
    {
        // the keeper and manager verbs run under the item's lock: a line they write must not wait for the lock they hold
        var l = new RecordingLock(busy: true); var r = new Rig(itemLock: l);
        Assert.True((await r.Ledger.AppendHeld(7, "matched", new Dictionary<string, object?>(Matched.ToDictionary(x => x.Item1, x => x.Item2)), "m1")).Posted);
        var holder = new ItemState { State = new JsonObject { ["schema"] = 1, ["item"] = 7 } };
        Assert.True(await r.Ledger.AddTo(7, holder, "keeper-action", new Dictionary<string, object?> { ["verb"] = "set-priority", ["policy"] = "p", ["by"] = "b", ["reason"] = "r", ["detail"] = "d" }, "k1"));
        Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task WithoutALock_TheLinesOfOneItemAreWrittenOneAfterTheOther()
    {
        var l = new InProcessItemLock();
        var inside = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var first = l.Use(3, async () => { inside.SetResult(); await release.Task; return 1; }, 5, default);
        await inside.Task;
        Assert.Equal(2, await l.Use(4, () => Task.FromResult(2), 1, default));   // another item is free
        await Assert.ThrowsAsync<ItemBusyException>(() => l.Use(3, () => Task.FromResult(3), 1, default));
        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(4, await l.Use(3, () => Task.FromResult(4), 1, default));   // released
    }

    // ---- journal-fold.ps1, step by step -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fold_RepeatedLinesEditOneCommentPerKind_AnotherLineEndsTheFold_FoldsDoNotCountAgainstTheCap()
    {
        var r = new Rig(waitingMinutes: 0); const int n = 5;
        foreach (var i in new[] { 1, 2, 3 }) Assert.True((await r.Line(n, "matched", $"m{i}", Matched)).Posted);
        var mc = r.Plant(n).Where(c => c.Body.Contains("plant event=matched")).ToList();
        Assert.Single(mc);   // three matched lines: one comment
        Assert.Contains("matched 3 times", mc[0].Body);
        Assert.Contains("<!-- plant event=matched key=m1 fold=3 -->", mc[0].Body);   // the first line's marker
        Assert.Equal(3, Regex.Matches(mc[0].Body, @"(?m)^> ").Count);
        var rp = await r.Line(n, "matched", "m3", Matched);
        Assert.False(rp.Posted);   // a redelivered line (same key) changes nothing
        Assert.Contains("matched 3 times", r.Plant(n).First().Body);
        foreach (var i in new[] { 1, 2 }) Assert.True((await r.Line(n, "waiting", "", ("reason", $"no session can take it ({i})"))).Posted);
        foreach (var i in new[] { 1, 2 }) Assert.True((await r.Line(n, "reassigned", $"r{i}", ("reason", $"w{i} did not accept in time"))).Posted);
        Assert.True((await r.Line(n, "offered", "o1", ("worker", "w9"), ("type", "persistent-local"), ("ackSeconds", 120))).Posted);
        Assert.True((await r.Line(n, "matched", "m4", Matched)).Posted);
        Assert.Equal("matched,waiting,reassigned,offered", string.Join(',', r.Events(n)));   // an offer does not end the folds
        var w = r.Plant(n).Single(c => c.Body.Contains("plant event=waiting")); var ra = r.Plant(n).Single(c => c.Body.Contains("plant event=reassigned"));
        Assert.Matches(@"Plant manager: waiting: no session can take it \(2\)\.\s", w.Body); Assert.Contains("waiting 2 times", w.Body); Assert.Contains("reassigned 2 times", ra.Body);
        var kinds = (JsonObject)r.J(n)["kinds"]!;
        Assert.Equal(new[] { 4, 2, 2, 1 }, new[] { Cnt(kinds, "matched"), Cnt(kinds, "waiting"), Cnt(kinds, "reassigned"), Cnt(kinds, "offered") });
        Assert.Equal(4, JournalJson.Int(r.J(n), "count"));   // only the new comments count: matched, waiting, reassigned, offered

        // another kind of line ends the folds: the next matched is a new comment after it
        Assert.True((await r.Line(n, "assigned", "a1", ("worker", "w9"), ("column", "Technical Design"), ("mode", "first"), ("attempt", 1), ("lease", "soon"))).Posted);
        Assert.True((await r.Line(n, "matched", "m5", Matched)).Posted);
        Assert.Equal("matched,waiting,reassigned,offered,assigned,matched", string.Join(',', r.Events(n)));
        var fold = (JsonObject)r.J(n)["fold"]!;
        Assert.Equal(["matched"], fold.Select(x => x.Key).ToArray());
        Assert.Equal("m5", fold["matched"]!["key"]!.ToString());

        // the cap: an item one comment short of it
        r.SetCount(n, 29);
        foreach (var i in new[] { 6, 7, 8 }) Assert.True((await r.Line(n, "matched", $"m{i}", Matched)).Posted);
        Assert.Equal(29, JournalJson.Int(r.J(n), "count"));   // folded lines do not count against the cap
        Assert.Equal(6, r.Plant(n).Count());
        Assert.Contains("matched 4 times", r.Plant(n).Last().Body);
        Assert.True((await r.Line(n, "released", "x1", ("worker", "w9"), ("reason", "test"))).Posted);   // the 30th comment
        Assert.True((await r.Line(n, "released", "x2", ("worker", "w9"), ("reason", "test"))).Posted);
        Assert.Contains("journal full (30 entries); later events are not posted here, see the gateway log.", r.Plant(n).Last().Body);   // the 31st is the journal full note
        Assert.Equal(31, JournalJson.Int(r.J(n), "count"));
        Assert.False((await r.Line(n, "released", "x3", ("worker", "w9"), ("reason", "test"))).Posted);   // and nothing after it
        Assert.Equal(8, r.Plant(n).Count());
    }

    // ---- W9.6: Add-Journal with the caller's holder (the keeper and manager verbs) ---------------------------------------------------------

    [Fact]
    public async Task AddTo_PostsTheLineAndUpdatesTheLedgerInTheCallersHolder_AndSavesNothing()
    {
        var r = new Rig();
        var holder = new ItemState { State = new JsonObject { ["schema"] = 1, ["item"] = 7 } };
        var fields = new Dictionary<string, object?> { ["verb"] = "set-priority", ["policy"] = "backlog-low", ["by"] = "backlog-keeper", ["reason"] = "first", ["detail"] = "set its priority to **top** (was normal)" };
        Assert.True(await r.Ledger.AddTo(7, holder, "keeper-action", fields, "set-priority|1"));
        Assert.Equal("\U0001F3ED Plant manager: the backlog keeper (**backlog-keeper**) set its priority to **top** (was normal), under policy `backlog-low`: first.\n\n<!-- plant event=keeper-action key=set-priority|1 -->",
            r.Plant(7).Single().Body);
        var j = (JsonObject)holder.State["journal"]!;
        Assert.Equal(["keeper-action|set-priority|1"], JournalJson.Strings(j, "keys"));
        Assert.Equal(1, JournalJson.Int(j, "count"));
        Assert.Empty(r.Api.Of(7).Where(c => c.Body.StartsWith($"<!-- factory-state:{Name} v1 -->")));   // the caller saves the holder with the state it changed
        Assert.False(await r.Ledger.AddTo(7, holder, "keeper-action", fields, "set-priority|1"));   // a repeat of the key is not written again
        Assert.Single(r.Plant(7));
        await Assert.ThrowsAsync<JournalRefusedException>(() => r.Ledger.AddTo(7, holder, "no-such-event", fields, "k"));
    }

    static int Cnt(JsonObject kinds, string k) => JournalJson.Int((JsonObject)kinds[k]!, "count");

    // ---- the text of the posted comment and of a fold, pinned to the script -----------------------------------------------------------------

    [Fact]
    public async Task APostedLine_IsTheFactoryLineWithItsHiddenMarker()
    {
        var r = new Rig();
        await r.Line(7, "matched", "k1", ("column", "Development"), ("reason", "the factory label"), ("trigger", "label"));
        Assert.Equal("\U0001F3ED Plant manager: matched label to work item #7 in **Development** (the factory label). Needs an owner session.\n\n<!-- plant event=matched key=k1 -->",
            r.Plant(7).Single().Body);
    }

    [Fact]
    public async Task AFoldedComment_SaysHowOftenAndWhen_WithTheLatestFiveLines()
    {
        var r = new Rig();
        for (var i = 1; i <= 7; i++) { r.Now = new DateTime(2026, 10, 27, 9, i, 0, DateTimeKind.Utc); await r.Line(3, "reassigned", $"r{i}", ("reason", $"w{i} did not accept in time")); }
        var body = r.Plant(3).Single().Body;
        Assert.Equal(
            "\U0001F3ED Plant manager: w7 did not accept in time; offering to the next worker.\n\n" +
            "reassigned 7 times, from 2026-10-27 09:01Z to 2026-10-27 09:07Z (the latest 5 below; this comment is edited instead of posting a new one each time).\n\n" +
            "> 2026-10-27 09:03Z w3 did not accept in time; offering to the next worker.\n> 2026-10-27 09:04Z w4 did not accept in time; offering to the next worker.\n" +
            "> 2026-10-27 09:05Z w5 did not accept in time; offering to the next worker.\n> 2026-10-27 09:06Z w6 did not accept in time; offering to the next worker.\n" +
            "> 2026-10-27 09:07Z w7 did not accept in time; offering to the next worker.\n\n<!-- plant event=reassigned key=r1 fold=7 -->", body);
        var fold = r.J(3)["fold"]!["reassigned"]!;
        Assert.Equal(("2026-10-27T09:01:00.000Z", "2026-10-27T09:07:00.000Z", 7), (fold["first"]!.ToString(), fold["last"]!.ToString(), JournalJson.Int((JsonObject)fold, "count")));
    }

    [Fact]
    public async Task ALongLine_IsTruncatedAt1500_AndSecretsAreRedacted()
    {
        var r = new Rig(patterns: ["gh[pousr]_[A-Za-z0-9]{20,}"]);
        var token = "ghp_" + new string('A', 26);   // composed at run time: the secret scan stays meaningful
        await r.Line(2, "skipped", "s1", ("reason", $"token {token} " + new string('x', 2000)));
        var body = r.Plant(2).Single().Body;
        Assert.DoesNotContain(token, body); Assert.Contains("[redacted]", body);
        var line = body["\U0001F3ED Plant manager: ".Length..body.IndexOf("\n\n<!-- plant", StringComparison.Ordinal)];
        Assert.Equal(1500 + " [truncated]".Length, line.Length); Assert.EndsWith(" [truncated]", line);
    }

    // ---- dedup, the waiting rate limit, ci-released ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ARepeatedKey_WritesNothingButTheRead()
    {
        var r = new Rig();
        await r.Line(4, "offered", "o1", ("worker", "w1"), ("type", "persistent-local"), ("ackSeconds", 30));
        r.Api.Calls.Clear();
        var again = await r.Line(4, "offered", "o1", ("worker", "w1"), ("type", "persistent-local"), ("ackSeconds", 30));
        Assert.False(again.Posted);
        Assert.Equal(["GET /repos/o/r/issues/4/comments?per_page=100&page=1"], r.Api.Calls);
    }

    [Fact]
    public async Task Waiting_IsWrittenOncePerWindow_AndSetsWaitingAt()
    {
        var r = new Rig(waitingMinutes: 30);
        Assert.True((await r.Line(8, "waiting", "", ("reason", "no worker"))).Posted);
        Assert.Equal("2026-10-27T09:00:00.000Z", r.J(8)["waiting_at"]!.ToString());
        r.Now = r.Now.AddMinutes(29); r.Api.Calls.Clear();
        var again = await r.Line(8, "waiting", "", ("reason", "no worker"));
        Assert.False(again.Posted); Assert.Equal("rate-limited", again.Reason);
        Assert.Single(r.Api.Calls);   // only the state read
        r.Now = r.Now.AddMinutes(2);
        Assert.True((await r.Line(8, "waiting", "", ("reason", "no worker"))).Posted);
        Assert.Equal("2026-10-27T09:31:00.000Z", r.J(8)["waiting_at"]!.ToString());
    }

    [Fact]
    public async Task CiReleased_IsMachineReadStateOnly_NoComment_TheLast20()
    {
        var r = new Rig();
        for (var i = 1; i <= 22; i++) { r.Now = r.Now.AddMinutes(1); var o = await r.Line(6, "ci-released", $"c{i}", ("run", i), ("pr", 9), ("sha7", "abc1234"), ("branch", "b")); Assert.True(o.Recorded); Assert.False(o.Posted); }
        Assert.Empty(r.Plant(6));
        var j = r.J(6);
        var rel = JournalJson.Strings(j, "ciReleased");
        Assert.Equal(20, rel.Length);
        Assert.Equal("2026-10-27 09:03Z released workflow run 3 of PR #9 (head `abc1234`, branch `b`).", rel[0]);
        Assert.Equal(22, JournalJson.Int(j, "count")); Assert.Equal(22, Cnt((JsonObject)j["kinds"]!, "ci-released"));
        var again = await r.Line(6, "ci-released", "c22", ("run", 22));
        Assert.False(again.Recorded);
    }

    [Fact]
    public async Task AnUnknownEvent_IsRefused()
    {
        var r = new Rig();
        await Assert.ThrowsAsync<JournalRefusedException>(() => r.Line(1, "promoted", "k"));
        Assert.Empty(r.Api.Calls);
    }

    // ---- the cap rules ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLinesAPersonLooksFor_TakeFourSlotsBeyondTheCap_AndALaunchWithATranscriptIsAlwaysPosted()
    {
        var r = new Rig(); const int n = 11;
        await r.Line(n, "released", "x0", ("worker", "w"), ("reason", "r")); r.SetCount(n, 33);
        Assert.True((await r.Line(n, "qc-ready", "q1", ("pr", 1), ("worker", "w"), ("attempt", 1))).Posted);   // count 33 < 30 + 4
        r.SetCount(n, 34);
        Assert.False((await r.Line(n, "qc-ready", "q2", ("pr", 1), ("worker", "w"), ("attempt", 1))).Posted);
        Assert.False((await r.Line(n, "released", "x1", ("worker", "w"), ("reason", "r"))).Posted);
        Assert.True((await r.Line(n, "launched", "l1", ("runtime", "octopus"), ("worker", "w"), ("phase", "Development"), ("task", "T1"),
            ("transcript", "https://gw.test/sessions/w"), ("url", "https://octo.test/t/1"))).Posted);
        Assert.Equal("\U0001F3ED Plant manager: octopus started **w** for **Development** (task `T1`). [session](https://gw.test/sessions/w) ([Octopus task](https://octo.test/t/1))\n\n<!-- plant event=launched key=l1 -->",
            r.Plant(n).Last().Body);
    }

    // ---- the order of the calls ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCallsOfALine_AreTheScriptsCallsInTheScriptsOrder()
    {
        var r = new Rig();
        await r.Line(9, "offered", "o1", ("worker", "w1"), ("type", "t"), ("ackSeconds", 1));
        // a first line: Get-State (list), the journal comment, Save-State creates the state comment
        Assert.Equal(["GET /repos/o/r/issues/9/comments?per_page=100&page=1", "POST /repos/o/r/issues/9/comments", "POST /repos/o/r/issues/9/comments"], r.Api.Calls);
        var stateId = r.Api.Of(9)[1].Id; var lineId = r.Api.Of(9)[0].Id;
        Assert.StartsWith("<!-- factory-state:", r.Api.Of(9)[1].Body);
        r.Api.Calls.Clear();
        await r.Line(9, "matched", "m1", Matched);
        await r.Line(9, "matched", "m2", Matched);
        // a new line: list, post, read the state comment (revision), patch it; a fold: list, patch the folded comment, read the state comment, patch it
        Assert.Equal([
            "GET /repos/o/r/issues/9/comments?per_page=100&page=1", "POST /repos/o/r/issues/9/comments", $"GET /repos/o/r/issues/comments/{stateId}", $"PATCH /repos/o/r/issues/comments/{stateId}",
            "GET /repos/o/r/issues/9/comments?per_page=100&page=1", $"PATCH /repos/o/r/issues/comments/{r.Api.Of(9)[2].Id}", $"GET /repos/o/r/issues/comments/{stateId}", $"PATCH /repos/o/r/issues/comments/{stateId}"], r.Api.Calls);
        Assert.Equal(3, JournalJson.Int(r.State(9), "rev"));   // raised by every save: three saves
        Assert.NotEqual(lineId, stateId);
    }

    [Fact]
    public async Task APageOf100Comments_IsFollowedByTheNextPage()
    {
        var r = new Rig();
        for (var i = 0; i < 100; i++) r.Api.Add(12, "someone", $"c{i}");
        await r.Line(12, "offered", "o1", ("worker", "w1"), ("type", "t"), ("ackSeconds", 1));
        Assert.Contains("GET /repos/o/r/issues/12/comments?per_page=100&page=1", r.Api.Calls);
        Assert.Contains("GET /repos/o/r/issues/12/comments?per_page=100&page=2", r.Api.Calls);
    }

    // ---- failures ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFoldWhoseCommentIsGone_IsReplacedByANewComment_AndTheFoldStartsAgain()
    {
        var r = new Rig();
        await r.Line(20, "matched", "m1", Matched);
        var first = r.Plant(20).Single(); r.Api.Delete(first.Id);
        Assert.True((await r.Line(20, "matched", "m2", Matched)).Posted);
        var now = r.Plant(20).Single();
        Assert.NotEqual(first.Id, now.Id); Assert.Contains("key=m2 -->", now.Body); Assert.DoesNotContain("times", now.Body);
        Assert.Equal(2, JournalJson.Int(r.J(20), "count"));
        Assert.Equal(now.Id, r.J(20)["fold"]!["matched"]!["id"]!.GetValue<long>());
    }

    [Fact]
    public async Task AFoldThatCannotBeEdited_403_IsReplacedByANewComment()
    {
        var r = new Rig();
        await r.Line(21, "matched", "m1", Matched);
        var foldId = r.Plant(21).Single().Id;
        r.Api.Intercept = req => req.Method == HttpMethod.Patch && req.RequestUri!.AbsolutePath.EndsWith($"/{foldId}")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"Resource not accessible by integration\"}") } : null;
        Assert.True((await r.Line(21, "matched", "m2", Matched)).Posted);
        Assert.Equal(2, r.Plant(21).Count());
    }

    [Fact]
    public async Task AServerError_OnTheFoldEdit_FailsTheLineAndLeavesTheLedgerAlone()
    {
        var r = new Rig();
        await r.Line(22, "matched", "m1", Matched);
        var before = r.Api.Of(22).Select(c => c.Body).ToList();
        r.Api.Intercept = req => req.Method == HttpMethod.Patch ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null;
        await Assert.ThrowsAsync<HttpRequestException>(() => r.Line(22, "matched", "m2", Matched));
        r.Api.Intercept = null;
        Assert.Equal(before, r.Api.Of(22).Select(c => c.Body).ToList());
        Assert.True((await r.Line(22, "matched", "m2", Matched)).Posted);   // and the repeat of the line works
        Assert.Contains("matched 2 times", r.Plant(22).Single().Body);
    }

    [Fact]
    public async Task ARateLimit_IsRetryable_AndAForbiddenPost_IsNot()
    {
        var r = new Rig();
        r.Api.Intercept = req => req.Method == HttpMethod.Post ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") } : null;
        await Assert.ThrowsAsync<HttpRequestException>(() => r.Line(23, "offered", "o1", ("worker", "w")));
        r.Api.Intercept = req => req.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"nope\"}") } : null;
        await Assert.ThrowsAnyAsync<Exception>(() => r.Line(23, "offered", "o1", ("worker", "w")));
        Assert.False(r.Api.Of(23).Any());
    }

    [Fact]
    public async Task AStateConflict_ReadsAgainAndRepeats_WithoutPostingTheCommentTwice()
    {
        var r = new Rig();
        await r.Line(30, "offered", "o0", ("worker", "w"), ("type", "t"), ("ackSeconds", 1));
        var stateId = r.Api.Of(30).Single(c => c.Body.StartsWith("<!-- factory-state:")).Id;
        var bumped = false;
        // another process saves the state between this line's read and its save: the revision moves once
        r.Api.AfterListing = _ =>
        {
            if (bumped) return; bumped = true;
            var s = ItemStateComment.Parse(r.Api.Find(stateId)!.Body)!; s["rev"] = JournalJson.Int(s, "rev") + 5;
            r.Api.Replace(stateId, ItemStateComment.Body(Name, 30, s));
        };
        // the line's own read saw the old revision; the check right before its save sees the new one
        Assert.True((await r.Line(30, "released", "x1", ("worker", "w"), ("reason", "r"))).Posted);
        Assert.Single(r.Plant(30), c => c.Body.Contains("plant event=released"));
        Assert.Equal(2, JournalJson.Int(r.J(30), "count"));
    }

    [Fact]
    public async Task AStateThatAnEarlierIdentityWrote_IsAdoptedAndRewrittenAsTheGatewaysOwn()
    {
        var r = new Rig(stateAuthors: ["old-login"]);
        var s = new JsonObject { ["schema"] = 1, ["item"] = 40, ["journal"] = new JsonObject { ["keys"] = new JsonArray("offered|o0"), ["count"] = 1 } };
        r.Api.Add(40, "old-login", ItemStateComment.Body(Name, 40, s));
        Assert.False((await r.Line(40, "offered", "o0", ("worker", "w"))).Posted);   // the adopted ledger knows the key
        Assert.True((await r.Line(40, "offered", "o1", ("worker", "w"))).Posted);
        Assert.Equal(2, r.Api.Of(40).Count(c => c.Body.StartsWith("<!-- factory-state:")));   // its own state comment next to the old one
        Assert.Equal(2, JournalJson.Int(r.J(40), "count"));
    }

    [Fact]
    public async Task OnlyTheGatewaysOwnStateComment_Counts()
    {
        var r = new Rig();
        var forged = new JsonObject { ["journal"] = new JsonObject { ["keys"] = new JsonArray("offered|o1"), ["count"] = 30 } };
        r.Api.Add(41, "someone-else", ItemStateComment.Body(Name, 41, forged));
        Assert.True((await r.Line(41, "offered", "o1", ("worker", "w"))).Posted);
    }

    [Fact]
    public async Task LinesOfOneItem_RunOneAfterTheOther_NoneIsLost()
    {
        var r = new Rig();
        await Task.WhenAll(Enumerable.Range(1, 12).Select(i => r.Line(50, "released", $"x{i}", ("worker", "w"), ("reason", $"r{i}"))));
        Assert.Equal(12, r.Plant(50).Count());
        Assert.Equal(12, JournalJson.Int(r.J(50), "count"));
        Assert.Equal(12, JournalJson.Strings(r.J(50), "keys").Length);
    }

    [Fact]
    public async Task TheKeysKept_AreTheLast200()
    {
        var r = new Rig();
        for (var i = 0; i < 205; i++) r.Now = r.Now.AddSeconds(1);
        var s = new JsonObject { ["journal"] = new JsonObject { ["keys"] = new JsonArray(Enumerable.Range(0, 200).Select(i => (JsonNode?)JsonValue.Create($"offered|k{i}")).ToArray()), ["count"] = 3 } };
        r.Api.Add(60, Login, ItemStateComment.Body(Name, 60, s));
        await r.Line(60, "offered", "k200", ("worker", "w"));
        var keys = JournalJson.Strings(r.J(60), "keys");
        Assert.Equal(200, keys.Length); Assert.Equal("offered|k1", keys[0]); Assert.Equal("offered|k200", keys[^1]);
    }

    // ---- the state's budget ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AStateOverTheBudget_LosesItsOldestPromoteHealthFlowAndLineage_AndRecordsTheTrim()
    {
        var s = new JsonObject
        {
            ["item"] = 1, ["promote_health"] = new JsonObject { ["tdd|1.0.1"] = Array(300) }, ["flow"] = Array(300), ["lineage"] = Array(300),
            ["journal"] = new JsonObject { ["keys"] = new JsonArray(), ["count"] = 0 },
        };
        Assert.True(ItemStateComment.Body(Name, 1, s).Length > ItemStateComment.Budget);
        ItemStateComment.Limit(Name, 1, s, new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc));
        Assert.True(ItemStateComment.Body(Name, 1, s).Length <= ItemStateComment.Budget);
        var trims = (JsonArray)((JsonObject)s["journal"]!)["trims"]!;
        Assert.Single(trims); Assert.Equal("2026-10-27T09:00:00.000Z", trims[0]!["at"]!.ToString());
        Assert.True(trims[0]!["dropped"]!["flow"]!.GetValue<int>() > 0);
        static JsonArray Array(int n) => new(Enumerable.Range(0, n).Select(i => (JsonNode?)JsonValue.Create(new string('z', 150) + i)).ToArray());
    }

    [Fact]
    public void AStateThatCannotBeTrimmedToTheBudget_IsRefused()
    {
        var s = new JsonObject { ["item"] = 1, ["big"] = new string('y', 50000) };
        Assert.Throws<JournalRefusedException>(() => ItemStateComment.Limit(Name, 1, s, DateTime.UtcNow));
    }

    // ---- the command ------------------------------------------------------------------------------------------------------------------------

    static WriteJournalLine Cmd(int item, string ev, string key, JsonObject f) => new(JournalKeys.For(item, ev, key), item, ev, f, key);

    [Fact]
    public async Task TheCommand_WritesTheLine_AndAnswersWithTheMessage_AReplayDoesNotWriteAgain()
    {
        var r = new Rig();
        var h = new WriteJournalLineHandler(new InMemoryIdempotencyLedger(), r.Ledger);
        var c = Cmd(70, "offered", "off|a1|w1", new JsonObject { ["worker"] = "w1", ["type"] = "persistent-local", ["ackSeconds"] = 120.0 });
        var o = await h.Process(c);
        Assert.True(o.Message!.Posted); Assert.False(o.Retry);
        Assert.Contains("offered to **w1** (persistent-local); waiting up to 120 s for it to accept.", r.Plant(70).Single().Body);
        var calls = r.Api.Calls.Count;
        var again = await h.Process(c);
        Assert.True(again.Message!.Posted); Assert.Equal(calls, r.Api.Calls.Count);   // answered from the idempotency ledger
        // and a new command with the same line key (a restart lost the in-memory ledger) is stopped by the state's ledger
        var h2 = new WriteJournalLineHandler(new InMemoryIdempotencyLedger(), r.Ledger);
        var replay = await h2.Process(c);
        Assert.False(replay.Message!.Posted); Assert.Single(r.Plant(70));
    }

    [Fact]
    public async Task TheCommand_ATransientFailureIsRetried_AnUnsupportedEventGoesToTheSaga()
    {
        var r = new Rig();
        var h = new WriteJournalLineHandler(new InMemoryIdempotencyLedger(), r.Ledger);
        r.Api.Intercept = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var o = await h.Process(Cmd(71, "offered", "k", new JsonObject { ["worker"] = "w" }));
        Assert.True(o.Retry); Assert.Equal(FailureKind.Transient, o.Result.Failure);
        r.Api.Intercept = null;
        var bad = await h.Process(Cmd(71, "promoted", "k", new JsonObject()));
        Assert.False(bad.Retry); Assert.Equal(FailureKind.Permanent, bad.Result.Failure); Assert.Equal("WriteJournalLine", bad.Failed!.CommandName);
        Assert.True((await h.Process(Cmd(71, "offered", "k", new JsonObject { ["worker"] = "w" }))).Message!.Posted);   // the retried command then works
    }

    [Fact]
    public void FieldsOfACommand_ReadLikeTheScriptsHashtable()
    {
        var f = JournalLedger.FieldsOf(JsonNode.Parse("""{"Worker":"w","n":3,"d":1.5,"b":false,"z":null,"o":{"a":1}}""")!.AsObject());
        Assert.Equal("w", f["worker"]); Assert.Equal(3L, f["N"]); Assert.Equal(1.5, f["d"]); Assert.Equal(false, f["b"]); Assert.Null(f["z"]); Assert.Equal("{\"a\":1}", f["o"]);
    }

    [Fact]
    public void TheIdempotencyKey_IsOnePerItemEventAndKey()
    {
        Assert.Equal(JournalKeys.For(5, "offered", "k"), JournalKeys.For(5, "offered", "k"));
        Assert.NotEqual(JournalKeys.For(5, "offered", "k"), JournalKeys.For(6, "offered", "k"));
        Assert.NotEqual(JournalKeys.For(5, "offered", "k"), JournalKeys.For(5, "matched", "k"));
        Assert.NotEqual(JournalKeys.For(5, "offered", "k"), JournalKeys.For(5, "offered", "k2"));
    }
}
