using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Workflows.Keeper;

namespace Fworker.Workflows.Reconcilers;

// W9.10g, second pull request (docs/design/fworker-wolverine-implementation.md, row W9.10g): the plant-wide steps of v1's sweep in C#. Each is the port of a function of the rule scripts and keeps its name in the comment:
//   ClaimRefReap   Invoke-ClaimRefReap   (Verbs.Sweep.ps1, issue 47 and 400): the claim refs with no live claim behind them
//   BoardGuard     Invoke-BoardGuard     (Verbs.Sweep.ps1, issue 411): open labelled issues without a card, or with a card without a column, are put in the intake column
//   Replenish      Invoke-Replenish      (Replenish.ps1): one docs-only item for each item that reached Done in the last 24 hours
//   DesignWaiting  the count of the sweep's item loop: the open factory items in the keeper's column
// The recurring jobs of the gateway (gateway/Ports/PlantStepJobs.cs) call them where the engine acts; the rule script keeps the same steps for the gateway that does not act (tdd while it only shadows) until W9.11b removes it.
// They stand on the ports of the keeper and manager verbs (Keeper/KeeperPorts.cs) plus the three reads of <see cref="IPlantGitHub"/>, and on the same locks and state comments, so v1's scripts and these steps exclude each other.

/// <summary>What the plant steps read of GitHub beyond the keeper's ports: the claim refs, the labelled issues of the repository (REST, paged) and the board's last column.</summary>
public interface IPlantGitHub : IKeeperGitHub
{
    /// <summary><c>Get-ClaimRefItems</c>: the numbers of the items that have a claim ref (<c>refs/factory/&lt;name&gt;/claims/N</c>), whatever GitHub's paging does with the page parameter.</summary>
    Task<IReadOnlyList<int>> ClaimRefItems();

    /// <summary>GET issues?state=&lt;state&gt;&amp;labels=&lt;the factory label&gt;&amp;per_page=100&amp;page=&lt;page&gt;: one page, pull requests included (the caller leaves them out, as GitHub lists them as issues).</summary>
    Task<IReadOnlyList<JsonObject>> LabelledIssues(string state, int page);

    /// <summary><c>Get-LastColumn</c>: the last Status option of the board.</summary>
    Task<string> LastColumn();
}

/// <summary>When each claim ref was first seen without a live claim (the script's <c>claim-orphans.json</c> of the state directory): a restart that loses it only delays the removal of an open item's orphan by a lease.</summary>
public interface IClaimOrphanMemory
{
    IReadOnlyDictionary<int, DateTime> Load();
    void Save(IReadOnlyDictionary<int, DateTime> firstSeen);
}

/// <summary>The memory kept in the process (a test, a gateway without a state directory).</summary>
public sealed class InMemoryClaimOrphans : IClaimOrphanMemory
{
    IReadOnlyDictionary<int, DateTime> kept = new Dictionary<int, DateTime>();
    public IReadOnlyDictionary<int, DateTime> Load() => kept;
    public void Save(IReadOnlyDictionary<int, DateTime> firstSeen) => kept = new Dictionary<int, DateTime>(firstSeen);
}

/// <summary><c>keeper.replenish</c> of the configuration (<c>Get-ReplenishCfg</c>): off unless it says enabled and names this environment (no list: every environment).</summary>
public sealed record ReplenishSettings(bool Enabled, int MaxPerDay, int TargetOpen, int HoldWhenWaiting, bool OnlyWhenIdle)
{
    public static ReplenishSettings Off { get; } = new(false, 30, 10, 0, false);

