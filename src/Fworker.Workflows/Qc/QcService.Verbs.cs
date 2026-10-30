using System.Text.Json.Nodes;

namespace Fworker.Workflows.Qc;

// The verbs of Qc.ps1, one method each, in the order the script reads and checks: a call that fails several ways fails the way the script fails first.

public sealed partial class QcService
{
    // ---- links and pages ------------------------------------------------------------------------------------------------------------------------------

    async Task<JsonObject> VerbLink(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr");
        cfg.Validate();
        if (cfg.TokenSecret.Length < 16) throw new QcReject(30, "no-token-secret", "FACTORY_TOKEN_SECRET is not set on the gateway");
        QcRules.BaseUrl(cfg);
        var info = await items.GetInfo(n, ct); var st = await items.ReadState(n, ct);
        var pr = await GetPull(prNum, ct);
        var el = await EligibilityWithFiles(n, info, st.State, pr, ct);
        if (!el.Ok) throw NotEligible(prNum, el);
        var o = await UpdateOffer(n, st, pr, false, ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = o };
    }

    async Task<JsonObject> VerbView(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr");
        var pr = await GetPull(prNum, ct); var head = HeadSha(pr);
        var info = await items.GetInfo(n, ct); var st = await items.ReadState(n, ct); var s = st.State;
        var view = new JsonObject
        {
            ["pr"] = prNum, ["item"] = n, ["title"] = QcJson.Str(pr, "title"), ["author"] = Login(pr), ["head_sha"] = head,
            ["pr_state"] = QcJson.Bool(pr, "merged") ? "merged" : QcJson.Str(pr, "state"),
        };
        if (QcJson.Has(a, "sha") && QcJson.Str(a, "sha") != head)
        {
            view["current_sha"] = head; view["current_token"] = QcRules.NewToken(cfg, prNum, head, n, new DateTimeOffset(utcNow(), TimeSpan.Zero), nonce).Token;
            throw new QcReject(11, "stale-head", "this link is for an older commit of the pull request", view);
        }
        var rec = await RecordOf(st, prNum, ct);
        var el = await EligibilityWithFiles(n, info, s, pr, ct);
        view["eligible"] = el.Ok; view["reasons"] = QcJson.ArrayOf(el.Reasons);
        view["needs_confirmation"] = new JsonArray();
        if (el.Ok && !QcRecords.IsLive(rec)) view["needs_confirmation"] = QcJson.ArrayOf(await Trust(n, pr, ct));
        if (rec is not null) view["record"] = rec.DeepClone();
        view["ok"] = true;
        return view;
    }

    // ---- Start, Reset, Teardown -----------------------------------------------------------------------------------------------------------------------

