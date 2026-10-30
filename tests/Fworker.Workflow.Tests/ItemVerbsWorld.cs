using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Workflows.WorkItem.Verbs;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.2: a GitHub of the item verbs' tests, in memory: the issues of one repository with their cards on a Projects board, their comments, the claim refs, the pull requests and their files. It answers the REST and GraphQL calls the
/// verbs make (the wire port), records every write the way the golden files of tests/scenarios/golden show them (method, path, status, body), keeps the state comments (the state port) and journals through the real JournalLedger.
/// </summary>
sealed class ItemWorld : IItemWire, IItemStateStore, IItemJournal, IItemLock, IItemRefusals, IItemV1, IJournalStore
{
    public const string Owner = "acme", Repo = "orders", Login = "factory-bot";
    public static readonly string[] Columns = ["Todo", "Technical Design", "Development", "Functional Testing", "Release Queue", "Done"];

    public sealed class Issue
    {
        public int Number; public string Title = "an item"; public string Body = ""; public bool Open = true; public List<string> Labels = ["factory worker"];
        public string? Column = "Technical Design"; public string CreatedAt = "2026-10-20T10:00:00Z"; public List<Comment> Comments = [];
    }
    public sealed record Comment(long Id, string Login, string Body, string CreatedAt);
    public sealed class Pull
    {
        public int Number; public string Head = ""; public string Sha = "0123456789abcdef0123456789abcdef01234567"; public string Title = ""; public string Body = ""; public bool Open = true; public bool Merged;
        public bool Draft; public string Base = "master"; public List<(string File, string Status)> Files = [];
    }

    public readonly Dictionary<int, Issue> Issues = [];
    public readonly List<Pull> Pulls = [];
    public readonly HashSet<int> ClaimRefs = [];
    public readonly Dictionary<int, JsonObject> States = [];
    public readonly Dictionary<int, long?> StateComments = [];
    public readonly Dictionary<int, int> Refusals = [];
    public readonly List<string> Writes = [];
    public readonly List<string> Reads = [];
    public readonly List<(string Verb, string Stdin)> V1Calls = [];
    public readonly List<string> Log = [];
    public DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    public ItemRawAnswer? V1Answer;
    public long NextId = 1000;
    public int NextPull = 100;
    public bool FailNextStateSave;
    public ItemVerbConfig Config { get; }
    public readonly ItemVerbs Verbs;
    readonly JournalLedger ledger;
    readonly Clock clock;

    sealed class Clock(ItemWorld w) : IItemClock { public DateTime UtcNow => w.Now; }

    public ItemWorld(bool messaging = false, Func<string, string?>? env = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json")));
        Config = ItemVerbConfig.From(root, env ?? (_ => null), "", messaging, false);
        clock = new Clock(this);
        ledger = new JournalLedger(this, new JournalSettings(30, []), () => Now, _ => { });
        var gh = new ItemGitHub(this, Config, clock);
        Verbs = new ItemVerbs(gh, new ItemBoard(gh, clock), new ItemPulls(gh, this), this, this, this, this, this, clock);
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json")) && Directory.Exists(Path.Combine(d.FullName, "gateway"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    public Issue AddIssue(int number, string? column = "Technical Design", string body = "", params string[] labels)
    {
        var i = new Issue { Number = number, Column = column, Body = body, Title = $"item {number}" };
        if (labels.Length > 0) i.Labels = [.. labels];
        Issues[number] = i;
        return i;
    }

    public Pull AddPull(int item, string? head = null, params string[] files)
    {
        var p = new Pull { Number = NextPull++, Head = head ?? $"factory-worker/{item}-development", Title = $"change (#{item})" };
        p.Files = files.Select(f => (f, "modified")).ToList();
        Pulls.Add(p);
        return p;
    }

    // ---- the verbs ------------------------------------------------------------------------------------------------------------------------------------

    public async Task<JsonObject> Call(string verb, object args, int expectExit = 0)
    {
        var a = await Verbs.Run(verb, args is string s ? s : System.Text.Json.JsonSerializer.Serialize(args), TimeSpan.FromSeconds(30));
        Assert.True(expectExit == a.Exit, $"{verb} exit {a.Exit} (expected {expectExit}): {a.Text}");
        return a.Body;
    }

    public JsonObject State(int item) => States[item];

    // ---- the wire -------------------------------------------------------------------------------------------------------------------------------------

    static ItemGhAnswer Ok(int status, JsonNode? body, params (string, string)[] headers) => new(status, body, headers.ToDictionary(h => h.Item1, h => h.Item2));

    void Write(string method, string path, int status, JsonNode? body)
    {
        var p = Regex.Replace(path.Split('?')[0], @"^repos/[^/]+/[^/]+", "/repos/{repo}");
        p = "/" + p.TrimStart('/');
        p = Regex.Replace(p, @"(?<=/(issues|pulls|claims)/)(\d+)", m => $"{{#{m.Groups[2].Value}}}");
        Writes.Add($"{method} {p} {status} {Sorted(body)}");
    }

    /// <summary>The body as the golden files show it: keys in alphabetical order, compact.</summary>
    public static string Sorted(JsonNode? n) => n switch
    {
        null => "-",
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"\"{kv.Key}\":{Sorted(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Sorted)) + "]",
        _ => n.ToJsonString(),
    };

