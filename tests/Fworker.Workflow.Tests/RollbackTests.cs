using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>The migration precondition's read: the files each range changed; a range nobody set up is a straight line with no migration.</summary>
sealed class FakeMigrationProbe : IMigrationProbe
{
    public readonly Dictionary<(string, string), ChangedFiles?> Answers = [];
    public ChangedFiles? Default = new("ahead", ["src/App/Order.cs"], true);
    public Exception? Fail;
    public int Calls;

    public Task<ChangedFiles?> ChangedBetween(RepoId repository, string fromCommit, string toCommit, CancellationToken ct)
    {
        Calls++;
        if (Fail is not null) throw Fail;
        return Task.FromResult(Answers.TryGetValue((fromCommit, toCommit), out var a) ? a : Default);
    }
}

/// <summary>
/// W8.3: the rollback through the delivery train on fakes: the tdd-only rule, the last good release, the migration precondition (a release whose migration is not backward compatible is escalated, never rolled
/// back), the command (once, sent again, given up), the deployment that is the rollback's, Undelivered, and the items becoming candidates again.
/// </summary>
public class RollbackTests
{
    const string Incident = "orders/tdd#1";

    /// <summary>tdd train; baseline 1.0.1 (commit d) in tdd; item #7 merged as commit a, release 1.0.2 (a) deployed to tdd and delivered; the app runs 1.0.2.</summary>
    static async Task<(TrainFixture F, FakeMigrationProbe Probe)> Delivered(string env = "tdd", bool withProbe = true)
    {
        var probe = new FakeMigrationProbe();
        var f = new TrainFixture(env, migrations: withProbe ? probe : null);
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a');
        f.Now = T0;
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Tdd", "Started");
        f.Now = T0.AddMinutes(2);
        await f.Deploy("1.0.2", "Tdd", "Succeeded");
        f.Now = T0.AddHours(1);
        f.All.Clear();
        return (f, probe);
    }

    static Task<TrainResult> Ask(TrainFixture f, string incident = Incident, string? to = null, string reason = "error rate above baseline")
        => f.Send(new TrainRollback(f.Key, incident, 1, to, reason));

    /// <summary>The answers that end a request (done, refused, failed); "started" is the one that tells the incident the deployment is asked for.</summary>
    static IEnumerable<RollbackAnswered> Finished(TrainFixture f) => f.Of<RollbackAnswered>().Where(a => a.Outcome != "started");

    static Task Rollback(TrainFixture f, string phase, string version = "1.0.1", string id = "D-rollback")
        => f.Send(new TrainDeploymentSeen(f.Key, id, "T-" + id, "Releases-" + version, version, "Tdd", phase, null, f.Now));

    // ---- the happy path ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTddTrain_DeploysTheLastGoodRelease_AndUndeliversTheItemsOfTheRolledBackOne()
    {
        var (f, probe) = await Delivered();
        await Ask(f);
        var cmd = Assert.Single(f.Of<RollbackRelease>());
        Assert.Equal(new RollbackRelease($"RollbackRelease|orders|{Incident}|1.0.1|tdd", "orders", "orders-project", "1.0.1", "tdd", Incident), cmd);
        Assert.Equal(1, probe.Calls);   // (one read of the range from the target's commit up to what runs now)
        Assert.Equal("rollback", f.LastDecision!.Action);
        Assert.Equal(["acme/orders#7"], f.LastDecision.Items);
        Assert.Empty(Finished(f));
        Assert.Empty(f.Of<Undelivered>());
        // the incident is told at once that the deployment is asked for (W8.1: "started"), and only then, when it ended, that it is done
        var started = Assert.Single(f.Of<RollbackAnswered>());
        Assert.Equal((f.Key, Incident, 1, "started", "", "1.0.1"), (started.Key, started.IncidentId, started.Generation, started.Outcome, started.Code, started.ToRelease));

        f.Now += TimeSpan.FromMinutes(1); await Rollback(f, "Started");
        Assert.True(f.State.DeploymentOf("1.0.1", "tdd")!.Rollback);
        Assert.Empty(Finished(f));
        f.Now += TimeSpan.FromMinutes(2); await Rollback(f, "Succeeded");

        var done = Assert.Single(Finished(f));
        Assert.Equal(("done", "", "1.0.1", Incident, 1), (done.Outcome, done.Code, done.ToRelease, done.IncidentId, done.Generation));
        Assert.Contains("1 item(s) are undelivered", done.Reason);
        Assert.Equal(f.Now, done.At);
        Assert.Equal(new Undelivered(new ItemId(Repo, 7), "tdd"), Assert.Single(f.Of<Undelivered>()));
        Assert.Empty(f.Of<DeliveredTo>());   // the rollback delivers nothing
        Assert.Equal("1.0.1", f.State.Current);
        Assert.Equal(["1.0.2"], f.State.RolledBack);
        Assert.Contains("acme/orders#7", f.State.Candidates.Keys);   // the release that fixes forward delivers it again
        Assert.DoesNotContain($"acme/orders#7|{Sha('a')}", f.State.DeliveredItems);
        Assert.Equal(RollbackPhase.Done, f.State.ActiveRollback!.Phase);
        // the same facts again (at-least-once) and a look later change nothing
        await Rollback(f, "Succeeded"); await f.Wake();
        Assert.Single(Finished(f)); Assert.Single(f.Of<Undelivered>()); Assert.Single(f.Of<RollbackRelease>());
    }

