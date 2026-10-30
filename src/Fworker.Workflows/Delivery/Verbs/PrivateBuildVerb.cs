using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

/// <summary>The arguments every verb reads the same way (Verbs.Item.ps1: Need, Get-ItemNumber, Assert-Open).</summary>
public static class VerbArgs
{
    /// <summary>Need: the argument, or the usage refusal when it is missing or empty.</summary>
    public static JsonNode Need(JsonObject a, string key)
    {
        if (!J.Has(a, key) || J.S(a, key) == "") throw new VerbReject(2, "usage", $"missing argument: {key}");
        return a[key]!;
    }

    public static int ItemNumber(JsonObject a) => J.I(Need(a, "item"));

    public static void AssertOpen(ItemInfo info)
    {
        if (!info.Open) throw new VerbReject(11, "item-closed", $"issue #{info.Number} is closed");
    }

    public static string Short(string sha) => sha[..Math.Min(7, sha.Length)];
}

// W9.4: private-build --attest SHA (Verbs.Pr.ps1 Verb_private_build, with the push gate of Get-RedChecksGate and Get-ChecksRedCount): the session states that the private build passed on that commit. Recorded in the
// item's state for the push gate of open-pr and update-pr. The client-side build (private-build --worktree) never reaches the gateway.
public static class PrivateBuildVerb
{
    public const string Verb = "private-build";

    /// <summary>Get-RedChecksGate: timing.redChecksGate, 2 when the setting is absent.</summary>
    public static int RedChecksGate(RuleKit kit) => J.Has(kit.Timing, "redChecksGate") ? J.I(kit.Timing, "redChecksGate") : 2;

    /// <summary>Get-ChecksRedCount.</summary>
    public static int ChecksRedCount(JsonObject s) => J.Has(s, "checks_red") && J.Has(s["checks_red"], "count") ? J.I(s["checks_red"], "count") : 0;

    public static async Task<JsonObject> Run(RuleKit kit, JsonObject a, CancellationToken ct)
    {
        var n = VerbArgs.ItemNumber(a);
        var sha = J.S(VerbArgs.Need(a, "attest")).Trim().ToLowerInvariant();
        if (!Regex.IsMatch(sha, "^[0-9a-f]{40}$")) throw new VerbReject(2, "usage", "attest takes the full 40-character sha of the commit the private build passed on");
        var info = await kit.GetItemInfo(n, ct); VerbArgs.AssertOpen(info);
        var holder = J.Has(a, "holder") ? J.S(a, "holder") : "anonymous";
        var st = await kit.GetState(n, ct); var s = st.State;
        s["private_build"] = new JsonObject { ["sha"] = sha, ["at"] = J.Iso(kit.UtcNow), ["holder"] = holder };
        await kit.SaveState(n, st, ct);
        return J.Obj(("ok", true), ("id", n), ("attested", true), ("sha", sha), ("checks_red", ChecksRedCount(s)), ("gate", RedChecksGate(kit)));
    }
}
