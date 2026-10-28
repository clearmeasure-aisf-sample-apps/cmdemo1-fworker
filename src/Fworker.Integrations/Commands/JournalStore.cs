using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Integrations.Commands;

// W3.2: the item's state comment and the issue comments the journal writes, as the journal ledger (JournalLedger) needs them. This is the C# side of
// Get-State, Save-State, Limit-StateBudget and Invoke-Gh of plugin/scripts/lib/{Board,Core}.ps1, kept to the same reads, writes and order.

/// <summary>One comment of an issue.</summary>
public sealed record IssueComment(long Id, string Login, string Body);

/// <summary>What Get-State returns: the state comment's id (null when there is none), the state, its revision and every comment of the issue.</summary>
public sealed class ItemState
{
    public long? CommentId { get; set; }
    public JsonObject State { get; set; } = new();
    /// <summary>The revision the state was read at; null for a holder that was not read (a cached read).</summary>
    public int? Rev { get; set; }
    public IReadOnlyList<IssueComment> Comments { get; init; } = [];
    public string? AdoptedFrom { get; init; }
}

/// <summary>The state comment changed since it was read (Reject 11 'state-conflict'): read again and retry.</summary>
public sealed class StateConflictException(string message) : Exception(message);

/// <summary>GitHub refused for good (a 4xx that is no rate limit), or the state does not fit its budget: not retried.</summary>
public sealed class JournalRefusedException(string message) : Exception(message);

public enum PatchOutcome { Ok, Gone, Forbidden }

/// <summary>The issue comments and the state comment of an item. The GitHub implementation is <see cref="GitHubJournalStore"/>; the tests have an in-memory one.</summary>
public interface IJournalStore
{
    /// <summary>Get-State: the state comment of the gateway's own login (or an adopted one) and all the comments of the issue.</summary>
    Task<ItemState> Read(int item, CancellationToken ct);
    /// <summary>Save-State: refused with <see cref="StateConflictException"/> when the revision moved since the read.</summary>
    Task Save(int item, ItemState holder, CancellationToken ct);
    /// <summary>POST issues/{item}/comments; the new comment's id (null when GitHub sent none).</summary>
    Task<long?> Post(int item, string body, CancellationToken ct);
    /// <summary>PATCH issues/comments/{id}: 404 and 410 are <see cref="PatchOutcome.Gone"/>, a 403 that is no rate limit <see cref="PatchOutcome.Forbidden"/>; anything else throws.</summary>
    Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct);
}

