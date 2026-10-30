using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Incident;
using Fworker.Workflows.WorkItem;
using Xunit;
using DeliveredToMessage = Fworker.Contracts.Messaging.DeliveredTo;

namespace Fworker.Workflow.Tests;

/// <summary>W8.1: the incident saga in a host without a database (the service and its store) and the router that turns facts into the incidents' inputs.</summary>
public class IncidentServiceTests
{
    static readonly DateTimeOffset T0 = IncidentRig.T0;
    const string Key = IncidentRig.Key;

    static IncidentSettings Settings() => new(new Dictionary<string, IncidentEnvironment> { ["tdd"] = new("tdd", RollbackMode.Automatic, false) });

    sealed class Rig
    {
        public readonly InMemoryIncidentStore Store = new();
        public readonly IncidentService Service;
        public DateTimeOffset Now = T0;
        public Rig() => Service = new IncidentService(Store, new IncidentSaga(Settings()), () => Now);
    }

    static AlertReceived Alert(string id = "a1", DateTimeOffset? at = null) => new(Key, id, "http-5xx", "Sev1", "errors", at ?? T0);

    [Fact]
    public async Task AnInput_IsAppliedAndSaved_WithTheRevisionItWasReadAt()
    {
        var rig = new Rig();
        var r = await rig.Service.Handle(Alert());
        Assert.Equal(IncidentOutcome.Applied, r.Outcome);
        Assert.Equal(1, rig.Store.States[Key].Generation);
        Assert.Equal(1, (await rig.Store.Load(Key, default))!.Revision);
        await rig.Service.Handle(Alert("a2"));
        Assert.Equal(2, (await rig.Store.Load(Key, default))!.Revision);
    }

    [Fact]
    public async Task AnInputThatChangesNothing_IsNotSaved()
    {
        var rig = new Rig();
        await rig.Service.Handle(Alert());
        var health = new HealthObserved(Key, "Healthy", "", T0.AddMinutes(1));   // (the incident is in its triage: a good read adds nothing)
        var r = await rig.Service.Handle(health);
        Assert.Equal(IncidentOutcome.Applied, r.Outcome);
        Assert.Equal(1, (await rig.Store.Load(Key, default))!.Revision);
        // and a quiet pair keeps no row for it
        var quiet = new Rig();
        await quiet.Service.Handle(health);
        Assert.Null(await quiet.Store.Load(Key, default));
    }

    [Fact]
    public async Task AWriterThatLostTheRevision_AppliesTheInputAgainOnTheStateThatWon()
    {
        var rig = new Rig();
        await rig.Service.Handle(Alert());
        var moved = false;
        rig.Store.BeforeSave = key =>
        {
            if (moved) return;
            moved = true;
            // another writer was first: it recorded an alert of its own
            var rec = rig.Store.Load(key, default).Result!;
            var saga = new IncidentSaga(Settings());
            var t = saga.Apply(rec.State, Alert("a-other", T0.AddMinutes(1)), T0.AddMinutes(1));
            Assert.True(rig.Store.Save(key, t.State!, rec.Revision, default).Result);
        };
        var r = await rig.Service.Handle(Alert("a-mine", T0.AddMinutes(2)));
        Assert.Equal(IncidentOutcome.Applied, r.Outcome);
        Assert.Equal(2, rig.Store.States[Key].Joined);   // both alerts joined: mine was applied on the state that won
        Assert.Equal(3, (await rig.Store.Load(Key, default))!.Revision);
    }

    [Fact]
    public async Task AWriterThatKeepsLosing_FailsWithAConflict_ForWolverineToRetry()
    {
        var rig = new Rig();
        await rig.Service.Handle(Alert());
        var n = 0;
        rig.Store.BeforeSave = key =>
        {
            // every attempt loses: another writer saves first, and the hook is off while it does
            var hook = rig.Store.BeforeSave; rig.Store.BeforeSave = null;
            var rec = rig.Store.Load(key, default).Result!;
            var t = new IncidentSaga(Settings()).Apply(rec.State, Alert("a-" + ++n, T0.AddMinutes(n)), T0.AddMinutes(n));
            rig.Store.Save(key, t.State!, rec.Revision, default).Wait();
            rig.Store.BeforeSave = hook;
        };
        await Assert.ThrowsAsync<IncidentConflictException>(() => rig.Service.Handle(Alert("a-mine")));
        Assert.Equal(5, n);   // (the service's five attempts)
    }

