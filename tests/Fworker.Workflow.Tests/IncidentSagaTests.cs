using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Incident;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>The rig of the incident tests: the saga on a state the test holds, a clock it moves, and everything the saga said.</summary>
sealed class IncidentRig
{
    public const string Key = "orders/tdd";
    public static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    public readonly IncidentSettings Settings;
    public readonly IncidentSaga Saga;
    public IncidentState? State;
    public DateTimeOffset Now = T0;
    public readonly List<IncidentDecided> Decisions = [];
    public readonly List<object> Messages = [];
    public readonly List<IncidentTimer> Timers = [];

    public IncidentRig(RollbackMode mode = RollbackMode.Automatic, bool failureOpens = false, string mention = "@owner")
    {
        Settings = new IncidentSettings(new Dictionary<string, IncidentEnvironment> { ["tdd"] = new("tdd", mode, failureOpens) }, Mention: mention);
        Saga = new IncidentSaga(Settings);
    }

    public IncidentResult Send(IncidentInput input)
    {
        var t = Saga.Apply(State, input, Now);
        if (t.Changed) State = t.State;
        Decisions.AddRange(t.Result.Decisions); Messages.AddRange(t.Result.Messages); Timers.AddRange(t.Result.Timers);
        return t.Result;
    }

    public IncidentState S => State!;
    public IEnumerable<T> Of<T>() => Messages.OfType<T>();
    public IncidentDecided LastDecision => Decisions[^1];
    public IncidentTimer LastTimer => Timers[^1];

    public IncidentResult After(TimeSpan span, IncidentInput input) { Now += span; return Send(input); }
    int reads;
    // (a read is a moment: two reads of one test never share a time, or the second would be taken for a redelivery of the first)
    public IncidentResult Bad(string status = "Unhealthy") => Send(new HealthObserved(Key, status, "", Now + TimeSpan.FromMilliseconds(++reads)));
    public IncidentResult Good() => Send(new HealthObserved(Key, "Healthy", "", Now + TimeSpan.FromMilliseconds(++reads)));
    public IncidentResult Alert(string id = "a1", string rule = "http-5xx") => Send(new AlertReceived(Key, id, rule, "Sev1", "errors above the threshold", Now));
    public IncidentResult Deploy(string release, string phase, string? id = null) => Send(new DeploymentObserved(Key, id ?? "D-" + release, release, phase, Now));

    /// <summary>Release 1.0.2 deployed (it succeeded two minutes after it started), then <paramref name="minutes"/> minutes pass.</summary>
    public void Deployed(string release = "1.0.2", int minutes = 10)
    {
        Deploy(release, "Started"); Now += TimeSpan.FromMinutes(2); Deploy(release, "Succeeded"); Now += TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Three bad reads a minute apart: the confirmation rule opens the incident.</summary>
    public void OpenByHealth()
    {
        Bad(); Now += TimeSpan.FromMinutes(1); Bad(); Now += TimeSpan.FromMinutes(1); Bad();
    }

    public IncidentResult Fire(IncidentTimer timer) { Now = timer.Due; return Send(timer.Message); }

    public IncidentResult Rollback(string outcome, string code = "", string? to = null, string reason = "") => Send(new RollbackReported(Key, S.Generation, outcome, code, to, reason, Now));
    public IncidentResult Triage(string verdict, string summary = "a defect in the last release") => Send(new TriageReported(Key, S.Generation, "r-" + Decisions.Count, verdict, summary));
    public IncidentResult Owner(string decision) => Send(new OwnerDecided(Key, S.Generation, "o-" + Decisions.Count, decision, "jeffrey", ""));
    public IncidentResult Hotfix(string stage, string item = "acme/work#42", string release = "") => Send(new HotfixProgress(Key, item, stage, release, Now));

    /// <summary>The incident verified and healthy for the whole window.</summary>
    public IncidentResult Heal() { Good(); Now += Settings.Healthy + TimeSpan.FromSeconds(1); return Good(); }
}

/// <summary>W8.1: the IncidentSaga as a pure state machine: signals, the confirmation rule, the stages, the rollback policy, the timeouts and the repeats.</summary>
public class IncidentSagaTests
{
    // ---- the settings ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSettings_AreReadFromTheConfiguration_AndAMissingBlockWatchesNothing()
    {
        var root = JsonNode.Parse("""
            { "escalationMention": "@jeffrey", "incidents": { "environments": ["tdd", "Prod", "qa"], "rollback": { "tdd": "automatic", "prod": "signoff", "uat": "automatic" }, "deploymentFailureOpens": ["prod"],
              "confirmReads": 4, "confirmWithinMinutes": 20, "rollbackWithinMinutes": 45, "triageMinutes": 15, "fixMinutes": 120, "maxRelapses": 1, "issueLabel": "inc" } }
            """);
        var s = IncidentSettings.From(root);
        Assert.Equal(["prod", "tdd"], s.Environments.Keys.Order());   // (qa is no environment; uat has a policy but is not watched)
        Assert.Equal((RollbackMode.Automatic, false), (s.Of("tdd")!.Rollback, s.Of("tdd")!.DeploymentFailureOpens));
        Assert.Equal((RollbackMode.Signoff, true), (s.Of("PROD")!.Rollback, s.Of("prod")!.DeploymentFailureOpens));
        Assert.False(s.Watches("uat")); Assert.Equal(RollbackMode.Off, s.RollbackOf("uat"));
        Assert.Equal((4, 20, 45, 15, 120, 1, "@jeffrey", "inc"), (s.ConfirmReads, (int)s.Window.TotalMinutes, (int)s.Correlation.TotalMinutes, (int)s.Triage.TotalMinutes, (int)s.Fix.TotalMinutes, s.MaxRelapses, s.Mention, s.IssueLabel));
        // the defaults of the design
        Assert.Equal((30, 30, 10, 60, 60), ((int)IncidentSettings.None.Triage.TotalMinutes, (int)IncidentSettings.None.RollingBack.TotalMinutes, (int)IncidentSettings.None.Healthy.TotalMinutes, (int)IncidentSettings.None.Verify.TotalMinutes, (int)IncidentSettings.None.Correlation.TotalMinutes));
        Assert.Empty(IncidentSettings.From(null).Environments);
        Assert.Empty(IncidentSettings.From(JsonNode.Parse("""{ "incidents": { "environments": "tdd" } }""")).Environments);
    }

