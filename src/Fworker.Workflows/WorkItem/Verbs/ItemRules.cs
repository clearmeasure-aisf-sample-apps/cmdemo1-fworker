using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

/// <summary>
/// The small rules of the item state that the verbs share, on the state comment's JSON as the rule scripts hold it (Board.ps1 Get-Park, Set-Park, Test-ClaimLive; Owner.ps1 Test-OwnedClaim, Add-FlowStamp, Move-OwnerTo;
/// Verbs.Delivery.ps1 Restore-Settle; Prs.ps1 Test-ItemDocsOnly). All of them work on the state in hand; the caller saves it.
/// </summary>
public static class ItemRules
{
    /// <summary>How many stage entries the state's <c>flow</c> keeps (Owner.ps1 FlowKeep).</summary>
    public const int FlowKeep = 15;

    /// <summary><c>Get-Park</c>: the park, unless its <c>until</c> has passed.</summary>
    public static JsonObject? GetPark(JsonObject s, DateTime now)
    {
        if (!Ps.Has(s, "park")) return null;
        var park = s["park"] as JsonObject;
        var until = Ps.FromIso(park?["until"]);
        if (until is { } u && u < now) return null;
        return park;
    }

    /// <summary><c>Set-Park</c>.</summary>
    public static void SetPark(JsonObject s, string reason, double minutes, DateTime now) =>
        s["park"] = new JsonObject { ["reason"] = reason, ["at"] = Ps.Iso(now), ["until"] = Ps.Iso(now.AddMinutes(minutes)) };

    /// <summary><c>Test-ClaimLive</c>.</summary>
    public static bool ClaimLive(JsonObject s, DateTime now)
    {
        if (!Ps.Has(s, "claim")) return false;
        var u = Ps.FromIso(Ps.Obj(s, "claim")?["lease_until"]);
        return u is { } until && until > now;
    }

    /// <summary><c>Test-OwnedClaim</c>.</summary>
    public static bool OwnedClaim(JsonObject s) => Ps.Has(s, "claim") && Ps.Has(s["claim"], "owner") && Ps.Truthy(Ps.Get(s["claim"], "owner"));

    /// <summary><c>Test-BotLive</c>.</summary>
    public static bool BotLive(JsonObject s, DateTime now)
    {
        if (!Ps.Has(s, "bot")) return false;
        var u = Ps.FromIso(Ps.Obj(s, "bot")?["lease_until"]);
        return u is { } until && until > now;
    }

    /// <summary><c>Add-FlowStamp</c>: one small entry per stage the item entered, the last <see cref="FlowKeep"/> kept; the stage the item is in is not added again.</summary>
    public static void AddFlowStamp(JsonObject s, string column, DateTime now)
    {
        if (column.Length == 0) return;
        var prev = (s["flow"] as JsonArray)?.Where(x => x is not null).Select(x => x!.DeepClone()).ToList() ?? [];
        if (prev.Count > 0 && Ps.Text(prev[^1], "c") == column) return;
        prev.Add(new JsonObject { ["c"] = column, ["at"] = Ps.Iso(now) });
        var a = new JsonArray();
        foreach (var e in prev.TakeLast(FlowKeep)) a.Add(e);
        s["flow"] = a;
    }

    /// <summary><c>Move-OwnerTo</c>: the owner finishes a stage and keeps the item; the claim moves to the new column with a fresh lease, the reviews of the new column start empty.</summary>
    public static void MoveOwnerTo(JsonObject s, string column, ItemVerbConfig cfg, DateTime now)
    {
        AddFlowStamp(s, column, now);
        var claim = (JsonObject)s["claim"]!;
        claim["column"] = column; claim["heartbeat"] = Ps.Iso(now); claim["lease_until"] = Ps.Iso(now.AddMinutes(cfg.LeaseMinutes));
        if (!Ps.Has(s, "dispatched_at")) s["dispatched_at"] = new JsonObject();
        Ps.CiSet((JsonObject)s["dispatched_at"]!, column, Ps.Iso(now));
        if (Ps.Obj(s, "reviews") is { } reviews) Ps.CiRemove(reviews, column);
        // a stage finished is progress: the run of session take-backs starts again, and a take-back mark of an earlier session goes
        foreach (var k in new[] { "session_launches", "taken_back" }) if (Ps.Has(claim, k)) claim.Remove(k);
    }

    /// <summary><c>Restore-Settle</c>: re-arms the settle step of an item that has a merge record but no wait and is not done; returns the wait it set, or "".</summary>
    public static string RestoreSettle(JsonObject s, ItemVerbConfig cfg, DateTime now, bool force = false)
    {
        if (!Ps.Has(s, "merge") || !Ps.Has(s["merge"], "sha") || !Ps.Truthy(Ps.Get(s["merge"], "sha"))) return "";
        var m = (JsonObject)s["merge"]!;
        if (Ps.Has(m, "awaiting") && Ps.Truthy(m["awaiting"])) return "";
        if (Ps.Has(m, "done_at") && Ps.Truthy(m["done_at"])) { if (!force) return ""; m.Remove("done_at"); }
        if (Ps.Has(m, "release") && Ps.Truthy(m["release"])) m["awaiting"] = "release-queue";
        else if (Ps.Has(m, "green_at") && Ps.Truthy(m["green_at"])) { m["awaiting"] = "release-candidate"; m["rc_deadline"] = Ps.Iso(now.AddMinutes(cfg.ReleaseCandidateTimeoutMinutes)); }
        else if (GetPark(s, now) is { } park && Ps.Text(park, "reason") == "master-green-timeout") m["awaiting"] = "master-timeout";
        else m["awaiting"] = "master-broken";   // polled: it goes on as soon as master is green for it, with no re-run
        m["rearmed_at"] = Ps.Iso(now);
        return Ps.Text(m, "awaiting");
    }