    [Fact]
    public async Task TheStoreFindsTheIncident_OfAHotfixItem_WhileItIsOpen()
    {
        var rig = new Rig();
        await rig.Service.Handle(Alert());
        await rig.Service.Handle(new TriageReported(Key, 1, "t", "fix-forward", "x"));
        await rig.Service.Handle(new HotfixCreated(Key, 1, "acme/work#42"));
        Assert.Equal([Key], await rig.Store.OpenOfHotfix("acme/work#42", default));
        Assert.Empty(await rig.Store.OpenOfHotfix("acme/work#43", default));
        Assert.Equal([Key], (await rig.Store.Open(default)).Select(s => s.Key));
        await rig.Service.Handle(new OwnerDecided(Key, 1, "o", "close", "x", ""));   // (refused: it is not escalated)
        await rig.Service.Handle(new TriageReported(Key, 1, "t2", "close", "x"));    // (stale: past its triage)
        Assert.Equal([Key], await rig.Store.OpenOfHotfix("acme/work#42", default));
    }

    // ---- the router ----------------------------------------------------------------------------------------------------------------------------------------

    static IncidentRouter Router(IIncidentStore store, DateTimeOffset now, IncidentSettings? settings = null)
        => new(settings ?? Settings(), new TrainFixture().Policies, store, () => now);

