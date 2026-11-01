using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Keeper;

/// <summary>
/// The helpers that the keeper and the manager verbs share, each the port of a helper of the rule scripts of the same meaning: <c>Assert-Open</c>, <c>Add-Comment</c> and its session footer, <c>Find-ItemPr</c>,
/// <c>Get-Park</c>, <c>Test-ClaimLive</c>, <c>Test-OwnedClaim</c>. One per process: it holds the rules, the ports and the clock.
/// </summary>
public sealed class VerbContext(FactoryRules rules, IKeeperGitHub github, IItemStateStore state, IVerbLocks locks, IKeeperSaga saga, Func<DateTime>? clock = null, KeeperDailyCap? keeperCap = null)
{
    public FactoryRules Rules { get; } = rules;
    public IKeeperGitHub GitHub { get; } = github;
    public IItemStateStore State { get; } = state;
    public IVerbLocks Locks { get; } = locks;
    public IKeeperSaga Saga { get; } = saga;
    /// <summary>V1.4: the keeper's limit of new items in 24 hours; null where none is kept (a test, the verbs of the manager).</summary>
    public KeeperDailyCap? KeeperCap { get; } = keeperCap;
    readonly Func<DateTime> clock = clock ?? (() => DateTime.UtcNow);

    /// <summary><c>Get-UtcNow</c>.</summary>
    public DateTime Now => clock();

    /// <summary><c>Assert-Open</c>: a closed issue is the 'item-closed' refusal (11).</summary>
    public static void AssertOpen(ItemInfo info)
    {
        if (!info.Open) throw new VerbRejection(11, "item-closed", $"issue #{info.Number} is closed");
    }

    /// <summary><c>Test-ClaimLive</c>: the claim's lease ends in the future.</summary>
    public bool ClaimLive(JsonObject s)
    {
        if (!Ps.Has(s, "claim") || s["claim"] is not JsonObject claim) return false;
        var until = Ps.FromIso(claim["lease_until"]);
        return until is { } u && u > Now;
    }

    /// <summary><c>Test-OwnedClaim</c>: a claim of an owner session.</summary>
    public static bool OwnedClaim(JsonObject s) => Ps.Has(s, "claim") && s["claim"] is JsonObject claim && Ps.Has(claim, "owner") && Ps.Truthy(claim["owner"]);

    /// <summary><c>Get-Park</c>: the park of the state unless it has run out.</summary>
    public JsonObject? Park(JsonObject s)
    {
        if (!Ps.Has(s, "park") || s["park"] is not JsonObject park) return null;
        var until = Ps.FromIso(park["until"]);
        if (until is { } u && u < Now) return null;
        return park;
    }

    /// <summary>The merge is awaited (the item is merged and its release is being settled).</summary>
    public static bool MergeAwaiting(JsonObject s) => Ps.Has(s, "merge") && s["merge"] is JsonObject merge && Ps.Has(merge, "awaiting") && Ps.Truthy(merge["awaiting"]);

    /// <summary><c>Get-SessionFooter</c>: "AI session: [name](url)", the name alone without a page, or nothing.</summary>
    public static string SessionFooter(JsonObject? session)
    {
        if (session == null) return "";
        var name = Ps.Str(session["name"]);
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", Ps.Match)) return "";
        var url = Ps.Str(session["url"]);
        if (url.Length > 0 && Regex.IsMatch(url, @"^https?://[^\s?#()<>\[\]]+$", Ps.Match)) return $"AI session: [{name}]({url})";
        return $"AI session: {name}";
    }

    /// <summary><c>Add-Comment</c>: a journal comment of the factory on the issue, ending with the session's footer once.</summary>
    public async Task AddComment(int item, string text, JsonObject? session)
    {
        var footer = SessionFooter(session);
        var body = footer.Length == 0 || text.TrimEnd().EndsWith(footer, StringComparison.Ordinal) ? text : $"{text}\n\n{footer}";
        await GitHub.PostComment(item, $"\U0001F916 {Rules.Name}: {body}\n\n<!-- factory-journal:{Rules.Name} -->");
    }

    /// <summary><c>Find-ItemPr</c>: the open pull request of an item, by its branch name; a Copilot branch is the item's when the state records it.</summary>
    public async Task<JsonObject?> FindItemPr(int item)
    {
        var open = await GitHub.OpenPulls();
        static string HeadRef(JsonObject p) => Ps.Str(p["head"]?["ref"]);
        var mine = open.Where(p => Ps.IsMatch(HeadRef(p), $"^{Regex.Escape(Rules.Name)}/{item}-")).ToList();
        if (mine.Count == 0) mine = open.Where(p => Ps.IsMatch(HeadRef(p), $"/{item}-")).ToList();
        if (mine.Count == 0 && open.Any(p => HeadRef(p).StartsWith("copilot/", StringComparison.OrdinalIgnoreCase)))
        {
            JsonObject? sp = null;
            try { sp = (await State.Read(item)).State; } catch (Exception) { }
            if (sp != null && Ps.Has(sp, "pr") && sp["pr"] is JsonObject pr && Ps.Has(pr, "branch"))
                mine = open.Where(p => string.Equals(HeadRef(p), Ps.Str(pr["branch"]), StringComparison.OrdinalIgnoreCase) && Number(p) == AsInt(pr["number"])).ToList();
        }
        return mine.Count == 0 ? null : mine[0];
    }

    public static int Number(JsonObject o) => AsInt(o["number"]);

    /// <summary><c>[int]</c> of a value; 0 for nothing or a text that is no number.</summary>
    public static int AsInt(JsonNode? n)
    {
        var text = Ps.Str(n).Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }

    /// <summary>A text of a state field, "" when it is not there.</summary>
    public static string Text(JsonObject? o, string key) => o == null ? "" : Ps.Str(o[key]);

    /// <summary>The journal line of an action, best effort (<c>Add-Journal</c>): true when it was written.</summary>
    public Task<bool> Journal(int item, StateHolder? holder, string ev, IEnumerable<KeyValuePair<string, JsonNode?>> fields, string key)
    {
        var o = new JsonObject();
        foreach (var kv in fields) o[kv.Key] = kv.Value;
        return State.Journal(item, holder, ev, o, key);
    }

    /// <summary>Ticks of now as the scripts write a key: <c>[string](Get-UtcNow).Ticks</c>.</summary>
    public string Ticks => Now.Ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>The state edited as a list the scripts append to: the existing items kept, the new one added.</summary>
    public static JsonArray Append(JsonNode? existing, JsonNode added)
    {
        var a = new JsonArray();
        if (existing is JsonArray old) foreach (var x in old) a.Add(x?.DeepClone());
        a.Add(added);
        return a;
    }

    public static JsonArray Ints(IEnumerable<int> values) { var a = new JsonArray(); foreach (var v in values) a.Add(v); return a; }
    public static JsonArray Strings(IEnumerable<string> values) { var a = new JsonArray(); foreach (var v in values) a.Add(v); return a; }

    public static string Sha7(string sha) => sha.Substring(0, 7);

    public override string ToString() => nameof(VerbContext);
}
