using Xunit;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Qc;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.1: a world for the QC service without GitHub: items with their state comments (revisioned, as Save-State), pull requests with files and reviews, comments, the board, the journal and the inspector's facts.
/// A test says what GitHub says; the service reads and writes it through the ports, as it does through the gateway's clients.
/// </summary>
public sealed class FakeQcWorld : IQcItems, IQcGitHub, IQcFacts
{
    public sealed class Item
    {
        public QcItemInfo Info = null!;
        public JsonObject State = new() { ["schema"] = 1 };
        public int Revision;
        public List<QcComment> Comments = [];
    }

    public readonly Dictionary<int, Item> Items = [];
    public readonly Dictionary<int, JsonObject> Pulls = [];
    public readonly Dictionary<int, List<string>> Files = [];
    public readonly Dictionary<int, List<JsonObject>> ReviewsOf = [];
    public readonly Dictionary<int, List<QcComment>> PullComments = [];
    public readonly List<QcBoardItem> BoardItems = [];
    public readonly List<(int Item, string Event, JsonObject Fields, string Key)> Journals = [];
    public readonly List<string> Writes = [];
    public List<string> Mandatory = [];
    public string Login = "factory-bot";
    long nextComment = 100;

    /// <summary>Runs before a state is saved (a test that moves the state under the writer).</summary>
    public Action<int>? BeforeSave { get; set; }
    public Exception? JournalFault { get; set; }
    /// <summary>Runs (and is waited for) when a state is read: a test that holds a verb inside its lock.</summary>
    public Func<int, Task>? OnRead { get; set; }

    // ---- building the world ---------------------------------------------------------------------------------------------------------------------------

    public static JsonObject Pr(int number, string sha, string headRef, string author = "alice", bool draft = false, string state = "open", string repo = "o/r", bool merged = false, string title = "Trial")
    {
        var pr = new JsonObject
        {
            ["number"] = number, ["state"] = state, ["draft"] = draft, ["title"] = title, ["merged"] = merged, ["node_id"] = "PR_" + number, ["created_at"] = "2026-10-29T08:00:00Z",
            ["user"] = new JsonObject { ["login"] = author },
            ["head"] = new JsonObject { ["sha"] = sha, ["ref"] = headRef, ["repo"] = new JsonObject { ["full_name"] = repo } },
        };
        return pr;
    }

    public const string Sha1 = "1111111111111111111111111111111111111111";
    public const string Sha2 = "2222222222222222222222222222222222222222";

    /// <summary>An item in Functional Testing with the factory label and a PASS verdict for <paramref name="prNumber"/>, whose pull request is on the item's own branch.</summary>
    public (int Item, int Pr) AddValidatedItem(int item = 5, int prNumber = 9, string sha = Sha1, string[]? labels = null, string column = "Functional Testing", string author = "alice", string[]? files = null)
    {
        Items[item] = new Item
        {
            Info = new QcItemInfo(item, true, "Item " + item, labels ?? ["factory worker"], column),
            State = new JsonObject { ["schema"] = 1, ["item"] = item, ["verdict"] = new JsonObject { ["passed"] = true, ["pr"] = prNumber, ["processed"] = false, ["head_sha"] = sha } },
        };
        Pulls[prNumber] = Pr(prNumber, sha, $"factory-worker/{item}-development", author);
        Files[prNumber] = [.. files ?? ["src/a.cs"]];
        BoardItems.Add(new QcBoardItem(item, true, column, labels ?? ["factory worker"]));
        return (item, prNumber);
    }

    public JsonObject State(int item) => Items[item].State;
    public JsonObject? Rec(int item, int pr) => QcRecords.Of(Items[item].State, pr);
    public void Push(int pr, string sha) { Pulls[pr]["head"]!["sha"] = sha; }

    // ---- IQcItems -------------------------------------------------------------------------------------------------------------------------------------

    public Task<QcItemInfo> GetInfo(int item, CancellationToken ct) =>
        Items.TryGetValue(item, out var i) ? Task.FromResult(i.Info) : throw new QcReject(12, "item-not-found", $"issue #{item} not found");