/// <summary>The format of the state comment (Board.ps1): the marker, the json fence, the revision, and the size budget with its trim.</summary>
public static class ItemStateComment
{
    /// <summary>GitHub caps a comment at 65,536 characters; the state keeps this budget (StateBudget).</summary>
    public const int Budget = 48000;
    static readonly string[] TrimOrder = ["promote_health", "flow", "lineage"];
    static readonly Regex Fence = new(@"```json\s*(.*?)\s*```", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Marker(string name) => $"<!-- factory-state:{name} v1 -->";

    /// <summary>The state in the first json fence of a comment body; null when there is none or it is not an object.</summary>
    public static JsonObject? Parse(string body)
    {
        var m = Fence.Match(body ?? "");
        if (!m.Success) return null;
        try { return JsonNode.Parse(m.Groups[1].Value) as JsonObject; } catch (JsonException) { return null; }
    }

    /// <summary>Get-StateRev: the counter in the state; a state written before it existed is revision 0.</summary>
    public static int RevOf(JsonObject? s)
    {
        if (s == null || !s.TryGetPropertyValue("rev", out var v) || v == null) return 0;
        return JournalJson.TryInt(v, out var i) ? i : 0;
    }

    public static string Body(string name, int item, JsonObject state) =>
        $"{Marker(name)}\nstate of #{item} for `{name}` (machine-read; edits by others are ignored)\n\n```json\n{state.ToJsonString(Indented)}\n```\n";

    /// <summary>
    /// Limit-StateBudget: trims state in place until its body fits the budget: the oldest entries of promote_health, flow and lineage go first, one
    /// round per step, and the trim is recorded in journal.trims (the last 5). A state that does not fit with those three empty is refused.
    /// </summary>
    public static void Limit(string name, int item, JsonObject s, DateTime now, Action<string>? log = null)
    {
        int Measure() => Body(name, item, s).Length;
        var before = Measure(); var len = before; var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        const int room = 400;
        while (len > Budget || (dropped.Count > 0 && len > Budget - room))
        {
            var entries = 0; foreach (var k in TrimOrder) entries += EntryCount(s, k);
            if (entries == 0) break;
            var step = Math.Max(1, (int)Math.Floor((len - (Budget - room)) / (Math.Max(1.0, (double)len / entries) * 3)));
            var any = false;
            foreach (var k in TrimOrder)
                for (var i = 0; i < step; i++)
                {
                    if (!RemoveOldest(s, k)) break;
                    dropped[k] = dropped.GetValueOrDefault(k) + 1; any = true;
                }
            if (!any) break;
            len = Measure();
        }
        if (dropped.Count > 0)
        {
            if (!JournalJson.Has(s, "journal")) s["journal"] = new JsonObject { ["keys"] = new JsonArray(), ["count"] = 0 };
            var j = (JsonObject)s["journal"]!;
            var trims = new JsonArray();
            if (j["trims"] is JsonArray prev) foreach (var t in prev) trims.Add(t?.DeepClone());
            var d = new JsonObject(); foreach (var kv in dropped) d[kv.Key] = kv.Value;
            trims.Add(new JsonObject { ["at"] = JournalJson.Iso(now), ["before"] = before, ["dropped"] = d });
            while (trims.Count > 5) trims.RemoveAt(0);
            j["trims"] = trims;
            log?.Invoke($"state of #{item} over the {Budget}-character budget ({before}): dropped {string.Join(", ", dropped.Select(kv => $"{kv.Key} x{kv.Value}"))}");
            len = Measure();
        }
        if (len > Budget) throw new JournalRefusedException($"state-too-large: the state of item {item} is {len} characters after trimming promote_health, flow and lineage; the budget is {Budget}");
    }

    static int EntryCount(JsonObject s, string key)
    {
        if (!JournalJson.Has(s, key)) return 0;
        switch (s[key])
        {
            case JsonObject o: return o.Sum(kv => kv.Value is JsonArray a ? a.Count : 1);
            case JsonArray a: return a.Count;
            default: return 0;
        }
    }

    static bool RemoveOldest(JsonObject s, string key)
    {
        if (!JournalJson.Has(s, key)) return false;
        switch (s[key])
        {
            case JsonObject c:
            {
                if (c.Count == 0) return false;
                string? best = null; var bestN = 0;
                foreach (var kv in c) if (kv.Value is JsonArray a && a.Count > bestN) { best = kv.Key; bestN = a.Count; }
                if (best != null)
                {
                    var arr = (JsonArray)c[best]!; arr.RemoveAt(0);
                    if (arr.Count == 0) c.Remove(best);
                    return true;
                }
                string? oldest = null; string? oldestAt = null;
                foreach (var kv in c)
                {
                    var at = kv.Value is JsonObject o && o["at"] != null ? o["at"]!.ToString() : "";
                    if (oldest == null || string.CompareOrdinal(at, oldestAt) < 0) { oldest = kv.Key; oldestAt = at; }
                }
                c.Remove(oldest!); return true;
            }
            case JsonArray a:
                if (a.Count == 0) return false;
                a.RemoveAt(0); return true;
            default: return false;
        }
    }
}

/// <summary>The small helpers of the state's json (PowerShell's Test-Has, [int] and ConvertTo-Iso / ConvertFrom-Iso).</summary>
public static class JournalJson
{
    /// <summary>Test-Has: the key exists and is not null.</summary>
    public static bool Has(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) && v != null;

    public static bool TryInt(JsonNode? n, out int i)
    {
        i = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<int>(out i)) return true;
        if (v.TryGetValue<long>(out var l)) { i = (int)l; return true; }
        if (v.TryGetValue<double>(out var d)) { i = (int)d; return true; }
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return true;
        return false;
    }

    /// <summary>[int]$o.k: 0 for a missing or unreadable value.</summary>
    public static int Int(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) && TryInt(v, out var i) ? i : 0;

    /// <summary>ConvertTo-Iso: yyyy-MM-ddTHH:mm:ss.fffZ.</summary>
    public static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>ConvertFrom-Iso: null for empty.</summary>
    public static DateTime? FromIso(string? s) =>
        string.IsNullOrEmpty(s) ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static string[] Strings(JsonObject o, string k) =>
        o.TryGetPropertyValue(k, out var v) && v is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToArray() : [];
}