    public static ReplenishSettings From(JsonNode? root, Func<string, string?> env)
    {
        JsonObject? r = null;
        try { r = root?["keeper"]?["replenish"] as JsonObject; } catch (Exception) { }
        if (r == null) return Off;
        var e = (env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant(); if (e.Length == 0) e = "tdd";
        var inEnv = !r.ContainsKey("environments") || r["environments"] is not JsonArray list || list.Any(x => string.Equals(Ps.Str(x), e, StringComparison.OrdinalIgnoreCase));
        int Num(string key, int fallback, int min, int max) => r.TryGetPropertyValue(key, out var v) && v != null && Ps.Str(v) is { } t && t.Length is > 0 and <= 6 && t.All(char.IsAsciiDigit)
            ? Math.Min(max, Math.Max(min, int.Parse(t, CultureInfo.InvariantCulture))) : fallback;
        return new ReplenishSettings(r["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var on) && on && inEnv, Num("maxPerDay", 30, 0, 500), Num("targetOpen", 10, 0, 200), Num("holdWhenWaiting", 0, 0, 1000),
            r["onlyWhenIdle"] is JsonValue ow && ow.TryGetValue<bool>(out var idle) && idle);
    }
}

/// <summary>What the steps take of the configuration: the lease of a claim (<c>timing.leaseMinutes</c>), how many issues one guard pass places, the replenishment.</summary>
public sealed record PlantStepSettings(double LeaseMinutes, ReplenishSettings Replenish, int GuardMax = 20)
{
    public static PlantStepSettings From(JsonNode? root, Func<string, string?> env)
    {
        var minutes = 30.0;
        try { if (root?["timing"]?["leaseMinutes"] is JsonValue v && v.TryGetValue<double>(out var d)) minutes = d; } catch (Exception) { }
        // the suites shorten it with FACTORY_TIMING_leaseMinutes, which the rule scripts read as well (Get-Cfg), whatever the file says
        if (env("FACTORY_TIMING_leaseMinutes") is { Length: > 0 } t && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o)) minutes = o;
        return new PlantStepSettings(minutes, ReplenishSettings.From(root, env));
    }
}

/// <summary>The lease the gateway holds ended while a step ran: what the step read is dropped, as the script's fenced answer was.</summary>
public sealed class PlantStepFenced : Exception;

/// <summary>What a step did, in the fields of the script's answer: the actions the log prints, the errors, and for the replenishment and the count what the plant takes from them.</summary>
public sealed class PlantStepResult
{
    public JsonArray Actions { get; } = [];
    public JsonArray Errors { get; } = [];
    public JsonObject? Replenish { get; set; }
    public int? DesignWaiting { get; set; }

    public void Action(JsonObject a) => Actions.Add(a);
    public void Error(int item, string code, string reason) => Errors.Add(new JsonObject { ["item"] = item, ["code"] = code, ["reason"] = reason });

    /// <summary>The answer in the shape the jobs read it (the fields of the script's answer that they apply): the actions and errors for the log, the replenishment status, the count.</summary>
    public JsonObject ToJson() => new() { ["ok"] = true, ["actions"] = Actions.DeepClone(), ["errors"] = Errors.DeepClone(), ["replenish"] = Replenish?.DeepClone(), ["design_waiting"] = DesignWaiting };
}

/// <summary>The four steps, as the recurring jobs of the gateway call them (the jobs' tests stand in for them).</summary>
public interface IPlantSteps
{
    /// <param name="skip">The items the saga owns: their refs are not reaped here.</param>
    Task<PlantStepResult> ClaimRefReap(IReadOnlySet<int> skip);
    Task<PlantStepResult> BoardGuard();
    /// <param name="waiting">The items of the plant's last plan that no session has accepted; null before a plan was counted.</param>
    /// <param name="freeWorkers">The sessions that could take a new item now.</param>
    Task<PlantStepResult> Replenish(int? waiting, int freeWorkers);
    Task<PlantStepResult> DesignWaiting();
}

public sealed class PlantSteps(VerbContext cx, IPlantGitHub github, IClaimOrphanMemory orphans, PlantStepSettings settings, Func<bool>? mayAct = null) : IPlantSteps
{
    FactoryRules Rules => cx.Rules;
    bool May() => mayAct?.Invoke() ?? true;

    void Fence() { if (!May()) throw new PlantStepFenced(); }

    static string CodeOf(Exception e) => e is VerbRejection r ? r.Code : "error";

    // a step never throws except for the fence: its failure is an error line of the answer, as the script's try/catch did
    async Task<PlantStepResult> Guarded(string step, Func<PlantStepResult, Task> body)
    {
        var res = new PlantStepResult();
        try { await body(res); }
        catch (PlantStepFenced) { throw; }
        catch (Exception e) when (e is not OperationCanceledException) { res.Error(0, CodeOf(e), $"{step}: {e.Message}"); }
        return res;
    }