    [Fact]
    public void ADeploymentEvent_ReachesTheIncidentOfEachWatchedEnvironmentItNames()
    {
        var router = Router(new InMemoryIncidentStore(), T0.AddMinutes(10));
        var routed = router.Route(new OctopusDeploymentChanged("D1", "T1", "Releases-1.0.2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, null, T0));
        var input = Assert.IsType<DeploymentObserved>(Assert.Single(routed));
        Assert.Equal(("orders/tdd", "D1", "1.0.2", "Succeeded", T0), (input.Key, input.DeploymentId, input.Release, input.Phase, input.At));
        // uat is not watched, and a name that is no environment of the chain is none
        Assert.Empty(router.Route(new OctopusDeploymentChanged("D2", "T2", "Releases-1.0.2", "1.0.2", "Uat", OctopusDeploymentPhase.Started, null, T0)));
        Assert.Empty(router.Route(new OctopusDeploymentChanged("D3", "T3", "Releases-1.0.2", "1.0.2", "Qa", OctopusDeploymentPhase.Started, null, T0)));
        // an event that carries no time, or one ahead of the clock, is stamped with the clock
        Assert.Equal(T0.AddMinutes(10), Assert.Single(router.Route(new OctopusDeploymentChanged("D4", "T4", "r", "1.0.3", "tdd", OctopusDeploymentPhase.Started, null))).Let(i => ((DeploymentObserved)i).At));
        Assert.Equal(T0.AddMinutes(10), Assert.Single(router.Route(new OctopusDeploymentChanged("D5", "T5", "r", "1.0.3", "tdd", OctopusDeploymentPhase.Started, null, T0.AddHours(3)))).Let(i => ((DeploymentObserved)i).At));
        // with no environment watched nothing is routed
        Assert.Empty(Router(new InMemoryIncidentStore(), T0, IncidentSettings.None).Route(new OctopusDeploymentChanged("D6", "T6", "r", "1.0.3", "tdd", OctopusDeploymentPhase.Started, null, T0)));
    }

    [Fact]
    public async Task TheDeliveryOfTheHotfixItem_ReachesItsIncident_ButOnlyForTheIncidentsEnvironment()
    {
        var rig = new Rig();
        await rig.Service.Handle(Alert());
        await rig.Service.Handle(new TriageReported(Key, 1, "t", "fix-forward", "x"));
        await rig.Service.Handle(new HotfixCreated(Key, 1, "acme/orders#42"));
        var router = Router(rig.Store, T0.AddHours(1));
        var routed = await router.Route(new DeliveredToMessage(ItemId.Parse("acme/orders#42"), "tdd", "1.0.3"), default);
        var progress = Assert.IsType<HotfixProgress>(Assert.Single(routed));
        Assert.Equal((Key, "acme/orders#42", "delivered", "1.0.3"), (progress.Key, progress.Item, progress.Stage, progress.Release));
        Assert.Empty(await router.Route(new DeliveredToMessage(ItemId.Parse("acme/orders#42"), "uat", "1.0.3"), default));
        Assert.Empty(await router.Route(new DeliveredToMessage(ItemId.Parse("acme/orders#7"), "tdd", "1.0.3"), default));
    }

    [Fact]
    public async Task TheDecisionsOfAHotfixItemsSaga_ReachItsIncident_TheOthersDoNot()
    {
        var rig = new Rig();
        await rig.Service.Handle(Alert());
        await rig.Service.Handle(new TriageReported(Key, 1, "t", "fix-forward", "x"));
        await rig.Service.Handle(new HotfixCreated(Key, 1, "acme/orders#42"));
        var router = Router(rig.Store, T0.AddHours(1));
        var item = ItemId.Parse("acme/orders#42");
        WorkItemDecided Decided(string action, string plan) => new(item, 1, 3, action, plan, "Merge", "1.2", [], T0.AddMinutes(5));
        Assert.Equal("merged", Assert.IsType<HotfixProgress>(Assert.Single(await router.Route(Decided(WorkItemActions.Merged, "hotfix"), default))).Stage);
        Assert.Equal("parked", Assert.IsType<HotfixProgress>(Assert.Single(await router.Route(Decided(WorkItemActions.Parked, "hotfix"), default))).Stage);
        Assert.Equal("handed-back", Assert.IsType<HotfixProgress>(Assert.Single(await router.Route(Decided(WorkItemActions.HandedBack, "hotfix"), default))).Stage);
        Assert.Empty(await router.Route(Decided(WorkItemActions.StepEntered, "hotfix"), default));
        Assert.Empty(await router.Route(Decided(WorkItemActions.Merged, "feature"), default));
        var other = new WorkItemDecided(ItemId.Parse("acme/orders#9"), 1, 3, WorkItemActions.Merged, "hotfix", "Merge", "1.2", [], T0);
        Assert.Empty(await router.Route(other, default));
    }

    [Fact]
    public void TheTrainsAnswer_BecomesTheRollbackReportOfItsIncident()
    {
        var at = T0.AddMinutes(3);
        var input = IncidentRouter.Route(new RollbackAnswered(Key, "orders/tdd#2", 2, "refused", "migration", "1.0.1", "a migration", at));
        var report = Assert.IsType<RollbackReported>(input);
        Assert.Equal((Key, 2, "refused", "migration", "1.0.1", "a migration", at), (report.Key, report.Generation, report.Outcome, report.Code, report.ToRelease, report.Reason, report.At));
    }

    [Fact]
    public void EveryInputHasAHandler_AndEveryCommandOfTheSaga_AFirstClassRoute()
    {
        var handled = typeof(IncidentHandlers).GetMethods().Where(m => m.Name == "Handle").Select(m => m.GetParameters()[0].ParameterType).ToHashSet();
        Assert.All(IncidentTopology.Inputs, t => Assert.Contains(t, handled));
        var issue = typeof(IncidentIssueHandlers).GetMethods().Where(m => m.Name == "Handle").Select(m => m.GetParameters()[0].ParameterType).ToHashSet();
        Assert.All(IncidentTopology.IssueCommands, t => Assert.Contains(t, issue));
        Assert.Equal(issue.OrderBy(t => t.Name), IncidentTopology.IssueCommands.OrderBy(t => t.Name));
        // every input names a stable identity, the messages of a saga that can be sent a second time
        Assert.All(IncidentTopology.Inputs, t => Assert.NotNull(Fworker.Contracts.Messaging.MessageIdentity.NameOf(t)));
        Assert.All(IncidentTopology.IssueCommands, t => Assert.NotNull(Fworker.Contracts.Messaging.MessageIdentity.NameOf(t)));
    }

    [Fact]
    public void TheMessagesOfTheIncident_HaveStableIdentities_NoTwoTheSame()
    {
        var types = IncidentTopology.Inputs.Concat(IncidentTopology.IssueCommands)
            .Concat([typeof(IncidentDecided), typeof(TriageRequested), typeof(TrainHold), typeof(TrainRollback), typeof(RollbackAnswered)]).ToList();
        var names = types.Select(Fworker.Contracts.Messaging.MessageIdentity.NameOf).ToList();
        Assert.All(names, Assert.NotNull);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("incident.alert", names); Assert.Contains("delivery.train.rollback", names);
        // none of them clashes with a message of the other sagas
        var others = typeof(IncidentInput).Assembly.GetTypes().Where(t => !types.Contains(t)).Select(Fworker.Contracts.Messaging.MessageIdentity.NameOf).Where(n => n is not null);
        Assert.Empty(names.Intersect(others));
    }
}

static class LetExtensions
{
    public static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