    [Fact]
    public void TheShippedDefaults_WatchTddOnly_WithAnAutomaticRollback()
    {
        var path = Path.Combine(RepoPaths.Root, "config", "instance.json");
        var s = IncidentSettings.From(JsonNode.Parse(File.ReadAllText(path)));
        Assert.Equal(["tdd"], s.Environments.Keys);
        Assert.Equal(RollbackMode.Automatic, s.RollbackOf("tdd"));
        Assert.Equal(RollbackMode.Off, s.RollbackOf("uat")); Assert.Equal(RollbackMode.Off, s.RollbackOf("prod"));
        Assert.False(s.Of("tdd")!.DeploymentFailureOpens);
        Assert.Equal((3, 15, 60, 30, 30, 240, 10, 60, 60), (s.ConfirmReads, (int)s.Window.TotalMinutes, (int)s.Correlation.TotalMinutes, (int)s.Triage.TotalMinutes, (int)s.RollingBack.TotalMinutes, (int)s.Fix.TotalMinutes, (int)s.Healthy.TotalMinutes, (int)s.Verify.TotalMinutes, (int)s.Deployment.TotalMinutes));
    }

    [Theory]
    [InlineData("Healthy", HealthClass.Good)]
    [InlineData("healthy", HealthClass.Good)]
    [InlineData("Unhealthy", HealthClass.Bad)]
    [InlineData("unreachable", HealthClass.Bad)]
    [InlineData("status 503", HealthClass.Bad)]
    [InlineData("", HealthClass.Bad)]
    [InlineData("Degraded", HealthClass.Neutral)]
    public void AHealthStatus_IsGoodBadOrNeither(string status, HealthClass expected) => Assert.Equal(expected, IncidentSettings.Classify(status));

    // ---- the signals ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AGoodRead_InAQuietPair_ChangesNothing_AndSavesNothing()
    {
        var r = new IncidentRig();
        var result = r.Good();
        Assert.Equal(IncidentOutcome.Applied, result.Outcome);
        Assert.Null(r.State);
        Assert.Empty(r.Decisions);
    }

    [Fact]
    public void ThreeBadReadsInARow_OpenAnIncident_TheIssue_TheHold_AndATriage()
    {
        var r = new IncidentRig();
        r.Bad(); r.Now += TimeSpan.FromMinutes(1); r.Bad();
        Assert.Empty(r.Decisions);
        Assert.Equal(2, r.S.SuspectReads);
        r.Now += TimeSpan.FromMinutes(1);
        var result = r.Bad();
        Assert.Equal([IncidentActions.Detected, IncidentActions.Triaging], result.Decisions.Select(d => d.Action));
        Assert.Equal(IncidentStatus.Triaging, r.S.Status); Assert.Equal(1, r.S.Generation); Assert.Equal("health", r.S.Trigger);
        Assert.Contains("3 bad health reads in a row since 10:00Z (Unhealthy)", result.Decisions[0].Reasons[1]);
        var issue = Assert.Single(result.Messages.OfType<OpenIncidentIssue>());
        Assert.Equal(("orders/tdd", 1, "incident"), (issue.Key, issue.Generation, issue.Label));
        Assert.Contains("orders in tdd is failing (health)", issue.Title);
        var hold = Assert.Single(result.Messages.OfType<TrainHold>());
        Assert.Equal(("orders/tdd", true, "orders/tdd#1"), (hold.Key, hold.Held, hold.IncidentId));
        Assert.Empty(hold.Allow!);
        Assert.Equal("orders/tdd#1", Assert.Single(result.Messages.OfType<TriageRequested>()) is { } t ? $"{t.Key}#{t.Generation}" : "");
        var timer = Assert.Single(result.Timers);
        Assert.Equal(("triage", r.Now + TimeSpan.FromMinutes(30)), (timer.Message.Stage, timer.Due));
        Assert.Equal(0, r.S.SuspectReads);
    }

