using System.Globalization;
using System.Text;
using Fworker.Workflows.Delivery;

namespace Fworker.Workflows.Incident;

// W8.1 (#349): IncidentSaga, one per application and environment (docs/design/wolverine-factory.md 3.3, diagrams/wvf-incident.puml). This is its state machine as a pure function: Apply takes the state the pair has (or
// none), one input and the time, and answers the new state, the decisions to publish, the messages to send and the timer to ask for. Nothing here reads or writes anything; IncidentService loads and saves the state
// with a revision and the Wolverine handlers publish the result, so the whole machine is tested without a database.
//
//   quiet --signal--> triaging --> rolling-back --> verifying --> quiet (resolved)
//                        |   \--> fixing (hotfix plan) ---^
//                        \--> escalated (the owner decides: roll back, fix forward, fixed by hand, close)
//   every stage has a timeout that escalates; verifying goes back to triaging when the signal returns.
//
//  - Signals: an alert (W8.2's webhook), bad health reads confirmed by the confirmation rule (N in a row), a failed deployment where the environment's policy says so. Alerts and health reads are suppressed while a
//    deployment to the environment runs. A signal while an incident is open joins it; the open-instance index is the pair's row.
//  - On opening the saga opens the incident issue (the owner is notified by it), HOLDS the delivery train of the pair (the incident's own hotfix release passes the hold) and, where the rollback policy of the
//    environment is automatic and a release deployed shortly before the incident correlates with it, asks the train for a rollback. The rollback is a request to the train, which owns the deployments: the saga
//    never calls Octopus.
//  - Fixing creates the hotfix item (rank top, every gate) and waits for its delivery to the environment; verifying needs the Healthy reads for the settings' window; resolving closes the issue with a note built from
//    the incident's own timeline and releases the hold.
//  - Delivery is at least once: an input with a business key is applied once, and the input that was the last applied one finds its key as LastKey and publishes the same decisions, messages and timer again.

public enum IncidentOutcome
{
    /// <summary>The input was taken (it may have changed nothing).</summary>
    Applied,
    /// <summary>The environment is not watched, or the signal is suppressed: nothing happens.</summary>
    Ignored,
    /// <summary>The business key was applied before.</summary>
    Duplicate,
    /// <summary>The input answers another incident, another stage or a timer that is not the pending one: ignored.</summary>
    Stale,
    /// <summary>The input is valid but not allowed in the incident's stage.</summary>
    Refused,
}

/// <summary>The timer a transition asks for: the message and when it is due.</summary>
public sealed record IncidentTimer(IncidentTimerFired Message, DateTimeOffset Due);

/// <summary>What one input made the saga do: the decisions to publish, the messages to send (commands to the issue port, the hotfix, the train) and the timer.</summary>
public sealed record IncidentResult(IncidentOutcome Outcome, string Reason, IReadOnlyList<IncidentDecided> Decisions, IReadOnlyList<object> Messages, IReadOnlyList<IncidentTimer> Timers)
{
    public static IncidentResult Quiet(IncidentOutcome outcome, string reason) => new(outcome, reason, [], [], []);
}

public sealed record IncidentTransition(IncidentState? State, bool Changed, IncidentResult Result);

public sealed class IncidentSaga(IncidentSettings settings)
{
    public IncidentSettings Settings => settings;

    readonly record struct Answer(IncidentOutcome Outcome, string Reason = "")
    {
        public static readonly Answer Applied = new(IncidentOutcome.Applied);
        public static Answer Ignored(string reason) => new(IncidentOutcome.Ignored, reason);
        public static Answer Stale(string reason) => new(IncidentOutcome.Stale, reason);
        public static Answer Refused(string reason) => new(IncidentOutcome.Refused, reason);
    }

    sealed class Ctx(IncidentState state, IncidentSettings cfg, DateTimeOffset now)
    {
        public IncidentState S { get; } = state;
        public IncidentSettings Cfg { get; } = cfg;
        public DateTimeOffset Now { get; } = now;
        public List<IncidentDecided> Decisions { get; } = [];
        public List<IncidentEffect> Effects { get; } = [];

