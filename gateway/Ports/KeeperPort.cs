using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Keeper;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.WorkItem;

// W9.6 (#358): the keeper and manager verbs on v2 (Fworker.Workflows.Keeper), in the gateway. The verbs are the port of Verbs.Keeper.ps1 and Verbs.Manager.ps1; this file is what they stand on, each part the C# of a helper of the rule
// scripts that the verbs called: the GitHub calls of Core.ps1 and Board.ps1 (Invoke-Gh with its answers and its three tries, Get-ItemInfo, Move-Card, Add-ToBoard, Get-BoardList), the state comment and the journal (the
// W3.2 ledger: Get-State, Save-State, Add-Journal), the locks (Use-ItemLock and Use-IntakeLock, the same files in the same state directory as the scripts', so a verb still running in pwsh for the same item excludes this one),
// and the word to the saga when the verb ends a park or closes an item it owns. Registered by Routes/Keeper.cs, only where the engine acts and the gateway has a GitHub identity and a home repository.

/// <summary>GitHub for the keeper and manager verbs: the rules of Invoke-Gh (Core.ps1), request by request.</summary>
sealed class KeeperGitHub(HttpClient http, FactoryRules rules, int project, Action<JsonObject>? observe = null, Func<TimeSpan, Task>? delay = null) : IPlantGitHub
{
    const string MoveCardMutation = BoardColumnProjection.MoveMutation;
    const string AddCardMutation = "mutation($p:ID!,$c:ID!){ addProjectV2ItemById(input:{projectId:$p,contentId:$c}){ item{ id } } }";
    const string MetaQuery = "query($o:String!,$n:Int!){ organization(login:$o){ projectV2(number:$n){ id fields(first:30){ nodes{ ... on ProjectV2SingleSelectField{ id name options{ id name } } } } } } }";
    const string ItemQuery = """
        query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id number state title body url createdAt
          labels(first:50){ nodes{ name } }
          projectItems(first:20){ nodes{ id project{ number } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
        """;
    const string BoardQuery = """
        query($o:String!,$n:Int!,$after:String){ organization(login:$o){ projectV2(number:$n){ items(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
          nodes{ id fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } }
            content{ ... on Issue{ number state title body repository{ nameWithOwner } labels(first:30){ nodes{ name } } } } } } } } }
        """;

    static readonly TimeSpan MetaTtl = TimeSpan.FromMinutes(10);
    readonly Func<TimeSpan, Task> wait = delay ?? Task.Delay;
    (string ProjectId, string FieldId, Dictionary<string, string> Options, DateTime At)? meta;

    string Repo => $"repos/{rules.Owner}/{rules.Repo}";

    // ---- Invoke-GhRaw and Invoke-Gh --------------------------------------------------------------------------------------------------------

    sealed record Raw(int Status, JsonNode? Body, string Text, HttpResponseHeaders Headers);

    /// <summary><c>Invoke-GhRaw</c>: three tries on a network error or a 502 to 504; the answer whatever its status.</summary>
    async Task<Raw> Send(HttpMethod method, string path, JsonNode? body = null)
    {
        const int tries = 3;
        for (var attempt = 1; attempt <= tries; attempt++)
        {
            HttpResponseMessage resp; string text;
            try
            {
                using var req = new HttpRequestMessage(method, path.TrimStart('/'));
                if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                // test-only (golden writes): the cause id of the harness's call this verb serves, sent with every write
                if (TestCause.On && TestCause.Current is { } cause && Regex.IsMatch(cause, "^[A-Za-z0-9._-]{1,80}$")) req.Headers.TryAddWithoutValidation("X-Fake-Cause", cause);
                resp = await http.SendAsync(req);
                text = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (attempt >= tries) throw VerbRejection.Retry(20, "github-unreachable", e.Message, 30);
                await wait(TimeSpan.FromSeconds(2 * attempt)); continue;
            }
            using (resp)
            {
                Observe(resp);
                if ((int)resp.StatusCode is 502 or 503 or 504 && attempt < tries) { await wait(TimeSpan.FromSeconds(2 * attempt)); continue; }
                JsonNode? parsed = null;
                if (text.Length > 0) { try { parsed = JsonNode.Parse(text); } catch (JsonException) { parsed = null; } }
                return new Raw((int)resp.StatusCode, parsed, text, resp.Headers);
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    // the rate limit the answer reports goes to the gateway's shared budget (issue 92), as the rule script's _github field did
    void Observe(HttpResponseMessage resp)
    {
        static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
        if (observe == null || H(resp, "x-ratelimit-remaining") is not { } rem || !Regex.IsMatch(rem, @"^\d+$")) return;
        var resource = H(resp, "x-ratelimit-resource") is { } r && Regex.IsMatch(r, "^[a-z_]{1,30}$") ? r : "core";
        observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = rem, ["limit"] = H(resp, "x-ratelimit-limit"), ["reset"] = H(resp, "x-ratelimit-reset") } });
    }

