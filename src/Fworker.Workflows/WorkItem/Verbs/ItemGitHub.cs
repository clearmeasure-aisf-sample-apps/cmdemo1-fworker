using System.Text.Json.Nodes;

namespace Fworker.Workflows.WorkItem.Verbs;

/// <summary>A GitHub answer the caller allowed although it is no 2xx (<c>-Allow</c>): the status and what GitHub said; or the body of a 2xx.</summary>
public sealed record ItemGhReply(bool Allowed, int Status, JsonNode? Body);

/// <summary>
/// The rule scripts' reads and writes of GitHub on top of the wire (Core.ps1): <c>Invoke-Gh</c> with its refusals (not found 12, rate limit 20, forbidden 30, any other 20), the allowed statuses, and
/// <c>Invoke-GhGraphQL</c>. The repository is the instance's home repository, as <c>Get-RepoPath</c>.
/// </summary>
public sealed class ItemGitHub(IItemWire wire, ItemVerbConfig config, IItemClock? clock = null)
{
    readonly IItemClock clock = clock ?? new SystemItemClock();

    public ItemVerbConfig Config => config;
    public string RepoPath => config.RepoPath;

    /// <summary><c>Invoke-Gh</c>: the body of a 2xx, else the refusal.</summary>
    public async Task<JsonNode?> Json(string method, string path, JsonNode? body, CancellationToken ct)
    {
        var r = await wire.Send(new ItemGhCall(method, path, body), ct);
        if (r.Status is >= 200 and < 300) return r.Body;
        throw Refusal(method, path, r);
    }

    public Task<JsonNode?> Get(string path, CancellationToken ct) => Json("GET", path, null, ct);

    /// <summary><c>Invoke-Gh -Allow</c>: the body of a 2xx, or the answer itself for a status the caller allows.</summary>
    public async Task<ItemGhReply> Allow(string method, string path, int[] allow, JsonNode? body, CancellationToken ct)
    {
        var r = await wire.Send(new ItemGhCall(method, path, body), ct);
        if (r.Status is >= 200 and < 300) return new ItemGhReply(false, r.Status, r.Body);
        if (allow.Contains(r.Status)) return new ItemGhReply(true, r.Status, r.Body);
        throw Refusal(method, path, r);
    }

    /// <summary><c>Invoke-GhRaw</c>: whatever GitHub answered, for a caller that decides itself.</summary>
    public Task<ItemGhAnswer> Raw(ItemGhCall call, CancellationToken ct) => wire.Send(call, ct);

    ItemRefusal Refusal(string method, string path, ItemGhAnswer r)
    {
        var detail = r.Body is null ? "null" : r.Body is JsonValue v && v.TryGetValue<string>(out var s) ? s : r.Body.ToJsonString();
        if (detail.Length > 400) detail = detail[..400];
        switch (r.Status)
        {
            case 404:
                return new ItemRefusal(12, "github-not-found", $"GitHub 404 on {method} {path}", new JsonObject { ["detail"] = detail });
            case 403 or 429:
                var msg = Ps.Get(r.Body, "message") is { } m ? Ps.Text(m) : detail;
                var wait = RateLimitWait(r.Status, r, msg);
                if (wait >= 0)
                {
                    var x = new JsonObject { ["retryAfterSeconds"] = wait, ["detail"] = detail };
                    if (long.TryParse(r.Header("x-ratelimit-reset"), out var reset)) x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime.ToString("o");
                    return new ItemRefusal(20, "github-rate-limited", $"GitHub {r.Status} on {method} {path}", x);
                }
                return new ItemRefusal(30, "github-forbidden", $"GitHub refused {method} {path} (403): {msg}", new JsonObject { ["status"] = 403, ["detail"] = detail });
            default:
                return new ItemRefusal(20, "github-error", $"GitHub {r.Status} on {method} {path}", new JsonObject { ["status"] = r.Status, ["detail"] = detail, ["retryAfterSeconds"] = 30 });
        }
    }

    /// <summary><c>Get-GhRateLimitWait</c>: the seconds to wait when a 403 or 429 is a rate limit, else -1.</summary>
    int RateLimitWait(int status, ItemGhAnswer r, string message)
    {
        var ra = r.Header("retry-after");
        if (ra is { Length: > 0 } && ra.All(char.IsAsciiDigit)) return Math.Max(1, int.Parse(ra));
        var rem = r.Header("x-ratelimit-remaining"); var reset = r.Header("x-ratelimit-reset");
        if (rem == "0")
        {
            if (reset is not null && long.TryParse(reset, out var rs)) return (int)Math.Max(1, rs - new DateTimeOffset(clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds());
            return 60;
        }
        if (status == 429 || Ps.Match(message, "rate limit|abuse detection|secondary rate")) return 60;
        return -1;
    }

    /// <summary><c>Invoke-GhGraphQL</c>: the data of the answer, or the refusal.</summary>
    public async Task<JsonNode?> GraphQL(string query, JsonObject variables, CancellationToken ct)
    {
        var r = await wire.Send(new ItemGhCall("POST", "graphql", new JsonObject { ["query"] = query, ["variables"] = variables.DeepClone() }), ct);
        if (r.Status != 200) throw new ItemRefusal(20, "github-graphql-error", $"GraphQL HTTP {r.Status}", new JsonObject { ["retryAfterSeconds"] = 30 });
        if (Ps.Get(r.Body, "errors") is JsonArray { Count: > 0 } errors)
        {
            var msg = string.Join("; ", errors.Select(e => Ps.Text(e, "message")));
            if (errors.Any(e => Ps.Text(e, "type") == "RATE_LIMITED"))
            {
                var reset = r.Header("x-ratelimit-reset");
                var wait = reset is not null && long.TryParse(reset, out var rs) ? (int)Math.Max(1, rs - new DateTimeOffset(clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds()) : 60;
                var x = new JsonObject { ["retryAfterSeconds"] = wait };
                if (reset is not null && long.TryParse(reset, out var rs2)) x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(rs2).UtcDateTime.ToString("o");
                throw new ItemRefusal(20, "github-rate-limited", msg, x);
            }
            throw new ItemRefusal(20, "github-graphql-error", msg, new JsonObject { ["retryAfterSeconds"] = 30 });
        }
        return Ps.Get(r.Body, "data");
    }

    /// <summary>A list read page by page (<c>?per_page=100&amp;page=N</c> until a page comes back short).</summary>
    public async Task<List<JsonNode>> Pages(string pathWithQuery, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        for (var page = 1; ; page++)
        {
            var sep = pathWithQuery.Contains('?') ? '&' : '?';
            var chunk = await Get($"{pathWithQuery}{sep}per_page=100&page={page}", ct);
            var items = chunk switch { JsonArray a => a.Where(x => x is not null).Select(x => x!).ToList(), null => [], var one => [one] };
            all.AddRange(items);
            if (items.Count != 100) return all;
        }
    }
}