        public void Decide(string action, params string[] reasons)
        {
            S.Sequence++;
            Decisions.Add(new IncidentDecided(S.Key, S.Generation, S.Sequence, action, S.Status, reasons, Now));
            S.Timeline.Add(Line(Now, action, reasons));
            if (S.Timeline.Count > IncidentState.KeepTimeline) S.Timeline.RemoveRange(0, S.Timeline.Count - IncidentState.KeepTimeline);
        }

        /// <summary>A line of the story that is not a decision (a release deployed meanwhile, a read that came back).</summary>
        public void Note(string text)
        {
            S.Timeline.Add(Line(Now, "note", [text]));
            if (S.Timeline.Count > IncidentState.KeepTimeline) S.Timeline.RemoveRange(0, S.Timeline.Count - IncidentState.KeepTimeline);
        }

        public void Send(IncidentEffect e) => Effects.Add(e);

        public void Arm(string stage, TimeSpan after)
        {
            S.TimerStage = stage; S.TimerDue = Now + after;
            Effects.Add(new TimerEffect(stage, S.TimerDue.Value));
        }

        public void Disarm() { S.TimerStage = ""; S.TimerDue = null; }

        static string Line(DateTimeOffset at, string action, IReadOnlyList<string> reasons)
            => $"{at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z {action}{(reasons.Count == 0 ? "" : ": " + string.Join("; ", reasons))}";
    }

    // ---- the entry ---------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>What a redelivery of the input is recognised by: its business key, or for the timer what identifies it.</summary>
    public static string ReplayKeyOf(IncidentInput input) => input switch
    {
        IIncidentKeyed k => k.BusinessKey,
        IncidentTimerFired t => $"timer|{t.Generation}|{t.Stage}|{t.Due.UtcTicks}",
        _ => "",
    };

    public IncidentTransition Apply(IncidentState? existing, IncidentInput input, DateTimeOffset now)
    {
        if (!input.Key.Contains('/')) return Nothing(existing, IncidentOutcome.Refused, "the key is not deployable/environment");
        var environment = TrainKey.Split(input.Key).Environment;
        if (!settings.Watches(environment)) return Nothing(existing, IncidentOutcome.Ignored, $"incidents are not handled in {environment}");
        var replay = ReplayKeyOf(input);
        if (existing is not null && replay.Length > 0 && replay == existing.LastKey) return Reemit(existing);

        var state = existing is null ? IncidentState.New(input.Key) : IncidentState.FromJson(existing.ToJson());
        if (input is IIncidentKeyed { BusinessKey.Length: > 0 } keyed && state.Keys.Contains(keyed.BusinessKey)) return Nothing(existing, IncidentOutcome.Duplicate, "the key was applied");
        if (input is IIncidentGeneration g && g.Generation != 0 && g.Generation != state.Generation) return Nothing(existing, IncidentOutcome.Stale, $"the incident is on generation {state.Generation}");

        var before = Core(state);
        var c = new Ctx(state, settings, now);
        var answer = Route(c, input);
        if (answer.Outcome != IncidentOutcome.Applied) return Nothing(existing, answer.Outcome, answer.Reason);
        // a read that changed nothing (a good read in a quiet pair, a bad read after the streak was reset) is not saved: the health poll must not write the row every minute
        if (c.Decisions.Count == 0 && c.Effects.Count == 0 && Core(state) == before) return Nothing(existing, IncidentOutcome.Applied, "");
        return Seal(c, replay);
    }

    /// <summary>The state without its bookkeeping: what a transition changed, for the test of "nothing changed".</summary>
    static string Core(IncidentState s)
    {
        var copy = IncidentState.FromJson(s.ToJson());
        copy.Keys = []; copy.LastKey = ""; copy.Emitted = []; copy.Outbox = [];
        return copy.ToJson();
    }