    [Fact]
    public async Task TheItemComesBack_WhenTheNextReleaseCarriesItsCommit()
    {
        var (f, _) = await Delivered();
        await Ask(f);
        f.Now += TimeSpan.FromMinutes(1); await Rollback(f, "Started"); f.Now += TimeSpan.FromMinutes(2); await Rollback(f, "Succeeded");
        f.All.Clear();
        // the fix: release 1.0.3 (commit b, after a) goes to tdd; the range from what runs (1.0.1, commit d) carries a again
        f.Range('d', 'a', 'b'); f.Reads.Running["tdd"] = "1.0.3";
        f.Now += TimeSpan.FromMinutes(5);
        await f.Release("1.0.3", 'b'); await f.Deploy("1.0.3", "Tdd", "Started"); f.Now += TimeSpan.FromMinutes(2); await f.Deploy("1.0.3", "Tdd", "Succeeded");
        Assert.Equal(new DeliveredTo(new ItemId(Repo, 7), "tdd", "1.0.3"), Assert.Single(f.Of<DeliveredTo>()));
        Assert.Equal("1.0.3", f.State.Current);
    }

    [Fact]
    public async Task AnOlderDeploymentOfTheTargetSeenAgain_DoesNotTakeThePlaceOfTheRollbacksOwn()
    {
        var (f, _) = await Delivered();
        await Ask(f);
        f.Now += TimeSpan.FromMinutes(1); await Rollback(f, "Started"); f.Now += TimeSpan.FromMinutes(2); await Rollback(f, "Succeeded");
        // the watcher's replay shows the first deployment of 1.0.1 again, from a day ago
        await f.Send(new TrainDeploymentSeen(f.Key, "D-1.0.1-Tdd", "T-1.0.1-Tdd", "Releases-1.0.1", "1.0.1", "Tdd", "Succeeded", null, T0.AddDays(-1)));
        Assert.Equal("D-rollback", f.State.DeploymentOf("1.0.1", "tdd")!.DeploymentId);
        Assert.Equal("1.0.1", f.State.RunningIn("tdd")!.Value.Version);
        Assert.Single(f.Of<Undelivered>());
    }

    // ---- the last good release ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheNamedRelease_IsTheTarget_AndAnUnknownOneIsEscalated()
    {
        var (f, _) = await Delivered();
        await Ask(f, to: "9.9.9");
        Assert.Empty(f.Of<RollbackRelease>());
        var fin = Assert.Single(Finished(f));
        Assert.True(fin.Outcome is "refused" or "failed", fin.Outcome);
        Assert.Equal(("refused", RollbackCodes.Unavailable), (fin.Outcome, fin.Code));
        Assert.Contains("9.9.9 is not known", fin.Reason);

        await Ask(f, "orders/tdd#2", to: "1.0.1");
        Assert.Equal("1.0.1", Assert.Single(f.Of<RollbackRelease>()).Version);
    }

