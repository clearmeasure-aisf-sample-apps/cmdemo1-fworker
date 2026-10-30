using System.Text.Json.Nodes;

namespace Fworker.Workflows.ReadModels;

/// <summary>One request to GitHub (<c>Invoke-GhRaw</c>): <see cref="Path"/> is relative to the API root (<c>repos/o/r/pulls/3</c>, <c>graphql</c>); <see cref="OutFile"/> writes the body to that file instead of reading it.</summary>
public sealed record GhCall(string Method, string Path, JsonNode? Body = null, string Accept = "application/vnd.github+json", string? OutFile = null, TimeSpan? Timeout = null, int Tries = 3);

/// <summary>GitHub's answer: the status, the JSON body (null when there is none or it went to a file) and the headers.</summary>
public sealed record GhAnswer(int Status, JsonNode? Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// The wire to GitHub with the gateway's identity, one request at a time, as <c>Invoke-GhRaw</c> makes it: the headers, the retries on a network fault or a 502 to 504, the rate-limit numbers handed to the gateway's budget. A fault that
/// outlasts the tries is a <see cref="VerbRefusal"/> (20, github-unreachable). The gateway implements it; the tests have a scripted one.
/// </summary>
public interface IGitHubApi
{
    Task<GhAnswer> Send(GhCall call, CancellationToken ct);
}

/// <summary>What <see cref="RuleGitHub.Optional"/> read: the body, or the refusal as data (the answer of a verb is never an error because a detail could not be read).</summary>
public sealed record GhOptional(bool Ok, JsonNode? Body, string Code = "", string Reason = "");

/// <summary>A GitHub answer the caller allowed although it is no 2xx (<c>-Allow</c>): the status and what GitHub said; or the body of a 2xx.</summary>
public sealed record GhReply(bool Allowed, int Status, JsonNode? Body);

/// <summary>
/// The rule scripts' reads of GitHub on top of the wire (Core.ps1): <c>Invoke-Gh</c> with its refusals (not found 12, rate limit 20, forbidden 30, any other 20), <c>Invoke-GhGraphQL</c>, and the optional read of Checks.ps1 (<c>Invoke-GhOptional</c>)
/// that turns a refusal into data. The repository is the instance's home repository, as <c>Get-RepoPath</c>.
/// </summary>
public sealed class RuleGitHub(IGitHubApi api, ReadModelSettings settings, Func<DateTime>? clock = null)
{
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);

    public ReadModelSettings Settings => settings;
    public Func<DateTime> Now => now;

    /// <summary><c>Get-RepoPath</c>.</summary>
    public string RepoPath => $"repos/{settings.Owner}/{settings.Repo}";

    /// <summary><c>Invoke-Gh</c> without an allowance: the body of a 2xx, else the refusal.</summary>
    public async Task<JsonNode?> Json(string method, string path, JsonNode? body = null, CancellationToken ct = default, string accept = "application/vnd.github+json")
    {
        var r = await api.Send(new GhCall(method, path, body, accept), ct);
        if (r.Status is >= 200 and < 300) return r.Body;
        throw Refusal(method, path, r);
    }

    public Task<JsonNode?> Get(string path, CancellationToken ct = default) => Json("GET", path, null, ct);

    /// <summary><c>Invoke-Gh -Allow</c>: the body of a 2xx, or the answer itself (<see cref="GhReply.Allowed"/>) for a status the caller allows.</summary>
    public async Task<GhReply> Allow(string method, string path, int[] allow, JsonNode? body = null, CancellationToken ct = default)
    {
        var r = await api.Send(new GhCall(method, path, body), ct);
        if (r.Status is >= 200 and < 300) return new GhReply(false, r.Status, r.Body);
        if (allow.Contains(r.Status)) return new GhReply(true, r.Status, r.Body);
        throw Refusal(method, path, r);
    }

    /// <summary><c>Invoke-GhRaw</c>: whatever GitHub answered, for a caller that decides itself (a rerun it may be refused).</summary>
    public Task<GhAnswer> Raw(GhCall call, CancellationToken ct = default) => api.Send(call, ct);

    VerbRefusal Refusal(string method, string path, GhAnswer r)
    {
        var detail = r.Body is null ? "null" : r.Body is JsonValue v && v.TryGetValue<string>(out var s) ? s : r.Body.ToJsonString();
        if (detail.Length > 400) detail = detail[..400];
        switch (r.Status)
        {
            case 404:
                return new VerbRefusal(12, "github-not-found", $"GitHub 404 on {method} {path}", new JsonObject { ["detail"] = detail });
            case 403 or 429:
                var msg = Ps.Prop(r.Body, "message") is { } m ? Ps.Text(m) : detail;
                var wait = RateLimitWait(r.Status, r, msg);
                if (wait >= 0)
                {
                    var x = new JsonObject { ["retryAfterSeconds"] = wait, ["detail"] = detail };
                    if (long.TryParse(r.Header("x-ratelimit-reset"), out var reset)) x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime.ToString("o");
                    return new VerbRefusal(20, "github-rate-limited", $"GitHub {r.Status} on {method} {path}", x);
                }
                // a refusal of permission, not a rate limit: exit 30, with what GitHub said
                return new VerbRefusal(30, "github-forbidden", $"GitHub refused {method} {path} (403): {msg}", new JsonObject { ["status"] = 403, ["detail"] = detail });
            default:
                return new VerbRefusal(20, "github-error", $"GitHub {r.Status} on {method} {path}", new JsonObject { ["status"] = r.Status, ["detail"] = detail, ["retryAfterSeconds"] = 30 });
        }
    }

    /// <summary><c>Get-GhRateLimitWait</c>: the seconds to wait when a 403 or 429 is a rate limit (a Retry-After, no requests left, or the message of a limit), else -1.</summary>
    int RateLimitWait(int status, GhAnswer r, string message)
    {
        var ra = r.Header("retry-after");
        if (ra is not null && ra.Length > 0 && ra.All(char.IsAsciiDigit)) return Math.Max(1, int.Parse(ra));
        var rem = r.Header("x-ratelimit-remaining"); var reset = r.Header("x-ratelimit-reset");
        if (rem == "0")
        {
            if (reset is not null && long.TryParse(reset, out var rs)) return (int)Math.Max(1, rs - new DateTimeOffset(now(), TimeSpan.Zero).ToUnixTimeSeconds());
            return 60;
        }
        if (status == 429 || Ps.Match(message, "rate limit|abuse detection|secondary rate")) return 60;
        return -1;
    }

    /// <summary><c>Invoke-GhGraphQL</c>: the data of the answer, or the refusal (the HTTP status, the errors, a rate limit the query reports with status 200).</summary>
    public async Task<JsonNode?> GraphQL(string query, JsonObject variables, CancellationToken ct = default)
    {
        var r = await api.Send(new GhCall("POST", "graphql", new JsonObject { ["query"] = query, ["variables"] = variables.DeepClone() }), ct);
        if (r.Status != 200) throw new VerbRefusal(20, "github-graphql-error", $"GraphQL HTTP {r.Status}", new JsonObject { ["retryAfterSeconds"] = 30 });
        if (Ps.Prop(r.Body, "errors") is JsonArray { Count: > 0 } errors)
        {
            var msg = string.Join("; ", errors.Select(e => Ps.Str(e, "message")));
            if (errors.Any(e => Ps.Str(e, "type") == "RATE_LIMITED"))
            {
                var reset = r.Header("x-ratelimit-reset");
                var wait = reset is not null && long.TryParse(reset, out var rs) ? (int)Math.Max(1, rs - new DateTimeOffset(now(), TimeSpan.Zero).ToUnixTimeSeconds()) : 60;
                var x = new JsonObject { ["retryAfterSeconds"] = wait };
                if (reset is not null && long.TryParse(reset, out var rs2)) x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(rs2).UtcDateTime.ToString("o");
                throw new VerbRefusal(20, "github-rate-limited", msg, x);
            }
            throw new VerbRefusal(20, "github-graphql-error", msg, new JsonObject { ["retryAfterSeconds"] = 30 });
        }
        return Ps.Prop(r.Body, "data");
    }

    /// <summary>The longest one GitHub call for detail may take (<c>$script:CkCallSeconds</c>).</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>Invoke-GhOptional</c>: one GET that GitHub may refuse; the refusal is an answer, never an error. One attempt of at most <see cref="CallLimit"/>, and none at all once <paramref name="deadline"/> has passed
    /// (detail-deadline): collecting detail must never cost the caller its answer. <paramref name="what"/> names the thing for the code (logs, annotations, artifacts, baseline).
    /// </summary>
    public async Task<GhOptional> Optional(string path, string what, DateTime? deadline, string? outFile = null, CancellationToken ct = default)
    {
        var limit = CallLimit;
        if (deadline is { } d)
        {
            var left = (int)(d - now()).TotalSeconds;
            if (left < 1) return new GhOptional(false, null, "detail-deadline", "not read: the time this answer may take was used up; ask again, or for one check with --check");
            limit = TimeSpan.FromSeconds(Math.Min(CallLimit.TotalSeconds, left));
        }
        GhAnswer r;
        try { r = await api.Send(new GhCall("GET", path, OutFile: outFile, Timeout: limit, Tries: 1), ct); }
        catch (VerbRefusal e) { return new GhOptional(false, null, e.Code, $"GitHub could not be read for this ({what}): it did not answer in {(int)limit.TotalSeconds} s or the connection failed"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new GhOptional(false, null, "github-unreachable", $"GitHub could not be read for this ({what})"); }
        if (r.Status is >= 200 and < 300) return new GhOptional(true, r.Body);
        var msg = Ps.Str(r.Body, "message");
        var limited = r.Status == 429 || Ps.Match(msg, "rate limit|abuse") || r.Header("x-ratelimit-remaining") == "0";
        var need = what is "annotations" or "baseline" ? "Checks: read" : "Actions: read";
        if (limited) return new GhOptional(false, null, "github-rate-limited", $"GitHub {r.Status}: the rate limit of the gateway's identity is reached; ask again later");
        return r.Status switch
        {
            403 => new GhOptional(false, null, $"{what}-not-permitted", $"GitHub refused (403): the gateway's GitHub identity lacks the repository permission '{need}'. Only the owner can grant it; nothing a session does changes this."),
            404 => new GhOptional(false, null, $"{what}-not-found", $"GitHub has none (404). For a private repository this is also what a missing permission '{need}' looks like."),
            410 => new GhOptional(false, null, $"{what}-expired", "GitHub no longer keeps it (410): past the retention period"),
            _ => new GhOptional(false, null, "github-error", $"GitHub answered {r.Status}"),
        };
    }
}

/// <summary>The item's state comment (<c>Get-State</c>, <c>Save-State</c>): the state, and a read-change-save that the gateway makes against the revision it read.</summary>
public interface IItemStateStore
{
    /// <summary>Get-State: the state object of the item (a new one with schema and item when there is no comment yet).</summary>
    Task<JsonObject> Read(int item, CancellationToken ct);

    /// <summary>
    /// Reads the state, lets <paramref name="mutate"/> change it (it answers whether to save) and saves it. A state that changed since it was read is a <see cref="VerbRefusal"/> 11 state-conflict, as <c>Save-State</c> refuses it, after
    /// <paramref name="tries"/> reads in all (<c>Update-State</c>: <paramref name="mutate"/> runs again on each fresh read, so a mutation with an effect outside the state asks for one try). Returns whether it saved.
    /// </summary>
    Task<bool> Update(int item, Func<JsonObject, Task<bool>> mutate, CancellationToken ct, int tries = 1);
}

/// <summary>One verb at a time per item on this machine (<c>Use-ItemLock</c>), the lock the rule scripts take too: exit 20 item-busy when it is not free in 45 seconds.</summary>
public interface IItemLock
{
    Task<T> Use<T>(int item, Func<Task<T>> body, CancellationToken ct);
}
