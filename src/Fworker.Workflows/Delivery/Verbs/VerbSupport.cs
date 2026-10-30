using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fworker.Workflows.Delivery.Verbs;

// W9.4 (#356): the delivery verbs on v2. This file is what the rule scripts' Core.ps1 and factory.ps1 give a verb: the rejection (FactoryReject, exit codes), the shape of an answer (Write-Out),
// the small helpers of the state's json (Test-Has, [int], ConvertTo-Iso) and the two ports a verb reaches the outside through. Fworker.Workflows never references Integrations, so GitHub, the item's
// state comment and Octopus are ports here (IGhTransport, IItemStateStore, IOctopusReads) and the gateway's adapters (gateway/Ports/DeliveryVerbHost.cs) are the HTTP side.

/// <summary>FactoryReject: a refusal with an exit code (2 usage, 10 gate refused, 11 stale, 12 not found, 20 retryable external, 21 pending, 30 config or secret, 40 claim held), a code and a reason.</summary>
public sealed class VerbReject(int exit, string code, string reason, JsonObject? extra = null) : Exception(reason)
{
    public int Exit => exit;
    public string Code => code;
    public JsonObject? Extra => extra;
}

/// <summary>What a GitHub call answered: the status, the parsed body (null when it had none) and the headers (lower case names).</summary>
public sealed record GhResult(int Status, JsonNode? Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public bool Ok => Status is >= 200 and < 300;
    public string Message => Body is JsonObject o && o["message"] is { } m ? m.ToString() : "";
}

/// <summary>
/// GitHub for a verb (Invoke-GhRaw): one request to the home repository's API, answered with the status and the body. It confirms the leader lease before a write, retries a network error and a
/// 502 to 504 as Invoke-GhRaw does, sends the test cause header, and reports the rate limit; a network error that is still there after the retries is a <see cref="VerbReject"/> 20
/// 'github-unreachable'. The path is relative to the API root ('repos/o/r/issues/5') or an absolute address.
/// </summary>
public interface IGhTransport
{
    Task<GhResult> Send(HttpMethod method, string path, JsonNode? body, CancellationToken ct, string? accept = null, int tries = 3);

    /// <summary>A GET of the Octopus API (Invoke-Octo): the parsed body, null for a 404; any other refusal a <see cref="VerbReject"/> (30 'secret-missing', 20 'octopus-error').</summary>
    Task<JsonNode?> OctopusGet(string path, CancellationToken ct);

    /// <summary>The release asset upload of publish-demo: POST of the file to the upload address, answered with the status and the body. Tried twice by the caller.</summary>
    Task<GhResult> UploadFile(string uploadUrl, string contentType, string file, CancellationToken ct);

    /// <summary>The installation's Actions permission as the author App reads it with its JWT ('read', 'write', 'none'), '' when it cannot be read (Get-AppActionsPermission).</summary>
    Task<string> AppActionsPermission(CancellationToken ct);
}

/// <summary>The item's state comment and the comments around it (Get-State, Save-State). Save is refused with a <see cref="VerbReject"/> 11 'state-conflict' when the revision moved since the read.</summary>
public interface IItemStateStore
{
    Task<StateHolder> Read(int item, CancellationToken ct);
    Task Save(int item, StateHolder holder, CancellationToken ct);
}

/// <summary>What Get-State returns: the state (the comment's json), the comment's id (null when the item has none yet) and the revision it was read at.</summary>
public sealed class StateHolder
{
    public long? CommentId { get; set; }
    public JsonObject State { get; set; } = new();
    public int? Rev { get; set; }
    /// <summary>What the store keeps of its own read (the comments around the state); not for the rules.</summary>
    public object? Tag { get; set; }
}