    [Fact]
    public void AGoodRead_EndsTheStreak_AndReadsOutsideTheWindowStartItAgain()
    {
        var r = new IncidentRig();
        r.Bad(); r.Now += TimeSpan.FromMinutes(1); r.Bad(); r.Now += TimeSpan.FromMinutes(1); r.Good();
        Assert.Equal(0, r.S.SuspectReads);
        r.Now += TimeSpan.FromMinutes(1); r.Bad(); r.Now += TimeSpan.FromMinutes(1); r.Bad();
        Assert.Empty(r.Decisions);
        // the third comes after the window of the first (15 minutes): a new streak, not a third read
        r.Now += TimeSpan.FromMinutes(16); r.Bad();
        Assert.Empty(r.Decisions); Assert.Equal(1, r.S.SuspectReads);
        // a degraded read neither counts nor ends the streak
        r.Now += TimeSpan.FromMinutes(1); r.Bad("Degraded");
        Assert.Equal(1, r.S.SuspectReads);
    }

    [Fact]
    public void WhileADeploymentRuns_TheReadsAndTheAlertsAreSuppressed_AndItsEndLiftsTheSuppression()
    {
        var r = new IncidentRig();
        r.Bad(); r.Now += TimeSpan.FromMinutes(1);
        r.Deploy("1.0.2", "Started");
        Assert.Equal(0, r.S.SuspectReads);   // a deployment explains an unhealthy app: the streak starts again
        r.Now += TimeSpan.FromMinutes(1); r.Bad(); r.Bad(); r.Bad(); r.Bad();
        Assert.Empty(r.Decisions);
        Assert.Equal(IncidentOutcome.Ignored, r.Alert().Outcome);
        r.Now += TimeSpan.FromMinutes(1); r.Deploy("1.0.2", "Succeeded");
        Assert.False(r.S.Deploying); Assert.Equal("1.0.2", r.S.Release);
        Assert.Equal(IncidentOutcome.Applied, r.Alert().Outcome);
        Assert.Equal(IncidentActions.Detected, r.Decisions[0].Action);
    }

    [Fact]
    public void ADeploymentWhoseEndWasLost_StopsSuppressing_AfterItsMaximumAge()
    {
        var r = new IncidentRig();
        r.Deploy("1.0.2", "Started");
        r.Now += TimeSpan.FromMinutes(59);
        Assert.Equal(IncidentOutcome.Ignored, r.Alert().Outcome);
        r.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(IncidentOutcome.Applied, r.Alert("a2").Outcome);
        Assert.Equal(1, r.S.Generation);
    }

    [Fact]
    public void AnAlert_OpensAnIncident_AndFurtherSignalsJoinIt()
    {
        var r = new IncidentRig();
        var open = r.Alert();
        Assert.Equal([IncidentActions.Detected, IncidentActions.Triaging], open.Decisions.Select(d => d.Action));
        Assert.Equal("alert", r.S.Trigger);
        Assert.Contains("alert http-5xx (Sev1): errors above the threshold", open.Decisions[0].Reasons);
        r.Now += TimeSpan.FromMinutes(1);
        var again = r.Alert("a2", "latency");
        Assert.Equal(IncidentActions.Joined, Assert.Single(again.Decisions).Action);
        Assert.Equal(1, r.S.Generation); Assert.Equal(1, r.S.Joined);
        Assert.Empty(again.Messages.OfType<OpenIncidentIssue>());
        // the same alert delivered again does nothing
        Assert.Equal(IncidentOutcome.Duplicate, r.Alert("a2", "latency").Outcome);
        Assert.Equal(1, r.S.Joined);
    }

