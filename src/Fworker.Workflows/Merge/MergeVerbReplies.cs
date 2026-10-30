using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;

namespace Fworker.Workflows.Merge;

// W9.3 (#355): the answers of the merge train verbs in the shape of the rule scripts (plugin/scripts/factory.ps1, server mode): a success carries the script's fields and, besides them, ok, verb and exit 0; a refusal
// (Reject) is ok false with verb, code, reason, the extra fields of the refusal and exit; a fault of the call itself is the script's internal-error (exit 20, retryAfterSeconds 30). The gateway's pipeline reads the exit
// code to choose the HTTP status (VerbPolicy.StatusOfExit) and the sessions read the fields, so the shape is the contract.

/// <summary>Builds the answers of a verb the way <c>factory.ps1 --server</c> prints them.</summary>
public static class MergeVerbReplies
{
    /// <summary>A success: the fields of the verb's own answer, then <c>ok</c> (true unless the answer says), <c>verb</c> and <c>exit</c> 0.</summary>
    public static VerbAnswer Ok(string verb, JsonObject fields)
    {
        if (!fields.ContainsKey("ok")) fields["ok"] = true;
        fields["verb"] = verb; fields["exit"] = 0;
        return new VerbAnswer(0, fields.ToJsonString());
    }

    /// <summary>A refusal of the script (<c>Reject exit code reason extra</c>): never a thrown exception in v2, but the same answer.</summary>
    public static VerbAnswer Reject(string verb, int exit, string code, string reason, JsonObject? extra = null)
    {
        var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = code, ["reason"] = reason };
        if (extra is not null) foreach (var (k, v) in extra) o[k] = v?.DeepClone();
        o["exit"] = exit;
        return new VerbAnswer(exit, o.ToJsonString());
    }

    /// <summary>The script's answer to a fault it did not expect: it can be asked again.</summary>
    public static VerbAnswer InternalError(string verb, string reason)
    {
        var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "internal-error", ["reason"] = reason, ["retryAfterSeconds"] = 30, ["exit"] = 20 };
        return new VerbAnswer(20, o.ToJsonString());
    }

    /// <summary><c>Need $a 'key'</c>: the value of a required argument, or the usage refusal the script gives ("missing argument: key", exit 2).</summary>
    public static bool Need(string verb, JsonObject body, string key, out string value, out VerbAnswer refusal)
    {
        value = "";
        refusal = default;
        if (body[key] is { } node && !(node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0))
        {
            value = node is JsonValue sv && sv.TryGetValue<string>(out var text) ? text : node.ToString();
            if (value.Length > 0) return true;
        }
        refusal = Reject(verb, 2, "usage", $"missing argument: {key}");
        return false;
    }

    /// <summary><c>Get-ItemNumber</c>: the item as a whole number. A value that is no number is the script's cast failure, an internal error.</summary>
    public static bool ItemNumber(string verb, JsonObject body, out int item, out VerbAnswer refusal)
    {
        item = 0;
        if (!Need(verb, body, "item", out var text, out refusal)) return false;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out item))
        {
            refusal = InternalError(verb, $"Cannot convert value \"{text}\" to type \"System.Int32\".");
            return false;
        }
        return true;
    }

    /// <summary>An optional whole number argument (<c>Test-Has $a 'pr'</c> then <c>[int]$a.pr</c>); null when absent.</summary>
    public static int? OptionalInt(JsonObject body, string key)
    {
        if (body[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out i) ? i : null;
    }
}