    Answer Route(Ctx c, IncidentInput input) => input switch
    {
        AlertReceived a => Alert(c, a),
        HealthObserved h => Health(c, h),
        DeploymentObserved d => Deployment(c, d),
        TriageReported t => Triage(c, t),
        RollbackReported r => RollbackAnswer(c, r),
        IssueOpened i => Issue(c, i),
        HotfixCreated h => HotfixMade(c, h),
        HotfixProgress p => Progress(c, p),
        OwnerDecided o => Owner(c, o),
        IncidentTimerFired t => Timer(c, t),
        _ => Answer.Refused($"no handling for {input.GetType().Name}"),
    };

    // ---- signals -----------------------------------------------------------------------------------------------------------------------------------------

    static bool IsDeploying(Ctx c) => c.S.Deploying && c.S.DeployingSince is { } since && c.Now - since < c.Cfg.Deployment;

    Answer Alert(Ctx c, AlertReceived a)
    {
        var s = c.S;
        if (IsDeploying(c)) return Answer.Ignored("a deployment to the environment is running: the alert is suppressed");
        var detail = $"alert {a.Rule} ({a.Severity}): {a.Summary}";
        if (!s.IsOpen) { Open(c, "alert", detail); return Answer.Applied; }
        s.Joined++;
        c.Decide(IncidentActions.Joined, detail);
        if (s.Status == IncidentStatus.Verifying) Relapse(c, $"alert {a.Rule} while verifying");
        return Answer.Applied;
    }

    Answer Health(Ctx c, HealthObserved h)
    {
        var s = c.S;
        var cls = IncidentSettings.Classify(h.Status);
        if (IsDeploying(c)) { s.SuspectReads = 0; s.SuspectSince = null; return Answer.Applied; }   // (a deployment explains an unhealthy app: nothing counts while it runs)
        if (!s.IsOpen)
        {
            switch (cls)
            {
                case HealthClass.Good: s.SuspectReads = 0; s.SuspectSince = null; break;
                case HealthClass.Bad:
                    // the confirmation rule: N bad reads in a row, each within the window of the first, and a good read ends the streak
                    if (s.SuspectSince is not { } since || h.At - since > c.Cfg.Window) { s.SuspectReads = 1; s.SuspectSince = h.At; }
                    else s.SuspectReads++;
                    if (s.SuspectReads >= c.Cfg.ConfirmReads)
                    {
                        var n = s.SuspectReads; var first = s.SuspectSince ?? h.At;
                        s.SuspectReads = 0; s.SuspectSince = null;
                        Open(c, "health", $"{n} bad health reads in a row since {first.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}Z ({h.Status}{(h.Detail.Length == 0 ? "" : ": " + h.Detail)})");
                    }
                    break;
            }
            return Answer.Applied;
        }
        if (s.Status != IncidentStatus.Verifying) return Answer.Applied;   // (the reads that count are those of the verification)
        switch (cls)
        {
            case HealthClass.Good:
                s.BadReads = 0; s.HealthySince ??= h.At;
                if (h.At - s.HealthySince >= c.Cfg.Healthy) Resolve(c, $"Healthy since {s.HealthySince.Value.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}Z ({Minutes(c.Cfg.Healthy)} minutes without a bad read)");
                break;
            case HealthClass.Bad:
                s.BadReads++; s.HealthySince = null;
                if (s.BadReads >= c.Cfg.ConfirmReads) Relapse(c, $"{s.BadReads} bad health reads in a row while verifying ({h.Status})");
                break;
        }
        return Answer.Applied;
    }