    [Fact]
    public void ADeploymentThatFailed_OpensAnIncident_OnlyWhereThePolicySaysSo_AndNeverRollsBack()
    {
        var off = new IncidentRig(failureOpens: false);
        off.Deploy("1.0.3", "Started"); off.Now += TimeSpan.FromMinutes(1); off.Deploy("1.0.3", "Failed");
        Assert.Null(off.State?.IsOpen == true ? off.State : null);
        Assert.Empty(off.Decisions);

        var on = new IncidentRig(failureOpens: true);
        on.Deployed("1.0.2", 5);   // a release that would correlate with a failure
        on.Deploy("1.0.3", "Started"); on.Now += TimeSpan.FromMinutes(1);
        var result = on.Deploy("1.0.3", "Failed");
        Assert.Equal("deployment-failed", on.S.Trigger);
        Assert.Equal(IncidentStatus.Triaging, on.S.Status);   // (the failed release never ran: there is nothing to roll back)
        Assert.Empty(result.Messages.OfType<TrainRollback>());
        // an old failure the watcher replays after a restart is no news
        var replay = new IncidentRig(failureOpens: true);
        replay.Now = IncidentRig.T0.AddHours(5);
        replay.Send(new DeploymentObserved(IncidentRig.Key, "D-old", "1.0.0", "Failed", IncidentRig.T0));
        Assert.Null(replay.State?.IsOpen == true ? replay.State : null);
    }

    // ---- the rollback policy ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AReleaseDeployedShortlyBefore_IsRolledBack_WhenThePolicyIsAutomatic()
    {
        var r = new IncidentRig();
        r.Deployed("1.0.2", 10);
        var result = r.Alert();
        Assert.Equal([IncidentActions.Detected, IncidentActions.RollingBack], result.Decisions.Select(d => d.Action));
        Assert.Equal("release 1.0.2 was deployed 10 minutes before the incident", result.Decisions[1].Reasons.Single());
        Assert.Equal(IncidentStatus.RollingBack, r.S.Status); Assert.True(r.S.RollbackTried);
        var rollback = Assert.Single(result.Messages.OfType<TrainRollback>());
        Assert.Equal(("orders/tdd", "orders/tdd#1", 1, (string?)null), (rollback.Key, rollback.IncidentId, rollback.Generation, rollback.ToRelease));
        Assert.Contains("1.0.2", rollback.Reason);
        Assert.Equal("rollback", Assert.Single(result.Timers).Message.Stage);
        Assert.Empty(result.Messages.OfType<TriageRequested>());
        Assert.True(Assert.Single(result.Messages.OfType<TrainHold>()).Held);   // (the hold is on from the first moment; the rollback passes it)
    }

    [Fact]
    public void ARelease_DeployedLongBefore_DoesNotCorrelate()
    {
        var r = new IncidentRig();
        r.Deployed("1.0.2", 90);
        var result = r.Alert();
        Assert.Equal(IncidentStatus.Triaging, r.S.Status);
        Assert.Equal("no recent release correlates", result.Decisions[1].Reasons.Single());
        Assert.Empty(result.Messages.OfType<TrainRollback>());
    }

    [Fact]
    public void ASignoffPolicy_AsksThePerson_AndAnOffPolicyFixesOrEscalates()
    {
        var signoff = new IncidentRig(RollbackMode.Signoff);
        signoff.Deployed("1.0.2", 10);
        var result = signoff.Alert();
        Assert.Equal(IncidentStatus.Escalated, signoff.S.Status);
        Assert.Equal([IncidentActions.Detected, IncidentActions.Escalated], result.Decisions.Select(d => d.Action));
        Assert.Contains("needs a person's Sign-off", signoff.S.EscalationReason);
        var comment = Assert.Single(result.Messages.OfType<CommentIncidentIssue>());
        Assert.StartsWith("@owner The factory cannot settle this incident by itself", comment.Body);
        Assert.Empty(result.Messages.OfType<TrainRollback>());

        var off = new IncidentRig(RollbackMode.Off);
        off.Deployed("1.0.2", 10);
        var triage = off.Alert();
        Assert.Equal(IncidentStatus.Triaging, off.S.Status);
        Assert.Equal("release 1.0.2 correlates, but the rollback policy of tdd is off", triage.Decisions[1].Reasons.Single());
    }

    [Fact]
    public void TheRollback_FollowsTheTrainsAnswers()
    {
        var r = new IncidentRig();
        r.Deployed(); r.Alert();
        var started = r.Rollback("started", to: "1.0.1");
        Assert.Equal(IncidentActions.RollbackStarted, Assert.Single(started.Decisions).Action);
        Assert.Equal("1.0.1", r.S.RollbackTo);
        // Octopus shows the previous release running again
        r.Now += TimeSpan.FromMinutes(2);
        var done = r.Deploy("1.0.1", "Succeeded", "D-rollback");
        Assert.Equal(IncidentStatus.Verifying, r.S.Status);
        Assert.Equal(IncidentActions.Verifying, done.Decisions.Single().Action);
        Assert.Equal("1.0.1", r.S.Release); Assert.Equal("1.0.2", r.S.PreviousRelease);
        Assert.Equal("verify", Assert.Single(done.Timers).Message.Stage);
    }

    [Fact]
    public void TheTrainsDone_IsEnough_WithoutTheWatcher()
    {
        var r = new IncidentRig();
        r.Deployed(); r.Alert();
        r.Rollback("done", to: "1.0.1");
        Assert.Equal(IncidentStatus.Verifying, r.S.Status);
        Assert.Equal("1.0.1", r.S.RollbackTo);
    }