    /// <summary><c>Get-PublicState</c>: the part of the state a session may see.</summary>
    public static JsonObject PublicState(JsonObject s)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "claim", "park", "verdict", "merge", "pr", "reviews", "demotions", "fix_forward_rounds", "awaiting_checks", "handoff", "qc" })
            if (Ps.Has(s, k)) o[k] = s[k]!.DeepClone();
        return o;
    }

    /// <summary><c>Test-ItemDocsOnly</c>: what the design step recorded (the marker in the body alone proves nothing); items an older version designed fall back to the marker in the body.</summary>
    public static bool ItemDocsOnly(JsonObject s, ItemInfo? info, ItemVerbConfig cfg)
    {
        if (Ps.Has(s, "design") && Ps.Has(s["design"], "docs_only")) return Ps.Truthy(Ps.Get(s["design"], "docs_only"));
        if (info is null || cfg.HasSection(info.ColumnText)) return false;
        var cut = cfg.LegacyCutoff;
        DateTime? made = null;
        if (info.CreatedAt.Length > 0) { try { made = Ps.FromIso(JsonValue.Create(info.CreatedAt)); } catch { } }
        if (cut is null || made is null || made >= cut) return false;
        return info.Body.Contains(cfg.DocsOnlyMarker, StringComparison.Ordinal);
    }

    /// <summary><c>Test-DocsOnlyFiles</c>: every changed file matches a documentation pattern (and there is at least one).</summary>
    public static bool DocsOnlyFiles(IEnumerable<string> filenames, ItemVerbConfig cfg)
    {
        var list = filenames.ToList();
        if (list.Count == 0) return false;
        var pats = cfg.DocsOnlyPaths;
        foreach (var f in list) if (!pats.Any(p => Ps.Match(f, p))) return false;
        return true;
    }

    /// <summary>Acceptance criteria worded in timing or statistical terms (<c>Get-TimingCriteria</c>).</summary>
    public static List<string> TimingCriteria(ItemInfo info, ItemVerbConfig cfg)
    {
        var body = info.Body;
        foreach (var kv in cfg.RequiredBodySection) { var at = body.IndexOf(kv.Value, StringComparison.OrdinalIgnoreCase); if (at >= 0) body = body[..at]; }
        const string pat = @"\b\d+(\.\d+)?\s*(ms|milliseconds?|s|secs?|seconds?|minutes?|mins?)\b|\bp(50|9\d|99\.9)\b|\d\s*%|\bpercent(age|ile)?\b|\baverage\b|\bmedian\b|\bmean\b|\blatency\b|\bthroughput\b|\bper (second|minute|hour)\b|\brequests? per\b|\bunder load\b|\bconcurrent users?\b|\bat least \d+ (times|out of)\b|\b\d+ out of \d+\b|\bstatistic(al|ally)?\b|\bprobability\b|\bon average\b|\bresponse time\b";
        return body.Split('\n').Select(l => l.Trim()).Where(l => Ps.Match(l, @"^([-*+]|\d+[.)])\s+") && Ps.Match(l, pat))
            .Select(l => Ps.Replace(l, @"^([-*+]|\d+[.)])\s+(\[[ xX]\]\s+)?", "").Trim()).ToList();
    }

    /// <summary><c>Get-CitedCriteria</c>: the acceptance criteria a review or a FAIL verdict cites that the item itself holds, folded, distinct, in order.</summary>
    public static List<string> CitedCriteria(ItemInfo info, string text, ItemVerbConfig cfg)
    {
        var own = info.Body;
        foreach (var kv in cfg.RequiredBodySection) { var at = own.IndexOf(kv.Value, StringComparison.OrdinalIgnoreCase); if (at >= 0) own = own[..at]; }
        own = Ps.Fold(own);
        var output = new List<string>();
        // a criterion may itself contain double quotes: try every closing quote after the opening one, shortest first, and take the first grounded
        foreach (Match m in Regex.Matches(text, @"criterion\s+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var start = m.Index + m.Length; var end = text.IndexOf('"', start);
            while (end >= 0)
            {
                var q = Ps.Fold(text.Substring(start, end - start));
                if (q.Length >= 8 && own.Contains(q, StringComparison.Ordinal)) { if (!output.Contains(q)) output.Add(q); break; }
                end = text.IndexOf('"', end + 1);
            }
        }
        return output;
    }

    /// <summary><c>Test-RepeatedCriteria</c>: records the criteria of one failing round and answers the ones the previous failing round of the same source also cited; <paramref name="clear"/> forgets the source.</summary>
    public static List<string> RepeatedCriteria(JsonObject s, string source, IReadOnlyList<string> cited, bool clear = false)
    {
        if (!Ps.Has(s, "cited")) s["cited"] = new JsonObject();
        var table = (JsonObject)s["cited"]!;
        var prev = Ps.CiGet(table, source) is JsonArray p ? p.Select(Ps.Text).ToList() : [];
        if (clear) { Ps.CiRemove(table, source); return []; }
        Ps.CiSet(table, source, Ps.Array(cited));
        return cited.Where(c => prev.Any(x => Ps.Eq(x, c))).ToList();
    }
}