    Answer Deployment(Ctx c, DeploymentObserved d)
    {
        var s = c.S;
        var env = c.Cfg.Of(s.Environment)!;
        // the watcher replays Octopus's history after a restart: a deployment older than the correlation window is no news for an incident (it must not open one, or hold the health reads)
        var recent = c.Now - d.At <= c.Cfg.Correlation;
        switch (d.Phase)
        {
            case "Started":
                if (c.Now - d.At > c.Cfg.Deployment) return Answer.Applied;
                s.Deploying = true; s.DeployingId = d.DeploymentId; s.DeployingSince = d.At;
                s.SuspectReads = 0; s.SuspectSince = null;
                return Answer.Applied;
            case "Succeeded":
                EndDeploying(s, d.DeploymentId);
                if (s.ReleaseAt is null || d.At >= s.ReleaseAt)
                {
                    if (!string.Equals(s.Release, d.Release, StringComparison.OrdinalIgnoreCase)) { s.PreviousRelease = s.Release; s.Release = d.Release; }
                    s.ReleaseAt = d.At;
                }
                s.SuspectReads = 0; s.SuspectSince = null;
                if (!s.IsOpen) return Answer.Applied;
                if (s.Status == IncidentStatus.RollingBack && s.RollbackTo.Length > 0 && string.Equals(d.Release, s.RollbackTo, StringComparison.OrdinalIgnoreCase))
                    StartVerifying(c, $"release {d.Release} runs again after the rollback");
                else c.Note($"release {d.Release} was deployed meanwhile");
                return Answer.Applied;
            case "Failed":
                EndDeploying(s, d.DeploymentId);
                if (!recent) return Answer.Applied;
                if (s.Status == IncidentStatus.RollingBack && s.RollbackTo.Length > 0 && string.Equals(d.Release, s.RollbackTo, StringComparison.OrdinalIgnoreCase))
                { Escalate(c, $"the deployment of release {d.Release} (the rollback) failed"); return Answer.Applied; }
                if (!env.DeploymentFailureOpens) return Answer.Applied;
                var detail = $"the deployment of release {d.Release} failed";
                if (!s.IsOpen) { Open(c, "deployment-failed", detail); return Answer.Applied; }
                s.Joined++;
                c.Decide(IncidentActions.Joined, detail);
                return Answer.Applied;
            default:
                return Answer.Refused($"no deployment phase '{d.Phase}'");
        }
    }

    static void EndDeploying(IncidentState s, string deploymentId)
    {
        // the end of a deployment other than the one that was running leaves the running one running
        if (s.DeployingId.Length > 0 && deploymentId.Length > 0 && !string.Equals(s.DeployingId, deploymentId, StringComparison.Ordinal)) return;
        s.Deploying = false; s.DeployingId = ""; s.DeployingSince = null;
    }

    // ---- the stages --------------------------------------------------------------------------------------------------------------------------------------

    void Open(Ctx c, string trigger, string detail)
    {
        var s = c.S;
        s.Generation++;
        s.Status = IncidentStatus.Triaging; s.OpenedAt = c.Now; s.StatusSince = c.Now; s.ResolvedAt = null;
        s.Trigger = trigger; s.Joined = 0; s.Timeline = []; s.IssueNumber = 0;
        s.RollbackTried = false; s.RollbackTo = ""; s.HotfixItem = ""; s.HotfixMerged = false; s.HotfixDelivered = false; s.HotfixRelease = "";
        s.HealthySince = null; s.BadReads = 0; s.Relapses = 0; s.EscalationReason = "";
        s.SuspectReads = 0; s.SuspectSince = null;
        c.Decide(IncidentActions.Detected, trigger, detail);
        c.Send(new OpenIssueEffect($"Incident: {s.Deployable} in {s.Environment} is failing ({trigger})", IssueBody(s, detail)));
        c.Send(new HoldEffect(true, HoldReason(s, detail), []));
        BeginTriage(c, detail);
    }

    /// <summary>The first look at a new or returned incident: roll back by policy when a release correlates, otherwise ask for a verdict.</summary>
    void BeginTriage(Ctx c, string detail)
    {
        var s = c.S;
        var mode = c.Cfg.RollbackOf(s.Environment);
        var minutes = s.ReleaseAt is { } at && s.OpenedAt is { } opened ? (opened - at).TotalMinutes : double.MaxValue;
        var correlated = s.Trigger != "deployment-failed" && s.Release.Length > 0 && minutes >= 0 && minutes <= c.Cfg.Correlation.TotalMinutes;
        if (correlated && !s.RollbackTried)
        {
            var why = $"release {s.Release} was deployed {Math.Round(minutes)} minutes before the incident";
            if (mode == RollbackMode.Automatic) { StartRollback(c, why); return; }
            if (mode == RollbackMode.Signoff) { Escalate(c, $"{why}; rolling it back needs a person's Sign-off (the rollback policy of {s.Environment}); recommended: roll back"); return; }
        }
        s.Status = IncidentStatus.Triaging; s.StatusSince = c.Now;
        c.Decide(IncidentActions.Triaging, !correlated ? "no recent release correlates" : s.RollbackTried ? $"release {s.Release} correlates, but a rollback was tried already" : $"release {s.Release} correlates, but the rollback policy of {s.Environment} is off");
        c.Arm("triage", c.Cfg.Triage);
        c.Send(new TriageEffect(s.Trigger, detail));
    }