    [Fact]
    public async Task ARunningTargetOrOneThatNeverRan_IsNoRollback()
    {
        var (f, _) = await Delivered();
        await Ask(f, to: "1.0.2");
        Assert.Contains("already runs 1.0.2", Assert.Single(Finished(f)).Reason);
        await f.Release("1.0.3", 'b');   // seen, never deployed to tdd
        await Ask(f, "orders/tdd#2", to: "1.0.3");
        Assert.Contains("never ran successfully", Finished(f).Last().Reason);
        Assert.Empty(f.Of<RollbackRelease>());
    }

    [Fact]
    public async Task ATrainThatHasSeenOneReleaseOnly_HasNothingToGoBackTo()
    {
        var f = new TrainFixture("tdd", migrations: new FakeMigrationProbe());
        await f.Baseline();
        await Ask(f);
        Assert.Contains("nothing to go back to", Assert.Single(Finished(f)).Reason);
        Assert.Empty(f.Of<RollbackRelease>());
    }

    [Fact]
    public async Task AFailedDeployment_IsRolledBackToTheReleaseThatStillRuns()
    {
        var (f, _) = await Delivered();
        f.Range('a', 'b');
        await f.Release("1.0.3", 'b'); await f.Deploy("1.0.3", "Tdd", "Started"); f.Now += TimeSpan.FromMinutes(3); await f.Deploy("1.0.3", "Tdd", "Failed");
        f.All.Clear();
        await Ask(f);
        var cmd = Assert.Single(f.Of<RollbackRelease>());
        Assert.Equal("1.0.2", cmd.Version);   // 1.0.2 is what ran before the failed deployment of 1.0.3
        f.Now += TimeSpan.FromMinutes(1); await Rollback(f, "Started", "1.0.2"); f.Now += TimeSpan.FromMinutes(2); await Rollback(f, "Succeeded", "1.0.2");
        var done = Assert.Single(Finished(f));
        Assert.Equal("done", done.Outcome);
        Assert.Empty(f.Of<Undelivered>());   // the failed deployment delivered nothing
    }

    // ---- the migration precondition ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReleaseThatAddsAMigration_IsNotRolledBackAutomatically_ItIsEscalated()
    {
        var (f, probe) = await Delivered();
        probe.Answers[(Sha('d'), Sha('a'))] = new ChangedFiles("ahead", ["src/App/Order.cs", "src/Database/scripts/Update/0042_AddOrderColumn.sql"], true);
        await Ask(f);
        Assert.Empty(f.Of<RollbackRelease>());
        var fin = Assert.Single(Finished(f));
        Assert.True(fin.Outcome is "refused" or "failed", fin.Outcome);
        Assert.Equal(("refused", RollbackCodes.Migration), (fin.Outcome, fin.Code));   // (the incident fixes forward on it)
        Assert.Contains("adds a database migration since 1.0.1", fin.Reason);
        Assert.Contains("0042_AddOrderColumn.sql", fin.Reason);
        Assert.Contains("forward-only", fin.Reason);
        Assert.Equal("rollback-escalated", f.LastDecision!.Action);
        Assert.Empty(f.Of<Undelivered>());
        Assert.Equal(RollbackPhase.Escalated, f.State.ActiveRollback!.Phase);
        Assert.Equal("1.0.2", f.State.Current);
    }

    [Fact]
    public async Task AReadThatFails_DoesNotEscalate_TheTrainLooksAgain()
    {
        var (f, probe) = await Delivered();
        probe.Fail = new HttpRequestException("GitHub does not answer");
        var r = await Ask(f);
        Assert.Empty(f.Of<RollbackRelease>()); Assert.Empty(Finished(f));
        Assert.Equal(f.Now + DeliveryTrainService.TransientRetry, r.WakeAt);   // the request is kept; the look is repeated
        probe.Fail = null;
        await f.After(DeliveryTrainService.TransientRetry);
        Assert.Single(f.Of<RollbackRelease>());
    }

    [Theory]
    [InlineData("behind")]
    [InlineData("diverged")]
    public async Task ARangeThatIsNotAStraightLine_CannotShowThatNoMigrationWasAdded(string status)
    {
        var (f, probe) = await Delivered();
        probe.Answers[(Sha('d'), Sha('a'))] = new ChangedFiles(status, ["src/App/Order.cs"], true);
        await Ask(f);
        Assert.Empty(f.Of<RollbackRelease>());
        Assert.Contains($"compare says {status}", Assert.Single(Finished(f)).Reason);
    }

