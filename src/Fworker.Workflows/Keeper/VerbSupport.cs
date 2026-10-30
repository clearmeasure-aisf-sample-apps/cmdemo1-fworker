using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Keeper;

// W9.6 (#358): what the keeper and manager verbs of v1 (plugin/scripts/lib/Verbs.Keeper.ps1, Verbs.Manager.ps1) take from PowerShell and from Core.ps1, kept in one place so the
// verbs read as the scripts do: a refusal is an exit code, a code and a reason (Reject), the arguments are a hashtable read with Test-Has and Need, a value is stringified the way "$x"
// does, and a regular expression is matched with -match (case-insensitive, culture-invariant).

/// <summary>Core.ps1's <c>Reject</c>: the exit code (2 usage, 10 gate refused, 11 stale, 12 not found, 20 retryable external, 30 config, 40 claim held), the code, the reason, and the extra fields of the answer.</summary>
public sealed class VerbRejection(int exit, string code, string reason, IReadOnlyDictionary<string, object?>? extra = null) : Exception(reason)
{
    public int Exit { get; } = exit;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?> Extra { get; } = extra ?? new Dictionary<string, object?>();

    public static VerbRejection Usage(string reason) => new(2, "usage", reason);

    /// <summary>The refusals of an answer from GitHub that may be tried again later carry <c>retryAfterSeconds</c>.</summary>
    public static VerbRejection Retry(int exit, string code, string reason, int seconds) => new(exit, code, reason, new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds });
}

/// <summary>PowerShell's behaviour the scripts lean on.</summary>
public static class Ps
{
    /// <summary><c>-match</c>: case-insensitive, culture-invariant.</summary>
    public const RegexOptions Match = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static bool IsMatch(string input, string pattern, RegexOptions extra = RegexOptions.None) => Regex.IsMatch(input, pattern, Match | extra);

    /// <summary><c>-replace</c> is case-insensitive too.</summary>
    public static string Replace(string input, string pattern, string replacement) => Regex.Replace(input, pattern, replacement, Match);

    /// <summary><c>"$x"</c>: a string as it is, a number as its digits, a boolean as True or False, nothing as empty, a list as its items with a space between them.</summary>
    public static string Str(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("R", CultureInfo.InvariantCulture),
        JsonArray a => string.Join(' ', a.Select(Str)),
        JsonObject => "System.Collections.Hashtable",
        _ => n.ToJsonString(),
    };

    /// <summary>The truth of a value in <c>-and</c>, <c>-not</c> and <c>if</c>: a string is true when it is not empty, a number when it is not zero, a list when it has items.</summary>
    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<long>(out var l) => l != 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonArray a => a.Count > 0,
        _ => true,
    };

    /// <summary><c>$x -eq $true</c>: true for a boolean true, for the text "true" in any case (the boolean is converted to the text of the other side), and for a number other than zero.</summary>
    public static bool EqTrue(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => string.Equals(s, "True", StringComparison.OrdinalIgnoreCase),
        JsonValue v when v.TryGetValue<long>(out var l) => l == 1,
        _ => false,
    };

    /// <summary><c>Test-NameIn</c>: <c>-ieq</c> against each of the list.</summary>
    public static bool NameIn(string name, IEnumerable<string> list) => list.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>ConvertTo-Iso</c>.</summary>
    public static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>ConvertFrom-Iso</c>: null for nothing.</summary>
    public static DateTime? FromIso(JsonNode? n)
    {
        var s = Str(n);
        return s.Length == 0 ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    /// <summary><c>Test-Has</c> on a state or any object: the key is there and is not null.</summary>
    public static bool Has(JsonObject? o, string key) => o != null && o.TryGetPropertyValue(key, out var v) && v != null;
}

/// <summary>The arguments of a verb call: the JSON the rule script reads as a hashtable (<c>Test-Has</c>, <c>Need</c>, <c>"$($a.x)"</c>).</summary>
public sealed class VerbArgs(JsonObject body)
{
    public JsonObject Body { get; } = body;

    /// <summary>The value of a key, whatever the case of its name; null when it is not there.</summary>
    public JsonNode? Get(string key)
    {
        foreach (var kv in Body) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary><c>Test-Has $a 'key'</c>.</summary>
    public bool Has(string key) => Get(key) != null;

    /// <summary><c>"$($a.key)"</c>.</summary>
    public string Str(string key) => Ps.Str(Get(key));

    /// <summary><c>Need $a 'key'</c>: the key is there and its text is not empty, else a usage refusal.</summary>
    public string Need(string key)
    {
        if (!Has(key) || Str(key).Length == 0) throw VerbRejection.Usage($"missing argument: {key}");
        return Str(key);
    }

    /// <summary><c>Get-ItemNumber</c>: <c>[int](Need $a 'item')</c>.</summary>
    public int ItemNumber() => ToInt(Need("item"));

    /// <summary>PowerShell's <c>[int]</c> of a text; a text that is no number is an error of the script (an internal error, not a refusal).</summary>
    public static int ToInt(string text)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) < int.MaxValue) return (int)Math.Round(d, MidpointRounding.ToEven);
        throw new FormatException($"Cannot convert value \"{text}\" to type \"System.Int32\". Error: \"The input string '{text}' was not in a correct format.\"");
    }
}