    [Fact]
    public void ARollbackRefusedForAMigration_IsFixedForward_AnyOtherRefusalAndAFailureGoToTheOwner()
    {
        var migration = new IncidentRig();
        migration.Deployed(); migration.Alert();
        var fixing = migration.Rollback("refused", "migration", reason: "a migration was added");
        Assert.Equal(IncidentStatus.Fixing, migration.S.Status);
        var hotfix = Assert.Single(fixing.Messages.OfType<CreateHotfixItem>());
        Assert.Equal(("orders/tdd", 1), (hotfix.Key, hotfix.Generation));
        Assert.Contains("Why a fix and not a rollback: a migration was added since the previous release", hotfix.Body);
        Assert.Equal("fix", Assert.Single(fixing.Timers).Message.Stage);

        foreach (var (outcome, code) in new[] { ("refused", "unavailable"), ("refused", "no-previous-release"), ("failed", "deployment") })
        {
            var r = new IncidentRig();
            r.Deployed(); r.Alert();
            r.Rollback(outcome, code, reason: "x");
            Assert.Equal(IncidentStatus.Escalated, r.S.Status);
            Assert.Contains($"({code})", r.S.EscalationReason);
        }
    }

    [Fact]
    public void ARollbackDeploymentThatFails_GoesToTheOwner()
    {
        var r = new IncidentRig();
        r.Deployed(); r.Alert();
        r.Rollback("started", to: "1.0.1");
        r.Now += TimeSpan.FromMinutes(1);
        r.Deploy("1.0.1", "Failed", "D-rollback");
        Assert.Equal(IncidentStatus.Escalated, r.S.Status);
        Assert.Equal("the deployment of release 1.0.1 (the rollback) failed", r.S.EscalationReason);
    }

    // ---- triage, the hotfix and the owner ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATriageVerdict_ChoosesTheStage()
    {
        var fix = new IncidentRig(); fix.Alert();
        var fixing = fix.Triage("fix-forward");
        Assert.Equal(IncidentStatus.Fixing, fix.S.Status);
        Assert.Single(fixing.Messages.OfType<CreateHotfixItem>());

        var rollback = new IncidentRig(); rollback.Alert();
        var rolling = rollback.Triage("rollback");
        Assert.Equal(IncidentStatus.RollingBack, rollback.S.Status);
        Assert.Single(rolling.Messages.OfType<TrainRollback>());
        // (a second rollback is not tried: the owner decides)
        var again = new IncidentRig(); again.Alert(); again.Triage("rollback"); again.Rollback("done", to: "1.0.1"); again.Alert("a9");
        Assert.Equal(IncidentStatus.Triaging, again.S.Status);   // (the alert in verifying: a relapse)
        again.Triage("rollback");
        Assert.Equal(IncidentStatus.Escalated, again.S.Status);
        Assert.Contains("one was tried already", again.S.EscalationReason);

        var escalate = new IncidentRig(); escalate.Alert(); escalate.Triage("escalate", "unclear");
        Assert.Equal(IncidentStatus.Escalated, escalate.S.Status);

        var close = new IncidentRig(); close.Alert();
        var closed = close.Triage("close", "a test alert");
        Assert.Equal(IncidentStatus.Quiet, close.S.Status);
        Assert.Equal(IncidentActions.Resolved, closed.Decisions.Single().Action);
        Assert.False(Assert.Single(closed.Messages.OfType<TrainHold>()).Held);

        Assert.Equal(IncidentOutcome.Refused, new IncidentRig().Also(x => x.Alert()).Triage("maybe").Outcome);
        Assert.Equal(IncidentOutcome.Stale, fix.Triage("escalate").Outcome);   // (the incident is past its triage)
    }

    [Fact]
    public void ARollbackVerdict_FollowsThePolicyOfTheEnvironment()
    {
        var signoff = new IncidentRig(RollbackMode.Signoff); signoff.Alert(); signoff.Triage("rollback");
        Assert.Equal(IncidentStatus.Escalated, signoff.S.Status); Assert.Contains("needs a person's Sign-off", signoff.S.EscalationReason);
        var off = new IncidentRig(RollbackMode.Off); off.Alert(); off.Triage("rollback");
        Assert.Equal(IncidentStatus.Escalated, off.S.Status); Assert.Contains("the rollback policy of tdd is off", off.S.EscalationReason);
    }