    async Task<JsonObject> VerbRequest(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr");
        var isOp = QcJson.Has(a, "operator") && QcJson.Bool(a, "operator");
        var confirm = isOp && QcJson.Has(a, "confirm") && QcJson.Bool(a, "confirm");
        var info = await items.GetInfo(n, ct); var st = await items.ReadState(n, ct); var s = st.State;
        var pr = await GetPull(prNum, ct); var head = HeadSha(pr);
        if (QcJson.Has(a, "sha") && QcJson.Str(a, "sha") != head) throw new QcReject(11, "stale-head", "this link is for an older commit; nothing was queued", new JsonObject { ["current_sha"] = head });
        var el = await EligibilityWithFiles(n, info, s, pr, ct);
        if (!el.Ok) throw NotEligible(prNum, el);
        var existing = await RecordOf(st, prNum, ct);
        if (QcRecords.IsLive(existing) && QcJson.Str(existing, "sha") == head)
            return WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "existing" }, existing!);
        var needs = await Trust(n, pr, ct);
        if (needs.Count > 0 && !confirm) throw new QcReject(10, "trust-confirm-needed", $"this run needs the operator's confirmation: {string.Join(", ", needs)}", new JsonObject { ["reasons"] = QcJson.ArrayOf(needs) });
        // the record the new one replaces, as it was (it is detached from the state when the new record is saved)
        var previous = QcRecords.IsLive(existing) ? (JsonObject)existing!.DeepClone() : null;
        var attempt = existing is not null && QcJson.Str(existing, "sha") == head ? QcJson.Int(existing, "attempt") + 1 : 1;
        var rec = QcRecords.New(n, pr, attempt, "requested", utcNow(), cfg.SecretPatterns);
        rec["link_nonce"] = Text(QcJson.Has(a, "link_nonce") ? QcJson.Str(a, "link_nonce") : "", 40);
        if (needs.Count > 0) rec["trust_confirmed"] = true;
        if (existing is not null && QcJson.Has(existing, "ran_sha")) rec["ran_sha"] = QcJson.Str(existing, "ran_sha");
        await SaveRecord(n, st, rec, ct);
        await JournalLine(n, "qc-requested", new JsonObject { ["pr"] = prNum, ["sha7"] = Sha7(head), ["attempt"] = attempt }, $"{prNum}|{head}|{attempt}", ct);
        var o = WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "started" }, rec);
        if (previous is not null) o["previous"] = previous;
        return o;
    }

    async Task<JsonObject> VerbReset(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr");
        var info = await items.GetInfo(n, ct); var st = await items.ReadState(n, ct); var s = st.State;
        var rec = await RecordOf(st, prNum, ct);
        if (rec is null || QcJson.Str(rec, "state") == "torn-down") throw new QcReject(12, "no-environment", $"no QC environment exists for PR #{prNum}; press Start");
        var pr = await GetPull(prNum, ct); var head = HeadSha(pr);
        if (QcJson.Has(a, "sha") && QcJson.Str(a, "sha") != head) throw new QcReject(11, "stale-head", "the reset was asked for an older commit", new JsonObject { ["current_sha"] = head });
        var el = await EligibilityWithFiles(n, info, s, pr, ct);
        if (!el.Ok) throw NotEligible(prNum, el);
        var previous = new JsonObject
        {
            ["worker"] = QcJson.Str(rec, "worker"), ["assignment"] = QcJson.Str(rec, "assignment"), ["env_id"] = QcJson.Str(rec, "env_id"), ["sha"] = QcJson.Str(rec, "sha"), ["accepted"] = QcJson.Has(rec, "accepted_at"),
        };
        var attempt = QcJson.Str(rec, "sha") == head ? QcJson.Int(rec, "attempt") + 1 : 1;
        var fresh = QcRecords.New(n, pr, attempt, "reset", utcNow(), cfg.SecretPatterns);
        var oldAssignments = new List<string>();
        if (rec["old_assignments"] is JsonArray old) foreach (var x in old) oldAssignments.Add(QcJson.Text(x));
        if (QcJson.Str(previous, "assignment").Length > 0) oldAssignments.Add(QcJson.Str(previous, "assignment"));
        fresh["old_assignments"] = QcJson.ArrayOf(oldAssignments.TakeLast(5));
        if (QcJson.Has(rec, "trust_confirmed")) fresh["trust_confirmed"] = rec["trust_confirmed"]!.DeepClone();
        if (QcJson.Has(rec, "ran_sha")) fresh["ran_sha"] = QcJson.Str(rec, "ran_sha");
        await SaveRecord(n, st, fresh, ct);
        await JournalLine(n, "qc-reset", new JsonObject { ["pr"] = prNum, ["attempt"] = attempt }, $"{prNum}|{head}|{attempt}", ct);
        var o = WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "reset" }, fresh);
        o["previous"] = previous;
        return o;
    }

    async Task<JsonObject> VerbTeardown(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr");
        var reason = QcJson.Has(a, "reason") ? QcJson.Str(a, "reason") : "person";
        if (!QcRecords.TeardownReasons.Contains(reason)) reason = "person";
        // an item that vanished has nothing left to tear down; the gateway still drops whatever it holds in memory
        QcItemState? st;
        try { st = await items.ReadState(n, ct); }
        catch (QcReject e) when (e.Code == "github-not-found") { st = null; }
        var rec = st is null ? null : await RecordOf(st, prNum, ct);
        if (rec is null || QcJson.Str(rec, "state") == "torn-down") return new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "already" };
        var needsStop = QcJson.Has(rec, "accepted_at");
        var env = new JsonObject { ["worker"] = QcJson.Str(rec, "worker"), ["assignment"] = QcJson.Str(rec, "assignment"), ["env_id"] = QcJson.Str(rec, "env_id"), ["sha"] = QcJson.Str(rec, "sha") };
        if (!needsStop)
        {
            QcRecords.SetState(rec, "torn-down", utcNow()); rec["teardown"] = new JsonObject { ["reason"] = reason, ["at"] = QcJson.Iso(utcNow()) };
            await SaveRecord(n, st!, rec, ct);
            await JournalLine(n, "qc-torn-down", new JsonObject { ["pr"] = prNum, ["reason"] = reason }, $"{prNum}|{QcJson.Str(rec, "sha")}|{QcJson.Str(rec, "attempt")}", ct);
            return Tear(WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "torn-down", ["needs_stop"] = false }, rec), env);
        }
        // asked before and still waiting for the worker's qc-stopped: the first reason stands and nothing is written again
        if (!QcJson.Has(rec, "teardown"))
        {
            rec["teardown"] = new JsonObject { ["reason"] = reason, ["at"] = QcJson.Iso(utcNow()) };
            rec["rev"] = 1 + QcJson.Int(rec, "rev");
            await SaveRecord(n, st!, rec, ct);
        }
        return Tear(WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "stopping", ["needs_stop"] = true }, rec), env);
    }

    static JsonObject Tear(JsonObject answer, JsonObject env) { answer["env"] = env; return answer; }

    // ---- internal: the gateway moves a record ---------------------------------------------------------------------------------------------------------

    async Task<JsonObject> VerbTransition(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr"); var to = Need(a, "to");
        var st = await items.ReadState(n, ct);
        var rec = await RecordOf(st, prNum, ct);
        if (rec is null) throw new QcReject(12, "no-environment", $"no QC record for PR #{prNum}");
        if (QcJson.Has(a, "sha") && QcJson.Str(a, "sha") != QcJson.Str(rec, "sha")) throw new QcReject(11, "stale-head", "the record is for another commit");
        if (QcJson.Has(a, "assignment_was") && QcJson.Str(a, "assignment_was") != QcJson.Str(rec, "assignment")) throw new QcReject(11, "stale-assignment", "the record has a newer assignment");
        var state = QcJson.Str(rec, "state");
        var now = utcNow();
        if (to == "assigned" && QcJson.Has(a, "relaunch") && QcJson.Bool(a, "relaunch"))
        {
            // The worker holding the environment was lost (its lease ran out): back to assigned for another worker, up to timing.qcMaxRelaunches times.
            if (state is not ("assigned" or "building" or "running" or "ready")) throw new QcReject(10, "bad-transition", $"cannot relaunch from {state}");
            var max = QcJson.Has(a, "max_relaunches") ? QcJson.Int(a, "max_relaunches") : (int)cfg.TimingOr0("qcMaxRelaunches");
            var done = QcJson.Has(rec, "relaunches") ? QcJson.Int(rec, "relaunches") : 0;
            if (done >= max)
            {
                rec["fail"] = new JsonObject { ["code"] = "no-worker", ["reason"] = Text($"the worker was lost {done} times; the environment was not started again", 300) };
                QcRecords.SetState(rec, "failed", now);
                await SaveRecord(n, st, rec, ct);
                await JournalLine(n, "qc-failed", new JsonObject { ["pr"] = prNum, ["code"] = "no-worker", ["reason"] = JournalText(QcJson.Str(QcJson.Obj(rec, "fail"), "reason"), 300) }, $"{prNum}|{QcJson.Str(rec, "sha")}|{QcJson.Str(rec, "attempt")}", ct);
                return WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "failed" }, rec);
            }
            if (QcJson.Has(rec, "assignment") && QcJson.Str(rec, "assignment").Length > 0) rec["old_assignments"] = QcRecords.WithOld(rec, QcJson.Str(rec, "assignment"));
            rec["assignment"] = ""; rec["worker"] = "";
            foreach (var k in new[] { "accepted_at", "ready_at", "expires_at", "port", "probe", "fail" }) rec.Remove(k);
            rec["relaunches"] = done + 1; rec["mode"] = "relaunch";
            QcRecords.SetState(rec, "assigned", now);
            await SaveRecord(n, st, rec, ct);
            return WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "relaunch" }, rec);
        }
        if (to == "assigned")
        {
            if (state is not ("requested" or "reset" or "assigned")) throw new QcReject(10, "bad-transition", $"cannot assign from {state}");
            var newId = Text(Need(a, "assignment"), 40);
            if (QcJson.Has(rec, "assignment") && QcJson.Str(rec, "assignment").Length > 0 && QcJson.Str(rec, "assignment") != newId) rec["old_assignments"] = QcRecords.WithOld(rec, QcJson.Str(rec, "assignment"));
            rec["assignment"] = newId; rec["worker"] = Text(Need(a, "worker"), 40);
            if (QcJson.Has(a, "mode")) rec["mode"] = Text(QcJson.Str(a, "mode"), 20);
            if (state != "assigned") QcRecords.SetState(rec, "assigned", now); else QcRecords.BumpRev(rec);
            await SaveRecord(n, st, rec, ct);
            await UpdateAssignedComments(n, st, rec, ct);
            return WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum }, rec);
        }
        if (!QcRecords.CanMove(state, to)) throw new QcReject(10, "bad-transition", $"a QC environment cannot go from {state} to {to}");
        if (to == "failed")
        {
            var code = QcJson.Has(a, "code") && QcRecords.FailCodes.Contains(QcJson.Str(a, "code")) ? QcJson.Str(a, "code") : "start-failed";
            rec["fail"] = new JsonObject { ["code"] = code, ["reason"] = Text(QcJson.Has(a, "reason") ? QcJson.Str(a, "reason") : "", 300) };
            QcRecords.SetState(rec, "failed", now);
            await SaveRecord(n, st, rec, ct);
            await JournalLine(n, "qc-failed", new JsonObject { ["pr"] = prNum, ["code"] = code, ["reason"] = JournalText(QcJson.Str(QcJson.Obj(rec, "fail"), "reason"), 300) }, $"{prNum}|{QcJson.Str(rec, "sha")}|{QcJson.Str(rec, "attempt")}", ct);
        }
        else if (to == "torn-down")
        {
            var reason = QcJson.Has(a, "reason") && QcRecords.TeardownReasons.Contains(QcJson.Str(a, "reason")) ? QcJson.Str(a, "reason")
                : QcJson.Has(rec, "teardown") ? QcJson.Str(QcJson.Obj(rec, "teardown"), "reason") : "person";
            rec["teardown"] = new JsonObject { ["reason"] = reason, ["at"] = QcJson.Iso(now) };
            QcRecords.SetState(rec, "torn-down", now);
            await SaveRecord(n, st, rec, ct);
            await JournalLine(n, "qc-torn-down", new JsonObject { ["pr"] = prNum, ["reason"] = reason }, $"{prNum}|{QcJson.Str(rec, "sha")}|{QcJson.Str(rec, "attempt")}", ct);
        }
        else
        {
            QcRecords.SetState(rec, to, now);
            await SaveRecord(n, st, rec, ct);
        }
        return WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum }, rec);
    }

    // ---- the dashboard's list -------------------------------------------------------------------------------------------------------------------------

    async Task<JsonObject> VerbList(JsonObject a, CancellationToken ct)
    {
        var records = new List<JsonObject>(); var available = new JsonArray();
        var open = await github.OpenPulls(ct);
        foreach (var it in await items.Board(ct))
        {
            if (!it.Open || !QcJson.NameIn(cfg.Label, it.Labels) || !string.Equals(it.Column ?? "", cfg.ValidationColumn, StringComparison.OrdinalIgnoreCase)) continue;
            var st = await items.ReadState(it.Number, ct); var s = st.State;
            if (QcJson.Has(s, "qc") && s["qc"] is JsonObject map)
                foreach (var kv in map)
                {
                    if (kv.Value is not JsonObject r0) continue;
                    // the dashboard shows the title and flags a record whose head is no longer the PR's head (a copy: nothing here is saved)
                    var copy = (JsonObject)r0.DeepClone();
                    var p0 = open.FirstOrDefault(p => QcJson.Int(p, "number") == QcJson.Int(r0, "pr"));
                    copy["title"] = p0 is not null ? QcJson.Str(p0, "title") : ""; copy["head_sha"] = p0 is not null ? HeadSha(p0) : ""; copy["pr_open"] = p0 is not null;
                    records.Add(copy);
                }
            var verdict = QcJson.Obj(s, "verdict");
            if (verdict is null || !QcJson.Bool(verdict, "passed") || QcJson.Has(s, "merge")) continue;
            var pr = open.FirstOrDefault(p => QcJson.Int(p, "number") == QcJson.Int(verdict, "pr"));
            if (pr is null) continue;
            var el = QcRules.Eligibility(cfg, it.Number, it.Labels, it.Column, s, pr, await github.PullFiles(QcJson.Int(pr, "number"), ct));
            var rec = QcRecords.Of(s, QcJson.Int(pr, "number"));
            if (el.Ok && !QcRecords.IsLive(rec))
                available.Add(new JsonObject
                {
                    ["item"] = it.Number, ["pr"] = QcJson.Int(pr, "number"), ["sha"] = HeadSha(pr), ["title"] = QcJson.Str(pr, "title"), ["author"] = Login(pr), ["created_at"] = QcJson.Str(pr, "created_at"),
                });
        }
        var all = QcJson.Has(a, "include") && QcJson.Str(a, "include") == "all";
        var shown = all ? records : records.Where(QcRecords.IsLive).ToList();
        var arr = new JsonArray(); foreach (var r in shown) arr.Add(r);
        return new JsonObject { ["ok"] = true, ["available"] = available, ["records"] = arr };
    }

    // ---- the worker's side ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Invoke-QcAccept: the QC worker accepts its job (the <c>accept</c> verb with kind qc-env; its owner is the item verbs, which call this). The first accept stamps the record and journals qc-assigned.
    /// A job whose assignment the record no longer holds is stale (11).
    /// </summary>
    public async Task<JsonObject> Accept(int item, string assignment, string holder, CancellationToken ct = default) =>
        await locks.Run(item, TimeSpan.FromSeconds(45), async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await AcceptOnce(item, assignment, holder, ct); }
                catch (QcStateConflictException) when (attempt < 3) { }
                catch (QcStateConflictException e) { throw new QcReject(11, "state-conflict", e.Message); }
            }
        }, ct);

    async Task<JsonObject> AcceptOnce(int n, string assignment, string holder, CancellationToken ct)
    {
        var st = await items.ReadState(n, ct); var s = st.State;
        JsonObject? rec = null;
        if (QcJson.Obj(s, "qc") is { } map) foreach (var kv in map) if (kv.Value is JsonObject x && QcJson.Has(x, "assignment") && string.Equals(QcJson.Str(x, "assignment"), assignment, StringComparison.OrdinalIgnoreCase)) { rec = x; break; }
        if (rec is null || QcJson.Str(rec, "state") is not ("assigned" or "building" or "running" or "ready")) throw new QcReject(11, "stale-assignment", "this QC assignment is no longer current");
        if (!QcJson.Has(rec, "accepted_at"))
        {
            rec["accepted_at"] = QcJson.Iso(utcNow()); rec["worker"] = Text(holder, 40);
            QcRecords.BumpRev(rec);
            await SaveRecord(n, st, rec, ct);
            await JournalLine(n, "qc-assigned", new JsonObject { ["worker"] = QcJson.Str(rec, "worker"), ["pr"] = QcJson.Int(rec, "pr"), ["attempt"] = QcJson.Int(rec, "attempt") }, assignment, ct);
        }
        var o = new JsonObject
        {
            ["ok"] = true, ["id"] = n, ["accepted"] = true, ["kind"] = "qc-env", ["role"] = "qc", ["record"] = rec.DeepClone(), ["pr"] = QcJson.Int(rec, "pr"), ["sha"] = QcJson.Str(rec, "sha"),
            ["env_id"] = QcJson.Str(rec, "env_id"), ["mode"] = QcJson.Has(rec, "mode") ? QcJson.Str(rec, "mode") : "first", ["assignment"] = assignment, ["state"] = QcJson.Str(rec, "state"),
        };
        return o;
    }

    async Task<JsonObject> VerbJob(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr");
        var assignment = Need(a, "assignment");
        _ = await items.GetInfo(n, ct);   // (the script reads the item for the trust check; a missing or foreign item is refused here as there)
        var st = await items.ReadState(n, ct); var rec = await RecordOf(st, prNum, ct);
        AssertAssignment(rec, assignment);
        var state = QcJson.Str(rec, "state");
        if (state is not ("assigned" or "building" or "running" or "ready")) throw new QcReject(10, "wrong-state", $"the environment is {state}");
        var pr = await GetPull(prNum, ct); var head = HeadSha(pr);
        var full = QcJson.Str(QcJson.Obj(QcJson.Obj(pr, "head"), "repo"), "full_name");
        if (!string.Equals(full, cfg.FullName, StringComparison.OrdinalIgnoreCase)) throw new QcReject(10, "fork-pr", "pull requests from forks are never run");
        if (head != QcJson.Str(rec, "sha")) throw new QcReject(11, "stale-head", $"the pull request moved to {Sha7(head)}; press Reset", new JsonObject { ["current_sha"] = head });
        var needs = await Trust(n, pr, ct);
        if (needs.Count > 0 && !QcJson.Has(rec, "trust_confirmed")) throw new QcReject(10, "qc-trust-refused", $"this run needs the operator's confirmation: {string.Join(", ", needs)}", new JsonObject { ["reasons"] = QcJson.ArrayOf(needs) });
        var life = cfg.LifetimeMinutes;
        return new JsonObject
        {
            ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["sha"] = head, ["branch"] = QcJson.Str(QcJson.Obj(pr, "head"), "ref"), ["env_id"] = QcJson.Str(rec, "env_id"), ["attempt"] = QcJson.Int(rec, "attempt"),
            ["trust"] = new JsonObject { ["ok"] = true, ["reasons"] = QcJson.ArrayOf(needs) },
            ["limits"] = new JsonObject { ["buildCapMinutes"] = cfg.TimingOr0("qcBuildCapMinutes"), ["lifetimeMinutes"] = life, ["lifetimeHours"] = life / 60 },
            ["title"] = new JsonObject { ["text"] = QcJson.Str(pr, "title"), ["untrusted"] = true },
        };
    }

    async Task<JsonObject> VerbProgress(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr"); var to = Need(a, "state");
        if (to is not ("building" or "running")) throw new QcReject(2, "usage", "state must be building or running");
        var st = await items.ReadState(n, ct); var rec = await RecordOf(st, prNum, ct);
        AssertAssignment(rec, Need(a, "assignment"));
        if (QcJson.Str(rec, "state") != to)
        {
            if (!QcRecords.CanMove(QcJson.Str(rec, "state"), to)) throw new QcReject(10, "bad-transition", $"a QC environment cannot go from {QcJson.Str(rec, "state")} to {to}");
            QcRecords.SetState(rec!, to, utcNow());
            await SaveRecord(n, st, rec!, ct);
        }
        var log = QcJson.Has(a, "log_tail") ? Text(QcJson.Str(a, "log_tail"), 1500) : "";
        return new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["state"] = QcJson.Str(rec, "state"), ["record"] = rec!.DeepClone(), ["log_tail"] = log };
    }

    // qc-report: the gateway has already checked the URL and probed it; this records the persisted part (never the URL).
    async Task<JsonObject> VerbReport(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr"); var sha = Need(a, "sha");
        var st = await items.ReadState(n, ct); var rec = await RecordOf(st, prNum, ct);
        AssertAssignment(rec, Need(a, "assignment"));
        if (QcJson.Str(rec, "sha") != sha) throw new QcReject(11, "stale-head", "the report is for another commit than the record", new JsonObject { ["current_sha"] = QcJson.Str(rec, "sha") });
        if (QcJson.Str(a, "env_id") != QcJson.Str(rec, "env_id")) throw new QcReject(11, "stale-assignment", "the report is for another environment than the record");
        var probe = QcJson.Has(a, "probe") && QcJson.Str(a, "probe") is "verified" or "unverified" ? QcJson.Str(a, "probe") : "unverified";
        var firstTime = QcJson.Str(rec, "state") != "ready";
        if (firstTime)
        {
            if (!QcRecords.CanMove(QcJson.Str(rec, "state"), "ready")) throw new QcReject(10, "bad-transition", $"a QC environment cannot go from {QcJson.Str(rec, "state")} to ready");
            var now = utcNow();
            QcRecords.SetState(rec!, "ready", now);
            rec!["ready_at"] = QcJson.Iso(now); rec["expires_at"] = QcJson.Iso(now.AddMinutes(cfg.LifetimeMinutes));
            if (QcJson.Has(a, "port") && int.TryParse(QcJson.Str(a, "port"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port)) rec["port"] = port;
            rec["probe"] = probe;
            // The record of a run for the merge gate: the head this environment reached ready for. Written here and nowhere else; a new record for the same PR carries it over.
            rec["ran_sha"] = sha;
            await SaveRecord(n, st, rec, ct);
            await JournalLine(n, "qc-ready", new JsonObject { ["pr"] = prNum, ["worker"] = QcJson.Str(rec, "worker"), ["attempt"] = QcJson.Int(rec, "attempt"), ["url"] = QcJson.Has(a, "url") ? QcJson.Str(a, "url") : "" }, $"{prNum}|{QcJson.Str(rec, "sha")}|{QcJson.Str(rec, "attempt")}", ct);
        }
        else if (QcJson.Str(rec, "probe") != probe)
        {
            rec!["probe"] = probe; QcRecords.BumpRev(rec);
            await SaveRecord(n, st, rec, ct);
        }
        return new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["outcome"] = "ready", ["probe"] = probe, ["record"] = rec!.DeepClone() };
    }

    async Task<JsonObject> VerbFail(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr"); var code = Need(a, "code");
        if (!QcRecords.FailCodes.Contains(code)) throw new QcReject(2, "usage", $"unknown failure code '{code}'");
        var st = await items.ReadState(n, ct); var rec = await RecordOf(st, prNum, ct);
        AssertAssignment(rec, Need(a, "assignment"));
        if (!QcRecords.CanMove(QcJson.Str(rec, "state"), "failed")) throw new QcReject(10, "bad-transition", $"a QC environment cannot go from {QcJson.Str(rec, "state")} to failed");
        var reason = Text(QcJson.Has(a, "reason") ? QcJson.Str(a, "reason") : "", 300);
        rec!["fail"] = new JsonObject { ["code"] = code, ["reason"] = reason };
        QcRecords.SetState(rec, "failed", utcNow());
        await SaveRecord(n, st, rec, ct);
        await JournalLine(n, "qc-failed", new JsonObject { ["pr"] = prNum, ["code"] = code, ["reason"] = JournalText(reason, 300) }, $"{prNum}|{QcJson.Str(rec, "sha")}|{QcJson.Str(rec, "attempt")}", ct);
        var log = QcJson.Has(a, "log_tail") ? Text(QcJson.Str(a, "log_tail"), 1500) : "";
        return new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["state"] = "failed", ["record"] = rec.DeepClone(), ["log_tail"] = log };
    }

    async Task<JsonObject> VerbStopped(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var envId = Need(a, "env_id"); var assignment = Need(a, "assignment");
        var st = await items.ReadState(n, ct); var s = st.State;
        JsonObject? rec = null;
        if (QcJson.Has(s, "qc") && s["qc"] is JsonObject map)
            foreach (var kv in map)
            {
                if (kv.Value is not JsonObject x) continue;
                if (QcJson.Str(x, "env_id") == envId) rec = x;
                else if (QcJson.Has(x, "old_assignments") && x["old_assignments"] is JsonArray old && old.Any(o => string.Equals(QcJson.Text(o), assignment, StringComparison.OrdinalIgnoreCase)))
                    return new JsonObject { ["ok"] = true, ["id"] = n, ["outcome"] = "old-environment" };
            }
        if (rec is null) throw new QcReject(12, "no-environment", $"no QC environment '{envId}' is recorded for this item");
        AssertAssignment(rec, assignment);
        if (QcJson.Str(rec, "state") == "torn-down") return WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["outcome"] = "already", ["pr"] = QcJson.Int(rec, "pr") }, rec);
        var reason = QcJson.Has(rec, "teardown") ? QcJson.Str(QcJson.Obj(rec, "teardown"), "reason") : "worker-stopped";
        rec["teardown"] = new JsonObject { ["reason"] = reason, ["at"] = QcJson.Iso(utcNow()) };
        QcRecords.SetState(rec, "torn-down", utcNow());
        await SaveRecord(n, st, rec, ct);
        await JournalLine(n, "qc-torn-down", new JsonObject { ["pr"] = QcJson.Int(rec, "pr"), ["reason"] = reason }, $"{QcJson.Str(rec, "pr")}|{QcJson.Str(rec, "sha")}|{QcJson.Str(rec, "attempt")}", ct);
        return WithRecord(new JsonObject { ["ok"] = true, ["id"] = n, ["outcome"] = "torn-down", ["pr"] = QcJson.Int(rec, "pr") }, rec);
    }

    // Internal (admin): the gateway asks after a pull_request_review webhook. Reads GitHub for the truth, keeps the persisted copy in step (set, or cleared by a dismissal), and answers what the gateway needs to wake the owner.
    async Task<JsonObject> VerbApproval(JsonObject a, CancellationToken ct)
    {
        var n = ItemNumber(a); var prNum = NeedInt(a, "pr");
        var pr = await GetPull(prNum, ct); var head = HeadSha(pr);
        var by = await HumanApproval(pr, ct);
        var st = await items.ReadState(n, ct); var rec = await RecordOf(st, prNum, ct);
        var changed = await UpdateApproved(n, st, rec, head, by, ct);
        var ran = rec is not null && QcJson.Has(rec, "ran_sha") && QcJson.Str(rec, "ran_sha") == head;
        return new JsonObject { ["ok"] = true, ["id"] = n, ["pr"] = prNum, ["sha"] = head, ["approved"] = !string.IsNullOrEmpty(by), ["by"] = by ?? "", ["ran"] = ran, ["changed"] = changed };
    }
}