/// <summary>An item as one verb needs to know it (Get-ItemInfo): the issue and its card.</summary>
public sealed class ItemInfo
{
    public int Number { get; init; }
    public string NodeId { get; init; } = "";
    public bool Open { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Url { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? CardId { get; init; }
    public string? Column { get; init; }

    public bool HasLabel(string name) => Labels.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The small helpers of the state's json: PowerShell's Test-Has, "$x", [int] and [bool], ConvertTo-Iso and ConvertFrom-Iso.</summary>
public static class J
{
    /// <summary>Test-Has: the key exists and is not null.</summary>
    public static bool Has(JsonNode? o, string k) => o is JsonObject jo && jo.TryGetPropertyValue(k, out var v) && v != null;

    /// <summary>"$x": the text of a value, '' for null.</summary>
    public static string S(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s ?? "",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => n.ToJsonString(),
    };

    /// <summary>"$($o.k)": the text of a member, '' when the key is missing or null.</summary>
    public static string S(JsonNode? o, string k) => o is JsonObject jo && jo.TryGetPropertyValue(k, out var v) ? S(v) : "";

    /// <summary>[int]$x: 0 for a missing or unreadable value.</summary>
    public static int I(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
        return 0;
    }

    public static int I(JsonNode? o, string k) => o is JsonObject jo && jo.TryGetPropertyValue(k, out var v) ? I(v) : 0;

    public static long L(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
        return 0;
    }

    /// <summary>[bool]$x: a JSON boolean as it is; anything else by PowerShell's truthiness (a non-empty text, a non-zero number, an object).</summary>
    public static bool B(JsonNode? n)
    {
        if (n is null) return false;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s.Length > 0;
            if (v.TryGetValue<double>(out var d)) return d != 0;
            return true;
        }
        if (n is JsonArray a) return a.Count > 0;
        return true;
    }

    public static bool B(JsonNode? o, string k) => o is JsonObject jo && jo.TryGetPropertyValue(k, out var v) && B(v);

    /// <summary>`$x -eq $true`: a JSON true, or the text "true" (PowerShell compares a string with the text of true, ignoring case).</summary>
    public static bool IsTrue(JsonNode? n) => n is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>ConvertTo-Iso: yyyy-MM-ddTHH:mm:ss.fffZ.</summary>
    public static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>ConvertFrom-Iso: null for empty or unreadable.</summary>
    public static DateTime? FromIso(JsonNode? n)
    {
        var s = S(n);
        if (s.Length == 0) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    public static DateTime? FromIso(JsonNode? o, string k) => o is JsonObject jo && jo.TryGetPropertyValue(k, out var v) ? FromIso(v) : null;

    public static JsonObject Obj(JsonNode? o, string k) => o is JsonObject jo && jo[k] is JsonObject c ? c : new JsonObject();

    public static IEnumerable<JsonNode?> Items(JsonNode? n) => n switch { JsonArray a => a, null => [], _ => [n] };

    /// <summary>@($x): an array's items, a single value as one item, null as none.</summary>
    public static List<string> Strings(JsonNode? n) => Items(n).Where(x => x != null).Select(S).ToList();

    public static JsonObject Obj(params (string Key, object? Value)[] members)
    {
        var o = new JsonObject();
        foreach (var (k, v) in members) o[k] = Node(v);
        return o;
    }

    public static JsonNode? Node(object? v) => v switch
    {
        null => null,
        JsonNode n => n.Parent is null ? n : n.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        DateTime t => JsonValue.Create(Iso(t)),
        IEnumerable<string> list => new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        IEnumerable<JsonNode?> nodes => new JsonArray(nodes.Select(x => x?.Parent is null ? x : x.DeepClone()).ToArray()),
        _ => JsonValue.Create(v.ToString()),
    };

    /// <summary>The json of an answer as PowerShell's ConvertTo-Json writes it: compact.</summary>
    public static string Text(JsonNode n) => n.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>Invoke-Gh and Invoke-GhGraphQL over an <see cref="IGhTransport"/>: the answer of a 2xx, an allowed status as it is, any other refusal a <see cref="VerbReject"/> with the codes of Core.ps1.</summary>
public static class GhApi
{
    public static async Task<GhResult> Call(IGhTransport t, HttpMethod method, string path, JsonNode? body, CancellationToken ct, int[]? allow = null, string? accept = null)
    {
        var r = await t.Send(method, path, body, ct, accept);
        if (r.Ok) return r;
        if (allow is not null && allow.Contains(r.Status)) return r;
        var detail = r.Body is null ? "" : r.Body is JsonValue v && v.TryGetValue<string>(out var s) ? s : r.Body.ToJsonString();
        if (detail.Length > 400) detail = detail[..400];
        var detailObj = new JsonObject { ["detail"] = detail };
        switch (r.Status)
        {
            case 404: throw new VerbReject(12, "github-not-found", $"GitHub 404 on {method.Method} {path}", detailObj);
            case 403 or 429:
                var msg = r.Message is { Length: > 0 } m ? m : detail;
                var wait = RateLimitWait(r.Status, r.Headers, msg);
                if (wait >= 0)
                {
                    var x = new JsonObject { ["retryAfterSeconds"] = wait, ["detail"] = detail };
                    if (r.Headers is not null && r.Headers.TryGetValue("x-ratelimit-reset", out var reset) && long.TryParse(reset, out var rs))
                        x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(rs).UtcDateTime.ToString("o");
                    throw new VerbReject(20, "github-rate-limited", $"GitHub {r.Status} on {method.Method} {path}", x);
                }
                throw new VerbReject(30, "github-forbidden", $"GitHub refused {method.Method} {path} (403): {msg}", new JsonObject { ["status"] = 403, ["detail"] = detail });
            default:
                throw new VerbReject(20, "github-error", $"GitHub {r.Status} on {method.Method} {path}", new JsonObject { ["status"] = r.Status, ["detail"] = detail, ["retryAfterSeconds"] = 30 });
        }
    }

    public static Task<GhResult> Get(IGhTransport t, string path, CancellationToken ct, params int[] allow) => Call(t, HttpMethod.Get, path, null, ct, allow.Length == 0 ? null : allow);

    /// <summary>Get-GhRateLimitWait: the seconds to wait when GitHub says it is a rate limit, -1 when it is a refusal of permission.</summary>
    public static int RateLimitWait(int status, IReadOnlyDictionary<string, string>? headers, string message)
    {
        string? H(string n) => headers is not null && headers.TryGetValue(n, out var v) ? v : null;
        if (H("retry-after") is { } ra && int.TryParse(ra, out var secs)) return Math.Max(1, secs);
        if (H("x-ratelimit-remaining") == "0")
        {
            if (H("x-ratelimit-reset") is { } reset && long.TryParse(reset, out var rs)) return (int)Math.Max(1, rs - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return 60;
        }
        if (status == 429 || System.Text.RegularExpressions.Regex.IsMatch(message, "rate limit|abuse detection|secondary rate", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 60;
        return -1;
    }

    public static async Task<JsonNode> GraphQl(IGhTransport t, string query, JsonObject variables, CancellationToken ct)
    {
        var r = await t.Send(HttpMethod.Post, "graphql", new JsonObject { ["query"] = query, ["variables"] = variables }, ct);
        if (r.Status != 200) throw new VerbReject(20, "github-graphql-error", $"GraphQL HTTP {r.Status}", new JsonObject { ["retryAfterSeconds"] = 30 });
        if (r.Body?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var msg = string.Join("; ", errors.Select(e => J.S(e, "message")));
            if (errors.Any(e => J.S(e, "type") == "RATE_LIMITED"))
            {
                var wait = r.Headers is not null && r.Headers.TryGetValue("x-ratelimit-reset", out var reset) && long.TryParse(reset, out var rs) ? (int)Math.Max(1, rs - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : 60;
                var x = new JsonObject { ["retryAfterSeconds"] = wait };
                if (r.Headers is not null && r.Headers.TryGetValue("x-ratelimit-reset", out var rr) && long.TryParse(rr, out var rs2)) x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(rs2).UtcDateTime.ToString("o");
                throw new VerbReject(20, "github-rate-limited", msg, x);
            }
            throw new VerbReject(20, "github-graphql-error", msg, new JsonObject { ["retryAfterSeconds"] = 30 });
        }
        return r.Body?["data"] ?? new JsonObject();
    }
}

/// <summary>The one answer shape of a verb (factory.ps1, server mode): the result with verb, ok and exit; a refusal as {ok:false, verb, code, reason, ...extra, exit}.</summary>
public static class VerbAnswers
{
    public static (int Exit, string Output) Success(string verb, JsonObject result)
    {
        result["verb"] = verb;
        if (!result.ContainsKey("ok")) result["ok"] = true;
        result["exit"] = 0;
        return (0, J.Text(result));
    }

    public static (int Exit, string Output) Rejected(string verb, VerbReject e)
    {
        var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = e.Code, ["reason"] = e.Message };
        if (e.Extra is not null) foreach (var kv in e.Extra) o[kv.Key] = kv.Value?.DeepClone();
        o["exit"] = e.Exit;
        return (e.Exit, J.Text(o));
    }

    public static (int Exit, string Output) InternalError(string verb, Exception e)
    {
        var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "internal-error", ["reason"] = e.Message, ["retryAfterSeconds"] = 30, ["exit"] = 20 };
        return (20, J.Text(o));
    }

    /// <summary>Runs a verb body the way factory.ps1 --server does: a <see cref="VerbReject"/> is the refusal, a network fault the retryable 'github-unreachable', anything else 'internal-error' (exit 20).</summary>
    public static async Task<(int Exit, string Output)> Run(string verb, Func<Task<JsonObject>> body)
    {
        try { return Success(verb, await body()); }
        catch (VerbReject e) { return Rejected(verb, e); }
        catch (HttpRequestException e) { return Rejected(verb, new VerbReject(20, "github-unreachable", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 })); }
        catch (OperationCanceledException e) { return Rejected(verb, new VerbReject(20, "timeout", "the verb did not finish in time: " + e.Message, new JsonObject { ["retryAfterSeconds"] = 30 })); }
        catch (Exception e) { return InternalError(verb, e); }
    }
}

/// <summary>"One verb at a time per item" (Use-ItemLock) inside this process: the file lock of the rule scripts is another process's, the state comment's revision guards the rest.</summary>
public sealed class ItemLocks
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> locks = new();

    public async Task<T> With<T>(int item, Func<Task<T>> body, int waitSeconds = 45)
    {
        var gate = locks.GetOrAdd(item, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(waitSeconds)))
            throw new VerbReject(20, "item-busy", $"another operation on item {item} is running", new JsonObject { ["retryAfterSeconds"] = 5 });
        try { return await body(); }
        finally { gate.Release(); }
    }
}