    void StartRollback(Ctx c, string reason)
    {
        var s = c.S;
        s.Status = IncidentStatus.RollingBack; s.StatusSince = c.Now; s.RollbackTried = true; s.RollbackTo = "";
        c.Decide(IncidentActions.RollingBack, reason);
        c.Send(new RollbackEffect(reason));
        c.Arm("rollback", c.Cfg.RollingBack);
    }

    void EnterFixing(Ctx c, string reason)
    {
        var s = c.S;
        s.Status = IncidentStatus.Fixing; s.StatusSince = c.Now; s.EscalationReason = "";
        c.Decide(IncidentActions.Fixing, reason);
        if (s.HotfixItem.Length == 0 || s.HotfixDelivered)
        {
            s.HotfixItem = ""; s.HotfixMerged = false; s.HotfixDelivered = false; s.HotfixRelease = "";
            c.Send(new HotfixEffect($"Hotfix: {s.Deployable} in {s.Environment} is failing ({s.Trigger})", HotfixBody(s, reason)));
        }
        else c.Send(new HoldEffect(true, HoldReason(s, reason), [s.HotfixItem]));   // (the item exists: its release passes the hold)
        c.Arm("fix", c.Cfg.Fix);
    }

    void StartVerifying(Ctx c, string reason)
    {
        var s = c.S;
        s.Status = IncidentStatus.Verifying; s.StatusSince = c.Now; s.HealthySince = null; s.BadReads = 0; s.EscalationReason = "";
        c.Decide(IncidentActions.Verifying, reason);
        c.Arm("verify", c.Cfg.Verify);
    }

    void Relapse(Ctx c, string reason)
    {
        var s = c.S;
        s.Relapses++; s.HealthySince = null; s.BadReads = 0;
        if (s.Relapses > c.Cfg.MaxRelapses) { Escalate(c, $"{reason}; the incident came back {s.Relapses} times"); return; }
        s.Status = IncidentStatus.Triaging; s.StatusSince = c.Now;
        c.Decide(IncidentActions.Relapsed, reason);
        c.Decide(IncidentActions.Triaging, "the signal returned; looking again");
        c.Arm("triage", c.Cfg.Triage);
        c.Send(new TriageEffect(s.Trigger, reason));
    }

    void Escalate(Ctx c, string reason)
    {
        var s = c.S;
        s.Status = IncidentStatus.Escalated; s.StatusSince = c.Now; s.EscalationReason = reason;
        c.Disarm();
        c.Decide(IncidentActions.Escalated, reason);
        var mention = c.Cfg.Mention.Length == 0 ? "" : c.Cfg.Mention.Trim() + " ";
        c.Send(new CommentEffect($"escalated|{s.Generation}|{s.Sequence}",
            $"{mention}The factory cannot settle this incident by itself: {reason}.\n\nThe owner chooses: roll back, fix forward, fixed by hand (verify it), or close. The delivery train of {s.Key} stays held until the incident is resolved."));
    }

    void Resolve(Ctx c, string reason)
    {
        var s = c.S;
        s.Status = IncidentStatus.Quiet; s.StatusSince = c.Now; s.ResolvedAt = c.Now;
        c.Disarm();
        c.Decide(IncidentActions.Resolved, reason);
        c.Send(new CloseIssueEffect(ResolutionNote(s)));
        c.Send(new HoldEffect(false, "", []));
        s.HotfixItem = ""; s.HealthySince = null; s.BadReads = 0; s.SuspectReads = 0; s.SuspectSince = null; s.EscalationReason = "";
    }