    static string? Header(HttpResponseHeaders h, string name) => h.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    /// <summary><c>Get-GhRateLimitWait</c>: the seconds to wait when the answer is a rate limit, else -1.</summary>
    static int RateLimitWait(int status, HttpResponseHeaders h, string message)
    {
        if (Header(h, "retry-after") is { } ra && Regex.IsMatch(ra, @"^\d+$")) return Math.Max(1, int.Parse(ra, CultureInfo.InvariantCulture));
        var rem = Header(h, "x-ratelimit-remaining"); var reset = Header(h, "x-ratelimit-reset");
        if (rem == "0")
        {
            if (reset != null && Regex.IsMatch(reset, @"^\d+$")) return (int)Math.Max(1, long.Parse(reset, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return 60;
        }
        if (status == 429 || Regex.IsMatch(message, "rate limit|abuse detection|secondary rate", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return 60;
        return -1;
    }

    /// <summary><c>Invoke-Gh</c>: a 2xx answer is its body; an allowed status is null (<paramref name="allowed"/> says which); anything else is the refusal of the script.</summary>
    async Task<JsonNode?> Call(HttpMethod method, string path, JsonNode? body = null, params int[] allowed)
    {
        var r = await Send(method, path, body);
        if (r.Status is >= 200 and < 300) return r.Body ?? JsonValue.Create(r.Text.Length == 0 ? null : r.Text);
        if (allowed.Contains(r.Status)) return null;
        var detail = r.Text; if (detail.Length > 400) detail = detail[..400];
        switch (r.Status)
        {
            case 404: throw new VerbRejection(12, "github-not-found", $"GitHub 404 on {method} {path}", new Dictionary<string, object?> { ["detail"] = detail });
            case 403 or 429:
                var msg = r.Body?["message"] is JsonNode m ? Ps.Str(m) : detail;
                var secs = RateLimitWait(r.Status, r.Headers, msg);
                if (secs >= 0)
                {
                    var extra = new Dictionary<string, object?> { ["retryAfterSeconds"] = secs, ["detail"] = detail };
                    if (Header(r.Headers, "x-ratelimit-reset") is { } reset && Regex.IsMatch(reset, @"^\d+$")) extra["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(long.Parse(reset, CultureInfo.InvariantCulture)).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                    throw new VerbRejection(20, "github-rate-limited", $"GitHub {r.Status} on {method} {path}", extra);
                }
                // (issue 92) a refusal of permission, not a rate limit: exit 30, with what GitHub said
                throw new VerbRejection(30, "github-forbidden", $"GitHub refused {method} {path} (403): {msg}", new Dictionary<string, object?> { ["status"] = 403, ["detail"] = detail });
            default:
                throw new VerbRejection(20, "github-error", $"GitHub {r.Status} on {method} {path}", new Dictionary<string, object?> { ["status"] = r.Status, ["detail"] = detail, ["retryAfterSeconds"] = 30 });
        }
    }

    /// <summary><c>Invoke-GhGraphQL</c>: the data of the answer.</summary>
    async Task<JsonNode> GraphQl(string query, JsonObject variables)
    {
        var r = await Send(HttpMethod.Post, "graphql", new JsonObject { ["query"] = query, ["variables"] = variables });
        if (r.Status != 200) throw VerbRejection.Retry(20, "github-graphql-error", $"GraphQL HTTP {r.Status}", 30);
        if (r.Body?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var msg = string.Join("; ", errors.Select(e => Ps.Str(e?["message"])));
            if (errors.Any(e => Ps.Str(e?["type"]) == "RATE_LIMITED"))
            {
                var reset = Header(r.Headers, "x-ratelimit-reset");
                var wait = reset != null && Regex.IsMatch(reset, @"^\d+$") ? (int)Math.Max(1, long.Parse(reset, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : 60;
                var extra = new Dictionary<string, object?> { ["retryAfterSeconds"] = wait };
                if (reset != null && Regex.IsMatch(reset, @"^\d+$")) extra["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(long.Parse(reset, CultureInfo.InvariantCulture)).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                throw new VerbRejection(20, "github-rate-limited", msg, extra);
            }
            throw VerbRejection.Retry(20, "github-graphql-error", msg, 30);
        }
        return r.Body?["data"] ?? JsonValue.Create("")!;
    }

    // ---- the board ----------------------------------------------------------------------------------------------------------------------------

    /// <summary><c>Get-ProjectMeta</c>: the project's id, the Status field and its options, cached ten minutes.</summary>
    async Task<(string ProjectId, string FieldId, Dictionary<string, string> Options, DateTime At)> Meta()
    {
        if (meta is { } cached && DateTime.UtcNow - cached.At < MetaTtl) return cached;
        var d = await GraphQl(MetaQuery, new JsonObject { ["o"] = rules.Owner, ["n"] = project });
        var p = d["organization"]?["projectV2"];
        var status = (p?["fields"]?["nodes"] as JsonArray ?? []).FirstOrDefault(f => f != null && Ps.Str(f["name"]) == "Status");
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in status?["options"] as JsonArray ?? []) if (o != null) options[Ps.Str(o["name"])] = Ps.Str(o["id"]);
        return (meta = (Ps.Str(p?["id"]), Ps.Str(status?["id"]), options, DateTime.UtcNow)).Value;
    }

    public async Task<ItemInfo> GetItemInfo(int item)
    {
        var d = await GraphQl(ItemQuery, new JsonObject { ["o"] = rules.Owner, ["r"] = rules.Repo, ["n"] = item });
        var i = d["repository"]?["issue"];
        if (i == null) throw new VerbRejection(12, "item-not-found", $"issue #{item} not found");
        var labels = (i["labels"]?["nodes"] as JsonArray ?? []).Select(l => Ps.Str(l?["name"])).ToList();
        if (!rules.InScope(labels))
            throw new VerbRejection(10, "out-of-scope", $"issue #{item} is outside this gateway's scope: it does not carry exactly one env: label, '{rules.EnvLabel}'");
        var card = (i["projectItems"]?["nodes"] as JsonArray ?? []).FirstOrDefault(c => c?["project"]?["number"] is JsonNode pn && Ps.Str(pn) == project.ToString(CultureInfo.InvariantCulture));
        string? column = card?["fieldValueByName"] is JsonNode fv && fv is JsonObject ? Ps.Str(fv["name"]) : null;
        return new ItemInfo(int.Parse(Ps.Str(i["number"]), CultureInfo.InvariantCulture), Ps.Str(i["id"]), Ps.Str(i["state"]) == "OPEN", Ps.Str(i["title"]), Ps.Str(i["body"]), labels,
            card == null ? null : Ps.Str(card["id"]), column);
    }

    public async Task<IReadOnlyList<BoardEntry>> ListBoard()
    {
        var all = new List<BoardEntry>(); string? after = null;
        while (true)
        {
            var d = await GraphQl(BoardQuery, new JsonObject { ["o"] = rules.Owner, ["n"] = project, ["after"] = after });
            var items = d["organization"]?["projectV2"]?["items"];
            foreach (var n in items?["nodes"] as JsonArray ?? [])
            {
                var content = n?["content"] as JsonObject;
                if (content == null || !content.ContainsKey("number")) continue;
                // a card of another repository linked to the board: its number is not an issue of this repository, so it is never seen here (a card without a repository is dropped as well)
                if (!string.Equals(Ps.Str(content["repository"]?["nameWithOwner"]), $"{rules.Owner}/{rules.Repo}", StringComparison.OrdinalIgnoreCase)) continue;
                var labels = (content["labels"]?["nodes"] as JsonArray ?? []).Select(l => Ps.Str(l?["name"])).ToList();
                if (!rules.InScope(labels)) continue;   // another environment's card (or an unlabelled one with scoping on): not seen here
                all.Add(new BoardEntry(int.Parse(Ps.Str(content["number"]), CultureInfo.InvariantCulture), Ps.Str(content["state"]) == "OPEN", Ps.Str(content["title"]),
                    n?["fieldValueByName"] is JsonObject fv ? Ps.Str(fv["name"]) : null, labels));
            }
            if (items?["pageInfo"]?["hasNextPage"] is not JsonNode hn || !Ps.Truthy(hn)) return all;
            after = Ps.Str(items["pageInfo"]!["endCursor"]);
        }
    }

    public async Task<string?> AddToBoard(string issueNodeId)
    {
        var m = await Meta();
        var d = await GraphQl(AddCardMutation, new JsonObject { ["p"] = m.ProjectId, ["c"] = issueNodeId });
        return Ps.Str(d["addProjectV2ItemById"]?["item"]?["id"]);
    }

    public async Task MoveCard(ItemInfo info, string column)
    {
        var m = await Meta();
        if (!m.Options.TryGetValue(column, out var option)) throw new VerbRejection(10, "unknown-column", $"board has no column '{column}'");
        if (string.IsNullOrEmpty(info.CardId)) throw new VerbRejection(10, "not-on-board", $"issue #{info.Number} is not on the board");
        await GraphQl(MoveCardMutation, new JsonObject { ["p"] = m.ProjectId, ["i"] = info.CardId, ["f"] = m.FieldId, ["o"] = option });
    }

    // ---- issues, labels, comments, pull requests --------------------------------------------------------------------------------------------

    public async Task<JsonNode> GetIssue(int item) => await Call(HttpMethod.Get, $"{Repo}/issues/{item}") ?? new JsonObject();

    public async Task SetBody(int item, string body) => await Call(HttpMethod.Patch, $"{Repo}/issues/{item}", new JsonObject { ["body"] = body });

    public async Task<int> CreateIssue(string title, string body, IReadOnlyList<string> labels)
    {
        var r = await Call(HttpMethod.Post, $"{Repo}/issues", new JsonObject { ["title"] = title, ["body"] = body, ["labels"] = new JsonArray(labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()) });
        return VerbContext.AsInt(r?["number"]);
    }

    public async Task AddLabels(int item, IReadOnlyList<string> names) =>
        await Call(HttpMethod.Post, $"{Repo}/issues/{item}/labels", new JsonObject { ["labels"] = new JsonArray(names.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()) });

    public async Task RemoveLabel(int item, string name) => await Call(HttpMethod.Delete, $"{Repo}/issues/{item}/labels/{Uri.EscapeDataString(name)}", null, 404);

    public async Task PostComment(int number, string body) => await Call(HttpMethod.Post, $"{Repo}/issues/{number}/comments", new JsonObject { ["body"] = body });

    public async Task CloseIssue(int item, string reason) => await Call(HttpMethod.Patch, $"{Repo}/issues/{item}", new JsonObject { ["state"] = "closed", ["state_reason"] = reason });

    public async Task<IReadOnlyList<JsonObject>> OpenPulls() =>
        ((await Call(HttpMethod.Get, $"{Repo}/pulls?state=open&per_page=100")) as JsonArray ?? []).OfType<JsonObject>().ToList();

    public async Task<JsonObject> GetPull(int number) => await Call(HttpMethod.Get, $"{Repo}/pulls/{number}") as JsonObject ?? new JsonObject();

    public async Task ClosePull(int number) => await Call(HttpMethod.Patch, $"{Repo}/pulls/{number}", new JsonObject { ["state"] = "closed" });

    public async Task<IReadOnlyList<JsonObject>> WorkflowRuns(string headSha) =>
        ((await Call(HttpMethod.Get, $"{Repo}/actions/runs?head_sha={headSha}&per_page=50"))?["workflow_runs"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    public async Task<int> RerunFailedJobs(long runId) => (await Send(HttpMethod.Post, $"{Repo}/actions/runs/{runId}/rerun-failed-jobs")).Status;

    public async Task RemoveClaimRef(int item) => await Call(HttpMethod.Delete, $"{Repo}/git/refs/factory/{rules.Name}/claims/{item}", null, 404, 422);

    // ---- the plant steps (W9.10g) ----------------------------------------------------------------------------------------------------------------

    /// <summary><c>Get-ClaimRefItems</c>: matching-refs may ignore the page parameter (as Get-RevokedRefs), so the pages end on an empty one or on one that brings nothing new.</summary>
    public async Task<IReadOnlyList<int>> ClaimRefItems()
    {
        var prefix = $"refs/factory/{rules.Name}/claims/"; var seen = new HashSet<int>(); var found = new List<int>();
        for (var page = 1; page <= 50; page++)
        {
            var rows = (await Call(HttpMethod.Get, $"{Repo}/git/matching-refs/factory/{rules.Name}/claims/?per_page=100&page={page}")) as JsonArray ?? [];
            if (rows.Count == 0) break;
            var fresh = 0;
            foreach (var r in rows)
            {
                var name = Ps.Str(r?["ref"]);
                if (name.StartsWith(prefix, StringComparison.Ordinal) && Regex.IsMatch(name[prefix.Length..], @"^\d{1,9}$") && int.TryParse(name[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && seen.Add(n)) { found.Add(n); fresh++; }
            }
            if (fresh == 0) break;
        }
        return found;
    }

    public async Task<IReadOnlyList<JsonObject>> LabelledIssues(string state, int page) =>
        ((await Call(HttpMethod.Get, $"{Repo}/issues?state={state}&labels={Uri.EscapeDataString(rules.Label)}&per_page=100&page={page}")) as JsonArray ?? []).OfType<JsonObject>().ToList();

    public async Task<string> LastColumn()
    {
        var m = await Meta();
        return m.Options.Keys.LastOrDefault() ?? "";
    }
}

/// <summary>The state comment and the journal of an item: the W3.2 store and ledger, with their failures turned into the script's refusals.</summary>
sealed class KeeperStateStore(IJournalStore store, JournalLedger ledger, Action<string>? log = null) : IItemStateStore
{
    sealed class Holder(ItemState state) : StateHolder
    {
        public ItemState Item { get; } = state;
        public override JsonObject State => Item.State;
    }

    public async Task<StateHolder> Read(int item)
    {
        try { return new Holder(await store.Read(item, CancellationToken.None)); }
        catch (Exception e) when (e is HttpRequestException or JournalRefusedException) { throw Refusal(e); }
    }

    public async Task Save(int item, StateHolder holder)
    {
        try { await store.Save(item, ((Holder)holder).Item, CancellationToken.None); }
        catch (StateConflictException e) { throw new VerbRejection(11, "state-conflict", e.Message, new Dictionary<string, object?> { ["item"] = item, ["retryAfterSeconds"] = 1 }); }
        catch (Exception e) when (e is HttpRequestException or JournalRefusedException) { throw Refusal(e); }
    }

    // Add-Journal never fails a verb: a line that cannot be written is logged and false
    public async Task<bool> Journal(int item, StateHolder? holder, string ev, JsonObject fields, string key)
    {
        try
        {
            var f = JournalLedger.FieldsOf(fields);
            if (holder is Holder h) return await ledger.AddTo(item, h.Item, ev, f, key);
            return (await ledger.AppendHeld(item, ev, f, key)).Posted;   // the verb holds the item's lock (KeeperFileLocks.Item), which is not reentrant
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            (log ?? Console.Error.WriteLine)($"journal {ev} for #{item} failed: {e.Message}");
            return false;
        }
    }

    static VerbRejection Refusal(Exception e) => e is HttpRequestException hr && hr.StatusCode is { } code
        ? new VerbRejection(20, "github-error", $"GitHub {(int)code} on the state comment of the item", new Dictionary<string, object?> { ["status"] = (int)code, ["retryAfterSeconds"] = 30 })
        : VerbRejection.Retry(20, "github-error", e.Message, 30);
}

/// <summary>
/// <c>Use-ItemLock</c> and <c>Use-IntakeLock</c> (Core.ps1): a file in the state directory opened for exclusive use, polled every 300 ms for 45 s, then the 'item-busy' or 'intake-busy' refusal. The same files as the rule scripts'
/// (item-N.lock, board-intake.lock in FACTORY_STATE_DIR, else the factory data directory's state folder), so the scripts and this engine exclude each other. W9.10l: the item's lock is the gateway's one <see cref="IItemLock"/>
/// (ItemLocks.cs), the lock of every port and of the journal ledger, handed out here as a handle; only the board-intake lock is this class's own file.
/// </summary>
sealed class KeeperFileLocks(IItemLock items, Func<string, string?>? env = null, TimeSpan? wait = null) : IVerbLocks
{
    readonly Func<string, string?> env = env ?? Environment.GetEnvironmentVariable;
    readonly TimeSpan wait = wait ?? TimeSpan.FromSeconds(45);

    /// <summary><c>Get-StateDir</c>: FACTORY_STATE_DIR, else &lt;data directory&gt;/state; the data directory is FACTORY_DATA_DIR, else ~/.local/share/factory-worker, else the temp folder's factory-worker (Paths.ps1).</summary>
    public string StateDir()
    {
        string dir;
        if (env("FACTORY_STATE_DIR") is { Length: > 0 } s) dir = s;
        else
        {
            string data;
            if (env("FACTORY_DATA_DIR") is { Length: > 0 } d) data = d;
            else
            {
                string? home = new[] { env("HOME"), env("USERPROFILE"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.FirstOrDefault(h => !string.IsNullOrEmpty(h) && Directory.Exists(h));
                data = Path.Combine(Path.GetTempPath(), "factory-worker");
                if (home != null)
                {
                    var candidate = Path.Combine(home, ".local/share/factory-worker");
                    try { Directory.CreateDirectory(candidate); data = candidate; } catch (Exception) { }
                }
            }
            dir = Path.Combine(data, "state");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The item's lock as a handle: <see cref="IItemLock"/> takes a body, so the body here waits until the handle is disposed. A lock that stays busy for the wait is the refusal 'item-busy' (20).</summary>
    public Task<IAsyncDisposable> Item(int item) => Item(item, wait);

    /// <summary><c>Use-ItemLock $n {...} $seconds</c> (W9.10g: the claim-ref reap takes it without waiting, the board guard's journal line waits 3 seconds).</summary>
    public async Task<IAsyncDisposable> Item(int item, TimeSpan waitFor)
    {
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = items.Use(item, async () => { acquired.TrySetResult(); await release.Task; return 0; }, (int)Math.Ceiling(waitFor.TotalSeconds), CancellationToken.None);
        if (await Task.WhenAny(acquired.Task, held) == held)
        {
            try { await held; }
            catch (ItemBusyException) { throw VerbRejection.Retry(20, "item-busy", $"another operation on item {item} is running", 5); }
        }
        return new ItemHandle(release, held);
    }

    sealed class ItemHandle(TaskCompletionSource release, Task held) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { release.TrySetResult(); await held; }
    }

    public Task<IAsyncDisposable> Intake() => Intake(wait);

    /// <summary><c>Use-IntakeLock {...} $seconds</c> (W9.10g: the board guard takes it without waiting).</summary>
    public Task<IAsyncDisposable> Intake(TimeSpan waitFor) => Take("board-intake.lock", () => VerbRejection.Retry(20, "intake-busy", "another work item is being put on the board", 5), waitFor);

    async Task<IAsyncDisposable> Take(string file, Func<VerbRejection> busy, TimeSpan waitFor)
    {
        var path = Path.Combine(StateDir(), file);
        var deadline = DateTime.UtcNow + waitFor;
        while (true)
        {
            try { return new Lock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) { if (DateTime.UtcNow > deadline) throw busy(); await Task.Delay(300); }
            catch (UnauthorizedAccessException) { if (DateTime.UtcNow > deadline) throw busy(); await Task.Delay(300); }
        }
    }

    sealed class Lock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { stream.Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>The word to the saga: an item the saga owns, whose park a verb released or which a verb closed.</summary>
sealed class KeeperSagaPort(V2Ownership? ownership, RepoId home, Func<object, Task<bool>> publish) : IKeeperSaga
{
    public async Task<bool> Owns(int item) => ownership is not null && await ownership.OwnsOrFindAsync(item);

    public async Task Resumed(int item, string key) => await Send(new ResumeWorkItem(new ItemId(home, item), 0, key));

    public async Task Closed(int item, string reason, string key) => await Send(new ParkWorkItem(new ItemId(home, item), 0, key, reason));

    // W9.10k: the plant manager's release-owner and requeue
    public async Task OwnerReleased(int item, string reason, string key) => await Send(new OwnerReleaseAsked(new ItemId(home, item), 0, key, reason));

    public async Task Requeued(int item, string reason, string key) => await Send(new RequeueWorkItem(new ItemId(home, item), 0, key, reason));

    // the verb's answer is the caller's, whatever the saga takes: a message the host did not take is logged (the saga's lease and the board's reconciler catch up)
    async Task Send(WorkItemInput input)
    {
        try { if (!await publish(input)) Console.Error.WriteLine($"keeper: {input.GetType().Name} for {input.Item} was not taken by the Wolverine host"); }
        catch (Exception e) { Console.Error.WriteLine($"keeper: {input.GetType().Name} for {input.Item}: {e.GetType().Name}"); }
    }
}

/// <summary>The first-seen times of the claim refs that have no live claim, in the file the rule script keeps (<c>claim-orphans.json</c>: item number to ISO time), so a restart does not start every wait again.</summary>
sealed class FileClaimOrphans(Func<string> stateDir) : IClaimOrphanMemory
{
    string OrphanFile => Path.Combine(stateDir(), "claim-orphans.json");

    public IReadOnlyDictionary<int, DateTime> Load()
    {
        var found = new Dictionary<int, DateTime>();
        try
        {
            if (!File.Exists(OrphanFile) || JsonNode.Parse(File.ReadAllText(OrphanFile)) is not JsonObject o) return found;
            foreach (var kv in o)
                if (int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && Ps.FromIso(kv.Value) is { } at) found[n] = at;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { }   // a file that cannot be read starts the waits again, which only delays a removal
        return found;
    }

    public void Save(IReadOnlyDictionary<int, DateTime> firstSeen)
    {
        var o = new JsonObject();
        foreach (var kv in firstSeen.OrderBy(k => k.Key)) o[kv.Key.ToString(CultureInfo.InvariantCulture)] = Ps.Iso(kv.Value);
        File.WriteAllText(OrphanFile, o.ToJsonString());
    }
}

static class KeeperWiring
{
    /// <summary>
    /// The verbs, made from the container: null (nothing is registered, v1 answers) without the instance configuration or its home repository, or without a GitHub identity (GH_TOKEN or the author App), which is what the rule
    /// scripts need as well.
    /// </summary>
    public static KeeperVerbs? Make(IServiceProvider services, Func<string, string?>? env = null, HttpClient? http = null) =>
        Build(services, env ?? Environment.GetEnvironmentVariable, http) is { } p ? new KeeperVerbs(p.Cx) : null;

    /// <summary>
    /// W9.10g: the plant steps (claim-ref reap, board guard, replenishment, the keeper's count) over the same ports as the verbs, for the recurring jobs: null without the instance configuration, its home repository or a GitHub
    /// identity. <paramref name="mayAct"/> is the lease: a step that finds it ended stops before its next write. The claim refs' first-seen memory is the file of the rule script (claim-orphans.json of the state directory).
    /// </summary>
    public static PlantSteps? MakePlantSteps(IServiceProvider services, Func<bool> mayAct, Func<string, string?>? env = null, HttpClient? http = null, IClaimOrphanMemory? orphans = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (Build(services, env, http) is not { } p) return null;
        return new PlantSteps(p.Cx, p.GitHub, orphans ?? new FileClaimOrphans(p.Locks.StateDir), PlantStepSettings.From(p.Root, env), mayAct);
    }

    /// <summary>V1.4: the flag of a Release Queue card without a merge record, over the same ports as the verbs; null without the instance configuration, its home repository or a GitHub identity.</summary>
    public static ReleaseQueueFlags? MakeReleaseQueueFlags(IServiceProvider services, Func<string, string?>? env = null, HttpClient? http = null) =>
        Build(services, env ?? Environment.GetEnvironmentVariable, http) is { } p ? new ReleaseQueueFlags(p.Cx) : null;

    sealed record Parts(VerbContext Cx, KeeperGitHub GitHub, KeeperFileLocks Locks, JsonNode Root);

    static Parts? Build(IServiceProvider services, Func<string, string?> env, HttpClient? http)
    {
        if (services.GetService<InstanceConfig>() is not { Root: { } root, Home: { } home }) return null;
        var tokens = GitHubTokenProvider.FromEnvironment(env);
        if (tokens is null) return null;
        var rules = FactoryRules.From(root, env);
        if (rules.Owner.Length == 0 || rules.Repo.Length == 0) return null;
        var api = env("GITHUB_API_URL");
        if (http is null)
        {
            http = new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(120) };
            // the User-Agent of the rule scripts, also on the state and journal writes of the W3.2 store: the golden writes count these as the rule's own, as they were
            http.DefaultRequestHeaders.UserAgent.ParseAdd("factory-worker-gateway");
        }
        var budget = services.GetService<GitHubBudget>();
        var project = 0; try { project = root["project"] is JsonNode p ? VerbArgs.ToInt(Ps.Str(p)) : 0; } catch (FormatException) { }
        var github = new KeeperGitHub(http, rules, project, budget is null ? null : budget.Observe);
        var clients = new GitHubClientFactory(http, new ProviderCredentialSource(tokens), new Fworker.Integrations.GitHub.GitHubBudget());
        var identity = Identity(root, env, http, tokens);
        // made at the first read of a state: a GitHub that cannot be reached at the start (an App token that cannot be minted) does not stop the gateway from starting
        IJournalStore store = new LazyJournalStore(async ct => new GitHubJournalStore(await clients.For(home, ct), identity));
        var itemLocks = ItemLocks.From(services, env);
        var ledger = new JournalLedger(store, JournalOptions(root, env), itemLock: itemLocks);
        var host = services.GetService<WolverineHostService>();
        var ownership = services.GetService<WorkItemParts>()?.Ownership;
        IKeeperSaga saga = host is null || ownership is null ? new NoSaga() : new KeeperSagaPort(ownership, home, m => host.PublishAsync(m));
        var locks = new KeeperFileLocks(itemLocks, env);
        return new Parts(new VerbContext(rules, github, new KeeperStateStore(store, ledger), locks, saga, null, services.GetService<Plant>()?.KeeperCap), github, locks, root);   // (V1.4: the keeper's limit of new items a day is the plant's record, the one the pipeline checks)
    }

    // the secrets catalogue and the waiting minutes of the journal, from the configuration the rule scripts read
    static Fworker.Integrations.Commands.JournalSettings JournalOptions(JsonNode root, Func<string, string?> env)
    {
        var minutes = 30.0;
        try { if (root["timing"]?["journalWaitingMinutes"] is JsonValue v && v.TryGetValue<double>(out var d)) minutes = d; } catch { }
        if (env("FACTORY_TIMING_journalWaitingMinutes") is { Length: > 0 } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var o)) minutes = o;
        var patterns = new List<string>();
        try { foreach (var group in new[] { "patterns", "redactOnly" }) if (root["secrets"]?[group] is JsonArray a) patterns.AddRange(a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0)); } catch { }
        return new Fworker.Integrations.Commands.JournalSettings(minutes, patterns);
    }

    // who the gateway writes as: the state comment of the instance and the logins of an earlier identity (as the journal command's store)
    static JournalIdentity Identity(JsonNode root, Func<string, string?> env, HttpClient http, GitHubTokenProvider tokens)
    {
        var name = root["name"]?.ToString() ?? "";
        var authors = root["stateAuthors"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        var login = env("FACTORY_SERVER_LOGIN");
        return new JournalIdentity(name, async ct =>
        {
            if (!string.IsNullOrEmpty(login)) return login;
            // an installation token cannot read /user: the App's login is its slug plus [bot]
            if (tokens.AppLogin is { } bot) return login = bot;
            using var req = new HttpRequestMessage(HttpMethod.Get, "user");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            login = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["login"]?.ToString() ?? throw new JournalRefusedException("GitHub did not name the gateway's login");
            return login;
        }, authors);
    }
}
