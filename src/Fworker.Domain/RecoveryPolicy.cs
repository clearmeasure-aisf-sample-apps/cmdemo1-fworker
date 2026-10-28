namespace Fworker.Domain;

// The recovery policy table (D5): from the cause of a setback to its class, its budget and the automatic action, delivery-speed.md section 8.
// A pure rule with no v1 function to port: the rows for the FAIL verdict, CI red, session lost and the relaunches restate budgets v1 already
// counts (maxDemotions, fixForwardMaxRounds, maxFailuresPerPhase, maxRelaunches); maxMechanical, the merge group once, the one extra Functional
// Testing run and autoRetries are new design, so the tests are specification tests and not parity tests. The defaults below are constants on
// purpose: W6.0/W7.0 move them into config/instance.json (recovery.*), and nothing here reads configuration, the clock or the network.
// The gateway (deterministic, never the LLM manager) calls Decide with the counters it keeps for the item and does what the answer says.

/// <summary>What went wrong, as the gateway observes it. <see cref="CauseCleared"/> and <see cref="QualityPark"/> are about an item that is already parked.</summary>
public enum RecoveryCause
{
    /// <summary>A Functional Testing FAIL verdict (quality).</summary>
    FailVerdict,
    /// <summary>CI red on the tested head, before or after the PASS and before the merge (fix-forward).</summary>
    CiRed,
    /// <summary>Merge conflict, merge group conflict or out of date (mechanical).</summary>
    MechanicalFailure,
    /// <summary>The merge group is red although the pull request alone was green (mechanical, once).</summary>
    MergeGroupRed,
    /// <summary>The owner's session was lost (ended-with-item) (runtime).</summary>
    SessionLost,
    /// <summary>A Functional Testing round without a verdict (runtime): relaunches first, then one more run.</summary>
    NoVerdict,
    /// <summary>A park of a mechanical or runtime row whose cause is gone (blocking item merged, capacity free, master green).</summary>
    CauseCleared,
    /// <summary>A park of the quality class (verdict-failed, demotion-limit, same-criterion-repeated).</summary>
    QualityPark,
}

public enum RecoveryClass { Quality, FixForward, Mechanical, Runtime, Park }

public enum RecoveryAction
{
    /// <summary>Back to Development (a quality demotion).</summary>
    DemoteToDevelopment,
    /// <summary>The owner fixes in Development; Functional Testing runs again on the next head (a fix-forward round).</summary>
    FixForward,
    /// <summary>The owner merges master; Functional Testing again on the new head.</summary>
    MergeMasterAndRetest,
    /// <summary>Enqueue the pull request in the merge queue once more.</summary>
    ReEnqueueOnce,
    /// <summary>Resume the item at rank 2 (the session is offered again).</summary>
    ResumeAtRank2,
    /// <summary>Relaunch the Functional Testing session (counts against maxRelaunches).</summary>
    RelaunchValidation,
    /// <summary>Run Functional Testing once more after the relaunches are spent.</summary>
    RunFunctionalTestingOnce,
    /// <summary>Lift the park once: the cause cleared (journaled "automatic retry 1 of 1").</summary>
    RetryPark,
    /// <summary>Park the item for a human with <see cref="RecoveryDecision.ParkReason"/>.</summary>
    Park,
    /// <summary>Nothing automatic: the park stays until a human acts.</summary>
    None,
}

/// <summary>The budgets of the table. <see cref="Default"/> holds the design values; a budget of 0 turns its automatic step off.</summary>
public sealed record RecoverySettings(
    int MaxDemotions = RecoverySettings.DefaultMaxDemotions,
    int FixForwardMaxRounds = RecoverySettings.DefaultFixForwardMaxRounds,
    int MaxMechanical = RecoverySettings.DefaultMaxMechanical,
    int MergeGroupRetries = RecoverySettings.DefaultMergeGroupRetries,
    int MaxFailuresPerPhase = RecoverySettings.DefaultMaxFailuresPerPhase,
    int MaxRelaunches = RecoverySettings.DefaultMaxRelaunches,
    int NoVerdictRuns = RecoverySettings.DefaultNoVerdictRuns,
    int AutoRetries = RecoverySettings.DefaultAutoRetries)
{
    /// <summary>FAIL verdict: demotions before the park (unchanged, config timing.maxDemotions).</summary>
    public const int DefaultMaxDemotions = 2;
    /// <summary>CI red: fix-forward rounds before the park (unchanged, timing.fixForwardMaxRounds).</summary>
    public const int DefaultFixForwardMaxRounds = 4;
    /// <summary>Conflict, merge group conflict, out of date: merges of master before the park (new).</summary>
    public const int DefaultMaxMechanical = 4;
    /// <summary>Merge group red with the pull request alone green: re-enqueues; red again is a fix-forward round.</summary>
    public const int DefaultMergeGroupRetries = 1;
    /// <summary>Session lost: failures per phase before the park (unchanged, runtimes.maxFailuresPerPhase).</summary>
    public const int DefaultMaxFailuresPerPhase = 2;
    /// <summary>No verdict: relaunches of the Functional Testing session (unchanged, timing.maxRelaunches).</summary>
    public const int DefaultMaxRelaunches = 3;
    /// <summary>No verdict after the relaunches: Functional Testing runs once more.</summary>
    public const int DefaultNoVerdictRuns = 1;
    /// <summary>Automatic retries of a mechanical or runtime park whose cause cleared (new).</summary>
    public const int DefaultAutoRetries = 1;

    public static readonly RecoverySettings Default = new();
}