    // ---- answers to the stages ---------------------------------------------------------------------------------------------------------------------------

    Answer Triage(Ctx c, TriageReported t)
    {
        var s = c.S;
        if (s.Status != IncidentStatus.Triaging) return Answer.Stale("the incident is not in its triage");
        var mode = c.Cfg.RollbackOf(s.Environment);
        switch (t.Verdict.Trim().ToLowerInvariant())
        {
            case "rollback":
                if (s.RollbackTried) Escalate(c, $"triage recommends a rollback, but one was tried already: {t.Summary}");
                else if (mode == RollbackMode.Automatic) StartRollback(c, $"triage: {t.Summary}");
                else if (mode == RollbackMode.Signoff) Escalate(c, $"triage recommends a rollback, which needs a person's Sign-off: {t.Summary}");
                else Escalate(c, $"triage recommends a rollback, but the rollback policy of {s.Environment} is off: {t.Summary}");
                return Answer.Applied;
            case "fix-forward": EnterFixing(c, $"triage: {t.Summary}"); return Answer.Applied;
            case "escalate": Escalate(c, $"triage: {t.Summary}"); return Answer.Applied;
            case "close": Resolve(c, $"no incident, by triage: {t.Summary}"); return Answer.Applied;
            default: return Answer.Refused($"no triage verdict '{t.Verdict}'");
        }
    }

    Answer RollbackAnswer(Ctx c, RollbackReported r)
    {
        var s = c.S;
        if (s.Status != IncidentStatus.RollingBack) return Answer.Stale("the incident is not rolling back");
        switch (r.Outcome.Trim().ToLowerInvariant())
        {
            case "started":
                s.RollbackTo = r.ToRelease ?? "";
                c.Decide(IncidentActions.RollbackStarted, r.ToRelease is { Length: > 0 } ? $"deploying release {r.ToRelease}" : "the train started the rollback");
                return Answer.Applied;
            case "done":
                if (r.ToRelease is { Length: > 0 }) s.RollbackTo = r.ToRelease;
                StartVerifying(c, $"rolled back to release {(s.RollbackTo.Length == 0 ? "the previous one" : s.RollbackTo)}");
                return Answer.Applied;
            case "refused":
                if (string.Equals(r.Code, "migration", StringComparison.OrdinalIgnoreCase)) EnterFixing(c, "a migration was added since the previous release, so a rollback is unsafe; fixing forward");
                else Escalate(c, $"the rollback was refused ({r.Code}): {r.Reason}");
                return Answer.Applied;
            case "failed":
                Escalate(c, $"the rollback failed ({r.Code}): {r.Reason}");
                return Answer.Applied;
            default: return Answer.Refused($"no rollback outcome '{r.Outcome}'");
        }
    }

    static Answer Issue(Ctx c, IssueOpened i)
    {
        c.S.IssueNumber = i.Number;
        return Answer.Applied;
    }

    Answer HotfixMade(Ctx c, HotfixCreated h)
    {
        var s = c.S;
        if (!s.IsOpen) return Answer.Stale("the incident is resolved");
        s.HotfixItem = h.Item.ToLowerInvariant(); s.HotfixMerged = false; s.HotfixDelivered = false;
        c.Decide(IncidentActions.HotfixCreated, h.Item);
        c.Send(new HoldEffect(true, HoldReason(s, $"the hotfix is {h.Item}"), [s.HotfixItem]));
        return Answer.Applied;
    }

