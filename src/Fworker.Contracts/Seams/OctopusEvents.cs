namespace Fworker.Contracts.Seams;

// W4.2: what the Octopus watcher publishes on the event ingress (S3). Octopus is read only: these say what Octopus shows, never a request to change it.
// Each event has a Key, its logical identity: the same fact read again by a later poll has the same key, so the ingress (W4.1) can drop a repeat.

/// <summary>How far a deployment of a release has come in one environment.</summary>
public enum OctopusDeploymentPhase { Started, Succeeded, Failed }

/// <summary>A release of the project exists. <paramref name="Commit"/> is the 40 character commit it was built from, or null when its build information and notes name none.</summary>
public sealed record OctopusReleaseCreated(string ReleaseId, string Version, string? Commit) : IngressEvent
{
    public string Key => $"octopus:release:{ReleaseId}";
}

/// <summary>The commit of a release was resolved (build information first, then the notes: the rule of Get-ReleaseCommit). Published once per release, the commit in lower case.</summary>
public sealed record ReleaseCommitResolved(string ReleaseId, string Version, string Commit) : IngressEvent
{
    public string Key => $"octopus:commit:{ReleaseId}";
}

/// <summary>A deployment started, succeeded or failed (failed: Failed, TimedOut or Canceled). Each phase of a deployment is published once.</summary>
public sealed record OctopusDeploymentChanged(string DeploymentId, string TaskId, string ReleaseId, string Version, string Environment, OctopusDeploymentPhase Phase, string? Commit) : IngressEvent
{
    public string Key => $"octopus:deployment:{DeploymentId}:{Phase}";
}

/// <summary>A running deployment waits at a manual intervention (the Sign-off step). <paramref name="InterruptionId"/> identifies the wait.</summary>
public sealed record SignOffRequested(string InterruptionId, string DeploymentId, string TaskId, string ReleaseId, string Version, string Environment, string? Commit) : IngressEvent
{
    public string Key => $"octopus:signoff:{InterruptionId}";
}