    [Fact]
    public async Task ACutListOfFiles_OrNoCompare_OrNoProbe_IsAnEscalation_NeverARollback()
    {
        var (f, probe) = await Delivered();
        probe.Answers[(Sha('d'), Sha('a'))] = new ChangedFiles("ahead", ["src/App/Order.cs"], false);
        await Ask(f);
        Assert.Contains("cut the list of files", Finished(f).Last().Reason);
        probe.Answers[(Sha('d'), Sha('a'))] = null;
        await Ask(f, "orders/tdd#2");
        Assert.Contains("cannot compare", Finished(f).Last().Reason);
        Assert.Empty(f.Of<RollbackRelease>());

        var (g, _) = await Delivered(withProbe: false);
        await Ask(g);
        Assert.Contains("cannot read the files changed", Assert.Single(Finished(g)).Reason);
        Assert.Empty(g.Of<RollbackRelease>());
    }

    [Fact]
    public async Task ASingleCommitReleaseWithoutACommit_CannotBeChecked()
    {
        var f = new TrainFixture("tdd", migrations: new FakeMigrationProbe());
        await f.Baseline();
        await f.Send(new TrainReleaseSeen(f.Key, "Releases-1.0.2", "1.0.2", null, f.Now));   // the commit is not resolved yet
        await f.Deploy("1.0.2", "Tdd", "Started"); f.Now += TimeSpan.FromMinutes(1); await f.Deploy("1.0.2", "Tdd", "Succeeded");
        f.All.Clear();
        await Ask(f);
        Assert.Contains("commit of 1.0.2 is not known", Assert.Single(Finished(f)).Reason);
        Assert.Empty(f.Of<RollbackRelease>());
    }

    [Theory]
    [InlineData("src/Database/scripts/Update/0042_x.sql", true)]   // (the default's place: not this deployable's)
    [InlineData("db/changes/0042_x.sql", false)]
    public async Task TheMigrationPathsAreTheDeployablesOwn(string changed, bool rolledBack)
    {
        var probe = new FakeMigrationProbe();
        var f = new TrainFixture("tdd", migrations: probe, migrationPaths: ["db/changes/"]);
        await f.Baseline(); f.Range('d', 'a'); await f.Merge(7, 'a'); f.Now = T0;
        await f.Release("1.0.2", 'a'); await f.Deploy("1.0.2", "Tdd", "Started"); f.Now += TimeSpan.FromMinutes(2); await f.Deploy("1.0.2", "Tdd", "Succeeded");
        probe.Answers[(Sha('d'), Sha('a'))] = new ChangedFiles("ahead", [changed], true);
        await Ask(f);
        Assert.Equal(rolledBack ? 1 : 0, f.Of<RollbackRelease>().Count());
        Assert.Equal(rolledBack ? 0 : 1, Finished(f).Count());
    }

    // ---- tdd only ----------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("uat")]
    [InlineData("prod")]
    public async Task UatAndProd_AreNotConfigured_TheRequestIsEscalatedAndNothingIsDeployed(string env)
    {
        var (f, probe) = await Delivered(env);
        await Ask(f);
        Assert.Empty(f.Of<RollbackRelease>());
        var fin = Assert.Single(Finished(f));
        Assert.True(fin.Outcome is "refused" or "failed", fin.Outcome);
        Assert.Equal(("refused", RollbackCodes.NotPermitted), (fin.Outcome, fin.Code));
        Assert.Contains("configured for tdd only", fin.Reason);
        Assert.Equal(0, probe.Calls);
        Assert.Empty(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task TheIncidentsRequest_ForADeployableTheTrainDoesNotKnow_IsNotApplied()
    {
        // (the incident names the train by its key; a deployable that left the catalog has no policy and the request goes nowhere)
        var (f, _) = await Delivered();
        var r = await new DeliveryTrainService(f.Store, f.Policies, f.Reads, f.Delivered, () => f.Now).Handle(new TrainRollback("gone/tdd", Incident, 1, null, "why"));
        Assert.Empty(r.Messages);
    }

    // ---- the command and the deployment it waits for -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestDeliveredTwice_IsOneRollback_AndTheCommandIsSentAgainOnlyWhenOctopusShowsNothing()
    {
        var (f, _) = await Delivered();
        await Ask(f); await Ask(f);
        Assert.Single(f.Of<RollbackRelease>());
        await f.After(TimeSpan.FromMinutes(2));
        Assert.Single(f.Of<RollbackRelease>());
        await f.After(TimeSpan.FromMinutes(4));   // five minutes since the command: it goes again, with the same key (the executor's ledger and Octopus make the second send harmless)
        var sent = f.Of<RollbackRelease>().ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0].IdempotencyKey, sent[1].IdempotencyKey);
        await Rollback(f, "Started");
        await f.After(TimeSpan.FromMinutes(10));
        Assert.Equal(2, f.Of<RollbackRelease>().Count());   // a deployment is there: no more sends
    }

