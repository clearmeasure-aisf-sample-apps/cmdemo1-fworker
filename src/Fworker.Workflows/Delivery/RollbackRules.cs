using Fworker.Contracts.Ids;

namespace Fworker.Workflows.Delivery;

// W8.3: the rules of a rollback that need no read: where it may run, which release is the last good one, what counts as a migration, and how a request changes a train's state.

/// <summary>The verdict of the migration precondition: <see cref="Safe"/> only when the range provably adds no database migration.</summary>
public sealed record MigrationVerdict(bool Safe, string Reason)
{
    public static readonly MigrationVerdict Clear = new(true, "");
}

public static class RollbackRules
{
    /// <summary>The one environment where a rollback is automatic (owner decision of 2026-10-29). In uat and prod a person decides; they are not configured.</summary>
    public const string AutomaticEnvironment = "tdd";

    /// <summary>Where the app's DbUp migrations are (a fragment of the path, compared without case): cmdemo1's scripts are numbered .sql files under scripts/Update. Configurable as delivery.rollback.migrationPaths.</summary>
    public static readonly IReadOnlyList<string> DefaultMigrationPaths = ["scripts/Update/"];

    /// <summary>How many of the newest delivered releases a train keeps the delivered items of (the items an Undelivered concerns after a rollback).</summary>
    public const int KeptDeliveries = 10;
    /// <summary>The finished requests a train remembers, so a redelivered request is not a second rollback.</summary>
    public const int KeptRequests = 20;

    /// <summary>The train may roll back by itself: it is the tdd train of a deployable that has an Octopus project (the deployment is created there).</summary>
    public static bool Automatic(DeliveryPolicy p) => string.Equals(p.Environment, AutomaticEnvironment, StringComparison.OrdinalIgnoreCase) && p.Project.Length > 0;

    public static bool IsMigration(string path, IReadOnlyList<string> fragments)
    {
        var normal = path.Replace('\\', '/');
        return fragments.Any(f => f.Length > 0 && normal.Contains(f.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// DbUp's migrations are forward-only: going back to <paramref name="target"/> is a rollback only when no migration was added since it. Anything that cannot prove that is a refusal: no comparison, a
    /// comparison that is not a straight line from the target to the release that runs now (it may have been rebuilt on another branch), a list of files GitHub cut. Only the files count, not the commit messages.
    /// </summary>
    public static MigrationVerdict Judge(ChangedFiles? files, IReadOnlyList<string> fragments, string target, string from)
    {
        if (files is null) return new(false, $"GitHub cannot compare the commits of {target} and {from}, so it cannot be shown that no database migration was added");
        if (files.Status is not ("ahead" or "identical")) return new(false, $"{from} does not follow {target} in a straight line (GitHub's compare says {files.Status ?? "nothing"}), so it cannot be shown that no database migration was added");
        if (!files.Complete) return new(false, $"GitHub cut the list of files changed between {target} and {from}, so it cannot be shown that no database migration was added");
        var hits = files.Paths.Where(f => IsMigration(f, fragments)).ToList();
        if (hits.Count == 0) return MigrationVerdict.Clear;
        var shown = string.Join(", ", hits.Take(3)) + (hits.Count > 3 ? $" and {hits.Count - 3} more" : "");
        return new(false, $"{from} adds {(hits.Count == 1 ? "a database migration" : hits.Count + " database migrations")} since {target} ({shown}): migrations are forward-only, so {target} is not a backward compatible rollback; fix forward or a person decides");
    }

    /// <summary>
    /// The release to go back to: the release that ran in the environment before <paramref name="from"/>, the newest one with a successful deployment there that is not <paramref name="from"/> and was not itself rolled back from.
    /// Null when there is none (a train that has seen one release only).
    /// </summary>
    public static string? LastGood(TrainState s, string envKey, string from)
    {
        return s.Deployments.Where(kv => kv.Key != from && !s.RolledBack.Contains(kv.Key) && kv.Value.TryGetValue(envKey, out var d) && d.Succeeded && s.Releases.TryGetValue(kv.Key, out var r) && !string.IsNullOrEmpty(r.Commit))
            .OrderByDescending(kv => kv.Value[envKey].FinishedAt ?? kv.Value[envKey].StartedAt).Select(kv => kv.Key).FirstOrDefault();
    }

    /// <summary>The release and deployment of the newest deployment to the environment, whatever its phase: what the environment is, or is about to be, running.</summary>
    public static (string Version, TrainDeployment Deployment)? Newest(TrainState s, string envKey)
    {
        var newest = s.Deployments.Where(kv => kv.Value.ContainsKey(envKey)).Select(kv => (Version: kv.Key, Dep: kv.Value[envKey])).OrderByDescending(x => x.Dep.StartedAt).FirstOrDefault();
        return newest.Version is null ? null : (newest.Version, newest.Dep);
    }

    /// <summary>Applies a rollback request or the executor's refusal to the state. Idempotent: delivery is at least once, and a request counts once whatever number of times it arrives.</summary>
    public static void Apply(TrainState s, TrainInput input, DateTimeOffset now)
    {
        switch (input)
        {
            case TrainRollbackRequested r:
                if (s.RollbackSeen.Contains(r.Incident)) break;
                s.RollbackSeen.Add(r.Incident);
                if (s.RollbackSeen.Count > KeptRequests) s.RollbackSeen.RemoveRange(0, s.RollbackSeen.Count - KeptRequests);
                var request = new RollbackRequest { Incident = r.Incident, ToRelease = string.IsNullOrWhiteSpace(r.ToRelease) ? null : r.ToRelease.Trim(), Reason = r.Reason, At = now };
                if (s.ActiveRollback is null || ((s.ActiveRollback.Terminal && s.ActiveRollback.Published) && s.RollbackQueue.Count == 0)) s.ActiveRollback = Start(request);
                else s.RollbackQueue.Add(request);   // (it starts when the one before has ended)
                break;
            case TrainRollbackFailed f:
                if (s.ActiveRollback is { Phase: RollbackPhase.Deploying or RollbackPhase.Requested } a && a.Incident == f.Incident)
                {
                    a.Phase = RollbackPhase.Escalated;
                    a.Reasons = [$"the rollback to {(a.To.Length > 0 ? a.To : "the last good release")} was refused: {f.Error}"];
                }
                break;
        }
    }

    public static RollbackState Start(RollbackRequest r) => new() { Incident = r.Incident, RequestedTo = r.ToRelease, Reason = r.Reason, RequestedAt = r.At };

    /// <summary>The command's idempotency key: one rollback of one incident to one release in one environment is one deployment.</summary>
    public static string KeyOf(string deployable, string incident, string version, string environment) => $"RollbackRelease|{deployable}|{incident}|{version}|{environment}";
}
