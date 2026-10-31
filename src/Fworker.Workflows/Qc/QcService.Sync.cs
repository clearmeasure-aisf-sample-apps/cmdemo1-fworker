using System.Text.Json.Nodes;

namespace Fworker.Workflows.Qc;

// Invoke-QcSync and Verb_qc_sync of Qc.ps1: the sweep's QC step. It offers and tears down; it never creates a record, never assigns or offers a job and never rebuilds on a new head (design 3.5).
// Teardowns it asks for: merged / closed / left-validation / label-changed, and from the persisted record: head-changed after timing.qcGraceMinutes in stale, lifetime after timing.qcLifetimeMinutes in ready.
// Idle and a lost lease need the gateway's memory and are decided there. The answer is what QcHub.Reconcile reads: the records, the teardowns and the approvals.

public sealed partial class QcService
{
    sealed class SyncResult
    {
        public readonly JsonArray Actions = new(), Errors = new(), Records = new(), Teardowns = new(), Approvals = new();
    }

    async Task<JsonObject> VerbSync(JsonObject a, CancellationToken ct)
    {
        if (!cfg.QcEnabled) throw new QcReject(30, "qc-disabled", "QC environments need the gateway in owner mode with FACTORY_TOKEN_SECRET");
        var dry = QcJson.Has(a, "dry") && QcJson.Bool(a, "dry");
        var r = await Sync(await items.Board(ct), dry, ct);
        return new JsonObject { ["ok"] = true, ["actions"] = r.Actions, ["errors"] = r.Errors, ["records"] = r.Records, ["teardowns"] = r.Teardowns, ["approvals"] = r.Approvals };
    }

    // the items with a live record: the local index of the state directory (v1's file), and the rows of the table that are live
    async Task<HashSet<int>> IndexedItems(CancellationToken ct)
    {
        var set = new HashSet<int>(index.Items());
        if (table is null) return set;
        try
        {
            var prefix = $"{cfg.Owner}/{cfg.Repo}#".ToLowerInvariant();
            foreach (var e in await table.Live($"{cfg.Owner}/{cfg.Repo}".ToLowerInvariant(), ct))
                if (QcRecords.LiveStates.Contains(e.Status) && e.ItemKey is { } k && k.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(k[prefix.Length..], out var number)) set.Add(number);
        }
        catch (Exception e) when (e is not OperationCanceledException) { log($"qc: the table of environments could not be read for the sync: {e.Message}"); }
        return set;
    }

    async Task<SyncResult> Sync(IReadOnlyList<QcBoardItem> list, bool dry, CancellationToken ct)
    {
        var res = new SyncResult();
        cfg.Validate();
        var open = await github.OpenPulls(ct);
        var indexed = await IndexedItems(ct);
        var now = utcNow();
        var gateApproval = cfg.GateActive && cfg.Gate.RequireApproval;
        foreach (var it in list)
        {
            if (!it.Open || !QcJson.NameIn(cfg.Label, it.Labels) || QcJson.NameIn(cfg.RivalLabel, it.Labels)) continue;
            var inVal = string.Equals(it.Column ?? "", cfg.ValidationColumn, StringComparison.OrdinalIgnoreCase);
            if (!inVal && !indexed.Contains(it.Number)) continue;
            try { await SyncItemRetrying(it, inVal, open, res, dry, now, gateApproval, ct); }
            catch (QcReject e)
            {
                if (e.Code is "github-not-found" or "item-not-found") res.Actions.Add(new JsonObject { ["item"] = it.Number, ["action"] = "qc-skip", ["reason"] = "vanished-during-sweep" });
                else res.Errors.Add(new JsonObject { ["item"] = it.Number, ["code"] = e.Code, ["reason"] = $"qc: {e.Message}" });
            }
            catch (Exception e) when (e is not OperationCanceledException) { res.Errors.Add(new JsonObject { ["item"] = it.Number, ["code"] = "error", ["reason"] = $"qc: {e.Message}" }); }
        }
        return res;
    }

