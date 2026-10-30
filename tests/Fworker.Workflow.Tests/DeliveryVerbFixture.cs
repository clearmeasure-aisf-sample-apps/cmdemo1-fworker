using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery.Verbs;

namespace Fworker.Workflow.Tests;

/// <summary>
/// The fakes of the delivery verbs' tests (W9.4): a GitHub that answers by rules and records every call, the item's state comment in memory, an Octopus that answers by path, and the kit the rules run on.
/// The rule scripts' own helpers (Get-ItemInfo, Get-ProjectMeta, Move-Card, Add-Comment, ...) are the kit's, so the calls the tests see are the calls v1 makes.
/// </summary>
sealed class FakeGh : IGhTransport
{
    public sealed record Call(HttpMethod Method, string Path, JsonNode? Body);

    readonly List<(Func<HttpMethod, string, bool> Match, Func<HttpMethod, string, JsonNode?, GhResult> Answer)> rules = [];
    public readonly List<Call> Calls = [];
    public readonly Dictionary<string, JsonNode?> Octopus = new(StringComparer.Ordinal);
    public readonly Queue<GhResult> Uploads = new();
    public string AppActions = "write";
    public Func<bool> Lease = () => true;
    public bool Fenced;
    public readonly List<string> UploadedTo = [];

    public IEnumerable<Call> Writes => Calls.Where(c => c.Method != HttpMethod.Get && !(c.Path == "graphql" && !(c.Body?["query"]?.ToString().StartsWith("mutation") ?? false)));

    public string WriteLines() => string.Join("\n", Writes.Select(c => $"{c.Method.Method} {c.Path}" + (c.Body is null ? "" : " " + c.Body.ToJsonString())));

    /// <summary>A rule: the newest that matches answers. A path is matched from its start (the query string included); "" matches every path.</summary>
    public FakeGh On(HttpMethod method, string pathPrefix, Func<JsonNode?, GhResult> answer)
    {
        rules.Insert(0, ((m, p) => m == method && p.StartsWith(pathPrefix, StringComparison.Ordinal), (_, _, b) => answer(b)));
        return this;
    }

    public FakeGh Get(string pathPrefix, JsonNode? body, int status = 200) => On(HttpMethod.Get, pathPrefix, _ => new GhResult(status, body));
    public FakeGh Write(HttpMethod method, string pathPrefix, JsonNode? body = null, int status = 200) => On(method, pathPrefix, _ => new GhResult(status, body ?? new JsonObject()));

    public Task<GhResult> Send(HttpMethod method, string path, JsonNode? body, CancellationToken ct, string? accept = null, int tries = 3)
    {
        if (Fenced && method != HttpMethod.Get && !(path == "graphql" && !(body?["query"]?.ToString().StartsWith("mutation") ?? false)) && !Lease())
            throw new VerbReject(20, "lease-lost", "this gateway no longer holds the leader lease");   // (the transport refuses before the request is made: it is no call)
        Calls.Add(new Call(method, path, body?.DeepClone()));
        foreach (var (match, answer) in rules)
            if (match(method, path)) return Task.FromResult(answer(method, path, body));
        return Task.FromResult(new GhResult(404, new JsonObject { ["message"] = "Not Found" }));
    }

    public Task<JsonNode?> OctopusGet(string path, CancellationToken ct) => Task.FromResult(Octopus.TryGetValue(path, out var n) ? n?.DeepClone() : null);

    public Task<GhResult> UploadFile(string uploadUrl, string contentType, string file, CancellationToken ct)
    {
        UploadedTo.Add(uploadUrl);
        return Task.FromResult(Uploads.Count > 0 ? Uploads.Dequeue() : new GhResult(201, new JsonObject { ["name"] = "demo.mp4", ["size"] = 3145728, ["browser_download_url"] = "https://dl/demo.mp4" }));
    }

    public Task<string> AppActionsPermission(CancellationToken ct) => Task.FromResult(AppActions);
}

sealed class FakeStateStore : IItemStateStore
{
    public readonly Dictionary<int, JsonObject> States = [];
    public readonly List<(int Item, string Json)> Saves = [];
    public int Reads;

    public JsonObject Of(int item) => States.TryGetValue(item, out var s) ? s : States[item] = new JsonObject { ["schema"] = 1, ["item"] = item };

    public Task<StateHolder> Read(int item, CancellationToken ct)
    {
        Reads++;
        var s = (JsonObject)Of(item).DeepClone();
        return Task.FromResult(new StateHolder { CommentId = 7, State = s, Rev = J.I(s, "rev") });
    }

    public Task Save(int item, StateHolder holder, CancellationToken ct)
    {
        var s = holder.State; s["item"] = item; s["rev"] = (holder.Rev ?? 0) + 1; holder.Rev = (int)s["rev"]!;
        States[item] = (JsonObject)s.DeepClone();
        Saves.Add((item, s.ToJsonString()));
        return Task.CompletedTask;
    }
}

