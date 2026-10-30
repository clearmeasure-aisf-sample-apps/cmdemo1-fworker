using Fworker.Contracts.Ids;

namespace Fworker.Workflows.Delivery;

// W8.3: what a train keeps of a rollback. It lives in the train's own state document (TrainState), so it is saved with the revision of every other fact of the train and needs no table of its own.

/// <summary>A rollback asked for while another one was running: it starts when that one has ended.</summary>
public sealed class RollbackRequest
{
    public string Incident { get; set; } = "";
    public int Generation { get; set; }
    public string? ToRelease { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public static class RollbackPhase
{
    /// <summary>Asked for; the target and the migration precondition are not settled yet.</summary>
    public const string Requested = "Requested";
    /// <summary>The command to deploy the target went out; the train waits for the deployment Octopus shows.</summary>
    public const string Deploying = "Deploying";
    /// <summary>The environment runs the target again.</summary>
    public const string Done = "Done";
    /// <summary>Nothing was rolled back, or the rollback deployment failed: a person decides.</summary>
    public const string Escalated = "Escalated";
}

public sealed class RollbackState
{
    /// <summary>The incident: the request's identity.</summary>
    public string Incident { get; set; } = "";
    /// <summary>The incident's generation, which the answers carry back.</summary>
    public int Generation { get; set; }
    public string? RequestedTo { get; set; }
    public string Reason { get; set; } = "";
    public string Phase { get; set; } = RollbackPhase.Requested;
    /// <summary>The release the environment ran when the rollback was planned, and the release it goes back to.</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    /// <summary>When the command first went out and when it last did (it is sent again when Octopus shows no deployment).</summary>
    public DateTimeOffset FirstCommandAt { get; set; }
    public DateTimeOffset CommandAt { get; set; }
    /// <summary>The items delivered by the releases that are rolled back (they receive Undelivered and become candidates again) and those releases.</summary>
    public List<TrainCandidate> Items { get; set; } = [];
    public List<string> RolledBack { get; set; } = [];
    /// <summary>Why the rollback was escalated (or the notes of its plan).</summary>
    public List<string> Reasons { get; set; } = [];
    /// <summary>Why it was escalated, as the code of the answer (<see cref="RollbackCodes"/>).</summary>
    public string Code { get; set; } = "";
    /// <summary>The outcome went out (the final RollbackAnswered: done, refused or failed); the next queued request may start.</summary>
    public bool Published { get; set; }

    public bool Terminal => Phase is RollbackPhase.Done or RollbackPhase.Escalated;
}

/// <summary>The files a compare of two commits changed. <paramref name="Complete"/> is false when GitHub cut the list (it lists at most 300 files) or the comparison is not a straight line from the first commit to the second.</summary>
public sealed record ChangedFiles(string? Status, IReadOnlyList<string> Paths, bool Complete);

/// <summary>
/// The read the migration precondition needs, apart from ITrainReads (whose implementations are the shadow run's and the tests'): which files changed between two commits of the deployable's repository.
/// Null when GitHub cannot compare them (a 404 or a 422). A fault of the read throws, as every other read of the train: the look is repeated.
/// </summary>
public interface IMigrationProbe
{
    Task<ChangedFiles?> ChangedBetween(RepoId repository, string fromCommit, string toCommit, CancellationToken ct);
}
