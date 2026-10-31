using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

// W9.2 (#354): the item verbs on v2. This file is the plumbing they share: the refusal of a rule script (Reject of Core.ps1), the PowerShell semantics the scripts lean on (a hashtable key without case, "$x" for a
// value, [int], -match and -eq without case, truthiness), and the answer in the shape factory.ps1 --server prints. Nothing here reads or writes anything.

/// <summary>A refusal of a verb: <c>Reject</c> of Core.ps1. The exit code is the HTTP status of the answer (VerbPolicy.StatusOfExit), the code and the reason are the answer's, <see cref="Extra"/> its further fields.</summary>
public sealed class ItemRefusal(int exit, string code, string reason, JsonObject? extra = null) : Exception(reason)
{
    public int Exit { get; } = exit;
    public string Code { get; } = code;
    public JsonObject Extra { get; } = extra ?? new JsonObject();
}

/// <summary>What a verb answers: the exit code and the answer's fields (the verb, ok and exit are added by <see cref="ItemVerbRunner"/>).</summary>
public sealed record ItemVerbAnswer(int Exit, JsonObject Body)
{
    /// <summary>The answer as the script prints it: one line of JSON.</summary>
    public string Text => Body.ToJsonString();
}

/// <summary>The PowerShell semantics of the rule scripts that a C# port has to keep.</summary>
public static class Ps
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The options of an argument object or a state: a PowerShell hashtable is read without regard to the case of a key.</summary>
    public static readonly JsonNodeOptions CaseBlind = new() { PropertyNameCaseInsensitive = true };

    public static JsonObject NewObject() => new(CaseBlind);

    /// <summary><c>Test-Has</c>: the key is there and its value is not null.</summary>
    public static bool Has(JsonNode? o, string key) => o is JsonObject j && j.TryGetPropertyValue(key, out var v) && v is not null;

    public static JsonNode? Get(JsonNode? o, string key) => o is JsonObject j && j.TryGetPropertyValue(key, out var v) ? v : null;

    public static JsonObject? Obj(JsonNode? o, string key) => Get(o, key) as JsonObject;

    /// <summary>A column-keyed table of the state (dispatched_at, last_append, reviews): the key is compared without case, as a hashtable does.</summary>
    public static string? CiKey(JsonObject table, string key) => table.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    public static JsonNode? CiGet(JsonObject? table, string key) => table is not null && CiKey(table, key) is { } k ? table[k] : null;
    public static bool CiHas(JsonObject? table, string key) => CiGet(table, key) is not null;
    public static void CiSet(JsonObject table, string key, JsonNode? value) { var k = CiKey(table, key); if (k is not null) table.Remove(k); table[key] = value; }
    public static void CiRemove(JsonObject table, string key) { if (CiKey(table, key) is { } k) table.Remove(k); }

    /// <summary><c>"$x"</c>: the text PowerShell makes of a value.</summary>
    public static string Text(JsonNode? n)
    {
        if (n is null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            if (v.TryGetValue<int>(out var i)) return i.ToString(Inv);
            if (v.TryGetValue<long>(out var l)) return l.ToString(Inv);
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", Inv);
            if (v.TryGetValue<decimal>(out var m)) return m.ToString(Inv);
            return v.ToString();
        }
        return n.ToJsonString();
    }

    public static string Text(JsonNode? o, string key) => Text(Get(o, key));

    /// <summary>The truth of a value in an <c>if</c>/<c>-and</c>: false for null, false, 0 and the empty text; a text such as "false" is true.</summary>
    public static bool Truthy(JsonNode? n)
    {
        if (n is null) return false;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s.Length > 0;
            if (v.TryGetValue<int>(out var i)) return i != 0;
            if (v.TryGetValue<long>(out var l)) return l != 0;
            if (v.TryGetValue<double>(out var d)) return d != 0;
            return true;
        }
        if (n is JsonArray a) return a.Count switch { 0 => false, 1 => Truthy(a[0]), _ => true };
        return n is not JsonObject o || o.Count > 0;
    }

    /// <summary>The value of the key as <c>if ($a.key)</c> reads it.</summary>
    public static bool TruthyKey(JsonNode? o, string key) => Truthy(Get(o, key));

    /// <summary><c>[int]$x</c>: numbers as they are, a text parsed (a fraction is rounded to even); anything else is the error the script would die of.</summary>
    public static int Int(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return checked((int)l);
            if (v.TryGetValue<double>(out var d)) return checked((int)Math.Round(d, MidpointRounding.ToEven));
            if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
            if (v.TryGetValue<string>(out var s))
            {
                var t = s.Trim();
                if (t.Length == 0) return 0;
                if (int.TryParse(t, NumberStyles.Integer, Inv, out i)) return i;
                if (double.TryParse(t, NumberStyles.Float, Inv, out d)) return checked((int)Math.Round(d, MidpointRounding.ToEven));
                throw new FormatException($"Cannot convert value \"{s}\" to type \"System.Int32\".");
            }
        }
        if (n is null) return 0;
        throw new FormatException("Cannot convert the value to type \"System.Int32\".");
    }

    public static int Int(JsonNode? o, string key) => Int(Get(o, key));

    /// <summary><c>[double]$x</c>.</summary>
    public static double Double(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d)) return d;
        }
        if (n is null) return 0;
        throw new FormatException("Cannot convert the value to type \"System.Double\".");
    }

    /// <summary><c>-ieq</c> and the plain <c>-eq</c> of two texts.</summary>
    public static bool Eq(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Test-NameIn</c>: the name is one of the list, without regard to case.</summary>
    public static bool NameIn(string name, IEnumerable<string> list) => list.Any(x => Eq(x, name));

    /// <summary><c>-match</c> (without case).</summary>
    public static bool Match(string? input, string pattern) => Regex.IsMatch(input ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><c>-cmatch</c>.</summary>
    public static bool CMatch(string? input, string pattern) => Regex.IsMatch(input ?? "", pattern, RegexOptions.CultureInvariant);

    /// <summary><c>-replace</c> (without case).</summary>
    public static string Replace(string? input, string pattern, string replacement) => Regex.Replace(input ?? "", pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whitespace runs folded to one space and trimmed (the fold of the review and verdict rules).</summary>
    public static string Fold(string? text) => Regex.Replace(text ?? "", @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    /// <summary><c>ConvertTo-Iso</c>: yyyy-MM-ddTHH:mm:ss.fffZ.</summary>
    public static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv);

    /// <summary><c>ConvertFrom-Iso</c>: null for a missing or empty value.</summary>
    public static DateTime? FromIso(JsonNode? n)
    {
        var s = Text(n);
        if (s.Length == 0) return null;
        return DateTime.Parse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public static JsonArray Array(IEnumerable<string> items) { var a = new JsonArray(); foreach (var s in items) a.Add(s); return a; }

    public static IEnumerable<string> Strings(JsonNode? n) => n is JsonArray a ? a.Select(Text) : n is null ? [] : [Text(n)];

    /// <summary>A copy of a node (a value placed in two parents has to be cloned).</summary>
    public static JsonNode? Clone(JsonNode? n) => n?.DeepClone();
}

/// <summary>
/// Runs a verb the way <c>factory.ps1 --server</c> does (the `try` of its server mode): the stdin is the arguments (the keys without regard to case), the gateway's own fields <c>_session</c>, <c>_cause</c> and
/// <c>_lease</c> are taken out, the verb runs under the item's lock unless it is one of the verbs without one, and the answer is the verb's fields with <c>verb</c>, <c>ok</c> (true unless the handler set it) and <c>exit</c>.
/// A refusal is <c>ok: false</c> with its code and reason and its own fields; a fault of the handler is the internal-error of the script (exit 20, retry after 30 s).
/// </summary>
public sealed class ItemVerbRunner(IItemLock locks, IItemRefusals refusals)
{
    /// <summary><c>$NoLockVerbs</c> of factory.ps1, restricted to the verbs this group owns.</summary>
    public static readonly IReadOnlySet<string> NoLockVerbs = new HashSet<string>(StringComparer.Ordinal) { "get-item", "item-pr" };

    /// <summary>A verb that runs without the item's lock: the verbs above, and the accept of a QC environment, whose gate (<see cref="IItemQc"/>) takes the lock itself, as <c>Invoke-QcAccept</c> does.</summary>
    static bool Unlocked(string verb, JsonObject args) => NoLockVerbs.Contains(verb) || (verb == "accept" && Ps.Eq(Ps.Text(args, "kind"), "qc-env"));

    /// <summary>The verbs whose refusal 10 is one data point of the model ladder (Add-Refusal).</summary>
    static readonly IReadOnlySet<string> LadderVerbs = new HashSet<string>(StringComparer.Ordinal) { "complete", "record-review", "validation-verdict", "approve-pr", "merge", "open-pr", "append-body" };

    /// <summary>The fields the gateway adds to the arguments of a call; the handler gets them as <see cref="ItemCall"/>.</summary>
    public async Task<ItemVerbAnswer> Run(string verb, string stdin, Func<ItemCall, Task<JsonObject>> handler, CancellationToken ct = default)
    {
        JsonObject? args = null;
        try
        {
            var raw = stdin ?? "";
            args = raw.Trim().Length == 0 ? Ps.NewObject() : JsonNode.Parse(raw, Ps.CaseBlind) as JsonObject ?? throw new FormatException("the arguments are not an object");
            JsonObject? session = null;
            if (Ps.Has(args, "_session")) { session = args["_session"] as JsonObject; args.Remove("_session"); }
            string? cause = null;
            if (Ps.Has(args, "_cause")) { cause = Ps.Text(args, "_cause"); args.Remove("_cause"); }
            if (Ps.Has(args, "_lease")) args.Remove("_lease");
            var call = new ItemCall(verb, args, session, cause, ct);
            JsonObject body;
            if (Ps.Has(args, "item") && !Unlocked(verb, args)) body = await locks.Use(Ps.Int(args, "item"), () => handler(call), IItemLock.DefaultWaitSeconds, ct);
            else body = await handler(call);
            return Finish(verb, body, 0);
        }
        catch (ItemBusyException b)
        {
            // the lock of the item (IItemLock, the one of every port) was not free in time: Use-ItemLock's exit 20 item-busy
            return Finish(verb, new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "item-busy", ["reason"] = b.Message, ["retryAfterSeconds"] = 5 }, 20, refused: true);
        }
        catch (ItemRefusal e)
        {
            // a refused gate call is one data point for the model ladder: repeated refusals on one item mean the session is looping
            if (e.Exit == 10 && LadderVerbs.Contains(verb) && args is not null && Ps.Has(args, "item")) { try { await refusals.Add(Ps.Int(args, "item"), ct); } catch { } }
            var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = e.Code, ["reason"] = e.Message };
            foreach (var kv in e.Extra) o[kv.Key] = kv.Value?.DeepClone();
            return Finish(verb, o, e.Exit, refused: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"internal error in {verb} : {e.GetType().Name}: {e.Message}");
            var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "internal-error", ["reason"] = e.Message, ["retryAfterSeconds"] = 30 };
            return Finish(verb, o, 20, refused: true);
        }
    }

    static ItemVerbAnswer Finish(string verb, JsonObject body, int exit, bool refused = false)
    {
        if (!refused)
        {
            body["verb"] = verb;
            if (!body.ContainsKey("ok")) body["ok"] = true;
        }
        body["exit"] = exit;
        return new ItemVerbAnswer(exit, body);
    }
}

/// <summary>One call of a verb: the arguments (without the gateway's own fields), the AI session the gateway runs it for (the footer of a comment), the cause id of a test, and the token to stop by.</summary>
public sealed record ItemCall(string Verb, JsonObject Args, JsonObject? Session, string? Cause, CancellationToken Ct)
{
    /// <summary><c>Need</c>: the argument is there and not empty, else the usage refusal.</summary>
    public JsonNode Need(string key)
    {
        if (!Ps.Has(Args, key) || Ps.Text(Args, key) == "") throw new ItemRefusal(2, "usage", $"missing argument: {key}");
        return Args[key]!;
    }

    public string NeedText(string key) => Ps.Text(Need(key));

    /// <summary><c>Get-ItemNumber</c>.</summary>
    public int Item => Ps.Int(Need("item"));

    public bool Has(string key) => Ps.Has(Args, key);
    public string Text(string key) => Ps.Text(Args, key);
    public bool Truthy(string key) => Ps.TruthyKey(Args, key);
}