    /// <summary>Whether an item is gone or does not exist: a read of its issue or its state comment that GitHub answers with 404.</summary>
    static bool NotThere(VerbRejection r) => r.Code is "github-not-found" or "item-not-found" || (r.Code == "github-error" && r.Extra.TryGetValue("status", out var s) && s is 404);

    // ---- claim refs with no live claim behind them (issue 47) ------------------------------------------------------------------------------

    /// <summary>
    /// <c>Invoke-ClaimRefReap</c>: for each claim ref, under the item's lock (taken without waiting: a busy item is left to a later pass, issue 400) and on a fresh read of the state, a ref with no live claim is removed at once when
    /// the item is closed or gone and, when it is open, once it has been seen without a claim for longer than a lease. An item the saga owns (<paramref name="skip"/>) is not reaped here. Another environment's item is not ours.
    /// </summary>
    public Task<PlantStepResult> ClaimRefReap(IReadOnlySet<int> skip) => Guarded("claim-ref-reap", async res =>
    {
        var board = new Dictionary<int, BoardEntry>();
        foreach (var e in await github.ListBoard()) board.TryAdd(e.Number, e);
        var first = orphans.Load(); var keep = new Dictionary<int, DateTime>(); var now = cx.Now;
        foreach (var n in await github.ClaimRefItems())
        {
            Fence();
            if (skip.Contains(n)) { if (first.TryGetValue(n, out var f0)) keep[n] = f0; continue; }   // an item that has a saga: its refs are not reaped by v1
            try
            {
                bool? open;
                if (board.TryGetValue(n, out var entry)) open = entry.Open;
                else
                {
                    try { open = (await github.GetItemInfo(n)).Open; }
                    catch (VerbRejection r) when (NotThere(r)) { open = false; }
                    catch (VerbRejection r) when (r.Code == "out-of-scope") { open = null; }
                }
                if (open is null) continue;   // another environment's claim (the refs are shared when the instance name is): not ours to reap
                var since = first.TryGetValue(n, out var seen) ? seen : now;
                var aged = (now - since).TotalMinutes >= settings.LeaseMinutes;
                string outcome;
                await using (await cx.Locks.Item(n, TimeSpan.Zero))
                {
                    StateHolder? st = null;
                    try { st = await cx.State.Read(n); } catch (VerbRejection r) when (NotThere(r)) { }
                    if (st != null && cx.ClaimLive(st.State)) outcome = "live";
                    else if (open == false || aged) { await github.RemoveClaimRef(n); outcome = "removed"; }
                    else outcome = "orphan";
                }
                if (outcome == "removed") res.Action(new JsonObject { ["item"] = n, ["action"] = "claim-ref-removed", ["reason"] = open == false ? "item-closed" : "orphan" });
                else if (outcome == "orphan") { keep[n] = since; res.Action(new JsonObject { ["item"] = n, ["action"] = "orphan-claim", ["since"] = Ps.Iso(since) }); }
            }
            catch (VerbRejection r)
            {
                if (first.TryGetValue(n, out var f1)) keep[n] = f1;
                res.Action(new JsonObject { ["item"] = n, ["action"] = "skip", ["reason"] = $"claim-ref-reap: {r.Code}" });
            }
        }
        orphans.Save(keep);
    });