/// <summary>What the item has already used, per budget (0 for a fresh item).</summary>
public sealed record RecoveryCounters(
    int Demotions = 0,
    int FixForwardRounds = 0,
    int MechanicalFixes = 0,
    int MergeGroupRetries = 0,
    int SessionFailuresInPhase = 0,
    int Relaunches = 0,
    int NoVerdictRuns = 0,
    int AutoRetries = 0)
{
    public static readonly RecoveryCounters None = new();
}

/// <summary>
/// The answer: the class of the cause, the budget of the row and what the item has used of it (<see cref="Used"/>), the action, and for
/// <see cref="RecoveryAction.Park"/> the park reason.
/// </summary>
public sealed record RecoveryDecision(RecoveryClass Class, int Budget, int Used, RecoveryAction Action, string? ParkReason = null)
{
    /// <summary>True when the policy acts without a human: everything but a park and the park that stays.</summary>
    public bool Automatic => Action is not (RecoveryAction.Park or RecoveryAction.None);

    /// <summary>The journal line of the retry of a park ("automatic retry 1 of 1"); null for every other action.</summary>
    public string? Journal => Action == RecoveryAction.RetryPark ? $"automatic retry {Used + 1} of {Budget}" : null;
}

public static class RecoveryPolicy
{
    /// <summary>Parks of the quality class: never retried automatically (delivery-speed.md section 8).</summary>
    public static readonly IReadOnlyList<string> QualityParks = new[] { "verdict-failed", "demotion-limit", "same-criterion-repeated" };

    public static bool IsQualityPark(string? parkReason) => parkReason is not null && QualityParks.Contains(parkReason);

    /// <summary>
    /// The row for a cause. While the budget is not spent (<c>used &lt; budget</c>) the row's automatic action runs; once <c>used</c> reaches
    /// the budget the item is parked for a human. The park reasons are those v1 writes where it has one (verdict-failed, fix-forward-exhausted,
    /// validation-no-verdict); mechanical-exhausted and runtime-failed are named here for the new rows.
    /// </summary>
    public static RecoveryDecision Decide(RecoveryCause cause, RecoveryCounters counters, RecoverySettings settings)
    {
        switch (cause)
        {
            case RecoveryCause.FailVerdict:
                return Spend(RecoveryClass.Quality, settings.MaxDemotions, counters.Demotions, RecoveryAction.DemoteToDevelopment, "verdict-failed");

            case RecoveryCause.CiRed:
                return Spend(RecoveryClass.FixForward, settings.FixForwardMaxRounds, counters.FixForwardRounds, RecoveryAction.FixForward, "fix-forward-exhausted");

            case RecoveryCause.MechanicalFailure:
                return Spend(RecoveryClass.Mechanical, settings.MaxMechanical, counters.MechanicalFixes, RecoveryAction.MergeMasterAndRetest, "mechanical-exhausted");

            case RecoveryCause.MergeGroupRed:
                // Once; red again is a fix-forward round and follows the CI red row.
                return counters.MergeGroupRetries < settings.MergeGroupRetries
                    ? new RecoveryDecision(RecoveryClass.Mechanical, settings.MergeGroupRetries, counters.MergeGroupRetries, RecoveryAction.ReEnqueueOnce)
                    : Decide(RecoveryCause.CiRed, counters, settings);

            case RecoveryCause.SessionLost:
                return Spend(RecoveryClass.Runtime, settings.MaxFailuresPerPhase, counters.SessionFailuresInPhase, RecoveryAction.ResumeAtRank2, "runtime-failed");

            case RecoveryCause.NoVerdict:
                // The relaunches of v1 first (maxRelaunches); when they are spent, Functional Testing runs once more; then the park.
                return counters.Relaunches < settings.MaxRelaunches
                    ? new RecoveryDecision(RecoveryClass.Runtime, settings.MaxRelaunches, counters.Relaunches, RecoveryAction.RelaunchValidation)
                    : Spend(RecoveryClass.Runtime, settings.NoVerdictRuns, counters.NoVerdictRuns, RecoveryAction.RunFunctionalTestingOnce, "validation-no-verdict");

            case RecoveryCause.CauseCleared:
                // One retry per item; after it the park stays for a human.
                return counters.AutoRetries < settings.AutoRetries
                    ? new RecoveryDecision(RecoveryClass.Park, settings.AutoRetries, counters.AutoRetries, RecoveryAction.RetryPark)
                    : new RecoveryDecision(RecoveryClass.Park, settings.AutoRetries, counters.AutoRetries, RecoveryAction.None);

            case RecoveryCause.QualityPark:
                // Never retried, whatever the counters and the settings say.
                return new RecoveryDecision(RecoveryClass.Quality, 0, counters.AutoRetries, RecoveryAction.None);

            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, "unknown recovery cause");
        }
    }

    static RecoveryDecision Spend(RecoveryClass cls, int budget, int used, RecoveryAction action, string parkReason)
        => used < budget
            ? new RecoveryDecision(cls, budget, used, action)
            : new RecoveryDecision(cls, budget, used, RecoveryAction.Park, parkReason);
}
