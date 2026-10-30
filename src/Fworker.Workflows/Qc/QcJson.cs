using System.Globalization;
using System.Text.Json.Nodes;

namespace Fworker.Workflows.Qc;

// W9.1: the QC record and the state it lives in are JSON documents of v1's shape (plugin/scripts/lib/Qc.ps1 keeps them as hashtables). They stay JsonObjects here, field for field, so that the state comment v1 reads
// (the rollback path, the sweep's Invoke-QcSync, the merge gate's ran_sha) holds exactly what v1 wrote, and a field v2 does not know is kept when it saves.

/// <summary>A refusal of a verb: the exit code, the code and the reason of the answer, and the fields the answer carries besides (Reject of Core.ps1).</summary>
public sealed class QcReject(int exit, string code, string reason, JsonObject? extra = null) : Exception(reason)
{
    public int Exit { get; } = exit;
    public string Code { get; } = code;
    public JsonObject? Extra { get; } = extra;
}

/// <summary>The state comment moved since it was read (Save-State's Reject 11 'state-conflict'): the verb reads again and runs again.</summary>
public sealed class QcStateConflictException(string message) : Exception(message);

/// <summary>The small helpers of PowerShell's reading of a hashtable: Test-Has (the key exists and is not null), "$x" (the text of a value, empty for none) and [int] (0 for none).</summary>
public static class QcJson
{
    public static bool Has(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out var v) && v is not null;

    public static JsonObject? Obj(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out var v) ? v as JsonObject : null;

    public static JsonArray? Arr(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out var v) ? v as JsonArray : null;

    /// <summary>"$($o.key)": the text of the value; empty when the key is missing or null.</summary>
    public static string Str(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out var v) && v is not null ? Text(v) : "";

    public static string Text(JsonNode? v) => v switch
    {
        null => "",
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        JsonValue jv when jv.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonValue jv => jv.ToString(),
        _ => v.ToJsonString(),
    };

    public static int Int(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out var v) && TryInt(v, out var i) ? i : 0;

    public static bool TryInt(JsonNode? n, out int i)
    {
        i = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<int>(out i)) return true;
        if (v.TryGetValue<long>(out var l)) { i = (int)l; return true; }
        if (v.TryGetValue<double>(out var d)) { i = (int)d; return true; }
        return v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
    }

    /// <summary>[bool] of a value: true and false as they are, a text by whether it is empty (PowerShell), a number by whether it is 0.</summary>
    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<long>(out var l) => l != 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true,
    };

    public static bool Bool(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out var v) && Truthy(v);

    /// <summary>ConvertTo-Iso: yyyy-MM-ddTHH:mm:ss.fffZ.</summary>
    public static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>ConvertFrom-Iso: null for empty.</summary>
    public static DateTime? FromIso(string? s) =>
        string.IsNullOrEmpty(s) ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>Test-NameIn: the name is in the list, without regard to case.</summary>
    public static bool NameIn(string name, IEnumerable<string> list) => list.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    public static JsonArray ArrayOf(IEnumerable<string> items) { var a = new JsonArray(); foreach (var s in items) a.Add(s); return a; }

    /// <summary>A copy to put into an answer: a node that is part of the state has a parent and cannot be added to another object.</summary>
    public static JsonNode? Copy(JsonNode? n) => n?.DeepClone();
}
