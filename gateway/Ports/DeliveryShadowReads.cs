using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Delivery;

// W5.2: the reads of the delivery train (ITrainReads) over HTTP, for the shadow run. READ ONLY, and a test pins it: every request this class sends is a GET (the pull request read of
// IMergeFactsReader is a GET too). The budget follows v1's rules (Verbs.Promote.ps1): a compare is kept for two minutes (the pass judges every release against the same range), a commit that is
// a work item's settled merge is kept (Find-CommitItem's commit cache), a commit that is none is not looked at again for ten minutes, an item that is no candidate is read once a minute at
// most, and the train itself spaces its probes of the app (the policy's read gap). The answers' rate-limit headers go to the gateway's GitHub budget.
// An answer that cannot change is kept for good (bounded by MaxKept): a compare of two fixed commits, and a commit that is none because it names no pull request or the pull request it names
// was not merged as it for longer than the ten minutes of the 'none' timer (a read of the pull request right after the merge can still say "not merged" or carry no merge commit: that is
// kept the short time, read again, and final only when the second read, ten minutes after the first, says the same). Without that, one evaluation of a long range (a read per commit) spends the minute's cap before it ends, the train retries it, and the answers it already had expire and are
// read again. A 'none' that depends on item state (no candidate item's state names the commit yet) and a 'waiting' keep their lifetimes: a longer one would put v2's decision behind v1's.
sealed class HttpTrainReads(HttpClient github, HttpClient app, string instanceName, IMergeFactsReader facts, ItemGateConfig gates, IDeliveryPolicies policies,
    Action<string, string?, string?, string?>? observe = null, Func<DateTimeOffset>? clock = null, Func<bool>? budgetLow = null) : ITrainReads
{
    /// <summary>Reads the shadow may make in a minute; v1's pass shares the token, so the shadow stays well under what a sweep needs.</summary>
    public const int MaxCallsPerMinute = 40;
    readonly Queue<DateTimeOffset> callTimes = new();
    static readonly TimeSpan CompareKeep = TimeSpan.FromMinutes(2), NoneKeep = TimeSpan.FromMinutes(10), GatesKeep = TimeSpan.FromMinutes(1), WaitingKeep = TimeSpan.FromSeconds(60);
    const int MaxKept = 500;

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly object gate = new();
    readonly Dictionary<string, (DateTimeOffset At, CompareRange? Range)> compares = [];
    readonly Dictionary<string, CompareRange> fixedCompares = [];
    readonly HashSet<string> neverItem = [];
    readonly Dictionary<string, (DateTimeOffset At, MergedPullRequest Pull)> pulls = [];
    readonly Dictionary<string, CommitItemLookup> settled = [];
    readonly Dictionary<string, DateTimeOffset> none = [];
    readonly Dictionary<string, DateTimeOffset> unmergedSince = [];   // when a read of the pull request first said "not merged as this commit": final only after NoneKeep
    readonly Dictionary<string, (DateTimeOffset At, CommitItemLookup Found)> waiting = [];
    readonly Dictionary<string, (DateTimeOffset At, ItemMergedGates? Gates)> gateReads = [];

    public async Task<CompareRange?> Compare(RepoId repository, string deployedCommit, string releaseCommit, CancellationToken ct)
    {
        var key = $"{repository}|{deployedCommit}|{releaseCommit}";
        var fixedRange = IsSha(deployedCommit) && IsSha(releaseCommit);
        lock (gate)
        {
            if (fixedRange && fixedCompares.TryGetValue(key, out var forever)) return forever;
            if (compares.TryGetValue(key, out var kept) && now() - kept.At < CompareKeep) return kept.Range;
        }
        var json = await Get($"repos/{repository}/compare/{deployedCommit}...{releaseCommit}", ct, allow: [HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity]);
        CompareRange? range = null;
        if (json is JsonObject o)
        {
            var commits = (o["commits"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new RangeCommit(Lower(c["sha"]), (c["parents"] as JsonArray ?? []).Select(p => Lower(p?["sha"])).ToList(), c["commit"]?["message"]?.ToString())).ToList();
            range = new CompareRange(o["status"]?.ToString(), commits, o["total_commits"] is JsonValue t && t.TryGetValue<int>(out var n) ? n : null);
        }
        lock (gate)
        {
            if (fixedRange && range is not null) { Trim(fixedCompares); fixedCompares[key] = range; }   // two fixed commits: GitHub's answer does not change (a 404 or 422 may, so it is kept the short time)
            else { Trim(compares); compares[key] = (now(), range); }
        }
        return range;
    }

    public async Task<CommitItemLookup> LookupCommit(RepoId repository, RangeCommit commit, CancellationToken ct)
    {
        var key = $"{repository}@{commit.Sha}";
        lock (gate)
        {
            if (settled.TryGetValue(key, out var s)) return s;
            if (neverItem.Contains(key)) return new CommitItemLookup(0, "");
            if (none.TryGetValue(key, out var at) && now() - at < NoneKeep) return new CommitItemLookup(0, "");
            if (waiting.TryGetValue(key, out var w) && now() - w.At < WaitingKeep) return w.Found;
        }
        var (found, verdict) = await Find(repository, commit, ct);
        lock (gate)
        {
            // the pull request read says "not merged as this commit": it may be a stale read right after the merge, so it is final only when it still says so NoneKeep after it first did
            var final = verdict == Finality.Yes;
            if (verdict == Finality.AfterGrace)
            {
                if (!unmergedSince.TryGetValue(key, out var since)) { Trim(unmergedSince); unmergedSince[key] = since = now(); }
                final = now() - since >= NoneKeep;
            }
            else unmergedSince.Remove(key);
            if (found.Item > 0 && found.Gap.Length == 0) { Trim(settled); settled[key] = found; }
            else if (found.Item == 0 && found.Gap.Length == 0 && final) { unmergedSince.Remove(key);  if (neverItem.Count >= MaxKept) foreach (var k in neverItem.Take(MaxKept / 4).ToList()) neverItem.Remove(k); neverItem.Add(key); }
            else if (found.Item == 0 && found.Gap.Length == 0) { Trim(none); none[key] = now(); }
            else if (found.Gap.Length > 0) { Trim(waiting); waiting[key] = (now(), found); }
        }
        return found;
    }

    /// <summary>
    /// Find-CommitItemUncached: the pull request the squash message names, merged as this commit, and the item of it whose state records the merge. Final: a none that cannot change
    /// (no pull request number in the subject, or the pull request is not merged as this commit), as against a none that waits for an item's state.
    /// </summary>
    enum Finality { No, Yes, AfterGrace }

    async Task<(CommitItemLookup Lookup, Finality Final)> Find(RepoId repository, RangeCommit commit, CancellationToken ct)
    {
        var first = (commit.Message ?? "").Split('\n')[0];
        var m = Regex.Match(first, @"\(#(\d+)\)\s*$");
        if (!m.Success) return (new CommitItemLookup(0, ""), Finality.Yes);
        var number = int.Parse(m.Groups[1].Value);
        // A merged pull request that names this commit is kept for CompareKeep: the read of its item's state may be refused by the minute's cap, and the
        // retry then does not read the pull request again (that read is a call of v1's token too).
        var pullKey = $"{repository}#{number}";
        MergedPullRequest? pull = null;
        lock (gate) if (pulls.TryGetValue(pullKey, out var kept) && now() - kept.At < CompareKeep) pull = kept.Pull;
        if (pull is null)
        {
            pull = await Facts(() => facts.ReadPullRequest(repository, number, ct));
            if (pull is { Merged: true } && string.Equals(pull.MergeCommit, commit.Sha, StringComparison.OrdinalIgnoreCase)) lock (gate) { Trim(pulls); pulls[pullKey] = (now(), pull); }
        }
        if (pull is null) return (new CommitItemLookup(0, ""), Finality.No);   // a 404 may be a permission or a lag, not a fact about the commit
        if (!pull.Merged || !string.Equals(pull.MergeCommit, commit.Sha, StringComparison.OrdinalIgnoreCase)) return (new CommitItemLookup(0, ""), Finality.AfterGrace);
        var short7 = commit.Sha.Length >= 7 ? commit.Sha[..7] : commit.Sha;
        foreach (var n in ContentGate.CandidateItems(pull.Number, pull.HeadRef, pull.Title, pull.Body))
        {
            var state = await ReadState(repository, n, ct);
            if (state is null) continue;
            var merge = state["merge"] as JsonObject;
            if (merge?["sha"]?.ToString().ToLowerInvariant() == commit.Sha)
            {
                var awaiting = merge["awaiting"]?.ToString() ?? "";
                var done = merge["done_at"] is { } d && d.ToString().Length > 0;
                if (awaiting == "release-queue" || (awaiting.Length == 0 && done)) return (new CommitItemLookup(n, ""), Finality.No);
                return (new CommitItemLookup(n, $"#{n}'s merge ({short7}) is not settled yet" + (awaiting.Length > 0 ? $" (awaiting {awaiting})" : "")), Finality.No);
            }
            var ownPr = state["verdict"]?["pr"]?.ToString() == pull.Number.ToString() || state["pr"]?["number"]?.ToString() == pull.Number.ToString();
            if (ownPr) return (new CommitItemLookup(n, $"#{n}'s merge of PR #{pull.Number} ({short7}) is not picked up by the gateway yet"), Finality.No);
        }
        return (new CommitItemLookup(0, ""), Finality.No);
    }

    /// <summary>The gateway's state comment of an item (the first comment with its marker), or null.</summary>
    async Task<JsonObject?> ReadState(RepoId repository, int number, CancellationToken ct)
    {
        var marker = ItemStateComment.Marker(instanceName);
        for (var page = 1; page <= 10; page++)
        {
            var comments = await Get($"repos/{repository}/issues/{number}/comments?per_page=100&page={page}", ct, allow: [HttpStatusCode.NotFound]) as JsonArray;
            if (comments is null) return null;
            foreach (var c in comments)
                if (c?["body"]?.ToString() is { } b && b.StartsWith(marker, StringComparison.Ordinal)) return ItemStateComment.Parse(b);
            if (comments.Count < 100) break;
        }
        return null;
    }

    public async Task<ItemMergedGates?> ReadGates(ItemId item, CancellationToken ct)
    {
        var key = item.ToString();
        lock (gate) if (gateReads.TryGetValue(key, out var kept) && now() - kept.At < GatesKeep) return kept.Gates;
        var read = await Facts(() => facts.ReadItem(item, ct));
        var g = read is null ? null : ItemMergedDeriver.GatesOf(read, item.Number, gates);
        lock (gate) { Trim(gateReads); gateReads[key] = (now(), g); }
        return g;
    }

    public async Task<EnvProbe> Probe(string deployable, string environment, CancellationToken ct)
    {
        var p = policies.For(deployable, environment);
        var url = p?.UrlOf(environment);
        if (p is null || url is null) return new EnvProbe(null, null, "", now());
        string? version = null;
        try
        {
            using var b = await app.GetAsync(url + "/_build", ct);
            if (b.IsSuccessStatusCode && JsonNode.Parse(await b.Content.ReadAsStringAsync(ct)) is JsonObject bo) version = bo["version"]?.ToString();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        var status = "unreachable";
        try
        {
            using var h = await app.GetAsync(url + p.HealthPath, ct);
            var text = await h.Content.ReadAsStringAsync(ct);
            status = (JsonNode.Parse(text) as JsonObject)?["overallStatus"]?.ToString() is { Length: > 0 } s ? s : $"status {(int)h.StatusCode} without overallStatus";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        return new EnvProbe(url, version, status, now());
    }

    // The shadow yields to v1: it reads nothing while the gateway's budget is low (the budget's Low), and at most MaxCallsPerMinute in a minute. A refused read is a failed read:
    // the train's input is retried by Wolverine and judged again later.
    void Admit()
    {
        if (budgetLow?.Invoke() == true) throw new HttpRequestException("the read budget is low: the shadow run does not read");
        lock (gate)
        {
            var t = now();
            while (callTimes.Count > 0 && t - callTimes.Peek() > TimeSpan.FromMinutes(1)) callTimes.Dequeue();
            if (callTimes.Count >= MaxCallsPerMinute) throw new HttpRequestException("the shadow run's reads of this minute are spent");
            callTimes.Enqueue(t);
        }
    }

    /// <summary>
    /// A read through the merge-facts reader (the pull request and the item). It is a GitHub call of v1's token like any other, so it obeys the same limits (<see cref="Admit"/>), and its refusal (a 5xx, a rate limit, no installation:
    /// <see cref="MergeFactsException"/>, <see cref="NoInstallationException"/>) is a failed read like the ones of <see cref="Get"/>: an <see cref="HttpRequestException"/>, which the train answers by looking again later. Left as it was, the
    /// exception was one the train does not know, and it ended a wake in the dead letters (tdd, 2026-10-30, 121 of them), and the reads were outside the cap and the low-budget check.
    /// </summary>
    async Task<T> Facts<T>(Func<Task<T>> read)
    {
        Admit();
        try { return await read(); }
        catch (Exception e) when (e is MergeFactsException or NoInstallationException) { throw new HttpRequestException(e.Message, e); }
    }

    async Task<JsonNode?> Get(string path, CancellationToken ct, HttpStatusCode[] allow)
    {
        Admit();
        using var resp = await github.GetAsync(path, ct);   // GET, always: this class has no other verb
        if (resp.Headers.TryGetValues("x-ratelimit-resource", out var res))
        {
            static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
            observe?.Invoke(res.First(), H(resp, "x-ratelimit-remaining"), H(resp, "x-ratelimit-reset"), H(resp, "x-ratelimit-limit"));
        }
        if (allow.Contains(resp.StatusCode)) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode} to a read of {path.Split('?')[0]}");
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    static bool IsSha(string s) => s.Length == 40 && s.All(Uri.IsHexDigit);

    static string Lower(JsonNode? n) => (n?.ToString() ?? "").ToLowerInvariant();

    static void Trim<TKey, TValue>(Dictionary<TKey, TValue> d) where TKey : notnull
    {
        if (d.Count < MaxKept) return;
        foreach (var k in d.Keys.Take(MaxKept / 4).ToList()) d.Remove(k);
    }
}
