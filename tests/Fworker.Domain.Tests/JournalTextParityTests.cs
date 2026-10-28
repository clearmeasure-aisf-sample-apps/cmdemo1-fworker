using System.Text.RegularExpressions;
using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// W1.3: the journal text ported into Domain, pinned to the text of Get-JournalText in plugin/scripts/lib/Journal.ps1. Two kinds of check:
/// pinned strings written out from the script's templates (every event, every branch), and golden strings: lines the Tier 1 scenarios made the
/// gateway write (tests/scenarios/golden/**/*.writes.txt) that the port must reproduce from the fields of the call.
/// </summary>
public sealed class JournalTextParityTests
{
    static Dictionary<string, object?> F(params (string K, object? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    static string Line(string ev, params (string K, object? V)[] kv) => JournalText.Format(ev, F(kv));

    // ---- pinned strings: one per template and branch of Get-JournalText ----------------------------------------------------------------------

    [Fact]
    public void Matched()
    {
        Assert.Equal("matched label to work item #7 in **Development** (the factory label). Needs an owner session.",
            Line("matched", ("item", 7), ("column", "Development"), ("trigger", "label"), ("reason", "the factory label")));
        Assert.Equal("matched a trigger to work item #7 in  (r). Needs a reviewer bot.",
            Line("matched", ("item", 7), ("reason", "r"), ("role", "reviewer")));
        Assert.Equal("matched  to work item #7 in  (r). Needs an owner session.",
            Line("matched", ("item", 7), ("reason", "r"), ("trigger", null), ("role", "")));
    }

    [Fact]
    public void OfferedAndRouted()
    {
        Assert.Equal("offered to **w1** (persistent-local); waiting up to 30 s for it to accept.", Line("offered", ("worker", "w1"), ("type", "persistent-local"), ("ackSeconds", 30)));
        Assert.Equal("told the owner (**w1**): merge master.", Line("routed", ("worker", "w1"), ("what", "merge master")));
    }

    [Fact]
    public void Assigned_ForAnOwner()
    {
        Assert.Equal("**w1** accepted: it owns this item from **Development** until it is merged (mode first, attempt 1). Lease until 2026-10-27T10:00:00Z.",
            Line("assigned", ("item", 5), ("column", "Development"), ("worker", "w1"), ("mode", "first"), ("attempt", 1), ("lease", "2026-10-27T10:00:00Z")));
        Assert.Equal("**w1** accepted (accepted by its client; the session starts on it next): it owns this item from **Todo** until it is merged (mode resume, attempt 2). Lease until L.",
            Line("assigned", ("column", "Todo"), ("worker", "w1"), ("mode", "resume"), ("attempt", 2), ("lease", "L"), ("by", "CLIENT")));
        Assert.Equal("**w1** accepted: it owns this item from **Todo** until it is merged (mode first, attempt 1). Model: opus (low), tier 2. Lease until L.",
            Line("assigned", ("column", "Todo"), ("worker", "w1"), ("mode", "first"), ("attempt", 1), ("lease", "L"), ("model", "opus"), ("effort", "low"), ("tier", 2), ("by", "x")));
    }

    [Fact]
    public void Assigned_WithASessionLink_PlacesTheLinkByKind()
    {
        // a Copilot or Cursor page goes to the end; any other page follows the worker's name
        Assert.Equal("**w1** accepted: it owns this item from **Todo** until it is merged (mode first, attempt 1). Lease until L. Copilot session: https://github.com/o/r/tasks/abc-1",
            Line("assigned", ("column", "Todo"), ("worker", "w1"), ("mode", "first"), ("attempt", 1), ("lease", "L"), ("url", "https://github.com/o/r/tasks/abc-1")));
        Assert.Equal("**w1** accepted: it owns this item from **Todo** until it is merged (mode first, attempt 1). Lease until L. Cursor session: https://cursor.com/agents/bc-1",
            Line("assigned", ("column", "Todo"), ("worker", "w1"), ("mode", "first"), ("attempt", 1), ("lease", "L"), ("url", "https://cursor.com/agents/bc-1")));
        Assert.Equal("**w1** [session](https://example.test/s/1) accepted: it owns this item from **Todo** until it is merged (mode first, attempt 1). Lease until L.",
            Line("assigned", ("column", "Todo"), ("worker", "w1"), ("mode", "first"), ("attempt", 1), ("lease", "L"), ("url", "https://example.test/s/1")));
    }

    [Fact]
    public void Assigned_ForABot()
    {
        Assert.Equal("**qa-1** accepted the reviewer job for PR #12.", Line("assigned", ("worker", "qa-1"), ("role", "reviewer"), ("pr", 12), ("url", "")));
        Assert.Equal("**qa-1** accepted the reviewer job for PR #12 ([session](https://x.test/s)).", Line("assigned", ("worker", "qa-1"), ("role", "reviewer"), ("pr", 12), ("url", "https://x.test/s")));
    }

    [Fact]
    public void SessionLink_OfEachKind()
    {
        Assert.Equal("", JournalText.SessionLink(F()));
        Assert.Equal("", JournalText.SessionLink(F(("url", ""))));
        Assert.Equal(" Copilot session: https://github.com/o/r/tasks/t1", JournalText.SessionLink(F(("url", "https://github.com/o/r/tasks/t1"))));
        Assert.Equal(" [session](https://github.com/o/r/tasks/t1/extra)", JournalText.SessionLink(F(("url", "https://github.com/o/r/tasks/t1/extra"))));
        Assert.Equal(" Cursor session: https://www.cursor.com/agents/a", JournalText.SessionLink(F(("url", "https://www.cursor.com/agents/a"))));
        Assert.Equal(" Copilot session: https://GitHub.com/o/r/tasks/t1", JournalText.SessionLink(F(("URL", "https://GitHub.com/o/r/tasks/t1"))));   // -match ignores case; so do hashtable keys
    }

    [Fact]
    public void Reassigned()
    {
        Assert.Equal("the execution of x ended (settled); offering to the next worker.", Line("reassigned", ("reason", "the execution of x ended (settled)")));
        Assert.Equal("gone; offering to the next worker. Copilot session: https://github.com/o/r/tasks/t", Line("reassigned", ("reason", "gone"), ("url", "https://github.com/o/r/tasks/t")));
    }

    [Fact]
    public void Launched_WithAndWithoutTranscript()
    {
        Assert.Equal("copilot-cloud started **cop-1** for **Development** (task `t1`). Copilot session: https://github.com/o/r/tasks/t1",
            Line("launched", ("runtime", "copilot-cloud"), ("worker", "cop-1"), ("phase", "Development"), ("task", "t1"), ("url", "https://github.com/o/r/tasks/t1")));
        Assert.Equal("octopus started **oct-1** for this item.", Line("launched", ("runtime", "octopus"), ("worker", "oct-1")));
        Assert.Equal("octopus started **oct-1** for **Technical Design** (task `ServerTasks-9`). [session](https://gw.test/sessions/s-1) ([Octopus task](https://oct.test/app#/tasks/ServerTasks-9))",
            Line("launched", ("runtime", "octopus"), ("worker", "oct-1"), ("phase", "Technical Design"), ("task", "ServerTasks-9"), ("transcript", "https://gw.test/sessions/s-1"), ("url", "https://oct.test/app#/tasks/ServerTasks-9")));
        Assert.Equal("cae started **c-1** for this item. [session](http://gw.test/sessions/a.b_c) ([execution](https://cae.test/x))",
            Line("launched", ("runtime", "cae"), ("worker", "c-1"), ("transcript", "http://gw.test/sessions/a.b_c"), ("url", "https://cae.test/x")));
        // a transcript that is not a session page of the gateway is ignored; a url with a space or a parenthesis gets no execution link
        Assert.Equal("cae started **c-1** for this item. [session](https://other.test/x)", Line("launched", ("runtime", "cae"), ("worker", "c-1"), ("transcript", "https://gw.test/other/s-1"), ("url", "https://other.test/x")));
        Assert.Equal("cae started **c-1** for this item. [session](http://gw.test/sessions/s)", Line("launched", ("runtime", "cae"), ("worker", "c-1"), ("transcript", "http://gw.test/sessions/s"), ("url", "https://x.test/a(b)")));
    }

    [Fact]
    public void TierWaitingSkippedPinFallback()
    {
        Assert.Equal("model tier raised to **high** (two failed rounds).", Line("tier", ("name", "high"), ("reasons", "two failed rounds")));
        Assert.Equal("waiting: every session is at capacity.", Line("waiting", ("reason", "every session is at capacity")));
        Assert.Equal("not assigned: no worker has the label.", Line("skipped", ("reason", "no worker has the label")));
        Assert.Equal("the pinned worker **w1** could not take the item for 10 min (offline): the pin is set aside and it is offered to **w2**. A new worker label pins it again.",
            Line("pin-fallback", ("pin", "w1"), ("minutes", 10), ("code", "offline"), ("worker", "w2")));
    }

    [Fact]
    public void QcLines()
    {
        Assert.Equal("a QC environment is offered for PR #4 at head abc1234; nothing runs until a person presses Start.", Line("qc-offered", ("pr", 4), ("sha7", "abc1234")));
        Assert.Equal("a person asked for a QC environment for PR #4 at head abc1234 (attempt 2).", Line("qc-requested", ("pr", 4), ("sha7", "abc1234"), ("attempt", 2)));
        Assert.Equal("**w1** accepted the QC environment job for PR #4 (attempt 1).", Line("qc-assigned", ("worker", "w1"), ("pr", 4), ("attempt", 1)));
        Assert.Equal("QC environment for PR #4 is ready on **w1** (attempt 1): http://h:1. Reachable only from networks that can reach that computer.", Line("qc-ready", ("pr", 4), ("worker", "w1"), ("attempt", 1), ("url", "http://h:1")));
        Assert.Equal("QC environment for PR #4 is ready on **w1** (attempt 1). Reachable only from networks that can reach that computer.", Line("qc-ready", ("pr", 4), ("worker", "w1"), ("attempt", 1)));
        Assert.Equal("QC environment for PR #4 failed (**build-failed**).\n```text\nboom\n```", Line("qc-failed", ("pr", 4), ("code", "build-failed"), ("reason", "boom")));
        Assert.Equal("QC environment for PR #4 was reset by a person (attempt 3).", Line("qc-reset", ("pr", 4), ("attempt", 3)));
        Assert.Equal("QC environment for PR #4 torn down (person).", Line("qc-torn-down", ("pr", 4), ("reason", "person")));
        Assert.Equal("QC environment for PR #4 is waiting: no worker.", Line("qc-waiting", ("pr", 4), ("reason", "no worker")));
    }

    [Fact]
    public void QuestionEscalated_SupervisorIsConnectedOrAbsent()
    {
        Assert.Equal("a question from **w1** has waited 15 min for the supervisor (supervisor: connected); a person is asked to look at the dashboard.",
            Line("question-escalated", ("worker", "w1"), ("minutes", 15), ("supervisor", "Connected")));
        Assert.Equal("a question from **w1** has waited 15 min for the supervisor (supervisor: absent); a person is asked to look at the dashboard.",
            Line("question-escalated", ("worker", "w1"), ("minutes", 15), ("supervisor", "away")));
        Assert.Contains("(supervisor: absent)", Line("question-escalated", ("worker", "w1"), ("minutes", 15)));
    }

    [Fact]
    public void MergeWaiting()
        => Assert.Equal("PR #9 has a PASS verdict and is not merged yet: its checks are running (`checks-pending`). The gateway merges it by itself when that clears; no session has to call anything.",
            Line("merge-waiting", ("pr", 9), ("reason", "its checks are running"), ("code", "checks-pending")));

    [Fact]
    public void BaseUpdated_Plain_WithMergedPrs_WithOverlap_AndConflict()
    {
        Assert.Equal("master moved to `abc1234`; told the owner (**w1**) to merge master into the branch now: 2 commit(s) behind.", Line("base-updated", ("sha7", "abc1234"), ("worker", "w1"), ("behind", 2)));
        Assert.Equal("master moved to `abc1234` (merged: #4, #5); told the owner (**w1**) to merge master into the branch now: 2 commit(s) behind, files changed on both sides: a.cs.",
            Line("base-updated", ("sha7", "abc1234"), ("worker", "w1"), ("behind", 2), ("prs", "#4, #5"), ("overlap", "a.cs"), ("conflict", false)));
        Assert.Equal("the pull request has a merge conflict with master (`abc1234`); told the owner (**w1**) to merge master into the branch and resolve it: 1 commit(s) behind, files changed on both sides: a.cs.",
            Line("base-updated", ("sha7", "abc1234"), ("worker", "w1"), ("behind", 1), ("overlap", "a.cs"), ("conflict", true), ("prs", "#4")));
        Assert.Equal("the pull request has a merge conflict with master (`abc1234`); told the owner (**w1**) to merge master into the branch and resolve it: 1 commit(s) behind.",
            Line("base-updated", ("sha7", "abc1234"), ("worker", "w1"), ("behind", 1), ("conflict", true)));
    }

    [Fact]
    public void CiAndBranchLines()
    {
        Assert.Equal("released workflow run 77 of PR #3 (head `abc1234`, branch `feat/x`).", Line("ci-released", ("run", 77), ("pr", 3), ("sha7", "abc1234"), ("branch", "feat/x")));
        Assert.Equal("owner action: the gateway could not release workflow run 77 of PR #3 (GitHub answered 403): grant the factory-worker-author App Actions: Read & write. It retries by itself once that is granted.",
            Line("ci-release-refused", ("run", 77), ("pr", 3), ("status", 403)));
        Assert.Equal("the gateway merged master into the branch of PR #3 (GitHub's update branch: no conflict, the owner had not updated it within 90 s).", Line("branch-updated", ("pr", 3), ("seconds", 90)));
    }

    [Fact]
    public void LeaseLines()
    {
        Assert.Equal("the lease of **w1** ran out at T1 while no gateway was running (down since T0); renewed for 20 min instead of offering the item again.",
            Line("gap-renewed", ("worker", "w1"), ("lapsed", "T1"), ("since", "T0"), ("minutes", 20)));
        Assert.Equal("the gateway ended the lease of **w1** (silent); the item is offered again in resume mode.", Line("lease-expired", ("worker", "w1"), ("reason", "silent")));
        Assert.Equal("ownership released by **w1** (done); the next trigger assigns it again.", Line("released", ("worker", "w1"), ("reason", "done")));
    }

    [Fact]
    public void FlaggedSettleBoardAddedPulledReplenish()
    {
        Assert.Equal("flagged `stuck` in **Development**: no progress.", Line("flagged", ("code", "stuck"), ("column", "Development"), ("reason", "no progress")));
        Assert.Equal("the settle step of this merged item is re-armed by operator (awaiting `ci`): retry. The gateway carries it on; it is not offered to a worker.",
            Line("settle-rearmed", ("by", "operator"), ("awaiting", "ci"), ("reason", "retry")));
        Assert.Equal("the item has the factory label but had no column on the board: the sweep put it in Todo, where it is offered like any new item.", Line("board-added", ("column", "Todo")));
        Assert.Equal("pulled from **Todo** into **Technical Design**: a worker slot was free and nothing waited there (1 of at most 2 items in the AI columns).",
            Line("pulled", ("from", "Todo"), ("column", "Technical Design"), ("wip", 1), ("wipLimit", 2)));
        Assert.Equal("the gateway created work item #12 (**Next thing**) to keep activity going after this item finished.", Line("replenish", ("new", 12), ("title", "Next thing")));
    }

    [Fact]
    public void Unparked_EveryBranch()
    {
        Assert.Equal("park `c` removed by sweep: r. The item is merged: the gateway settles it, nothing is offered.", Line("unparked", ("code", "c"), ("by", "sweep"), ("reason", "r"), ("merged", true), ("closed", true), ("reset", true)));
        Assert.Equal("park `c` removed by sweep: r. The item is closed: nothing is offered.", Line("unparked", ("code", "c"), ("by", "sweep"), ("reason", "r"), ("closed", true)));
        Assert.Equal("park `master-broken` released early by operator: r. Its fix-forward rounds and demotions count from 0 again. The item waits for master green again, then for its release candidate.",
            Line("unparked", ("code", "master-broken"), ("by", "operator"), ("reason", "r"), ("reset", true)));
        Assert.Equal("park `c` expired (r). The next sweep takes it up again in **Development**.", Line("unparked", ("code", "c"), ("by", "expiry"), ("reason", "r"), ("column", "Development")));
        Assert.Equal("park `master-broken` expired (r). Its fix-forward rounds and demotions count from 0 again. The next sweep takes it up again in **Todo**.",
            Line("unparked", ("code", "master-broken"), ("by", "expiry"), ("reason", "r"), ("column", "Todo"), ("reset", true)));
        Assert.Equal("park `c` released early by person: r. The next sweep takes it up again in **Todo**.", Line("unparked", ("code", "c"), ("by", "person"), ("reason", "r"), ("column", "Todo")));
    }

    [Fact]
    public void PromotionLines()
    {
        Assert.Equal("promoted release 1.0.5 to **uat** (Octopus task `ServerTasks-1`); the gateway follows the deployment and answers its Sign-off.", Line("promoted", ("release", "1.0.5"), ("environment", "uat"), ("task", "ServerTasks-1")));
        Assert.Equal("release 1.0.5 waits for the automatic promotion to **uat**: tdd is red. The item stays in the Release Queue; the gateway looks again at every sweep.", Line("promotion-waiting", ("release", "1.0.5"), ("environment", "uat"), ("reason", "tdd is red")));
        Assert.Equal("answered the Sign-off of the deployment of release 1.0.5 to **uat** (Octopus task `ServerTasks-1`): **Proceed**.", Line("promotion-signoff", ("release", "1.0.5"), ("environment", "uat"), ("task", "ServerTasks-1"), ("result", "Proceed"), ("reason", "")));
        Assert.Equal("answered the Sign-off of the deployment of release 1.0.5 to **uat** (Octopus task `ServerTasks-1`): **Abort** (gate red).", Line("promotion-signoff", ("release", "1.0.5"), ("environment", "uat"), ("task", "ServerTasks-1"), ("result", "Abort"), ("reason", "gate red")));
    }

    [Fact]
    public void KeeperAndManagerActions()
    {
        Assert.Equal("the operator split item 5, under policy `split`.", Line("keeper-action", ("by", "Operator"), ("detail", "split item 5"), ("policy", "split")));
        Assert.Equal("the backlog keeper (**keeper-1**) closed item 5, under policy `stale`: older than 90 days.", Line("keeper-action", ("by", "keeper-1"), ("detail", "closed item 5"), ("policy", "stale"), ("reason", "older than 90 days")));
        Assert.Equal("the plant manager (**pm-1**) parked item 5, under policy `loop`: three rounds ([Copilot session](https://s.test/1)).",
            Line("manager-action", ("by", "pm-1"), ("detail", "parked item 5"), ("policy", "loop"), ("reason", "three rounds"), ("session", "https://s.test/1")));
        Assert.Equal("the operator parked item 5, under policy `loop`.", Line("manager-action", ("by", "operator"), ("detail", "parked item 5"), ("policy", "loop"), ("reason", ""), ("session", "")));
    }

    [Fact]
    public void AnUnknownEvent_GivesItsName()
        => Assert.Equal("something-new", Line("something-new", ("item", 1)));

    [Fact]
    public void ValuesInterpolateLikePowerShell()
    {
        // a bool reads True, a missing field is empty, a null field is empty, a number has no culture
        Assert.Equal("waiting: True.", Line("waiting", ("reason", true)));
        Assert.Equal("waiting: .", Line("waiting"));
        Assert.Equal("waiting: .", Line("waiting", ("reason", null)));
        Assert.Equal("waiting: 1234567.5.", Line("waiting", ("reason", 1234567.5)));
    }

    // ---- the line helpers -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void LimitLine_FlattensFencesAndCaps()
    {
        Assert.Equal("a b c", JournalText.LimitLine("  a\n b\t\tc \n"));
        Assert.Equal("x ''' y", JournalText.LimitLine("x ```` y"));
        Assert.Equal("x `` y", JournalText.LimitLine("x `` y"));
        Assert.Equal(new string('a', 300), JournalText.LimitLine(new string('a', 300)));
        Assert.Equal(new string('a', 300) + " [...]", JournalText.LimitLine(new string('a', 301)));
    }

    [Fact]
    public void Stamp_IsUtcToTheMinute()
    {
        Assert.Equal("2026-10-27 09:05Z", JournalText.Stamp(new DateTime(2026, 10, 27, 9, 5, 59, DateTimeKind.Utc)));
        Assert.Equal("2026-10-27 07:05Z", JournalText.Stamp(new DateTimeOffset(2026, 10, 27, 9, 5, 0, TimeSpan.FromHours(2)).UtcDateTime));
    }

    [Fact]
    public void FormatFold_LatestFirst_ThenCountAndHistory()
    {
        var first = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 10, 27, 9, 30, 0, DateTimeKind.Utc);
        var lines = new[] { "2026-10-27 08:00Z waiting: a.", "2026-10-27 09:30Z waiting: b." };
        Assert.Equal(
            "\U0001F3ED Plant manager: waiting: b.\n\nwaiting 7 times, from 2026-10-27 08:00Z to 2026-10-27 09:30Z (the latest 2 below; this comment is edited instead of posting a new one each time).\n\n> 2026-10-27 08:00Z waiting: a.\n> 2026-10-27 09:30Z waiting: b.\n\n<!-- plant event=waiting key=k1 fold=7 -->",
            JournalText.FormatFold("waiting", 7, first, last, lines, "k1"));
        Assert.Contains("(each below;", JournalText.FormatFold("waiting", 2, first, last, lines, "k1"));
        Assert.StartsWith("\U0001F3ED Plant manager: \n\n", JournalText.FormatFold("waiting", 1, first, last, [], "k1"));
    }

    [Fact]
    public void FormatFold_IsCappedAt6000()
    {
        var t = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
        var body = JournalText.FormatFold("matched", 9, t, t, [new string('x', 7000) + " y"], "k");
        Assert.EndsWith("\n[truncated]\n\n<!-- plant event=matched key=k fold=9 -->", body);
        Assert.Equal(5900 + "\n[truncated]\n\n<!-- plant event=matched key=k fold=9 -->".Length, body.Length);
    }

    [Fact]
    public void FoldKinds_AreThoseOfTheScript()
        => Assert.Equal(new[] { "matched", "waiting", "reassigned" }, JournalText.FoldKinds);

    // ---- golden strings: lines the Tier 1 scenarios made the gateway write ----------------------------------------------------------------------

    static readonly Lazy<string> GoldenCorpus = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FactoryWorker.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var golden = Path.Combine(dir!.FullName, "tests", "scenarios", "golden");
        return string.Join("\n", Directory.EnumerateFiles(golden, "*.writes.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(File.ReadAllText));
    });

    // The golden files hold the line as the write recorder normalizes it (WriteRecorder.cs): "30 s" is "{dur}", and the comma list in a
    // parenthesis is sorted. Only these two normalizations touch the lines below; the rest of the line is the script's text.
    static string Normalize(string s)
    {
        s = Regex.Replace(s, @"\b\d+(?:\.\d+)?\s?(?:ms|s|sec|seconds?|m|min|minutes?|h|hours?)\b", "{dur}");
        return Regex.Replace(s, @"\(((?:[^()\n]|\([^()\n]*\))*)\)", m => "(" + string.Join(", ", m.Groups[1].Value.Split(", ").Order(StringComparer.Ordinal)) + ")");
    }

    // the line as it appears inside the JSON body of a recorded write: quotes, backslashes and line breaks escaped
    static string AsJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    public static IEnumerable<object[]> GoldenCases() => new (string Name, string Event, (string K, object? V)[] Fields)[]
    {
        ("offered", "offered", [("worker", "qw1"), ("type", "persistent-local"), ("ackSeconds", 30)]),
        ("assigned", "assigned", [("item", 1), ("column", "Technical Design"), ("worker", "bm1"), ("mode", "first"), ("attempt", 1), ("lease", "{time}")]),
        ("qc-offered", "qc-offered", [("pr", "{#2}"), ("sha7", "{sha}")]),
        ("qc-requested", "qc-requested", [("pr", "{#2}"), ("sha7", "{sha}"), ("attempt", 1)]),
        ("qc-assigned", "qc-assigned", [("worker", "qw1"), ("pr", "{#2}"), ("attempt", 1)]),
        ("qc-ready", "qc-ready", [("pr", "{#2}"), ("worker", "qw1"), ("attempt", 1), ("url", "http://127.0.0.1:{port}")]),
        ("qc-failed", "qc-failed", [("pr", "{#2}"), ("code", "app-down"), ("reason", "the worker reported that the application is not running")]),
        ("qc-reset", "qc-reset", [("pr", "{#2}"), ("attempt", 2)]),
        ("qc-torn-down", "qc-torn-down", [("pr", "{#2}"), ("reason", "worker-stopped")]),
        ("merge-waiting", "merge-waiting", [("pr", "{#2}"), ("reason", "its required checks are still running"), ("code", "checks-pending")]),
        ("base-updated", "base-updated", [("sha7", "{sha}"), ("worker", "bm1"), ("behind", 1), ("overlap", "src/Shared.cs"), ("prs", "#{#6}")]),
        ("branch-updated", "branch-updated", [("pr", "{#2}"), ("seconds", 30)]),
        ("board-added", "board-added", [("column", "Technical Design")]),
        ("pulled", "pulled", [("from", "Todo"), ("column", "Technical Design"), ("wip", 1), ("wipLimit", 2)]),
        ("unparked closed", "unparked", [("code", "runtime-failed"), ("by", "the sweep"), ("reason", "the item is closed"), ("closed", true)]),
        ("promoted", "promoted", [("release", "3.41.3"), ("environment", "uat"), ("task", "ServerTasks-{n}")]),
        ("promotion-signoff", "promotion-signoff", [("release", "3.41.3"), ("environment", "prod"), ("task", "ServerTasks-{n}"), ("result", "Proceed")]),
        ("promotion-waiting", "promotion-waiting", [("release", "3.43.4"), ("environment", "uat"), ("reason", "its deployment to tdd is Failed (Deployments-{dep:4})")]),
    }.Select(c => new object[] { c.Name, c.Event, c.Fields });

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void TheGatewaysRecordedLine_IsWhatThePortProduces(string name, string ev, (string K, object? V)[] fields)
    {
        var line = Normalize(Line(ev, fields));
        // the opening of the line as the rule script wrote it, up to the hidden marker that follows it
        var expected = "Plant manager: " + AsJson(line) + "\\n\\n<!-- plant event=" + ev;
        Assert.True(GoldenCorpus.Value.Contains(expected, StringComparison.Ordinal), $"no golden write holds the {name} line produced by the port:\n{line}");
    }

    // ---- W3.2: Protect-JournalText ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Protect_ReplacesEveryPatternOfTheCatalogue_AndNothingElse()
    {
        var token = "ghp_" + new string('A', 26);   // composed at run time: the secret scan stays meaningful
        Assert.Equal("a [redacted] b", JournalText.Protect($"a {token} b", ["gh[pousr]_[A-Za-z0-9]{20,}"]));
        Assert.Equal($"a {token} b", JournalText.Protect($"a {token} b", ["AKIA[0-9A-Z]{16}"]));
        Assert.Equal("[redacted]-[redacted]", JournalText.Protect("x1-y2", ["x1", "y2"]));
        Assert.Equal("", JournalText.Protect(null, ["x"]));
        Assert.Equal("", JournalText.Protect("", ["x"]));
    }

    [Fact]
    public void Protect_WithoutACatalogue_UsesTheFallbackPatternsOfTheScript()
    {
        var token = "ghp_" + new string('B', 26);
        Assert.Equal("t [redacted]", JournalText.Protect($"t {token}", null));
        Assert.Equal("t [redacted]", JournalText.Protect($"t {token}", [""]));
        Assert.Equal("k [redacted] z", JournalText.Protect("k AKIA" + new string('Q', 16) + " z", []));
        Assert.Equal("[redacted]", JournalText.Protect("password: " + new string('c', 14), null));
        Assert.Equal(6, JournalText.FallbackSecretPatterns.Count);
    }
}