    Answer Progress(Ctx c, HotfixProgress p)
    {
        var s = c.S;
        if (!s.IsOpen || s.HotfixItem.Length == 0 || !string.Equals(s.HotfixItem, p.Item, StringComparison.OrdinalIgnoreCase)) return Answer.Stale("the item is not the hotfix of the open incident");
        switch (p.Stage)
        {
            case "merged":
                if (s.HotfixMerged) return Answer.Applied;
                s.HotfixMerged = true;
                c.Decide(IncidentActions.HotfixMerged, p.Item);
                return Answer.Applied;
            case "delivered":
                s.HotfixDelivered = true; s.HotfixRelease = p.Release;
                if (s.Status is IncidentStatus.Fixing or IncidentStatus.Escalated or IncidentStatus.Triaging) StartVerifying(c, $"hotfix {p.Item} delivered in release {p.Release}");
                else c.Note($"hotfix {p.Item} was delivered in release {p.Release}");
                return Answer.Applied;
            case "parked" or "handed-back":
                if (s.Status == IncidentStatus.Fixing) Escalate(c, $"the hotfix {p.Item} is {p.Stage.Replace("-", " ")}");
                else c.Note($"the hotfix {p.Item} is {p.Stage.Replace("-", " ")}");
                return Answer.Applied;
            default: return Answer.Refused($"no hotfix stage '{p.Stage}'");
        }
    }

    Answer Owner(Ctx c, OwnerDecided o)
    {
        var s = c.S;
        if (s.Status != IncidentStatus.Escalated) return Answer.Refused("the incident is not escalated");
        var by = o.By.Length == 0 ? "the owner" : o.By;
        switch (o.Decision.Trim().ToLowerInvariant())
        {
            case "rollback": StartRollback(c, $"{by} chose a rollback"); return Answer.Applied;
            case "fix-forward" or "fix": EnterFixing(c, $"{by} chose to fix forward"); return Answer.Applied;
            case "fixed": StartVerifying(c, $"{by} says it is fixed by hand"); return Answer.Applied;
            case "close": Resolve(c, $"closed by {by}{(o.Note.Length == 0 ? "" : ": " + o.Note)}"); return Answer.Applied;
            default: return Answer.Refused($"no decision '{o.Decision}'");
        }
    }

    Answer Timer(Ctx c, IncidentTimerFired t)
    {
        var s = c.S;
        if (s.TimerDue != t.Due || s.TimerStage != t.Stage) return Answer.Stale("not the pending timer");
        c.Disarm();
        switch (t.Stage)
        {
            case "triage" when s.Status == IncidentStatus.Triaging: Escalate(c, $"no triage verdict within {Minutes(c.Cfg.Triage)} minutes"); return Answer.Applied;
            case "rollback" when s.Status == IncidentStatus.RollingBack: Escalate(c, $"the rollback did not finish within {Minutes(c.Cfg.RollingBack)} minutes"); return Answer.Applied;
            case "fix" when s.Status == IncidentStatus.Fixing: Escalate(c, $"the hotfix was not delivered within {Minutes(c.Cfg.Fix)} minutes"); return Answer.Applied;
            case "verify" when s.Status == IncidentStatus.Verifying: Escalate(c, $"health did not stay at baseline for {Minutes(c.Cfg.Healthy)} minutes within {Minutes(c.Cfg.Verify)} minutes"); return Answer.Applied;
            default: return Answer.Stale($"the {t.Stage} timer does not fit the stage {s.Status}");
        }
    }

    // ---- texts ---------------------------------------------------------------------------------------------------------------------------------------------

    static string Minutes(TimeSpan t) => Math.Round(t.TotalMinutes).ToString(CultureInfo.InvariantCulture);

    static string HoldReason(IncidentState s, string detail) => $"incident {s.Key}#{s.Generation}: {detail}";