    [Fact]
    public void TheHotfix_IsHeldFor_ThenFollowedToItsDelivery_AndVerified()
    {
        var r = new IncidentRig(); r.Alert(); r.Triage("fix-forward");
        var created = r.Send(new HotfixCreated(IncidentRig.Key, 1, "acme/Work#42"));
        Assert.Equal("acme/work#42", r.S.HotfixItem);
        var hold = Assert.Single(created.Messages.OfType<TrainHold>());
        Assert.True(hold.Held);
        Assert.Equal(["acme/work#42"], hold.Allow);
        Assert.Equal(IncidentActions.HotfixCreated, Assert.Single(created.Decisions).Action);
        r.Hotfix("merged");
        Assert.True(r.S.HotfixMerged);
        var sequence = r.S.Sequence;
        Assert.Equal(IncidentOutcome.Duplicate, r.Hotfix("merged").Outcome);   // (a repeat is the same fact: it publishes the decision again, it makes no new one)
        Assert.Equal(sequence, r.S.Sequence);
        var delivered = r.Hotfix("delivered", release: "1.0.3");
        Assert.Equal(IncidentStatus.Verifying, r.S.Status);
        Assert.Equal(("1.0.3", true), (r.S.HotfixRelease, r.S.HotfixDelivered));
        Assert.Contains("hotfix acme/work#42 delivered in release 1.0.3", delivered.Decisions.Single().Reasons.Single());
    }

    [Fact]
    public void AHotfixThatIsParkedOrHandedBack_GoesToTheOwner_AndAnotherItemIsNoneOfOurs()
    {
        var r = new IncidentRig(); r.Alert(); r.Triage("fix-forward"); r.Send(new HotfixCreated(IncidentRig.Key, 1, "acme/work#42"));
        Assert.Equal(IncidentOutcome.Stale, r.Hotfix("delivered", "acme/work#43").Outcome);
        r.Hotfix("parked");
        Assert.Equal(IncidentStatus.Escalated, r.S.Status);
        Assert.Equal("the hotfix acme/work#42 is parked", r.S.EscalationReason);
        // the owner says fix forward again: the item exists and is not delivered, so it is the one that is held for, no second item
        var again = r.Owner("fix-forward");
        Assert.Empty(again.Messages.OfType<CreateHotfixItem>());
        Assert.Equal(["acme/work#42"], Assert.Single(again.Messages.OfType<TrainHold>()).Allow);
    }

    [Fact]
    public void TheOwnerDecides_FromEscalated_AndOnlyThere()
    {
        var r = new IncidentRig(RollbackMode.Off); r.Alert();
        Assert.Equal(IncidentOutcome.Refused, r.Owner("close").Outcome);
        r.Triage("escalate", "unclear");
        var rollback = r.Owner("rollback");
        Assert.Equal(IncidentStatus.RollingBack, r.S.Status);   // (the owner's word overrides the policy)
        Assert.Single(rollback.Messages.OfType<TrainRollback>());

        var fixedByHand = new IncidentRig(); fixedByHand.Alert(); fixedByHand.Triage("escalate"); fixedByHand.Owner("fixed");
        Assert.Equal(IncidentStatus.Verifying, fixedByHand.S.Status);

        var close = new IncidentRig(); close.Alert(); close.Triage("escalate");
        var closed = close.Owner("close");
        Assert.Equal(IncidentStatus.Quiet, close.S.Status);
        Assert.Contains("closed by jeffrey", closed.Decisions.Single().Reasons.Single());

        var fix = new IncidentRig(); fix.Alert(); fix.Triage("escalate");
        Assert.Single(fix.Owner("fix-forward").Messages.OfType<CreateHotfixItem>());
        Assert.Equal(IncidentOutcome.Refused, new IncidentRig().Also(x => { x.Alert(); x.Triage("escalate"); }).Owner("dance").Outcome);
    }

    // ---- verifying and resolving -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TenMinutesOfHealthyReads_ResolveTheIncident_CloseTheIssueWithItsStory_AndReleaseTheHold()
    {
        var r = new IncidentRig();
        r.Deployed(); r.Alert();
        r.Send(new IssueOpened(IncidentRig.Key, 1, 77));
        r.Rollback("done", to: "1.0.1");
        r.Now += TimeSpan.FromMinutes(1);
        r.Good();
        r.Now += TimeSpan.FromMinutes(5);
        Assert.Empty(r.Good().Decisions);
        r.Now += TimeSpan.FromMinutes(5);
        var resolved = r.Good();
        Assert.Equal(IncidentActions.Resolved, Assert.Single(resolved.Decisions).Action);
        Assert.Equal(IncidentStatus.Quiet, r.S.Status);
        var close = Assert.Single(resolved.Messages.OfType<CloseIncidentIssue>());
        Assert.Equal((77, "orders/tdd", 1), (close.Number, close.Key, close.Generation));
        Assert.Contains("Incident orders/tdd#1 is resolved after 11 minutes", close.Note);
        Assert.Contains("detected: alert", close.Note); Assert.Contains("rolling-back", close.Note); Assert.Contains("verifying", close.Note); Assert.Contains("resolved: Healthy since", close.Note);
        var hold = Assert.Single(resolved.Messages.OfType<TrainHold>());
        Assert.False(hold.Held); Assert.Equal("", hold.IncidentId);
        Assert.Empty(resolved.Timers);
        Assert.Equal("", r.S.TimerStage);
        // the pair is quiet: the next bad read starts a streak, not an incident
        r.Now += TimeSpan.FromMinutes(1); r.Bad();
        Assert.Equal(1, r.S.SuspectReads);
    }

