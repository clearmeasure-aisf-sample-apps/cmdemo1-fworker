using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.ReadModels;

// W9.5 (#357): the read models of the verb groups (pr-checks, wait-for-checks, inspect-facts, get-delivery, model-tier, check-secrets) on v2, answering exactly what the rule scripts
// (plugin/scripts/lib/Verbs.Pr.ps1, Checks.ps1, Prs.ps1, Verbs.Inspect.ps1, Verbs.Delivery.ps1, Models.ps1, Verbs.Sweep.ps1) answer for the same input. This file is what they share: the refusal
// (Reject), the arguments (the JSON body the script read as a hashtable), the answer in the shape factory.ps1 --server printed, and the small PowerShell semantics the rules lean on (the
// way a value becomes text or a number, case-insensitive -match and -ieq).

/// <summary>The script's <c>Reject</c>: an answer with <c>ok</c> false, the code, the reason and the extra fields, and the exit code (2 usage, 10 refused, 11 stale, 12 not found, 20 retry later, 21 pending, 30 configuration).</summary>
public sealed class VerbRefusal(int exit, string code, string reason, JsonObject? extra = null) : Exception(reason)
{
    public int Exit { get; } = exit;
    public string Code { get; } = code;
    public JsonObject Extra { get; } = extra ?? [];
}

/// <summary>The JSON body of a verb call as the script's hashtable: a key is present when it is there and not null (<c>Test-Has</c>), and a value reads as text or as a number the way PowerShell converts it.</summary>
public sealed class VerbArgs
{
    readonly JsonObject body;

    public VerbArgs(JsonObject body) { this.body = body; }

    public static VerbArgs Parse(string stdin)
    {
        if (string.IsNullOrWhiteSpace(stdin)) return new VerbArgs([]);
        return new VerbArgs(JsonNode.Parse(stdin) as JsonObject ?? []);
    }

    public JsonObject Body => body;

    public JsonNode? Get(string key) => Ps.Prop(body, key);

    /// <summary>Test-Has.</summary>
    public bool Has(string key) => Get(key) is not null;

    /// <summary><c>"$($a[$key])"</c>: the text of the value ("" when there is none).</summary>
    public string Text(string key) => Ps.Text(Get(key));

    /// <summary><c>Need</c>: the value, or exit 2 usage "missing argument".</summary>
    public string Need(string key)
    {
        if (!Has(key) || Text(key) == "") throw new VerbRefusal(2, "usage", $"missing argument: {key}");
        return Text(key);
    }

    /// <summary><c>Get-ItemNumber</c>.</summary>
    public int ItemNumber() => Ps.ToInt(Need("item"));

    /// <summary>The value as <c>[int]</c> does it.</summary>
    public int Int(string key) => Ps.ToInt(Text(key));

    /// <summary><c>(Test-Has $a $k) -and "$($a.$k)" -ne 'False'</c>: a flag that is there and not false (any case).</summary>
    public bool Flag(string key) => Has(key) && !Text(key).Equals("False", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, JsonNode?>> Pairs => body;
}

/// <summary>PowerShell's conversions and comparisons, as the rule scripts rely on them.</summary>
public static class Ps
{
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A property of a JSON object, looked up without regard to case (a hashtable key is); null when the node is not an object or has no such property.</summary>
    public static JsonNode? Prop(JsonNode? node, string name)
    {
        if (node is not JsonObject o) return null;
        if (o.TryGetPropertyValue(name, out var exact)) return exact;
        foreach (var kv in o) if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>A property in a path of names.</summary>
    public static JsonNode? At(JsonNode? node, params string[] path)
    {
        foreach (var name in path) { node = Prop(node, name); if (node is null) return null; }
        return node;
    }

    /// <summary><c>"$value"</c>: strings as they are, numbers in the invariant culture, true and false as True and False, null as "".</summary>
    public static string Text(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
            if (v.TryGetValue<decimal>(out var m)) return m.ToString(CultureInfo.InvariantCulture);
            return v.ToString();
        }
        return node.ToJsonString();
    }

    /// <summary><c>"$value"</c> of a property.</summary>
    public static string Str(JsonNode? node, string name) => Text(Prop(node, name));

    /// <summary>The items of an array property without the nulls (<c>@($x | Where-Object { $_ })</c> for objects and strings); a property that is not an array reads as a one-item list, none as an empty one.</summary>
    public static IEnumerable<JsonNode> List(JsonNode? node, string name)
    {
        var p = Prop(node, name);
        if (p is JsonArray a) return a.Where(x => x is not null).Select(x => x!).ToList();
        return p is null ? [] : [p];
    }

    public static IEnumerable<JsonNode> Items(JsonNode? node)
    {
        if (node is JsonArray a) return a.Where(x => x is not null).Select(x => x!).ToList();
        return node is null ? [] : [node];
    }