    // ---- the board guard (issue 411) --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Invoke-BoardGuard</c>: an open issue with the factory label that has no card on the board, or a card with no column, is invisible to the sweep. Each pass lists the open labelled issues (paged; pull requests, the rival
    /// label's and other environments' issues left out) and, for each one the board shows without a column, reads the issue again under the board-intake lock (taken without waiting: a verb that is still creating an item keeps it)
    /// and puts the card in the intake column, the first column the factory works. One journal line each. At most <see cref="PlantStepSettings.GuardMax"/> attempts a pass; three failures in a row or a rate limit end it.
    /// </summary>
    public Task<PlantStepResult> BoardGuard() => Guarded("board-guard", async res =>
    {
        var placed = new HashSet<int>();
        foreach (var e in await github.ListBoard()) if ((e.Column ?? "").Length > 0) placed.Add(e.Number);
        var intake = Rules.AiColumns.Count > 0 ? Rules.AiColumns[0] : "";
        var page = 1; var done = 0; var fails = 0;
        IReadOnlyList<JsonObject> chunk;
        do
        {
            chunk = await github.LabelledIssues("open", page);
            foreach (var i in chunk)
            {
                var n = VerbContext.AsInt(i["number"]);
                if (i.ContainsKey("pull_request") || n <= 0 || placed.Contains(n)) continue;
                var labels = (i["labels"] as JsonArray ?? []).Select(l => Ps.Str(l?["name"])).ToList();
                if (Rules.RivalLabel.Length > 0 && Ps.NameIn(Rules.RivalLabel, labels)) continue;
                if (!Rules.InScope(labels)) continue;   // another environment's issue: its card is not ours to place
                if (done >= settings.GuardMax || fails >= 3) return;   // attempts count, failed ones too
                done++;
                Fence();
                try
                {
                    string how;
                    await using (await cx.Locks.Intake(TimeSpan.Zero))
                    {
                        var info = await github.GetItemInfo(n);
                        if (!info.Open || (info.Column ?? "").Length > 0) how = "placed";
                        else
                        {
                            var card = !string.IsNullOrEmpty(info.CardId) ? info.CardId : await github.AddToBoard(info.NodeId);
                            await github.MoveCard(info with { CardId = card }, intake);
                            how = !string.IsNullOrEmpty(info.CardId) ? "repaired" : "added";
                        }
                    }
                    if (how == "placed") { placed.Add(n); done--; continue; }   // (a read that found it placed is no attempt)
                    placed.Add(n); fails = 0;
                    res.Action(new JsonObject { ["item"] = n, ["action"] = "board-added", ["column"] = intake, ["repaired"] = how == "repaired" });
                    try { await using (await cx.Locks.Item(n, TimeSpan.FromSeconds(3))) await cx.Journal(n, null, "board-added", [new("column", intake)], n.ToString(CultureInfo.InvariantCulture)); }
                    catch (Exception e) when (e is not OperationCanceledException and not PlantStepFenced) { res.Action(new JsonObject { ["item"] = n, ["action"] = "board-added-unjournalled", ["reason"] = e.Message }); }
                }
                catch (VerbRejection r)
                {
                    if (r.Code == "intake-busy") { res.Action(new JsonObject { ["item"] = n, ["action"] = "skip", ["reason"] = "board-guard: intake-busy" }); done--; continue; }   // (no attempt, no failure)
                    if (r.Code == "github-rate-limited") { res.Action(new JsonObject { ["item"] = n, ["action"] = "board-add-failed", ["reason"] = "rate limited: the pass ends" }); return; }
                    res.Action(new JsonObject { ["item"] = n, ["action"] = "board-add-failed", ["reason"] = r.Message }); fails++;
                }
                catch (Exception e) when (e is not OperationCanceledException and not PlantStepFenced) { res.Action(new JsonObject { ["item"] = n, ["action"] = "board-add-failed", ["reason"] = e.Message }); fails++; }
            }
            page++;
        } while (chunk.Count == 100 && page <= 20);
    });

    // ---- the count of the keeper's column ---------------------------------------------------------------------------------------------------

    /// <summary>The open factory items in the keeper's column (<c>keeper.newItemColumn</c>): fewer than <c>keeper.minTechnicalDesign</c> make the keeper's backlog-low. The saga's items count: the backlog is the board's.</summary>
    public Task<PlantStepResult> DesignWaiting() => Guarded("design-waiting", async res =>
    {
        res.DesignWaiting = (await github.ListBoard()).Count(e => e.Open && Ps.NameIn(Rules.Label, e.Labels) && string.Equals(e.Column ?? "", Rules.KeeperColumn, StringComparison.OrdinalIgnoreCase));
    });

    // ---- the keeper's replenishment ---------------------------------------------------------------------------------------------------------

    // The cards of the last column that Replenish read and found not eligible, with the time before which they are not read again (DateTime.MaxValue: never, for the life of the process).
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> notEligible = new();