/// <summary>An item as the GraphQL read of Get-ItemInfo answers it, and the board of Get-ProjectMeta.</summary>
static class Gql
{
    public static readonly string[] Columns = ["Technical Design", "Development", "Functional Testing", "Release Queue", "Done"];

    public static JsonObject Item(int n, string column, bool open = true, string[]? labels = null, string body = "body", string created = "2026-10-20T00:00:00Z", string title = "Title") => new()
    {
        ["data"] = new JsonObject
        {
            ["repository"] = new JsonObject
            {
                ["issue"] = new JsonObject
                {
                    ["id"] = $"I_{n}", ["number"] = n, ["state"] = open ? "OPEN" : "CLOSED", ["title"] = title, ["body"] = body, ["url"] = $"https://github.com/acme/orders/issues/{n}", ["createdAt"] = created,
                    ["labels"] = new JsonObject { ["nodes"] = new JsonArray((labels ?? []).Select(l => (JsonNode?)new JsonObject { ["name"] = l }).ToArray()) },
                    ["projectItems"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = $"C_{n}", ["project"] = new JsonObject { ["number"] = 1 }, ["fieldValueByName"] = new JsonObject { ["name"] = column } }) },
                },
            },
        },
    };

    public static JsonObject Board() => new()
    {
        ["data"] = new JsonObject
        {
            ["organization"] = new JsonObject
            {
                ["projectV2"] = new JsonObject
                {
                    ["id"] = "P1",
                    ["fields"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "F1", ["name"] = "Status", ["options"] = new JsonArray(Columns.Select((c, i) => (JsonNode?)new JsonObject { ["id"] = $"o{i + 1}", ["name"] = c }).ToArray()) }) },
                },
            },
        },
    };

    /// <summary>The GraphQL rule of a fake: the item read (by its number), the board, and any mutation (recorded by the call list).</summary>
    public static FakeGh WithBoard(this FakeGh gh, Func<int, JsonObject> item)
    {
        gh.On(HttpMethod.Post, "graphql", b =>
        {
            var q = b?["query"]?.ToString() ?? "";
            if (q.StartsWith("mutation", StringComparison.Ordinal)) return new GhResult(200, new JsonObject { ["data"] = new JsonObject() });
            if (q.Contains("projectV2(number", StringComparison.Ordinal)) return new GhResult(200, Board());
            return new GhResult(200, item(b!["variables"]!["n"]!.GetValue<int>()));
        });
        return gh;
    }
}

static class VerbFixture
{
    public static JsonObject Config(Action<JsonObject>? edit = null)
    {
        var c = JsonNode.Parse("""
            {
              "name": "factory-worker", "owner": "acme", "repo": "orders", "project": 1, "defaultBranch": "master",
              "columns": { "ai": ["Technical Design", "Development", "Functional Testing"], "development": "Development", "validation": "Functional Testing", "hold": ["Release Queue"], "done": "Done" },
              "requiredBodySection": { "Technical Design": "## Technical Design" },
              "ciGates": ["Build result"], "videoLabel": "video", "autoMergeLabel": "Auto Merge",
              "docsOnlyMarker": "<!-- aisf:docs-only -->", "docsOnlyPaths": ["^docs/.*\\.md$"],
              "releaseCheckName": "release", "masterFlakyChecks": ["^Acceptance Tests \\[shard \\d+/\\d+\\]$"], "masterIgnoredChecks": ["release", "packages", "architecture"], "masterCodeChecks": ["Integration Build"],
              "octopus": { "url": "https://octopus.example", "space": "Spaces-1", "project": "orders-ui", "prodUrl": "https://prod.example", "environments": { "tdd": "tdd", "uat": "uat", "prod": "Prod" } },
              "timing": { "masterGreenTimeoutMinutes": 30, "parkHours": 24, "releaseCandidateTimeoutMinutes": 20, "lateReleaseCandidateHours": 24, "docsOnlyReleaseGraceSeconds": 120,
                          "masterContainsLookback": 10, "awaitingChecksParkMinutes": 60, "redChecksGate": 2, "docsOnlyLegacyBefore": "2026-10-16T00:00:00Z", "maxDemotions": 2 }
            }
            """)!.AsObject();
        edit?.Invoke(c);
        return c;
    }

    public static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    public static RuleKit Kit(FakeGh gh, FakeStateStore store, JsonObject? config = null, Func<string, string?>? env = null, Func<DateTime>? now = null, JsonNode? session = null) =>
        new(gh, store, config ?? Config(), new BoardMetaCache(), env ?? (_ => null), now ?? (() => Now), session);

    public static string Sha(char c) => new(c, 40);
}