    static string IssueBody(IncidentState s, string detail)
    {
        var b = new StringBuilder();
        b.AppendLine($"The factory opened this incident for **{s.Deployable}** in **{s.Environment}**.");
        b.AppendLine();
        b.AppendLine($"- Signal: {s.Trigger}: {detail}");
        b.AppendLine($"- Opened: {s.OpenedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z");
        if (s.Release.Length > 0) b.AppendLine($"- Release in the environment: {s.Release}{(s.ReleaseAt is { } at ? $" (deployed {at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z)" : "")}");
        b.AppendLine($"- The delivery train of {s.Key} is held until the incident is resolved; the incident's own hotfix and rollback pass the hold.");
        return b.ToString();
    }

    static string HotfixBody(IncidentState s, string reason)
    {
        var b = new StringBuilder();
        b.AppendLine($"Hotfix for the incident {s.Key}#{s.Generation}: {s.Deployable} in {s.Environment} is failing ({s.Trigger}).");
        b.AppendLine();
        b.AppendLine($"Why a fix and not a rollback: {reason}.");
        if (s.IssueNumber > 0) b.AppendLine($"The incident issue is #{s.IssueNumber}.");
        b.AppendLine();
        b.AppendLine("Acceptance criteria");
        b.AppendLine();
        b.AppendLine($"- The signal that opened the incident clears in {s.Environment} and stays clear for the verification window.");
        b.AppendLine("- The change fixes the cause and nothing else.");
        return b.ToString();
    }

    static string ResolutionNote(IncidentState s)
    {
        var b = new StringBuilder();
        var took = s.OpenedAt is { } o && s.ResolvedAt is { } r ? $" after {Math.Round((r - o).TotalMinutes)} minutes" : "";
        b.AppendLine($"Incident {s.Key}#{s.Generation} is resolved{took}. The factory releases the hold on the delivery train.");
        b.AppendLine();
        foreach (var line in s.Timeline) b.AppendLine("- " + line);
        var text = b.ToString();
        return text.Length <= 3500 ? text : text[..3500];
    }

    // ---- the end of every transition -----------------------------------------------------------------------------------------------------------------------

    IncidentTransition Seal(Ctx c, string replayKey)
    {
        var s = c.S;
        if (replayKey.Length > 0 && !replayKey.StartsWith("timer|", StringComparison.Ordinal)) { s.Keys.Add(replayKey); if (s.Keys.Count > IncidentState.KeepKeys) s.Keys.RemoveRange(0, s.Keys.Count - IncidentState.KeepKeys); }
        s.LastKey = replayKey;
        s.Emitted = c.Decisions;
        s.Outbox = c.Effects;
        return new IncidentTransition(s, true, ResultOf(s, IncidentOutcome.Applied, ""));
    }

    IncidentTransition Reemit(IncidentState saved)
        => new(IncidentState.FromJson(saved.ToJson()), false, ResultOf(saved, IncidentOutcome.Duplicate, "published again"));

    IncidentResult ResultOf(IncidentState s, IncidentOutcome outcome, string reason)
    {
        var messages = new List<object>();
        var timers = new List<IncidentTimer>();
        foreach (var e in s.Outbox)
            switch (e)
            {
                case OpenIssueEffect o: messages.Add(new OpenIncidentIssue(s.Key, s.Generation, o.Title, o.Body, settings.IssueLabel)); break;
                case CommentEffect m: messages.Add(new CommentIncidentIssue(s.Key, s.Generation, s.IssueNumber, m.CommentKey, m.Body)); break;
                case CloseIssueEffect x: messages.Add(new CloseIncidentIssue(s.Key, s.Generation, s.IssueNumber, x.Note)); break;
                case HoldEffect h: messages.Add(new TrainHold(s.Key, h.Held, h.Reason, h.Held ? $"{s.Key}#{s.Generation}" : "", h.Allow)); break;
                case RollbackEffect r: messages.Add(new TrainRollback(s.Key, $"{s.Key}#{s.Generation}", s.Generation, null, r.Reason)); break;
                case HotfixEffect f: messages.Add(new CreateHotfixItem(s.Key, s.Generation, f.Title, f.Body)); break;
                case TriageEffect t: messages.Add(new TriageRequested(s.Key, s.Generation, t.Trigger, t.Summary)); break;
                case TimerEffect t: timers.Add(new IncidentTimer(new IncidentTimerFired(s.Key, s.Generation, t.Stage, t.Due), t.Due)); break;
            }
        return new IncidentResult(outcome, reason, s.Emitted, messages, timers);
    }

    static IncidentTransition Nothing(IncidentState? state, IncidentOutcome outcome, string reason)
        => new(state, false, IncidentResult.Quiet(outcome, reason));
}