    /// <summary>
    /// <c>Invoke-Replenish</c>: when an item the gateway owns reached the last column (Done, delivered to production) within the last 24 hours and has not been replenished, ONE new, small, docs-only item is created in the keeper's
    /// column; the finished item's state records <c>replenished</c> first, so a failure after the issue exists never makes a second one. Never above <c>targetOpen</c> open factory items or <c>maxPerDay</c> in 24 hours; with
    /// <c>holdWhenWaiting</c> or <c>onlyWhenIdle</c> only when the plant is short of work (<paramref name="waiting"/>: the items of the last plan that no session has accepted, null before a plan was counted; <paramref name="freeWorkers"/>:
    /// the sessions that could take an item). The answer is the counts for /status; null when the replenishment is off.
    /// </summary>
    public Task<PlantStepResult> Replenish(int? waiting, int freeWorkers) => Guarded("replenish", async res =>
    {
        var rc = settings.Replenish;
        if (!rc.Enabled) return;
        var list = await github.ListBoard();
        var last = await github.LastColumn();
        var open = list.Count(e => e.Open && Ps.NameIn(Rules.Label, e.Labels));
        var created = new JsonArray();
        var r = new JsonObject
        {
            ["enabled"] = true, ["maxPerDay"] = rc.MaxPerDay, ["targetOpen"] = rc.TargetOpen, ["open"] = open, ["createdToday"] = 0, ["created"] = created, ["skipped"] = "", ["waiting"] = waiting,
            ["holdWhenWaiting"] = rc.HoldWhenWaiting, ["onlyWhenIdle"] = rc.OnlyWhenIdle,
        };
        res.Replenish = r;
        // backpressure: generated work only when the plant is short of it (unchanged when neither setting is on)
        if (rc.HoldWhenWaiting > 0 || rc.OnlyWhenIdle)
        {
            if (waiting is null) { r["skipped"] = "no-plan"; return; }
            if (rc.HoldWhenWaiting > 0 && waiting >= rc.HoldWhenWaiting) { r["skipped"] = "waiting"; return; }
            if (rc.OnlyWhenIdle && (waiting > 0 || freeWorkers <= 0)) { r["skipped"] = "not-idle"; return; }
        }
        var finished = list.Where(e => string.Equals(e.Column ?? "", last, StringComparison.OrdinalIgnoreCase) && Ps.NameIn(Rules.Label, e.Labels)).OrderByDescending(e => e.Number).ToList();
        List<JsonObject>? issues = null; var today = 0; var used = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = cx.Now.AddHours(-24);
        var reads = 0;
        foreach (var d in finished)
        {
            if (open >= rc.TargetOpen) { r["skipped"] = "target-open"; break; }
            // a card read before and found not to qualify (replenished, done more than 24 h ago, no done_at yet) costs no read this time: the newest 12 cards of the column must not hide an older one that finished today
            // (an item pulled from Todo is older in number than the replenished chain, and v2 finishes it; the script, which reads the newest 12 every time, never reaches it)
            if (notEligible.TryGetValue(d.Number, out var recheck) && cx.Now < recheck) continue;
            if (reads >= 12) break;
            reads++;
            Fence();
            var st = await cx.State.Read(d.Number); var s = st.State;
            if (!(s["merge"] is JsonObject merge && Ps.Has(merge, "done_at") && Ps.Truthy(merge["done_at"]))) { notEligible[d.Number] = cx.Now.AddMinutes(30); continue; }
            var doneAt = Ps.FromIso(merge["done_at"]);
            if (doneAt is null || (cx.Now - doneAt.Value).TotalHours > 24) { notEligible[d.Number] = DateTime.MaxValue; continue; }
            if (Ps.Has(s, "replenished") && Ps.Truthy(s["replenished"])) { notEligible[d.Number] = DateTime.MaxValue; continue; }
            if (issues is null)
            {
                issues = await FactoryIssues();
                foreach (var i in issues) used.Add(Ps.Str(i["title"]).ToLowerInvariant());
                today = issues.Count(i => Ps.Str(i["body"]).Contains("<!-- keeper-replenish -->", StringComparison.OrdinalIgnoreCase) && Ps.Str(i["created_at"]) is { Length: > 0 } c && Ps.FromIso(c) is { } at && at > cutoff);
            }
            if (today >= rc.MaxPerDay) { r["skipped"] = "max-per-day"; break; }
            var topic = ReplenishTopics.Next(used);
            var body = $"Docs-only change. Add or extend `{topic.File}` with a short, accurate page about {topic.About}. Keep it to what the repository's code and existing documentation already show.\n\n"
                + $"Created by the fworker gateway to keep activity going: work item #{d.Number} finished (Done, delivered to production), so the gateway added this one. <!-- keeper-replenish -->\n\n"
                + "## Acceptance criteria\n\n"
                + $"- `{topic.File}` exists and describes {topic.About}.\n"
                + "- The page is accurate against the code and existing documentation.\n"
                + "- Only Markdown files under `docs/` change; no source code, tests, scripts, workflows or configuration.";
            var labels = new[] { "enhancement", Rules.AutoMergeLabel, Rules.Label, Rules.EnvLabel }.Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).ToList();
            // Issue 411: from the issue to its card's column under the board-intake lock, as KeeperCommands does: the board guard (the script's or the job) places a card only under it.
            int n;
            await using (await cx.Locks.Intake())
            {
                n = await github.CreateIssue(topic.Title, body, labels);
                s["replenished"] = new JsonObject { ["item"] = n, ["at"] = Ps.Iso(cx.Now) };
                await cx.State.Save(d.Number, st);   // first: a failure below must not create a second item
                var info = await github.GetItemInfo(n);
                if (string.IsNullOrEmpty(info.CardId)) { await github.AddToBoard(info.NodeId); info = await github.GetItemInfo(n); }
                if (!string.Equals(info.Column ?? "", Rules.KeeperColumn, StringComparison.OrdinalIgnoreCase)) await github.MoveCard(info, Rules.KeeperColumn);
            }
            await using (await cx.Locks.Item(d.Number, TimeSpan.FromSeconds(3))) await cx.Journal(d.Number, null, "replenish", [new("new", n), new("title", topic.Title)], "");
            used.Add(topic.Title.ToLowerInvariant());
            open++; today++; created.Add(n);
            r["open"] = open; r["createdToday"] = today;
        }
        r["open"] = open; r["createdToday"] = today;
    });

