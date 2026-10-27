using System.Globalization;
using System.Text.RegularExpressions;

namespace Fworker.Domain;

// The deterministic text of the plant manager's journal lines, ported from plugin/scripts/lib/Journal.ps1 (Get-JournalText,
// Get-JournalSessionLink, Limit-JournalLine, Get-JournalStamp, Format-JournalFold). Pure: fields in, string out. The rule script still writes
// the comments; the parity tests pin this port to its text. Redaction (Protect-JournalText) reads the configuration and is not part of this port.
// PowerShell semantics are kept on purpose: field names are case-insensitive, a missing or null field interpolates as empty, "truthy" is
// PowerShell's (null, "", 0 and false are false), -eq and -match on text ignore case.
public static class JournalText
{
    static readonly Regex CopilotTask = new(@"^https://github\.com/[^/\s]+/[^/\s]+/tasks/[^/\s?#]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex CursorPage = new(@"^https://([a-z0-9-]+\.)?cursor\.com/[^\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex TranscriptUrl = new(@"^https?://[^\s()<>]+/sessions/[A-Za-z0-9._-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PlainUrl = new(@"^https?://[^\s()<>]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    static readonly Regex Fences = new(@"`{3,}", RegexOptions.CultureInvariant);
    static readonly Regex StampPrefix = new(@"^\S+ \S+ ", RegexOptions.CultureInvariant);

    /// <summary>A folded line kept in the state for the comment's history: at most this long (JournalFoldLineMax).</summary>
    public const int FoldLineMax = 300;

    /// <summary>The kinds whose repeats are folded into one comment (JournalFoldKinds).</summary>
    public static readonly IReadOnlyList<string> FoldKinds = ["matched", "waiting", "reassigned"];

    /// <summary>The fields of a line, read the way the script reads a hashtable.</summary>
    sealed class Fields
    {
        readonly Dictionary<string, object?> d;
        public Fields(IReadOnlyDictionary<string, object?> src) => d = new Dictionary<string, object?>(src, StringComparer.OrdinalIgnoreCase);
        public bool Has(string k) => d.ContainsKey(k);
        public object? Raw(string k) => d.TryGetValue(k, out var v) ? v : null;
        /// <summary>"$($F.k)": empty for missing and null.</summary>
        public string S(string k) => Str(Raw(k));
        /// <summary>PowerShell truthiness of $F.k.</summary>
        public bool T(string k) => Truthy(Raw(k));
        /// <summary>$F.ContainsKey(k) -and $F.k</summary>
        public bool HasT(string k) => Has(k) && T(k);
        /// <summary>$F.k -eq 'x' (text compares without case)</summary>
        public bool Eq(string k, string x) => string.Equals(S(k), x, StringComparison.OrdinalIgnoreCase);
    }

    static string Str(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    static bool Truthy(object? v) => v switch
    {
        null => false,
        string s => s.Length > 0,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToDecimal(v, CultureInfo.InvariantCulture) != 0,
        float or double or decimal => Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0,
        _ => true,
    };

    static string Bold(string s) => "**" + s + "**";

    /// <summary>The page of a runtime worker's execution (Get-JournalSessionLink): empty, or a leading space and the link text.</summary>
    public static string SessionLink(IReadOnlyDictionary<string, object?> fields) => SessionLink(new Fields(fields));

    static string SessionLink(Fields f)
    {
        if (!(f.Has("url") && f.T("url"))) return "";
        var u = f.S("url");
        if (CopilotTask.IsMatch(u)) return " Copilot session: " + u;
        if (CursorPage.IsMatch(u)) return " Cursor session: " + u;
        return $" [session]({u})";
    }

    /// <summary>The line the journal posts for an event (Get-JournalText). An unknown event gives its own name.</summary>
    public static string Format(string ev, IReadOnlyDictionary<string, object?> fields)
    {
        var f = new Fields(fields);
        var col = f.Has("column") ? Bold(f.S("column")) : "";
        var n = f.S("item");
        // PowerShell's switch compares text without case
        switch (ev.ToLowerInvariant())
        {
            case "matched":
                return $"matched {(f.Has("trigger") ? f.S("trigger") : "a trigger")} to work item #{n} in {col} ({f.S("reason")}). Needs {(f.HasT("role") ? $"a {f.S("role")} bot" : "an owner session")}.";
            case "offered":
                return $"offered to {Bold(f.S("worker"))} ({f.S("type")}); waiting up to {f.S("ackSeconds")} s for it to accept.";
            case "assigned":
            {
                if (f.HasT("role"))
                    return $"{Bold(f.S("worker"))} accepted the {f.S("role")} job for PR #{f.S("pr")}{(f.HasT("url") ? $" ([session]({f.S("url")}))" : "")}.";
                var m = f.HasT("model") ? $" Model: {f.S("model")} ({f.S("effort")}), tier {f.S("tier")}." : "";
                var sl = SessionLink(f);
                var cop = sl.StartsWith(" Copilot session: ", StringComparison.OrdinalIgnoreCase) || sl.StartsWith(" Cursor session: ", StringComparison.OrdinalIgnoreCase);
                var link = cop ? "" : sl;
                var by = f.Has("by") && f.Eq("by", "client") ? " (accepted by its client; the session starts on it next)" : "";
                return $"{Bold(f.S("worker"))}{link} accepted{by}: it owns this item from {col} until it is merged (mode {f.S("mode")}, attempt {f.S("attempt")}).{m} Lease until {f.S("lease")}.{(cop ? sl : "")}";
            }
            case "routed":
                return $"told the owner ({Bold(f.S("worker"))}): {f.S("what")}.";
            case "reassigned":
                return $"{f.S("reason")}; offering to the next worker.{SessionLink(f)}";
            case "launched":
            {
                var phase = f.T("phase") ? Bold(f.S("phase")) : "this item";
                var task = f.T("task") ? $" (task `{f.S("task")}`)" : "";
                if (f.Has("transcript") && TranscriptUrl.IsMatch(f.S("transcript")))
                {
                    var tl = f.Has("url") && PlainUrl.IsMatch(f.S("url")) ? $" ([{(f.Eq("runtime", "octopus") ? "Octopus task" : "execution")}]({f.S("url")}))" : "";
                    return $"{f.S("runtime")} started {Bold(f.S("worker"))} for {phase}{task}. [session]({f.S("transcript")}){tl}";
                }
                return $"{f.S("runtime")} started {Bold(f.S("worker"))} for {phase}{task}.{SessionLink(f)}";
            }
            case "tier":
                return $"model tier raised to {Bold(f.S("name"))} ({f.S("reasons")}).";
            case "waiting":
                return $"waiting: {f.S("reason")}.";
            case "pin-fallback":
                return $"the pinned worker {Bold(f.S("pin"))} could not take the item for {f.S("minutes")} min ({f.S("code")}): the pin is set aside and it is offered to {Bold(f.S("worker"))}. A new worker label pins it again.";
            case "skipped":
                return $"not assigned: {f.S("reason")}.";
            case "qc-offered":
                return $"a QC environment is offered for PR #{f.S("pr")} at head {f.S("sha7")}; nothing runs until a person presses Start.";
            case "qc-requested":
                return $"a person asked for a QC environment for PR #{f.S("pr")} at head {f.S("sha7")} (attempt {f.S("attempt")}).";
            case "qc-assigned":
                return $"{Bold(f.S("worker"))} accepted the QC environment job for PR #{f.S("pr")} (attempt {f.S("attempt")}).";
            case "qc-ready":
                return $"QC environment for PR #{f.S("pr")} is ready on {Bold(f.S("worker"))} (attempt {f.S("attempt")}){(f.HasT("url") ? ": " + f.S("url") : "")}. Reachable only from networks that can reach that computer.";
            case "qc-failed":
                return $"QC environment for PR #{f.S("pr")} failed ({Bold(f.S("code"))}).\n```text\n{f.S("reason")}\n```";
            case "qc-reset":
                return $"QC environment for PR #{f.S("pr")} was reset by a person (attempt {f.S("attempt")}).";
            case "qc-torn-down":
                return $"QC environment for PR #{f.S("pr")} torn down ({f.S("reason")}).";
            case "qc-waiting":
                return $"QC environment for PR #{f.S("pr")} is waiting: {f.S("reason")}.";
            case "question-escalated":
                return $"a question from {Bold(f.S("worker"))} has waited {f.S("minutes")} min for the supervisor (supervisor: {(f.Eq("supervisor", "connected") ? "connected" : "absent")}); a person is asked to look at the dashboard.";
            case "merge-waiting":
                return $"PR #{f.S("pr")} has a PASS verdict and is not merged yet: {f.S("reason")} (`{f.S("code")}`). The gateway merges it by itself when that clears; no session has to call anything.";
            case "base-updated":
            {
                var overlap = f.HasT("overlap") ? $", files changed on both sides: {f.S("overlap")}" : "";
                if (f.HasT("conflict"))
                    return $"the pull request has a merge conflict with master (`{f.S("sha7")}`); told the owner ({Bold(f.S("worker"))}) to merge master into the branch and resolve it: {f.S("behind")} commit(s) behind{overlap}.";
                return $"master moved to `{f.S("sha7")}`{(f.HasT("prs") ? $" (merged: {f.S("prs")})" : "")}; told the owner ({Bold(f.S("worker"))}) to merge master into the branch now: {f.S("behind")} commit(s) behind{overlap}.";
            }
            case "ci-released":
                return $"released workflow run {f.S("run")} of PR #{f.S("pr")} (head `{f.S("sha7")}`, branch `{f.S("branch")}`).";
            case "ci-release-refused":
                return $"owner action: the gateway could not release workflow run {f.S("run")} of PR #{f.S("pr")} (GitHub answered {f.S("status")}): grant the factory-worker-author App Actions: Read & write. It retries by itself once that is granted.";
            case "branch-updated":
                return $"the gateway merged master into the branch of PR #{f.S("pr")} (GitHub's update branch: no conflict, the owner had not updated it within {f.S("seconds")} s).";
            case "gap-renewed":
                return $"the lease of {Bold(f.S("worker"))} ran out at {f.S("lapsed")} while no gateway was running (down since {f.S("since")}); renewed for {f.S("minutes")} min instead of offering the item again.";
            case "lease-expired":
                return $"the gateway ended the lease of {Bold(f.S("worker"))} ({f.S("reason")}); the item is offered again in resume mode.";
            case "flagged":
                return $"flagged `{f.S("code")}` in {col}: {f.S("reason")}.";
            case "settle-rearmed":
                return $"the settle step of this merged item is re-armed by {f.S("by")} (awaiting `{f.S("awaiting")}`): {f.S("reason")}. The gateway carries it on; it is not offered to a worker.";
            case "board-added":
                return $"the item has the factory label but had no column on the board: the sweep put it in {f.S("column")}, where it is offered like any new item.";
            case "unparked":
            {
                var rs = f.HasT("reset") ? " Its fix-forward rounds and demotions count from 0 again." : "";
                if (f.HasT("merged"))
                    return $"park `{f.S("code")}` removed by {f.S("by")}: {f.S("reason")}. The item is merged: the gateway settles it, nothing is offered.";
                if (f.HasT("closed"))
                    return $"park `{f.S("code")}` removed by {f.S("by")}: {f.S("reason")}. The item is closed: nothing is offered.";
                if (f.Eq("code", "master-broken") && !f.Eq("by", "expiry"))
                    return $"park `master-broken` released early by {f.S("by")}: {f.S("reason")}.{rs} The item waits for master green again, then for its release candidate.";
                if (f.Eq("by", "expiry"))
                    return $"park `{f.S("code")}` expired ({f.S("reason")}).{rs} The next sweep takes it up again in {col}.";
                return $"park `{f.S("code")}` released early by {f.S("by")}: {f.S("reason")}.{rs} The next sweep takes it up again in {col}.";
            }
            case "promoted":
                return $"promoted release {f.S("release")} to {Bold(f.S("environment"))} (Octopus task `{f.S("task")}`); the gateway follows the deployment and answers its Sign-off.";
            case "promotion-waiting":
                return $"release {f.S("release")} waits for the automatic promotion to {Bold(f.S("environment"))}: {f.S("reason")}. The item stays in the Release Queue; the gateway looks again at every sweep.";
            case "promotion-signoff":
                return $"answered the Sign-off of the deployment of release {f.S("release")} to {Bold(f.S("environment"))} (Octopus task `{f.S("task")}`): {Bold(f.S("result"))}{(f.T("reason") ? $" ({f.S("reason")})" : "")}.";
            case "released":
                return $"ownership released by {Bold(f.S("worker"))} ({f.S("reason")}); the next trigger assigns it again.";
            case "pulled":
                return $"pulled from {Bold(f.S("from"))} into {col}: a worker slot was free and nothing waited there ({f.S("wip")} of at most {f.S("wipLimit")} items in the AI columns).";
            case "replenish":
                return $"the gateway created work item #{f.S("new")} ({Bold(f.S("title"))}) to keep activity going after this item finished.";
            case "keeper-action":
                return $"{(f.Eq("by", "operator") ? "the operator" : $"the backlog keeper ({Bold(f.S("by"))})")} {f.S("detail")}, under policy `{f.S("policy")}`{(f.HasT("reason") ? ": " + f.S("reason") : "")}.";
            case "manager-action":
                return $"{(f.Eq("by", "operator") ? "the operator" : $"the plant manager ({Bold(f.S("by"))})")} {f.S("detail")}, under policy `{f.S("policy")}`{(f.HasT("reason") ? ": " + f.S("reason") : "")}{(f.HasT("session") ? $" ([Copilot session]({f.S("session")}))" : "")}.";
            default:
                return ev;
        }
    }

    /// <summary>One line (no line breaks, no code fences), at most <see cref="FoldLineMax"/> long (Limit-JournalLine).</summary>
    public static string LimitLine(string line)
    {
        var one = Fences.Replace(Whitespace.Replace(line, " "), "'''").Trim();
        return one.Length > FoldLineMax ? one[..FoldLineMax] + " [...]" : one;
    }

    /// <summary>The time of a journal line, in UTC: 2026-10-27 09:05Z (Get-JournalStamp).</summary>
    public static string Stamp(DateTime at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// The body of a folded comment (Format-JournalFold): the latest line first, then how often and since when, then the latest lines with
    /// their times. <paramref name="lines"/> are the kept lines, each "stamp line"; <paramref name="key"/> is the first line's key.
    /// </summary>
    public static string FormatFold(string ev, int count, DateTime first, DateTime last, IReadOnlyList<string> lines, string key)
    {
        var latest = lines.Count > 0 ? StampPrefix.Replace(lines[^1], "") : "";
        var shown = count > lines.Count ? $"the latest {lines.Count}" : "each";
        var hist = string.Join("\n", lines.Select(l => "> " + l));
        var b = $"\U0001F3ED Plant manager: {latest}\n\n{ev} {count} times, from {Stamp(first)} to {Stamp(last)} ({shown} below; this comment is edited instead of posting a new one each time).\n\n{hist}\n\n<!-- plant event={ev} key={key} fold={count} -->";
        if (b.Length > 6000) b = b[..5900] + $"\n[truncated]\n\n<!-- plant event={ev} key={key} fold={count} -->";
        return b;
    }
}
