using System.Globalization;
using System.Text.Json.Nodes;

namespace Fworker.Workflows.Adoption;

// W9.10m: reads of v1's state comment (plugin/scripts/lib/Board.ps1: Get-State, Get-Park, Test-ClaimLive), forgiving the way the script is: a key that is absent, null or of another type is "not there", never a fault. The state is a
// JSON object written by PowerShell (ConvertTo-Json): numbers may be text, times are ISO text, a map may hold null for a key that was cleared.

internal static class V1State
{
    public static JsonObject? Obj(JsonObject? o, string key) => Get(o, key) as JsonObject;

    public static JsonNode? Get(JsonObject? o, string key)
    {
        if (o is null) return null;
        try { return o.TryGetPropertyValue(key, out var v) ? v : null; } catch (InvalidOperationException) { return null; }
    }

    public static bool Has(JsonObject? o, string key) => Get(o, key) is not null;

    public static string Str(JsonObject? o, string key) => Get(o, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : Get(o, key) is JsonValue n && n.GetValueKind() is System.Text.Json.JsonValueKind.Number ? n.ToString() : "";

    public static int Int(JsonObject? o, string key)
    {
        if (Get(o, key) is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    public static bool Bool(JsonObject? o, string key) => Get(o, key) is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) && p);

    public static bool? NullableBool(JsonObject? o, string key) => Get(o, key) is JsonValue v ? (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) ? p : null) : null;

    public static DateTimeOffset? At(JsonObject? o, string key)
    {
        var text = Str(o, key);
        return text.Length > 0 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
    }

    public static List<string> Strings(JsonObject? o, string key)
    {
        var list = new List<string>();
        if (Get(o, key) is JsonArray a) foreach (var x in a) if (x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) list.Add(s);
        return list;
    }

    /// <summary>Get-StateRev: the revision counter of the comment; a state written before it existed is revision 0.</summary>
    public static int Rev(JsonObject? state) => Int(state, "rev");

    /// <summary>Get-Park: the park, or null when there is none or its time has run out (an expired park is no park).</summary>
    public static JsonObject? LivePark(JsonObject state, DateTimeOffset now)
    {
        if (Obj(state, "park") is not { } park) return null;
        return At(park, "until") is { } until && until < now ? null : park;
    }

    /// <summary>Test-ClaimLive: the claim's lease runs out after now.</summary>
    public static bool ClaimLive(JsonObject? claim, DateTimeOffset now) => claim is not null && At(claim, "lease_until") is { } until && until > now;
}