    // The repository's factory issues, open and closed, newest first (up to 5 pages of 100): their titles (uniqueness) and the replenished ones of the day (Get-ReplenishIssues).
    async Task<List<JsonObject>> FactoryIssues()
    {
        var all = new List<JsonObject>();
        for (var page = 1; page <= 5; page++)
        {
            var rows = await github.LabelledIssues("all", page);
            all.AddRange(rows.Where(i => !i.ContainsKey("pull_request")));
            if (rows.Count < 100) break;
        }
        return all;
    }
}

/// <summary>The catalogue of the replenishment (<c>$script:ReplenishTopics</c> and <c>ReplenishTerms</c>): the first topic whose title is unused, then a glossary entry, then a numbered note.</summary>
public static class ReplenishTopics
{
    public sealed record Topic(string Title, string About, string File);

    static readonly Topic[] Catalogue =
    [
        new("Docs: work order lifecycle guide", "the lifecycle of a work order from Draft through Assigned and InProgress to Complete, including shelving back to Assigned and cancellation", "docs/domain/work-order-lifecycle.md"),
        new("Docs: work order statuses reference", "each WorkOrderStatus (Draft, Assigned, InProgress, Complete, Cancelled), what it means and which transitions leave it", "docs/domain/work-order-statuses.md"),
        new("Docs: waiting on vendor substate explained", "the WaitingOnVendor substate of InProgress and why waiting and resuming do not change the persisted status", "docs/domain/waiting-on-vendor.md"),
        new("Docs: work order priorities", "the Low, Medium (default) and High priorities and how they are stored and used when searching", "docs/domain/work-order-priorities.md"),
        new("Docs: employees and roles guide", "the Employee and Role model and what CanCreateWorkOrder and CanFulfillWorkOrder permit", "docs/domain/employees-and-roles.md"),
        new("Docs: buildings and rooms on a work order", "how Building (optional) and RoomNumber locate a work order and how the building room summary groups them", "docs/domain/buildings-and-rooms.md"),
        new("Docs: work order search guide", "the work order search filters, sort order, paging and the table and board views kept in the URL", "docs/domain/work-order-search.md"),
        new("Docs: work order numbers", "the unique work order Number, where it is shown and how a work order is looked up by it", "docs/domain/work-order-numbers.md"),
        new("Docs: assigning and reassigning a work order", "who may be assigned a work order, how CanReassign decides, and the AssignedDate", "docs/domain/assignment.md"),
        new("Docs: completing and cancelling a work order", "how a work order reaches the terminal states Complete and Cancelled and what CompletedDate records", "docs/domain/completion-and-cancellation.md"),
        new("Docs: due dates and overdue work orders", "how DueDate drives the overdue filter and the overdue counts on the search summary cards", "docs/domain/due-dates-and-overdue.md"),
        new("Docs: dashboard recent activity", "how the dashboard picks the last work orders by their latest created, assigned or completed date", "docs/domain/dashboard-recent-activity.md"),
        new("Docs: API tools endpoints overview", "the anonymous /api/tools endpoints (hash, base64, guid-generator, random, slugify, timestamp-converter), their methods and the versioned prefix", "docs/api/tools-endpoints.md"),
        new("Docs: feature flags endpoint", "the read-only /api/features/flags endpoint, its response shape and the flags catalog", "docs/api/feature-flags.md"),
        new("Docs: health and diagnostics endpoints", "the /_healthcheck, /_build and /_telemetry endpoints: what each answers and who may call it", "docs/api/health-endpoints.md"),
        new("Docs: work order status counts endpoint", "the /api/work-orders/status-counts endpoint and the query behind it", "docs/api/status-counts.md"),
        new("Docs: CQRS request flow walkthrough", "how a request flows from the Blazor UI through the API controller, IBus, MediatR and a handler to the database", "docs/conventions/cqrs-request-flow.md"),
        new("Docs: onion architecture dependency rules", "which project may reference which: Core, DataAccess, the UI layer and Database", "docs/conventions/onion-architecture-rules.md"),
        new("Docs: testing conventions cheat sheet", "the NUnit and Shouldly conventions: Stub prefix, test naming and the AAA layout without section comments", "docs/conventions/testing-cheat-sheet.md"),
        new("Docs: database migration how-to", "how to add a numbered DbUp script under src/Database/scripts/Update, with tabs for indentation", "docs/conventions/database-migrations.md"),
        new("Docs: code style conventions", "naming, file-scoped namespaces, null! for injected fields and the other code style rules of the repository", "docs/conventions/code-style.md"),
        new("Docs: branch naming and commit flow", "the {username}/{branch-description} format and when to run the quality gates", "docs/conventions/branch-naming.md"),
        new("Docs: running the application locally", "how to build, run the Server project, reach the health check and run the unit and integration tests", "docs/conventions/running-locally.md"),
        new("Docs: local telemetry with the Aspire dashboard", "how the AppHost sets the OTLP endpoint locally and where telemetry goes in Azure", "docs/conventions/local-telemetry.md"),
    ];