    public async Task<QcItemState> ReadState(int item, CancellationToken ct)
    {
        if (OnRead is { } hook) await hook(item);
        if (!Items.TryGetValue(item, out var i)) throw new QcReject(12, "github-not-found", $"GitHub 404 on GET issues/{item}/comments");
        return new QcItemState { State = (JsonObject)i.State.DeepClone(), Comments = [.. i.Comments], Handle = i.Revision };
    }

    public Task SaveState(int item, QcItemState state, CancellationToken ct)
    {
        BeforeSave?.Invoke(item);
        var i = Items[item];
        if ((int)state.Handle! != i.Revision) throw new QcStateConflictException($"the state of item {item} changed since it was read");
        i.Revision++;
        state.State["item"] = item; state.State["rev"] = i.Revision;
        i.State = (JsonObject)state.State.DeepClone();
        Writes.Add($"STATE #{item} rev {i.Revision}");
        return Task.CompletedTask;
    }

    public Task Journal(int item, string ev, JsonObject fields, string key, CancellationToken ct)
    {
        if (JournalFault is { } f) throw f;
        Journals.Add((item, ev, (JsonObject)fields.DeepClone(), key));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QcBoardItem>> Board(CancellationToken ct) => Task.FromResult<IReadOnlyList<QcBoardItem>>([.. BoardItems]);

    // ---- IQcGitHub ------------------------------------------------------------------------------------------------------------------------------------

    public Task<JsonObject?> GetPull(int number, CancellationToken ct) => Task.FromResult(Pulls.TryGetValue(number, out var p) ? (JsonObject?)p.DeepClone() : null);

    public Task<IReadOnlyList<JsonObject>> OpenPulls(CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonObject>>(Pulls.Values.Where(p => QcJson.Str(p, "state") == "open").Select(p => (JsonObject)p.DeepClone()).ToList());

    public Task<IReadOnlyList<string>> PullFiles(int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Files.TryGetValue(number, out var f) ? [.. f] : []);

    public Task<IReadOnlyList<JsonObject>> Reviews(int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonObject>>(ReviewsOf.TryGetValue(number, out var r) ? [.. r] : []);

    public Task<IReadOnlyList<QcComment>> Comments(int number, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QcComment>>(PullComments.TryGetValue(number, out var c) ? [.. c] : Items.TryGetValue(number, out var i) ? [.. i.Comments] : []);

    public Task PostComment(int number, string body, CancellationToken ct)
    {
        var c = new QcComment(++nextComment, Login, body);
        if (Items.TryGetValue(number, out var i)) i.Comments.Add(c);
        else { if (!PullComments.TryGetValue(number, out var l)) PullComments[number] = l = []; l.Add(c); }
        Writes.Add($"POST issues/{number}/comments");
        return Task.CompletedTask;
    }

    public Task PatchComment(long commentId, string body, CancellationToken ct)
    {
        foreach (var list in Items.Values.Select(i => i.Comments).Concat(PullComments.Values))
        {
            var at = list.FindIndex(c => c.Id == commentId);
            if (at >= 0) { list[at] = list[at] with { Body = body }; Writes.Add($"PATCH issues/comments/{commentId}"); return Task.CompletedTask; }
        }
        throw new QcReject(12, "github-not-found", $"GitHub 404 on PATCH issues/comments/{commentId}");
    }

    public Task<JsonObject> MarkReady(JsonObject pull, CancellationToken ct)
    {
        var n = QcJson.Int(pull, "number");
        Pulls[n]["draft"] = false; Writes.Add($"READY {n}");
        return Task.FromResult((JsonObject)Pulls[n].DeepClone());
    }

    public Task<string> ServerLogin(CancellationToken ct) => Task.FromResult(Login);

    // ---- IQcFacts -------------------------------------------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<string>> MandatoryCodes(int item, int pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([.. Mandatory]);

    // ---- what the offer comments hold -----------------------------------------------------------------------------------------------------------------

    public IEnumerable<QcComment> Offers(int number) =>
        (PullComments.TryGetValue(number, out var c) ? c : Items.TryGetValue(number, out var i) ? i.Comments : []).Where(x => x.Body.Contains("<!-- qc link pr="));
}

/// <summary>The service over a <see cref="FakeQcWorld"/> with a clock the test moves and the settings of the Tier 1 suites.</summary>
public sealed class QcHarness
{
    public static readonly string Secret = "gateway-" + new string('k', 24);   // (no credential: the secret of a test gateway, composed at run time)
    public readonly FakeQcWorld World = new();
    public readonly InMemoryQcIndex Index = new();
    public readonly InMemoryQcEnvironmentStore Table = new();
    public readonly List<string> Log = [];
    public DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
    public QcService Service { get; }
    public QcConfig Config { get; }

    public static string ConfigJson(string mergePolicy = "auto", string qcBlock = "{}", string humanApprovers = "[\"alice\"]") => $$"""
        {
          "name": "factory-worker", "owner": "o", "repo": "r", "project": 7, "label": "factory worker", "rivalLabel": "AI Factory", "autoMergeLabel": "Auto Merge",
          "columns": { "validation": "Functional Testing" }, "mergePolicy": "{{mergePolicy}}", "humanApprovers": {{humanApprovers}},
          "docsOnlyPaths": ["^docs/", "\\.md$"],
          "timing": { "qcLinkDays": 7, "qcGraceMinutes": 30, "qcLifetimeMinutes": 480, "qcMaxRelaunches": 3, "qcTokenHours": 16, "qcBuildCapMinutes": 8 },
          "qc": {{qcBlock}}
        }
        """;

    public QcHarness(string mergePolicy = "auto", string qcBlock = "{}", bool useTable = true, Func<string, string?>? env = null, string? configJson = null, string humanApprovers = "[\"alice\"]")
    {
        Func<string, string?> e = env ?? (n => n switch { "FACTORY_PUBLIC_URL" => "https://gateway.example.test/", "FACTORY_TOKEN_SECRET" => Secret, "FACTORY_OWNER_MODE" => "1", _ => null });
        Config = QcConfig.From(JsonNode.Parse(configJson ?? ConfigJson(mergePolicy, qcBlock, humanApprovers)), e);
        var n = 0;
        Service = new QcService(Config, World, World, World, Index, useTable ? Table : null, () => Now, () => $"nonce{++n}", m => Log.Add(m));
    }

    public async Task<(int Code, JsonObject Out)> Call(string verb, object? body = null)
    {
        var json = body switch { null => "{}", string s => s, JsonObject o => o.ToJsonString(), _ => System.Text.Json.JsonSerializer.Serialize(body) };
        var answer = await Service.Run(verb, json);
        return (answer.Code, (JsonObject)JsonNode.Parse(answer.Output)!);
    }

    /// <summary>Start for the pull request of the item, as the operator does it.</summary>
    public async Task<JsonObject> Start(int item = 5, int pr = 9, bool confirm = true)
    {
        var (code, o) = await Call("qc-request", new { item, pr, @operator = true, confirm });
        Assert.True(code == 0, o.ToJsonString());
        return o;
    }

    /// <summary>The gateway places the job on a worker (qc-transition to assigned) and the worker accepts it.</summary>
    public async Task<string> Place(int item = 5, int pr = 9, string worker = "qw1", string assignment = "a-1", bool accept = true)
    {
        var (code, o) = await Call("qc-transition", new { item, pr, to = "assigned", worker, assignment, mode = "first" });
        Assert.True(code == 0, o.ToJsonString());
        if (accept) await Service.Accept(item, assignment, worker);
        return assignment;
    }

    public async Task Progress(string state, int item = 5, int pr = 9, string assignment = "a-1")
    {
        var (code, o) = await Call("qc-progress", new { item, pr, state, assignment });
        Assert.True(code == 0, o.ToJsonString());
    }

    public async Task<JsonObject> Ready(int item = 5, int pr = 9, string assignment = "a-1", string sha = FakeQcWorld.Sha1, string probe = "verified")
    {
        await Progress("building", item, pr, assignment); await Progress("running", item, pr, assignment);
        var rec = World.Rec(item, pr)!;
        var (code, o) = await Call("qc-report", new { item, pr, assignment, sha, env_id = QcJson.Str(rec, "env_id"), probe, url = "http://qw1.test:5050", port = 5050 });
        Assert.True(code == 0, o.ToJsonString());
        return o;
    }

    public string State(int item = 5, int pr = 9) => QcJson.Str(World.Rec(item, pr), "state");
}