    [Fact]
    public void ABadRead_InTheVerification_StartsTheWindowAgain_AndAStreakOfThemIsARelapse()
    {
        var r = new IncidentRig(); r.Alert(); r.Triage("escalate"); r.Owner("fixed");
        r.Good(); r.Now += TimeSpan.FromMinutes(5); r.Bad();
        Assert.Null(r.S.HealthySince); Assert.Equal(1, r.S.BadReads);
        r.Now += TimeSpan.FromMinutes(1); r.Good();
        Assert.Equal(0, r.S.BadReads);
        r.Now += TimeSpan.FromMinutes(1); r.Bad(); r.Bad();
        var relapse = r.Bad();
        Assert.Equal([IncidentActions.Relapsed, IncidentActions.Triaging], relapse.Decisions.Select(d => d.Action));
        Assert.Equal(IncidentStatus.Triaging, r.S.Status); Assert.Equal(1, r.S.Relapses);
        Assert.Single(relapse.Messages.OfType<TriageRequested>());
        Assert.Equal(1, r.S.Generation);   // (the same incident)
    }

    [Fact]
    public void AnIncidentThatKeepsComingBack_GoesToTheOwner()
    {
        var r = new IncidentRig(); r.Alert();
        for (var i = 0; i < 3; i++)
        {
            r.Triage("escalate"); r.Owner("fixed");
            r.Alert("again-" + i);   // an alert while verifying is a relapse at once
        }
        Assert.Equal(IncidentStatus.Escalated, r.S.Status);
        Assert.Equal(3, r.S.Relapses);
        Assert.Contains("the incident came back 3 times", r.S.EscalationReason);
    }

    // ---- the timeouts --------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryStage_HasATimeout_ThatEscalates()
    {
        var triage = new IncidentRig(); triage.Alert();
        var fired = triage.Fire(triage.LastTimer);
        Assert.Equal(IncidentStatus.Escalated, triage.S.Status);
        Assert.Equal("no triage verdict within 30 minutes", triage.S.EscalationReason);
        Assert.StartsWith("@owner ", Assert.Single(fired.Messages.OfType<CommentIncidentIssue>()).Body);
        Assert.Empty(fired.Timers);

        var rollback = new IncidentRig(); rollback.Deployed(); rollback.Alert();
        rollback.Fire(rollback.LastTimer);
        Assert.Equal("the rollback did not finish within 30 minutes", rollback.S.EscalationReason);

        var fix = new IncidentRig(); fix.Alert(); fix.Triage("fix-forward");
        fix.Fire(fix.LastTimer);
        Assert.Equal("the hotfix was not delivered within 240 minutes", fix.S.EscalationReason);

        var verify = new IncidentRig(); verify.Alert(); verify.Triage("escalate"); verify.Owner("fixed");
        verify.Fire(verify.LastTimer);
        Assert.Equal("health did not stay at baseline for 10 minutes within 60 minutes", verify.S.EscalationReason);
    }

    [Fact]
    public void ATimerThatIsNotThePendingOne_IsStale_AndAFiredTimerIsAppliedOnce()
    {
        var r = new IncidentRig(); r.Alert();
        var first = r.LastTimer;
        r.Triage("fix-forward");   // the stage moved on: a new timer, the triage one is stale
        Assert.Equal(IncidentOutcome.Stale, r.Fire(first).Outcome);
        Assert.Equal(IncidentStatus.Fixing, r.S.Status);
        var wrongDue = new IncidentTimerFired(IncidentRig.Key, 1, "fix", r.LastTimer.Due.AddMinutes(1));
        Assert.Equal(IncidentOutcome.Stale, r.Send(wrongDue).Outcome);
        var fix = r.LastTimer;
        Assert.Equal(IncidentOutcome.Applied, r.Fire(fix).Outcome);
        Assert.Equal(IncidentOutcome.Duplicate, r.Send(fix.Message).Outcome);   // (a redelivery of the timer that was applied last: published again, changes nothing)
        Assert.Equal(IncidentStatus.Escalated, r.S.Status);
        var late = r.Send(new IncidentTimerFired(IncidentRig.Key, 1, "fix", fix.Due));
        Assert.Equal(IncidentOutcome.Duplicate, late.Outcome);
    }