    /// <summary><c>[bool]$value</c>: true and false as they are, a number unless it is zero, a string unless it is empty, an object or a list; nothing is false.</summary>
    public static bool Bool(JsonNode? node)
    {
        if (node is null) return false;
        switch (node.GetValueKind())
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined: return false;
            case JsonValueKind.Number: return decimal.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d != 0 : double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f != 0;
            case JsonValueKind.String: return node.ToString().Length > 0;
            default: return true;
        }
    }

    /// <summary><c>[int]$value</c>: a whole number as it is, a fraction rounded to even, a string parsed as a number; anything else is an error of the script (internal-error).</summary>
    public static int ToInt(JsonNode? node) => ToInt(Text(node));

    public static int ToInt(string text)
    {
        var t = text.Trim();
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && Math.Abs(d) < int.MaxValue) return (int)Math.Round(d, MidpointRounding.ToEven);
        throw new FormatException($"Cannot convert value \"{text}\" to type \"System.Int32\".");
    }

    public static long ToLong(JsonNode? node)
    {
        var t = Text(node).Trim();
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d)) return (long)Math.Round(d, MidpointRounding.ToEven);
        throw new FormatException($"Cannot convert value \"{t}\" to type \"System.Int64\".");
    }

    /// <summary><c>-ieq</c>.</summary>
    public static bool Ieq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Test-NameIn</c> / <c>-in</c> on strings: a name in a list without regard to case.</summary>
    public static bool NameIn(string name, IEnumerable<string> list) => list.Any(x => Ieq(x, name));

    /// <summary>The <c>-match</c> operator: a regular expression, case-insensitive, anywhere in the text.</summary>
    public static bool Match(string? input, string pattern) => Regex.IsMatch(input ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    public static Match? MatchOf(string? input, string pattern)
    {
        var m = Regex.Match(input ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        return m.Success ? m : null;
    }

    /// <summary><c>[regex]::Replace</c> (case-sensitive unless the pattern says otherwise).</summary>
    public static string Replace(string input, string pattern, string replacement) => Regex.Replace(input, pattern, replacement, RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Sort-Object on text: without regard to case.</summary>
    public static readonly StringComparer SortOrder = StringComparer.InvariantCultureIgnoreCase;

    /// <summary><c>ConvertFrom-Iso</c>: a time as UTC; a time with no zone is UTC. Null for nothing.</summary>
    public static DateTime? FromIso(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    /// <summary><c>ConvertTo-Iso</c>: <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> in UTC.</summary>
    public static string ToIso(DateTime at) => at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The way ConvertTo-Json writes a time that PowerShell had parsed from JSON (a local time with its offset, seven digits of fraction).</summary>
    public static string JsonTime(string text)
    {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) return text;
        return utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffK", CultureInfo.InvariantCulture);
    }
}

/// <summary>What the script printed: the exit code and the answer as one line of JSON, with the fields <c>verb</c>, <c>ok</c> and <c>exit</c> that <c>factory.ps1 --server</c> adds.</summary>
public static class VerbReply
{
    // written as the rule script wrote it: no escaping of <, >, & or of letters outside ASCII (the gateway re-serializes every script answer this way)
    static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static (int Exit, string Json) Ok(string verb, JsonObject result)
    {
        var o = new JsonObject();
        if (!Has(result, "ok")) o["ok"] = true;
        foreach (var kv in result) o[kv.Key] = kv.Value?.DeepClone();
        o["verb"] = verb;
        o["exit"] = 0;
        return (0, o.ToJsonString(Relaxed));
    }

    public static (int Exit, string Json) Refused(string verb, VerbRefusal r)
    {
        var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = r.Code, ["reason"] = r.Message };
        foreach (var kv in r.Extra) o[kv.Key] = kv.Value?.DeepClone();
        o["exit"] = r.Exit;
        return (r.Exit, o.ToJsonString(Relaxed));
    }

    /// <summary>The script's catch-all: a fault inside the verb is exit 20 internal-error, to be tried again.</summary>
    public static (int Exit, string Json) InternalError(string verb, Exception e)
    {
        var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "internal-error", ["reason"] = e.Message, ["retryAfterSeconds"] = 30, ["exit"] = 20 };
        return (20, o.ToJsonString(Relaxed));
    }

    static bool Has(JsonObject o, string key) => o.Any(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs a verb body and shapes what it answers or refuses, as the server wrapper of factory.ps1 does.</summary>
    public static async Task<(int Exit, string Json)> Run(string verb, Func<Task<JsonObject>> body)
    {
        try { return Ok(verb, await body()); }
        catch (VerbRefusal r) { return Refused(verb, r); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Console.Error.WriteLine($"internal error in {verb} : {e.GetType().Name}: {e.Message}"); return InternalError(verb, e); }
    }
}
