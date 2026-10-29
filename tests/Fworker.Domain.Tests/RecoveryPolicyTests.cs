using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D5: the recovery policy table of docs/design/delivery-speed.md section 8, as specification tests (v1 has no function for it; some rows are new
/// design). One test per row, the budget boundaries at n-1, n and n+1 (n-1 and n are the last automatic step and the first park of the row's
/// counter, n+1 is a counter past the budget), and the rule that quality parks are never retried.
/// </summary>
public sealed class RecoveryPolicyTests
{
    static readonly RecoverySettings S = RecoverySettings.Default;

    static RecoveryDecision Decide(RecoveryCause cause, RecoveryCounters? counters = null, RecoverySettings? settings = null)
        => RecoveryPolicy.Decide(cause, counters ?? RecoveryCounters.None, settings ?? S);

    // ---- the defaults are the table's values

    [Fact]
    public void Defaults_AreTheValuesOfTheTable()
    {
        Assert.Equal(2, S.MaxDemotions);
        Assert.Equal(4, S.FixForwardMaxRounds);
        Assert.Equal(4, S.MaxMechanical);
        Assert.Equal(1, S.MergeGroupRetries);
        Assert.Equal(2, S.MaxFailuresPerPhase);
        Assert.Equal(3, S.MaxRelaunches);
        Assert.Equal(1, S.NoVerdictRuns);
        Assert.Equal(1, S.AutoRetries);
    }

    // ---- FAIL verdict: quality, maxDemotions 2

    [Fact]
    public void FailVerdict_FreshItem_GoesBackToDevelopment()
    {
        var d = Decide(RecoveryCause.FailVerdict);
        Assert.Equal(new RecoveryDecision(RecoveryClass.Quality, 2, 0, RecoveryAction.DemoteToDevelopment), d);
        Assert.True(d.Automatic);
    }

    [Theory]
    [InlineData(1, RecoveryAction.DemoteToDevelopment, null)]
    [InlineData(2, RecoveryAction.Park, "verdict-failed")]
    [InlineData(3, RecoveryAction.Park, "verdict-failed")]
    public void FailVerdict_Boundary_DemotionsAtNMinusOneNAndNPlusOne(int demotions, RecoveryAction action, string? park)
    {
        var d = Decide(RecoveryCause.FailVerdict, new RecoveryCounters(Demotions: demotions));
        Assert.Equal(RecoveryClass.Quality, d.Class);
        Assert.Equal(2, d.Budget);
        Assert.Equal(action, d.Action);
        Assert.Equal(park, d.ParkReason);
    }

    // ---- CI red: fix-forward, fixForwardMaxRounds 4

    [Fact]
    public void CiRed_FreshItem_OwnerFixesInDevelopment()
        => Assert.Equal(new RecoveryDecision(RecoveryClass.FixForward, 4, 0, RecoveryAction.FixForward), Decide(RecoveryCause.CiRed));

    [Theory]
    [InlineData(3, RecoveryAction.FixForward, null)]
    [InlineData(4, RecoveryAction.Park, "fix-forward-exhausted")]
    [InlineData(5, RecoveryAction.Park, "fix-forward-exhausted")]
    public void CiRed_Boundary_RoundsAtNMinusOneNAndNPlusOne(int rounds, RecoveryAction action, string? park)
    {
        var d = Decide(RecoveryCause.CiRed, new RecoveryCounters(FixForwardRounds: rounds));
        Assert.Equal(RecoveryClass.FixForward, d.Class);
        Assert.Equal(4, d.Budget);
        Assert.Equal(action, d.Action);
        Assert.Equal(park, d.ParkReason);
    }

    [Fact]
    public void CiRed_IsNeverAQualityDemotion_EvenWithDemotionsSpent()
        => Assert.Equal(RecoveryAction.FixForward, Decide(RecoveryCause.CiRed, new RecoveryCounters(Demotions: 2)).Action);

    // ---- mechanical failure: maxMechanical 4 (new value)

    [Fact]
    public void MechanicalFailure_FreshItem_OwnerMergesMasterAndFunctionalTestingRunsAgain()
        => Assert.Equal(new RecoveryDecision(RecoveryClass.Mechanical, 4, 0, RecoveryAction.MergeMasterAndRetest), Decide(RecoveryCause.MechanicalFailure));

    [Theory]
    [InlineData(3, RecoveryAction.MergeMasterAndRetest, null)]
    [InlineData(4, RecoveryAction.Park, "mechanical-exhausted")]
    [InlineData(5, RecoveryAction.Park, "mechanical-exhausted")]
    public void MechanicalFailure_Boundary_FixesAtNMinusOneNAndNPlusOne(int fixes, RecoveryAction action, string? park)
    {
        var d = Decide(RecoveryCause.MechanicalFailure, new RecoveryCounters(MechanicalFixes: fixes));
        Assert.Equal(RecoveryClass.Mechanical, d.Class);
        Assert.Equal(4, d.Budget);
        Assert.Equal(action, d.Action);
        Assert.Equal(park, d.ParkReason);
    }

    // ---- merge group red (the pull request alone green): once, then a fix-forward round

    [Fact]
    public void MergeGroupRed_FirstTime_ReEnqueuesOnce()
        => Assert.Equal(new RecoveryDecision(RecoveryClass.Mechanical, 1, 0, RecoveryAction.ReEnqueueOnce), Decide(RecoveryCause.MergeGroupRed));

    [Fact]
    public void MergeGroupRed_AfterTheOnce_IsAFixForwardRound()
    {
        var d = Decide(RecoveryCause.MergeGroupRed, new RecoveryCounters(MergeGroupRetries: 1));
        Assert.Equal(new RecoveryDecision(RecoveryClass.FixForward, 4, 0, RecoveryAction.FixForward), d);
    }

    [Theory]
    [InlineData(0, 0, RecoveryAction.ReEnqueueOnce)]      // n-1
    [InlineData(1, 0, RecoveryAction.FixForward)]         // n: the once is spent
    [InlineData(2, 0, RecoveryAction.FixForward)]         // n+1
    [InlineData(1, 3, RecoveryAction.FixForward)]         // the fix-forward row's own budget still has a round
    [InlineData(1, 4, RecoveryAction.Park)]               // ... and when it is spent the item is parked
    public void MergeGroupRed_Boundary_OnceThenTheFixForwardBudget(int retries, int rounds, RecoveryAction action)
        => Assert.Equal(action, Decide(RecoveryCause.MergeGroupRed, new RecoveryCounters(MergeGroupRetries: retries, FixForwardRounds: rounds)).Action);

    [Fact]
    public void MergeGroupRed_AfterTheOnceWithFixForwardSpent_ParksLikeCiRed()
        => Assert.Equal("fix-forward-exhausted", Decide(RecoveryCause.MergeGroupRed, new RecoveryCounters(MergeGroupRetries: 1, FixForwardRounds: 4)).ParkReason);

    // ---- session lost: runtime, maxFailuresPerPhase 2

    [Fact]
    public void SessionLost_FreshItem_ResumesAtRank2()
        => Assert.Equal(new RecoveryDecision(RecoveryClass.Runtime, 2, 0, RecoveryAction.ResumeAtRank2), Decide(RecoveryCause.SessionLost));

    [Theory]
    [InlineData(1, RecoveryAction.ResumeAtRank2, null)]
    [InlineData(2, RecoveryAction.Park, "runtime-failed")]
    [InlineData(3, RecoveryAction.Park, "runtime-failed")]
    public void SessionLost_Boundary_FailuresAtNMinusOneNAndNPlusOne(int failures, RecoveryAction action, string? park)
    {
        var d = Decide(RecoveryCause.SessionLost, new RecoveryCounters(SessionFailuresInPhase: failures));
        Assert.Equal(RecoveryClass.Runtime, d.Class);
        Assert.Equal(2, d.Budget);
        Assert.Equal(action, d.Action);
        Assert.Equal(park, d.ParkReason);
    }

    // ---- no verdict: after maxRelaunches, Functional Testing runs once more, then the park

    [Fact]
    public void NoVerdict_BeforeTheRelaunchesAreSpent_RelaunchesTheSession()
        => Assert.Equal(new RecoveryDecision(RecoveryClass.Runtime, 3, 0, RecoveryAction.RelaunchValidation), Decide(RecoveryCause.NoVerdict));

    [Theory]
    [InlineData(2, RecoveryAction.RelaunchValidation)]      // n-1
    [InlineData(3, RecoveryAction.RunFunctionalTestingOnce)]  // n: the relaunches are spent, one more run
    [InlineData(4, RecoveryAction.RunFunctionalTestingOnce)]  // n+1
    public void NoVerdict_Boundary_RelaunchesAtNMinusOneNAndNPlusOne(int relaunches, RecoveryAction action)
        => Assert.Equal(action, Decide(RecoveryCause.NoVerdict, new RecoveryCounters(Relaunches: relaunches)).Action);

    [Theory]
    [InlineData(0, RecoveryAction.RunFunctionalTestingOnce, null)]            // n-1
    [InlineData(1, RecoveryAction.Park, "validation-no-verdict")]             // n
    [InlineData(2, RecoveryAction.Park, "validation-no-verdict")]             // n+1
    public void NoVerdict_Boundary_TheExtraRunAtNMinusOneNAndNPlusOne(int runs, RecoveryAction action, string? park)
    {
        var d = Decide(RecoveryCause.NoVerdict, new RecoveryCounters(Relaunches: 3, NoVerdictRuns: runs));
        Assert.Equal(RecoveryClass.Runtime, d.Class);
        Assert.Equal(1, d.Budget);
        Assert.Equal(action, d.Action);
        Assert.Equal(park, d.ParkReason);
    }

    // ---- cause cleared: autoRetries 1 (new value)

    [Fact]
    public void CauseCleared_FirstPark_IsRetriedOnceAndJournaled()
    {
        var d = Decide(RecoveryCause.CauseCleared);
        Assert.Equal(new RecoveryDecision(RecoveryClass.Park, 1, 0, RecoveryAction.RetryPark), d);
        Assert.True(d.Automatic);
        Assert.Equal("automatic retry 1 of 1", d.Journal);
    }

    [Theory]
    [InlineData(0, RecoveryAction.RetryPark)]   // n-1
    [InlineData(1, RecoveryAction.None)]        // n: a second park stays
    [InlineData(2, RecoveryAction.None)]        // n+1
    public void CauseCleared_Boundary_RetriesAtNMinusOneNAndNPlusOne(int retries, RecoveryAction action)
    {
        var d = Decide(RecoveryCause.CauseCleared, new RecoveryCounters(AutoRetries: retries));
        Assert.Equal(action, d.Action);
        Assert.Equal(action == RecoveryAction.RetryPark, d.Automatic);
        Assert.Equal(action == RecoveryAction.RetryPark ? "automatic retry 1 of 1" : null, d.Journal);
    }

    [Fact]
    public void CauseCleared_WithAutoRetriesZero_NeverRetries()
        => Assert.Equal(RecoveryAction.None, Decide(RecoveryCause.CauseCleared, settings: S with { AutoRetries = 0 }).Action);

    // ---- quality parks are never retried

    [Theory]
    [InlineData("verdict-failed")]
    [InlineData("demotion-limit")]
    [InlineData("same-criterion-repeated")]
    public void QualityParks_AreRecognized(string reason) => Assert.True(RecoveryPolicy.IsQualityPark(reason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fix-forward-exhausted")]
    [InlineData("mechanical-exhausted")]
    [InlineData("runtime-failed")]
    [InlineData("validation-no-verdict")]
    public void OtherParks_AreNotQualityParks(string? reason) => Assert.False(RecoveryPolicy.IsQualityPark(reason));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void QualityPark_IsNeverRetried_WhateverTheCountersSay(int autoRetries)
    {
        var d = Decide(RecoveryCause.QualityPark, new RecoveryCounters(AutoRetries: autoRetries));
        Assert.Equal(RecoveryAction.None, d.Action);
        Assert.Equal(RecoveryClass.Quality, d.Class);
        Assert.False(d.Automatic);
        Assert.Null(d.Journal);
    }

    [Fact]
    public void QualityPark_IsNeverRetried_EvenWithAGenerousAutoRetriesSetting()
        => Assert.Equal(RecoveryAction.None, Decide(RecoveryCause.QualityPark, settings: S with { AutoRetries = 99 }).Action);

    // ---- every cause has a row, a budget of 0 turns the automatic step off, and the exhausted answers are parks

    [Fact]
    public void EveryCause_HasARow()
    {
        foreach (var cause in Enum.GetValues<RecoveryCause>())
            Assert.NotNull(Decide(cause));
    }

    [Fact]
    public void ABudgetOfZero_ParksAtOnce()
    {
        var off = new RecoverySettings(MaxDemotions: 0, FixForwardMaxRounds: 0, MaxMechanical: 0, MaxFailuresPerPhase: 0);
        Assert.Equal(RecoveryAction.Park, Decide(RecoveryCause.FailVerdict, settings: off).Action);
        Assert.Equal(RecoveryAction.Park, Decide(RecoveryCause.CiRed, settings: off).Action);
        Assert.Equal(RecoveryAction.Park, Decide(RecoveryCause.MechanicalFailure, settings: off).Action);
        Assert.Equal(RecoveryAction.Park, Decide(RecoveryCause.SessionLost, settings: off).Action);
    }

    [Fact]
    public void AParkAnswer_IsNotAutomaticAndCarriesItsReason()
    {
        var d = Decide(RecoveryCause.CiRed, new RecoveryCounters(FixForwardRounds: 4));
        Assert.False(d.Automatic);
        Assert.Equal("fix-forward-exhausted", d.ParkReason);
        Assert.Null(d.Journal);
    }

    [Fact]
    public void AnUnknownCause_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Decide((RecoveryCause)99));
}