    // ---- repeats and the pair -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARedeliveryOfTheLastInput_PublishesTheSameAgain_AnOlderOneNothing()
    {
        var r = new IncidentRig();
        r.Deployed();
        var first = r.Alert();
        var stateBefore = r.S.ToJson();
        var again = r.Send(new AlertReceived(IncidentRig.Key, "a1", "http-5xx", "Sev1", "errors above the threshold", r.Now));
        Assert.Equal(IncidentOutcome.Duplicate, again.Outcome);
        Assert.Equal(first.Decisions, again.Decisions);
        Assert.Equal(first.Messages.Select(m => m.GetType()), again.Messages.Select(m => m.GetType()));
        Assert.Equal(first.Timers.Select(t => (t.Message, t.Due)), again.Timers.Select(t => (t.Message, t.Due)));
        Assert.Equal(stateBefore, r.S.ToJson());
        // another input has been applied since: the alert's key is among the applied ones, and nothing is published
        r.Now += TimeSpan.FromMinutes(1); r.Send(new AlertReceived(IncidentRig.Key, "a2", "x", "Sev2", "y", r.Now));
        var old = r.Send(new AlertReceived(IncidentRig.Key, "a1", "http-5xx", "Sev1", "errors above the threshold", r.Now));
        Assert.Equal(IncidentOutcome.Duplicate, old.Outcome);
        Assert.Empty(old.Decisions); Assert.Empty(old.Messages);
    }

    [Fact]
    public void AnAnswerOfAnotherIncident_IsStale_AndAnEnvironmentThatIsNotWatched_IsIgnored()
    {
        var r = new IncidentRig(); r.Alert();
        Assert.Equal(IncidentOutcome.Stale, r.Send(new TriageReported(IncidentRig.Key, 7, "x", "escalate", "")).Outcome);
        Assert.Equal(IncidentOutcome.Stale, r.Send(new RollbackReported(IncidentRig.Key, 7, "done", "", null, "", r.Now)).Outcome);
        Assert.Equal(IncidentOutcome.Ignored, r.Send(new AlertReceived("orders/prod", "p1", "rule", "Sev1", "x", r.Now)).Outcome);
        Assert.Equal(IncidentOutcome.Refused, r.Send(new AlertReceived("orders", "p1", "rule", "Sev1", "x", r.Now)).Outcome);
    }

    [Fact]
    public void ASecondIncident_OfThePair_IsTheNextGeneration_WithNothingLeftOfTheFirst()
    {
        var r = new IncidentRig();
        r.Alert(); r.Triage("fix-forward"); r.Send(new HotfixCreated(IncidentRig.Key, 1, "acme/work#42")); r.Hotfix("delivered", release: "1.0.3");
        r.Heal();
        Assert.Equal(IncidentStatus.Quiet, r.S.Status);
        Assert.Equal("", r.S.HotfixItem);
        r.Now += TimeSpan.FromHours(3);
        var second = r.Alert("a-second");
        Assert.Equal(2, r.S.Generation);
        Assert.Equal(("orders/tdd", 2), (Assert.Single(second.Messages.OfType<OpenIncidentIssue>()).Key, r.S.Generation));
        Assert.False(r.S.RollbackTried); Assert.False(r.S.HotfixDelivered); Assert.Equal(0, r.S.Relapses);
        Assert.DoesNotContain(r.S.Timeline, l => l.Contains("hotfix"));
        // the sequence of the decisions goes on across incidents
        Assert.True(second.Decisions[0].Sequence > 6);
        // and the answer to the first incident's issue is stale
        Assert.Equal(IncidentOutcome.Stale, r.Send(new IssueOpened(IncidentRig.Key, 1, 5)).Outcome);
    }

    [Fact]
    public void TheIssueNumber_IsUsedByTheLaterCommands()
    {
        var r = new IncidentRig(); r.Alert();
        r.Send(new IssueOpened(IncidentRig.Key, 1, 77));
        Assert.Equal(77, r.S.IssueNumber);
        var fired = r.Fire(r.LastTimer);
        Assert.Equal(77, Assert.Single(fired.Messages.OfType<CommentIncidentIssue>()).Number);
    }

    [Fact]
    public void TheStateRoundTripsThroughJson_EffectsIncluded()
    {
        var r = new IncidentRig(); r.Deployed(); r.Alert();
        var back = IncidentState.FromJson(r.S.ToJson());
        Assert.Equal(r.S.ToJson(), back.ToJson());
        Assert.Equal(r.S.Outbox.Select(e => e.GetType()), back.Outbox.Select(e => e.GetType()));
        Assert.Equal(IncidentStatus.RollingBack, back.Status);
        Assert.Null(new IncidentState().HotfixColumn);
        Assert.Equal("incident:orders/tdd", IncidentState.HistoryKey("orders/tdd"));
    }
}

static class RigExtensions
{
    public static IncidentRig Also(this IncidentRig r, Action<IncidentRig> act) { act(r); return r; }
}
