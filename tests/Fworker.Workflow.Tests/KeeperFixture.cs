using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Keeper;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.6: the world the keeper and manager verbs run in, in memory: a repository with issues, a board, pull requests and workflow runs; the state comments and the journal; the locks; the saga. It does what GitHub does for the calls the
/// verbs make (an issue created has the next number, a label added is on the issue), keeps every write in order for the tests to read, and refuses the way the ports refuse.
/// </summary>
public sealed class KeeperWorld : IKeeperGitHub, IItemStateStore, IVerbLocks, IKeeperSaga
{
    public sealed class Issue
    {
        public int Number; public string Title = ""; public string Body = ""; public bool Open = true; public List<string> Labels = []; public string? Card; public string? Column;
        public string NodeId => "I_" + Number;
    }

    public sealed class Holder(JsonObject state) : StateHolder { public override JsonObject State { get; } = state; }

    public DateTime Clock = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    public readonly Dictionary<int, Issue> Issues = [];
    public readonly Dictionary<int, JsonObject> States = [];
    public readonly List<string> Writes = [];
    public readonly List<string> Journal = [];
    public readonly List<JsonObject> Pulls = [];
    public readonly List<JsonObject> Runs = [];
    public readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase) { "Technical Design", "Todo", "Development" };
    public readonly List<int> LocksTaken = [];
    public int IntakeLocks;
    public readonly List<string> SagaCalls = [];
    public readonly HashSet<int> SagaOwned = [];
    public readonly Dictionary<long, int> RerunStatus = [];
    public int NextNumber = 100;

    public FactoryRules Rules = FactoryRules.From(JsonNode.Parse("""
        { "name": "factory-worker", "owner": "acme", "repo": "work", "project": 1, "label": "factory", "autoMergeLabel": "Auto Merge", "testLabel": "test", "videoLabel": "video",
          "requiredBodySection": { "Technical Design": "## Technical design", "Development": "## Development" },
          "columns": { "ai": ["Technical Design", "Development"] } }
        """), _ => null);

    public VerbContext Cx;
    public KeeperVerbs Verbs;

    public KeeperWorld()
    {
        Cx = new VerbContext(Rules, this, this, this, this, () => Clock);
        Verbs = new KeeperVerbs(Cx);
    }

    public Issue Add(int number, string title, string body, string column = "Technical Design", params string[] labels)
    {
        var i = new Issue { Number = number, Title = title, Body = body, Column = column, Card = "C_" + number, Labels = ["factory", .. labels] };
        Issues[number] = i; return i;
    }

    public JsonObject State(int item) => States.TryGetValue(item, out var s) ? s : States[item] = new JsonObject { ["schema"] = 1, ["item"] = item };

    public async Task<JsonObject> Run(string verb, object body, int exit = 0)
    {
        var answer = await Verbs.Run(verb, System.Text.Json.JsonSerializer.Serialize(body), TimeSpan.FromSeconds(30));
        Assert.Equal(exit, answer.Code);
        var o = JsonNode.Parse(answer.Output)!.AsObject();
        Assert.Equal(exit, o["exit"]!.GetValue<int>());
        Assert.Equal(verb, o["verb"]!.ToString());
        return o;
    }

    public async Task<JsonObject> Refused(string verb, object body, int exit, string code)
    {
        var o = await Run(verb, body, exit);
        Assert.False(o["ok"]!.GetValue<bool>());
        Assert.Equal(code, o["code"]!.ToString());
        return o;
    }

    // ---- IKeeperGitHub ----------------------------------------------------------------------------------------------------------------------

    static VerbRejection NotFound(string path) => new(12, "github-not-found", $"GitHub 404 on GET {path}");

    ItemInfo Info(Issue i) => new(i.Number, i.NodeId, i.Open, i.Title, i.Body, i.Labels.ToList(), i.Card, i.Column);

    public Task<ItemInfo> GetItemInfo(int item) => Issues.TryGetValue(item, out var i) ? Task.FromResult(Info(i)) : throw new VerbRejection(12, "item-not-found", $"issue #{item} not found");

    public Task<IReadOnlyList<BoardEntry>> ListBoard() =>
        Task.FromResult<IReadOnlyList<BoardEntry>>(Issues.Values.Where(i => i.Card != null).OrderBy(i => i.Number).Select(i => new BoardEntry(i.Number, i.Open, i.Title, i.Column, i.Labels.ToList())).ToList());

    public Task<JsonNode> GetIssue(int item) => Issues.TryGetValue(item, out var i) ? Task.FromResult<JsonNode>(new JsonObject { ["number"] = i.Number, ["state"] = i.Open ? "open" : "closed" }) : throw NotFound($"issues/{item}");

    public Task SetBody(int item, string body) { Issues[item].Body = body; Writes.Add($"PATCH issues/{item} body"); return Task.CompletedTask; }

    public Task<int> CreateIssue(string title, string body, IReadOnlyList<string> labels)
    {
        var n = NextNumber++;
        Issues[n] = new Issue { Number = n, Title = title, Body = body, Labels = labels.ToList() };
        Writes.Add($"POST issues -> {n} [{string.Join(",", labels)}]");
        return Task.FromResult(n);
    }

    public Task<string?> AddToBoard(string issueNodeId)
    {
        var i = Issues.Values.Single(x => x.NodeId == issueNodeId);
        i.Card = "C_" + i.Number; i.Column = "Todo";
        Writes.Add($"addCard {i.Number}");
        return Task.FromResult<string?>(i.Card);
    }

    public Task MoveCard(ItemInfo info, string column)
    {
        if (!Columns.Contains(column)) throw new VerbRejection(10, "unknown-column", $"board has no column '{column}'");
        if (info.CardId == null) throw new VerbRejection(10, "not-on-board", $"issue #{info.Number} is not on the board");
        Issues[info.Number].Column = column;
        Writes.Add($"moveCard {info.Number} {column}");
        return Task.CompletedTask;
    }

    public Task AddLabels(int item, IReadOnlyList<string> names) { Issues[item].Labels.AddRange(names); Writes.Add($"POST issues/{item}/labels {string.Join(",", names)}"); return Task.CompletedTask; }

    public Task RemoveLabel(int item, string name) { Issues[item].Labels.RemoveAll(l => l.Equals(name, StringComparison.OrdinalIgnoreCase)); Writes.Add($"DELETE issues/{item}/labels/{name}"); return Task.CompletedTask; }

    public Task PostComment(int number, string body) { Writes.Add($"POST issues/{number}/comments {body}"); return Task.CompletedTask; }

    public Task CloseIssue(int item, string reason) { Issues[item].Open = false; Writes.Add($"PATCH issues/{item} closed {reason}"); return Task.CompletedTask; }

    public Task<IReadOnlyList<JsonObject>> OpenPulls() => Task.FromResult<IReadOnlyList<JsonObject>>(Pulls.Where(p => p["state"]!.ToString() == "open").ToList());

    public Task<JsonObject> GetPull(int number) => Pulls.FirstOrDefault(p => p["number"]!.GetValue<int>() == number) is { } p ? Task.FromResult(p) : throw NotFound($"pulls/{number}");

    public Task ClosePull(int number) { Pulls.Single(p => p["number"]!.GetValue<int>() == number)["state"] = "closed"; Writes.Add($"PATCH pulls/{number} closed"); return Task.CompletedTask; }

    public Task<IReadOnlyList<JsonObject>> WorkflowRuns(string headSha) => Task.FromResult<IReadOnlyList<JsonObject>>(Runs.Where(r => r["head_sha"]!.ToString() == headSha).ToList());

    public Task<int> RerunFailedJobs(long runId) { Writes.Add($"POST actions/runs/{runId}/rerun-failed-jobs"); return Task.FromResult(RerunStatus.GetValueOrDefault(runId, 201)); }

    public Task RemoveClaimRef(int item) { Writes.Add($"DELETE claims/{item}"); return Task.CompletedTask; }

    public static JsonObject Pull(int number, string branch, string sha, string state = "open") =>
        new() { ["number"] = number, ["state"] = state, ["title"] = "pr", ["head"] = new JsonObject { ["ref"] = branch, ["sha"] = sha } };

    public static JsonObject Run(long id, string head, string status, string? conclusion, string ev = "pull_request") =>
        new() { ["id"] = id, ["head_sha"] = head, ["status"] = status, ["conclusion"] = conclusion, ["event"] = ev };

    // ---- IItemStateStore --------------------------------------------------------------------------------------------------------------------

    public Task<StateHolder> Read(int item) => Task.FromResult<StateHolder>(new Holder((JsonObject)State(item).DeepClone()));

    public Task Save(int item, StateHolder holder)
    {
        var s = holder.State; s["item"] = item; s["rev"] = (s["rev"]?.GetValue<int>() ?? 0) + 1;
        States[item] = (JsonObject)s.DeepClone(); Writes.Add($"SAVE state {item}");
        return Task.CompletedTask;
    }

    Task<bool> IItemStateStore.Journal(int item, StateHolder? holder, string ev, JsonObject fields, string key)
    {
        Journal.Add($"{item}|{ev}|{key.Split('|')[0]}|{fields["detail"]}|by={fields["by"]}|policy={fields["policy"]}|reason={fields["reason"]}");
        // the ledger keeps the keys in the state: a repeat of a key is not written
        var s = holder?.State ?? State(item);
        var j = s["journal"] as JsonObject ?? (JsonObject)(s["journal"] = new JsonObject { ["keys"] = new JsonArray(), ["count"] = 0 })!;
        ((JsonArray)j["keys"]!).Add($"{ev}|{key}");
        if (holder == null) { States[item] = (JsonObject)s.DeepClone(); }
        return Task.FromResult(true);
    }

    // ---- IVerbLocks -------------------------------------------------------------------------------------------------------------------------

    sealed class Nothing : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    public Task<IAsyncDisposable> Item(int item) { LocksTaken.Add(item); return Task.FromResult<IAsyncDisposable>(new Nothing()); }

    public Task<IAsyncDisposable> Intake() { IntakeLocks++; return Task.FromResult<IAsyncDisposable>(new Nothing()); }

    // ---- IKeeperSaga ------------------------------------------------------------------------------------------------------------------------

    public bool Owns(int item) => SagaOwned.Contains(item);
    Task IKeeperSaga.Resumed(int item, string key) { SagaCalls.Add($"resumed {item}"); return Task.CompletedTask; }
    Task IKeeperSaga.Closed(int item, string reason, string key) { SagaCalls.Add($"closed {item} {reason}"); return Task.CompletedTask; }
}