    [Fact]
    public async Task ADeploymentOctopusNeverShows_IsGivenUpOnAfterTheRequestExpiry()
    {
        var (f, _) = await Delivered();
        await Ask(f);
        for (var i = 0; i < 7; i++) await f.After(TimeSpan.FromMinutes(5));
        var fin = Assert.Single(Finished(f));
        Assert.True(fin.Outcome is "refused" or "failed", fin.Outcome);
        Assert.Equal(("failed", RollbackCodes.Failed), (fin.Outcome, fin.Code));
        Assert.Contains("showed none within 30 minutes", fin.Reason);
        Assert.Empty(f.Of<Undelivered>());
    }

    [Fact]
    public async Task AFailedRollbackDeployment_IsEscalated_AndUndeliversNothing()
    {
        var (f, _) = await Delivered();
        await Ask(f);
        f.Now += TimeSpan.FromMinutes(1); await Rollback(f, "Started"); f.Now += TimeSpan.FromMinutes(2); await Rollback(f, "Failed");
        var fin = Assert.Single(Finished(f));
        Assert.True(fin.Outcome is "refused" or "failed", fin.Outcome);
        Assert.Equal(("failed", RollbackCodes.Failed), (fin.Outcome, fin.Code));
        Assert.Contains("ended Failed (D-rollback)", fin.Reason);
        Assert.Empty(f.Of<Undelivered>());
        Assert.Contains("acme/orders#7|" + Sha('a'), f.State.DeliveredItems);   // the item stays delivered: nothing was taken back
        Assert.Empty(f.State.RolledBack);
    }

    [Fact]
    public async Task TheExecutorsRefusal_EscalatesTheRollback()
    {
        var (f, _) = await Delivered();
        await Ask(f);
        await f.Send(new TrainRollbackFailed(f.Key, Incident, "Octopus refused to create the rollback deployment (403, missing permission DeploymentCreate)"));
        var fin = Assert.Single(Finished(f));
        Assert.True(fin.Outcome is "refused" or "failed", fin.Outcome);
        Assert.Equal(("failed", RollbackCodes.Failed), (fin.Outcome, fin.Code));
        Assert.Contains("was refused: Octopus refused", fin.Reason);
        await f.Send(new TrainRollbackFailed(f.Key, Incident, "again"));   // (the same refusal again: nothing new)
        Assert.Single(Finished(f));
    }

    [Fact]
    public async Task ARunningDeployment_IsWaitedFor_BeforeTheRollbackIsPlanned()
    {
        var (f, probe) = await Delivered();
        f.Range('a', 'b');
        await f.Release("1.0.3", 'b'); await f.Deploy("1.0.3", "Tdd", "Started");
        f.All.Clear();
        await Ask(f);
        Assert.Empty(f.Of<RollbackRelease>()); Assert.Equal(0, probe.Calls);
        await f.After(TimeSpan.FromMinutes(3)); Assert.Empty(f.Of<RollbackRelease>());
        await f.Deploy("1.0.3", "Tdd", "Succeeded");
        var cmd = Assert.Single(f.Of<RollbackRelease>());
        Assert.Equal("1.0.2", cmd.Version);   // the release that ran before 1.0.3
    }