    // A state comment that moved under the sync (a verb, the sweep's other steps or a webhook wrote the item between this sync's read and its write) is read again and the item synced again, up to three
    // times, as WithRetry does for a verb and Update-State of the rule scripts does: the conflict is the normal outcome of two writers, not an error of the sweep (it was logged as one, "qc: the state of
    // item N changed since it was read", and failed the invariant of the suites that no sweep error is logged). Each attempt collects into a result of its own, so that an attempt that fails halfway
    // leaves nothing behind in the sweep's answer; the third conflict is raised as before.
    async Task SyncItemRetrying(QcBoardItem it, bool inVal, IReadOnlyList<JsonObject> open, SyncResult res, bool dry, DateTime now, bool gateApproval, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var mine = new SyncResult();
            try { await SyncItem(it, inVal, open, mine, dry, now, gateApproval, ct); }
            catch (QcStateConflictException) when (attempt < 3) { continue; }
            foreach (var x in mine.Actions) res.Actions.Add(x!.DeepClone());
            foreach (var x in mine.Records) res.Records.Add(x!.DeepClone());
            foreach (var x in mine.Teardowns) res.Teardowns.Add(x!.DeepClone());
            foreach (var x in mine.Approvals) res.Approvals.Add(x!.DeepClone());
            foreach (var x in mine.Errors) res.Errors.Add(x!.DeepClone());
            return;
        }
    }

    static void Teardown(SyncResult res, int item, JsonObject rec, string reason) =>
        res.Teardowns.Add(new JsonObject { ["item"] = item, ["pr"] = QcJson.Int(rec, "pr"), ["reason"] = reason });

    /// <summary>When the record went stale (the last history entry of that state).</summary>
    static DateTime? StaleSince(JsonObject rec)
    {
        DateTime? at = null;
        if (QcJson.Has(rec, "history") && rec["history"] is JsonArray h)
            foreach (var x in h) if (x is JsonObject o && QcJson.Str(o, "state") == "stale") at = QcJson.FromIso(QcJson.Str(o, "at"));
        return at;
    }

    async Task SyncItem(QcBoardItem it, bool inVal, IReadOnlyList<JsonObject> open, SyncResult res, bool dry, DateTime now, bool gateApproval, CancellationToken ct)
    {
        var n = it.Number;
        var st = await items.ReadState(n, ct); var s = st.State;
        var verdictPr = QcJson.Has(s, "verdict") && QcJson.Has(QcJson.Obj(s, "verdict"), "pr") ? QcJson.Int(QcJson.Obj(s, "verdict"), "pr") : 0;
        var live = LiveRecords(s);
        var pr = open.FirstOrDefault(p => QcJson.Int(p, "number") == verdictPr);
        if (!inVal)
        {
            foreach (var rec in live) { Teardown(res, n, rec, "left-validation"); res.Records.Add(rec.DeepClone()); }
            return;
        }
        if (pr is null)
        {
            var why = QcJson.Has(s, "merge") ? "merged" : "closed";
            foreach (var rec in live) { Teardown(res, n, rec, why); res.Records.Add(rec.DeepClone()); }
            return;
        }
        var head = HeadSha(pr);
        if (live.Count > 0 && !dry)
        {
            // a head that moved marks the live record stale. The write runs under the item lock on a fresh read: a verb that finished meanwhile (qc-stopped, a reset) is not overwritten with what this sweep read earlier.
            // Without waiting for the lock: a busy one skips the item for this sweep (a qc-skip), the next sweep finds the same moved head and marks the record on a fresh read, and the grace of the
            // head-changed teardown counts from that mark. The gateway acts only on the records a sweep reports, so one sweep without them changes nothing.
            var toStale = live.Where(r => QcJson.Str(r, "sha") != head && QcJson.Str(r, "state") != "stale" && QcRecords.CanMove(QcJson.Str(r, "state"), "stale")).Select(r => QcJson.Int(r, "pr")).ToList();
            if (toStale.Count > 0)
            {
                var busy = false;
                try
                {
                    await locks.Run(n, TimeSpan.Zero, async () =>
                    {
                        var st2 = await items.ReadState(n, ct);
                        foreach (var prNumber in toStale)
                        {
                            var cur = QcRecords.Of(st2.State, prNumber);
                            if (cur is not null && QcJson.Str(cur, "sha") != head && QcJson.Str(cur, "state") != "stale" && QcRecords.CanMove(QcJson.Str(cur, "state"), "stale"))
                            {
                                QcRecords.SetState(cur, "stale", utcNow()); await SaveRecord(n, st2, cur, ct);
                                res.Actions.Add(new JsonObject { ["item"] = n, ["action"] = "qc-stale", ["pr"] = prNumber });
                            }
                        }
                        return 0;
                    }, ct);
                }
                catch (QcReject e) when (e.Code == "item-busy") { busy = true; }
                if (busy) { res.Actions.Add(new JsonObject { ["item"] = n, ["action"] = "qc-skip", ["reason"] = "qc-stale: item-busy" }); return; }
                st = await items.ReadState(n, ct); s = st.State; live = LiveRecords(s);
            }
        }
        var graceMin = cfg.TimingOr0("qcGraceMinutes"); var lifeMin = cfg.LifetimeMinutes;
        foreach (var rec in live)
        {
            res.Records.Add(rec.DeepClone());
            if (dry) continue;
            if (QcJson.Str(rec, "state") == "stale")
            {
                var since = StaleSince(rec);
                if (since is { } sa && (now - sa).TotalMinutes >= graceMin) Teardown(res, n, rec, "head-changed");
            }
            else if (QcJson.Str(rec, "state") == "ready" && QcJson.Has(rec, "ready_at"))
            {
                var readyAt = QcJson.FromIso(QcJson.Str(rec, "ready_at"));
                if (readyAt is { } ra && (now - ra).TotalMinutes >= lifeMin) Teardown(res, n, rec, "lifetime");
            }
        }
        // a person's approval of the current head wakes the owner (qc.approved) so it retries merge; the persisted copy follows GitHub
        var verdict = QcJson.Obj(s, "verdict");
        if (gateApproval && !dry && verdict is not null && !QcJson.Bool(verdict, "processed") && !QcJson.NameIn(cfg.AutoMergeLabel, it.Labels))
        {
            var rec0 = QcRecords.Of(s, QcJson.Int(pr, "number"));
            var inspected = QcJson.Has(s, "approval") && QcJson.Str(QcJson.Obj(s, "approval"), "sha") == head;
            if (rec0 is not null || inspected)
            {
                var by = await HumanApproval(pr, ct);
                if (rec0 is not null && (QcRecords.IsLive(rec0) || QcJson.Has(rec0, "approved")))
                {
                    var approved = QcJson.Obj(rec0, "approved");
                    var differs = !string.IsNullOrEmpty(by) ? approved is null || QcJson.Str(approved, "sha") != head || QcJson.Str(approved, "by") != by : QcJson.Has(rec0, "approved");
                    // Without waiting for the lock. A busy one skips the item for this sweep (a qc-skip), the owner's wake-up included: the merge gate reads the approval from GitHub, not this copy,
                    // and the next sweep compares again on a fresh read, saves the copy and reports the approval (the gateway wakes the owner once per approval).
                    if (differs)
                    {
                        var busy = false;
                        try
                        {
                            await locks.Run(n, TimeSpan.Zero, async () =>
                            {
                                var st3 = await items.ReadState(n, ct);
                                await UpdateApproved(n, st3, QcRecords.Of(st3.State, QcJson.Int(pr, "number")), head, by, ct);
                                return 0;
                            }, ct);
                        }
                        catch (QcReject e) when (e.Code == "item-busy") { busy = true; }
                        if (busy) { res.Actions.Add(new JsonObject { ["item"] = n, ["action"] = "qc-skip", ["reason"] = "qc-approved: item-busy" }); return; }
                    }
                }
                var ran = rec0 is not null && QcJson.Has(rec0, "ran_sha") && QcJson.Str(rec0, "ran_sha") == head;
                if (!string.IsNullOrEmpty(by)) res.Approvals.Add(new JsonObject { ["item"] = n, ["pr"] = QcJson.Int(pr, "number"), ["sha"] = head, ["by"] = by, ["ran"] = ran });
            }
        }
        if (!dry && QcJson.Bool(pr, "draft") && QcJson.Has(s, "copilot_task") && verdict is not null && !QcJson.Bool(verdict, "processed")) pr = await github.MarkReady(pr, ct);
        var prNum = QcJson.Int(pr, "number");
        var el = QcRules.Eligibility(cfg, n, it.Labels, it.Column, s, pr, null);
        if (el.Ok) el = QcRules.Eligibility(cfg, n, it.Labels, it.Column, s, pr, await github.PullFiles(prNum, ct));
        if (el.Ok)
        {
            // only the journal line of the offer is written, on a fresh read (the ledger), never the state as this sweep read it
            if (!dry)
            {
                var o = await UpdateOffer(n, st, pr, false, ct);
                if (o != "unchanged") res.Actions.Add(new JsonObject { ["item"] = n, ["action"] = "qc-offer", ["pr"] = prNum, ["outcome"] = o });
            }
            return;
        }
        if (dry) return;
        var note = el.Reasons.Where(r => r is not ("auto-merge" or "docs-only" or "no-pass-verdict" or "not-in-validation")).ToList();
        if (note.Count > 0)
            await JournalLine(n, "skipped", new JsonObject { ["reason"] = $"QC is not offered for PR #{prNum}: {string.Join(", ", note)}" }, $"qc|{prNum}|{string.Join(",", note)}", ct);
        var hadOffer = QcRules.FindOffer(st.Comments, prNum, await github.ServerLogin(ct)) is not null;
        if (hadOffer)
        {
            await RemoveOffer(n, st, prNum, string.Join(", ", el.Reasons), ct);
            res.Actions.Add(new JsonObject { ["item"] = n, ["action"] = "qc-withdrawn", ["pr"] = prNum });
        }
        // an Auto Merge label added later ends a running environment
        if (el.Reasons.Contains("auto-merge")) foreach (var rec in live) Teardown(res, n, rec, "label-changed");
    }
}
