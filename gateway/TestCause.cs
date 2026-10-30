using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Test-only (Tier 1 golden writes, tests/scenarios/golden/README.md: Caused and background writes); inert unless FACTORY_TEST_CAUSE_HEADER=1.
// The harness tags each call of the scripted worker with a cause id, which the client sends as X-Fake-Cause. While the gateway serves that
// request, the id is held in a request-scoped box (the middleware in Program.cs), and every rule-script run started meanwhile gets it as the
// "_cause" field of its arguments (Runner.RunProcessAsync), which factory.ps1 --server takes out and sends as X-Fake-Cause with every write. So
// the fake tells the writes a call caused from the gateway's background work by where they came from, not by when.
static class TestCause
{
    /// <summary>Whether the test mode is on: FACTORY_TEST_CAUSE_HEADER is exactly "1" (read once). Off, nothing here does anything.</summary>
    public static readonly bool On = Environment.GetEnvironmentVariable("FACTORY_TEST_CAUSE_HEADER") == "1";

    /// <summary>The holder of one request's cause id. A box, not the id itself in the AsyncLocal: End clears it, so a task the request started
    /// that outlives it (a fire-and-forget continuation copies the execution context, and so the box) sees no id from then on.</summary>
    public sealed class Box { public string? Value; }

    static readonly AsyncLocal<Box?> current = new();
    static readonly Regex Valid = new("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant);

    /// <summary>Holds <paramref name="id"/> for the rest of this request's flow; the caller ends it in a finally (End).</summary>
    public static Box Begin(string? id) { var b = new Box { Value = id }; current.Value = b; return b; }

    /// <summary>Clears the box: whatever still holds it (work that outlives the request) sees no id any more.</summary>
    public static void End(Box b) { b.Value = null; if (ReferenceEquals(current.Value, b)) current.Value = null; }

    /// <summary>The cause id of the request this code runs for, or null.</summary>
    public static string? Current => current.Value?.Value;

    /// <summary>The cause id to send as X-Fake-Cause with a write the gateway makes itself (v2 verbs, as the rule script does): the current one when the test mode is on and it is 1 to 80 characters of [A-Za-z0-9._-]; else null.</summary>
    public static string? ForWrite => On && Current is { } id && Valid.IsMatch(id) ? id : null;

    /// <summary>The rule script's stdin with "_cause" added when the test mode is on and the current request carries a valid id; stdin that is
    /// not a JSON object, and every call without an id, is returned unchanged.</summary>
    public static string Inject(string stdin)
    {
        if (!On || Current is not { } id || !Valid.IsMatch(id)) return stdin;
        try
        {
            if (JsonNode.Parse(stdin) is not JsonObject o) return stdin;
            o["_cause"] = id;
            return o.ToJsonString();
        }
        catch { return stdin; }
    }
}