    /// <summary>A GitHub that answers slowly (the time a verb may run is the pipeline's).</summary>
    public TimeSpan SendDelay;

    public async Task<ItemGhAnswer> Send(ItemGhCall call, CancellationToken ct)
    {
        if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, ct);
        return await SendNow(call, ct);
    }

    Task<ItemGhAnswer> SendNow(ItemGhCall call, CancellationToken ct)
    {
        var path = call.Path.TrimStart('/');
        var m = call.Method;
        var rest = Regex.Replace(path, @"^repos/[^/]+/[^/]+/?", "");
        if (path == "graphql") return Task.FromResult(GraphQl(call.Body!));
        if (m != "GET") { }
        else Reads.Add($"{m} {path}");
        // issues
        var im = Regex.Match(rest, @"^issues/(\d+)(/comments)?(\?.*)?$");
        if (im.Success)
        {
            var n = int.Parse(im.Groups[1].Value); var issue = Issues[n];
            if (im.Groups[2].Success && m == "GET")
            {
                var items = new JsonArray(issue.Comments.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["user"] = new JsonObject { ["login"] = c.Login }, ["body"] = c.Body, ["created_at"] = c.CreatedAt }).ToArray());
                var page = Regex.Match(rest, @"[?&]page=(\d+)") is { Success: true } pm ? int.Parse(pm.Groups[1].Value) : 1;
                return Task.FromResult(Ok(200, page == 1 ? items : new JsonArray()));
            }
            if (im.Groups[2].Success && m == "POST")
            {
                var c = new Comment(NextId++, Login, call.Body!["body"]!.ToString(), Iso(Now)); issue.Comments.Add(c);
                Write(m, path, 201, call.Body);
                return Task.FromResult(Ok(201, new JsonObject { ["id"] = c.Id, ["body"] = c.Body }));
            }
            if (m == "PATCH") { issue.Body = call.Body!["body"]!.ToString(); Write(m, path, 200, call.Body); return Task.FromResult(Ok(200, new JsonObject { ["number"] = n })); }
        }
        // claim refs
        var rm = Regex.Match(rest, @"^git/refs(?:/factory/factory-worker/claims/(\d+))?$");
        if (m == "POST" && rest == "git/refs")
        {
            var n = int.Parse(Regex.Match(call.Body!["ref"]!.ToString(), @"claims/(\d+)$").Groups[1].Value);
            if (!ClaimRefs.Add(n)) { Write(m, path, 422, call.Body); return Task.FromResult(Ok(422, new JsonObject { ["message"] = "Reference already exists" })); }
            Write(m, path, 201, call.Body);
            return Task.FromResult(Ok(201, new JsonObject { ["ref"] = call.Body["ref"]!.ToString() }));
        }
        if (m == "DELETE" && rm.Success && rm.Groups[1].Success)
        {
            var n = int.Parse(rm.Groups[1].Value);
            var had = ClaimRefs.Remove(n);
            Write(m, path, had ? 204 : 404, null);
            return Task.FromResult(Ok(had ? 204 : 404, null));
        }
        if (m == "GET" && Regex.IsMatch(rest, @"^git/ref/heads/master$")) return Task.FromResult(Ok(200, new JsonObject { ["object"] = new JsonObject { ["sha"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } }));
        // pull requests
        if (m == "GET" && Regex.IsMatch(rest, @"^pulls\?state=open")) return Task.FromResult(Ok(200, new JsonArray(Pulls.Where(p => p.Open).Select(p => PullJson(p)).ToArray())));
        var pm2 = Regex.Match(rest, @"^pulls/(\d+)(/files)?(\?.*)?$");
        if (pm2.Success)
        {
            var p = Pulls.FirstOrDefault(x => x.Number == int.Parse(pm2.Groups[1].Value));
            if (p is null) return Task.FromResult(Ok(404, new JsonObject { ["message"] = "Not Found" }));
            if (m == "GET" && pm2.Groups[2].Success)
            {
                var page = Regex.Match(rest, @"[?&]page=(\d+)") is { Success: true } q ? int.Parse(q.Groups[1].Value) : 1;
                return Task.FromResult(Ok(200, page == 1 ? new JsonArray(p.Files.Select(f => (JsonNode)new JsonObject { ["filename"] = f.File, ["status"] = f.Status }).ToArray()) : new JsonArray()));
            }
            if (m == "GET") return Task.FromResult(Ok(200, PullJson(p)));
            if (m == "PATCH") { if (call.Body!["body"] is { } b) p.Body = b.ToString(); Write(m, path, 200, call.Body); return Task.FromResult(Ok(200, PullJson(p))); }
        }
        if (m == "POST" && rest == "pulls")
        {
            var head = call.Body!["head"]!.ToString();
            if (RejectPulls) { Write(m, path, 422, call.Body); return Task.FromResult(Ok(422, new JsonObject { ["message"] = "Validation Failed", ["errors"] = new JsonArray(new JsonObject { ["message"] = "No commits between master and " + head }) })); }
            var p = new Pull { Number = NextPull++, Head = head, Title = call.Body["title"]!.ToString(), Body = call.Body["body"]!.ToString(), Base = call.Body["base"]!.ToString() };
            Pulls.Add(p);
            Write(m, path, 201, call.Body);
            return Task.FromResult(Ok(201, PullJson(p)));
        }
        var rel = Regex.Match(rest, @"^releases/tags/demo-(\d+)$");
        if (rel.Success) return Task.FromResult(Releases.TryGetValue(int.Parse(rel.Groups[1].Value), out var r) ? Ok(200, r) : Ok(404, new JsonObject { ["message"] = "Not Found" }));
        throw new InvalidOperationException($"the fake GitHub does not know {m} {path}");
    }

    public bool RejectPulls;
    public readonly Dictionary<int, JsonObject> Releases = [];

    static string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    static JsonNode PullJson(Pull p) => new JsonObject
    {
        ["number"] = p.Number, ["html_url"] = $"https://github.com/o/r/pull/{p.Number}", ["title"] = p.Title, ["body"] = p.Body, ["state"] = p.Open ? "open" : "closed", ["merged"] = p.Merged, ["draft"] = p.Draft,
        ["merge_commit_sha"] = p.Merged ? "bbbb" : null, ["head"] = new JsonObject { ["ref"] = p.Head, ["sha"] = p.Sha }, ["base"] = new JsonObject { ["ref"] = p.Base }, ["changed_files"] = p.Files.Count,
    };

    ItemGhAnswer GraphQl(JsonNode body)
    {
        var q = body["query"]!.ToString(); var v = (JsonObject)body["variables"]!;
        if (q.Contains("updateProjectV2ItemFieldValue"))
        {
            var card = v["i"]!.ToString(); var opt = v["o"]!.ToString();
            var issue = Issues.Values.First(i => $"card_{i.Number}" == card);
            issue.Column = Columns.First(c => $"opt_{c.Replace(' ', '_')}" == opt);
            Writes.Add($"POST /graphql updateProjectV2ItemFieldValue 200 {Sorted(body)}");
            return Ok(200, new JsonObject { ["data"] = new JsonObject { ["updateProjectV2ItemFieldValue"] = new JsonObject() } });
        }
        if (q.Contains("fields(first:30)"))
            return Ok(200, new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["organization"] = new JsonObject
                    {
                        ["projectV2"] = new JsonObject
                        {
                            ["id"] = "PVT_fake",
                            ["fields"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "FIELD_status", ["name"] = "Status", ["options"] = new JsonArray(Columns.Select(c => (JsonNode)new JsonObject { ["id"] = $"opt_{c.Replace(' ', '_')}", ["name"] = c }).ToArray()) }) },
                        },
                    },
                },
            });
        if (q.Contains("projectItems(first:20)"))
        {
            var n = v["n"]!.GetValue<int>();
            if (!Issues.TryGetValue(n, out var i)) return Ok(200, new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["issue"] = null } } });
            return Ok(200, new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["repository"] = new JsonObject
                    {
                        ["issue"] = new JsonObject
                        {
                            ["id"] = $"I_{i.Number}", ["number"] = i.Number, ["state"] = i.Open ? "OPEN" : "CLOSED", ["title"] = i.Title, ["body"] = i.Body, ["url"] = $"https://github.com/o/r/issues/{i.Number}", ["createdAt"] = i.CreatedAt,
                            ["labels"] = new JsonObject { ["nodes"] = new JsonArray(i.Labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()) },
                            ["projectItems"] = new JsonObject
                            {
                                ["nodes"] = i.Column is null ? new JsonArray() : new JsonArray(new JsonObject { ["id"] = $"card_{i.Number}", ["project"] = new JsonObject { ["number"] = 679 }, ["fieldValueByName"] = new JsonObject { ["name"] = i.Column } }),
                            },
                        },
                    },
                },
            });
        }
        if (q.Contains("items(first:100"))
            return Ok(200, new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["organization"] = new JsonObject
                    {
                        ["projectV2"] = new JsonObject
                        {
                            ["items"] = new JsonObject
                            {
                                ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = null },
                                ["nodes"] = new JsonArray(Issues.Values.Select(i => (JsonNode)new JsonObject
                                {
                                    ["id"] = $"card_{i.Number}", ["fieldValueByName"] = i.Column is null ? null : new JsonObject { ["name"] = i.Column },
                                    ["content"] = new JsonObject
                                    {
                                        ["number"] = i.Number, ["state"] = i.Open ? "OPEN" : "CLOSED", ["title"] = i.Title, ["body"] = i.Body, ["repository"] = new JsonObject { ["nameWithOwner"] = $"{Config.Owner}/{Config.Repo}" },
                                        ["labels"] = new JsonObject { ["nodes"] = new JsonArray(i.Labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()) },
                                    },
                                }).ToArray()),
                            },
                        },
                    },
                },
            });
        throw new InvalidOperationException("the fake GitHub does not know this query: " + q);
    }

    // ---- the state comment (IJournalStore, the real ledger's store; IItemStateStore over it) ----------------------------------------------------------

    public Task<ItemState> Read(int item, CancellationToken ct)
    {
        var issue = Issues[item];
        if (!States.TryGetValue(item, out var s)) return Task.FromResult(new ItemState { CommentId = null, State = new JsonObject { ["schema"] = 1, ["item"] = item }, Comments = Comments(issue) });
        var copy = (JsonObject)JsonNode.Parse(s.ToJsonString())!;
        return Task.FromResult(new ItemState { CommentId = StateComments[item], State = copy, Rev = ItemStateComment.RevOf(copy), Comments = Comments(issue) });
    }

    static List<IssueComment> Comments(Issue i) => i.Comments.Select(c => new IssueComment(c.Id, c.Login, c.Body)).ToList();

    public Task Save(int item, ItemState holder, CancellationToken ct)
    {
        if (FailNextStateSave) { FailNextStateSave = false; throw new StateConflictException($"the state of item {item} changed since it was read"); }
        var s = holder.State; s["item"] = item; s["updated"] = Ps.Iso(Now);
        var curRev = States.TryGetValue(item, out var cur) ? ItemStateComment.RevOf(cur) : 0;
        if (holder.CommentId is not null && holder.Rev is { } rr && rr != curRev) throw new StateConflictException($"the state of item {item} changed since it was read (revision {rr}, now {curRev})");
        s["rev"] = (holder.Rev ?? ItemStateComment.RevOf(s)) + 1;
        States[item] = (JsonObject)JsonNode.Parse(s.ToJsonString())!;
        if (holder.CommentId is null) { holder.CommentId = NextId++; StateComments[item] = holder.CommentId; Writes.Add($"POST /repos/{{repo}}/issues/{{#{item}}}/comments 201 STATE factory-worker v1 #{{#{item}}}"); }
        else Writes.Add($"PATCH /repos/{{repo}}/issues/comments/{{id}} 200 STATE factory-worker v1 #{{#{item}}}");
        holder.Rev = s["rev"]!.GetValue<int>();
        return Task.CompletedTask;
    }

    public async Task<long?> Post(int item, string body, CancellationToken ct)
    {
        var r = await Send(new ItemGhCall("POST", $"{Config.RepoPath}/issues/{item}/comments", new JsonObject { ["body"] = body }), ct);
        return r.Body!["id"]!.GetValue<long>();
    }

    public async Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct)
    {
        foreach (var i in Issues.Values)
            if (i.Comments.FindIndex(c => c.Id == commentId) is var ix and >= 0)
            {
                i.Comments[ix] = i.Comments[ix] with { Body = body };
                Writes.Add($"PATCH /repos/{{repo}}/issues/comments/{{id}} 200 {Sorted(new JsonObject { ["body"] = body })}");
                return PatchOutcome.Ok;
            }
        await Task.CompletedTask;
        return PatchOutcome.Gone;
    }

    async Task<ItemStateHolder> IItemStateStore.Read(int item, CancellationToken ct)
    {
        var st = await Read(item, ct);
        st.State = (JsonObject)JsonNode.Parse(st.State.ToJsonString(), Ps.CaseBlind)!;
        return new ItemStateHolder { CommentId = st.CommentId, State = st.State, Rev = st.Rev, Backing = st };
    }

    async Task IItemStateStore.Save(int item, ItemStateHolder holder, CancellationToken ct)
    {
        var st = (ItemState)holder.Backing!;
        st.CommentId = holder.CommentId; st.Rev = holder.Rev; st.State = holder.State;
        try { await Save(item, st, ct); }
        catch (StateConflictException e) { throw new ItemRefusal(11, "state-conflict", e.Message, new JsonObject { ["retryAfterSeconds"] = 1 }); }
        holder.CommentId = st.CommentId; holder.Rev = st.Rev;
    }

    Task<string> IItemStateStore.Login(CancellationToken ct) => Task.FromResult(Login);

    public async Task<bool> Add(int item, ItemStateHolder holder, string ev, JsonObject fields, string key, CancellationToken ct)
    {
        var st = (ItemState)holder.Backing!; st.State = holder.State;
        return await ledger.AddInHand(item, st, ev, JournalLedger.FieldsOf(fields), key, ct);
    }

    // ---- locks, refusals, v1 --------------------------------------------------------------------------------------------------------------------------

    public async Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct) { Log.Add($"lock {item}"); try { return await body(); } finally { Log.Add($"unlock {item}"); } }

    public Task Add(int item, CancellationToken ct) { Refusals[item] = Refusals.GetValueOrDefault(item) + 1; return Task.CompletedTask; }
    public Task Reset(int item, CancellationToken ct) { Refusals.Remove(item); return Task.CompletedTask; }
    public Task<int> Count(int item, CancellationToken ct) => Task.FromResult(Refusals.GetValueOrDefault(item));

    public Task<ItemRawAnswer?> Run(string verb, string stdin, TimeSpan timeout, CancellationToken ct)
    {
        V1Calls.Add((verb, stdin));
        return Task.FromResult(V1Answer);
    }
}