    [Fact]
    public async Task ADeploymentThatNeverEnds_EscalatesTheRequest()
    {
        var (f, _) = await Delivered();
        await f.Release("1.0.3", 'b'); await f.Deploy("1.0.3", "Tdd", "Started");
        f.All.Clear();
        await Ask(f);
        for (var i = 0; i < 7; i++) await f.After(TimeSpan.FromMinutes(5));
        Assert.Contains("is still running after 30 minutes", Assert.Single(Finished(f)).Reason);
        Assert.Empty(f.Of<RollbackRelease>());
    }

    [Fact]
    public async Task AHeldTrain_StillRollsBack_ItIsTheIncidentsOwnDeployment()
    {
        var (f, _) = await Delivered();
        await f.Send(new TrainHold(f.Key, true, "incident orders/tdd#1"));
        await Ask(f);
        Assert.Single(f.Of<RollbackRelease>());
    }

    [Fact]
    public async Task ASecondIncident_StartsWhenTheFirstRollbackHasEnded()
    {
        var (f, _) = await Delivered();
        await Ask(f);
        await Ask(f, "orders/tdd#2", reason: "second");
        Assert.Equal(["orders/tdd#2"], f.State.RollbackQueue.Select(q => q.Incident));
        f.Now += TimeSpan.FromMinutes(1); await Rollback(f, "Started"); f.Now += TimeSpan.FromMinutes(2); await Rollback(f, "Succeeded");
        // the first ended; the second looked at 1.0.1, which runs now and has nothing before it
        Assert.Equal(2, Finished(f).Count());
        Assert.Equal("done", Finished(f).First().Outcome);
        var second = Finished(f).Last();
        Assert.Equal("orders/tdd#2", second.IncidentId);
        Assert.True(second.Outcome is "refused" or "failed", second.Outcome);
        Assert.Empty(f.State.RollbackQueue);
    }

    [Fact]
    public async Task TheDeliveriesAreKeptOnlyForTheNewestReleases()
    {
        var (f, _) = await Delivered();
        Assert.Equal("1.0.2", Assert.Single(f.State.DeliveredBy.Keys));
        var parent = 'a';
        for (var i = 0; i < RollbackRules.KeptDeliveries + 3; i++)
        {
            var commit = (char)('e' + i); var version = $"2.0.{i}";
            f.Range(parent, commit); await f.Merge(100 + i, commit);
            f.Now += TimeSpan.FromHours(1);
            await f.Release(version, commit); await f.Deploy(version, "Tdd", "Started"); f.Now += TimeSpan.FromMinutes(2); await f.Deploy(version, "Tdd", "Succeeded");
            parent = commit;
        }
        Assert.Equal(RollbackRules.KeptDeliveries, f.State.DeliveredBy.Count);
        Assert.DoesNotContain("1.0.2", f.State.DeliveredBy.Keys);
    }

    // ---- the rules -------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("src/Database/scripts/Update/001_Create.sql", true)]
    [InlineData("src\\Database\\scripts\\update\\001_Create.sql", true)]
    [InlineData("scripts/Update/002.sql", true)]
    [InlineData("src/Database/scripts/Everytime/views.sql", false)]
    [InlineData("docs/scripts-update.md", false)]
    public void IsMigration_MatchesTheDbUpUpdateFolder_WithoutCase(string path, bool expected)
        => Assert.Equal(expected, RollbackRules.IsMigration(path, RollbackRules.DefaultMigrationPaths));

    [Fact]
    public void Judge_ClearsOnlyWhatItCanProve()
    {
        var paths = RollbackRules.DefaultMigrationPaths;
        Assert.True(RollbackRules.Judge(new ChangedFiles("ahead", ["a.cs"], true), paths, "1", "2").Safe);
        Assert.True(RollbackRules.Judge(new ChangedFiles("identical", [], true), paths, "1", "2").Safe);
        Assert.False(RollbackRules.Judge(null, paths, "1", "2").Safe);
        Assert.False(RollbackRules.Judge(new ChangedFiles(null, [], true), paths, "1", "2").Safe);
        var many = RollbackRules.Judge(new ChangedFiles("ahead", Enumerable.Range(1, 5).Select(i => $"scripts/Update/{i}.sql").ToList(), true), paths, "1", "2");
        Assert.False(many.Safe);
        Assert.Contains("5 database migrations", many.Reason);
        Assert.Contains("and 2 more", many.Reason);
    }
}