    static readonly string[] Terms =
    [
        "Work order", "Draft", "Assigned", "InProgress", "Complete", "Cancelled", "WaitingOnVendor", "Creator", "Assignee", "Priority", "Due date", "Overdue", "Building", "Room number", "Employee", "Role", "Query", "Command", "Handler", "IBus",
        "MediatR", "DataContext", "Smart enum", "Onion architecture", "DbUp", "Lamar", "Blazor", "Playwright", "bUnit", "Shouldly", "Feature flag", "Health check", "Release candidate", "Technical debt",
    ];

    /// <summary><c>Get-ReplenishTopic</c>: <paramref name="used"/> holds the lower-cased titles of the repository's issues.</summary>
    public static Topic Next(ISet<string> used)
    {
        bool Free(string title) => !used.Contains(title.ToLowerInvariant());
        foreach (var t in Catalogue) if (Free(t.Title)) return t;
        foreach (var term in Terms)
        {
            var title = $"Docs: glossary entry for {term}";
            if (!Free(title)) continue;
            var slug = System.Text.RegularExpressions.Regex.Replace(term.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return new Topic(title, $"the term '{term}' as it is used in this application, in two or three sentences with a pointer to the code or page where it appears", $"docs/glossary/{slug}.md");
        }
        for (var k = 1; k < 100000; k++)
        {
            var title = $"Docs: note {k}";
            if (Free(title)) return new Topic(title, "one short, accurate fact about how this application works, taken from the existing code or documentation", $"docs/notes/note-{k}.md");
        }
        throw new InvalidOperationException("no free replenishment topic");
    }
}
