namespace Fworker.Domain;

// Release choice (D3): which release a deployable's environment takes next. Targets, NextTarget and CarryingRelease port v1 (Verbs.Promote.ps1,
// Verbs.Delivery.ps1 Get-Delivery); NewestQualifying has no v1 function: it is the promotion train of docs/design/delivery-speed.md section 12 (design). Pure
// values; v1 keeps its own functions and the parity tests pin this port against what they decide.

/// <summary>One Octopus deployment of a release to an environment: its task state and whether the task has completed.</summary>
public sealed record EnvDeployment(string State, bool Completed)
{
    public bool Succeeded => State == "Success";
}

/// <summary>A release that is a candidate: its version, when it was assembled and whether it passes the content and item gates for the target.</summary>
public sealed record ReleaseCandidate(string Version, DateTimeOffset Assembled, bool Qualifies);

public static class ReleaseChoice
{
    static readonly string[] Chain = { "tdd", "uat", "prod" };

    /// <summary>
    /// The targets of the pass: the configured environments, lower case, that come after tdd in the chain tdd, uat, prod (the configured order is kept).
    /// </summary>
    public static IReadOnlyList<string> Targets(IEnumerable<string> configured)
        => configured.Select(e => e.ToLowerInvariant()).Where(e => Array.IndexOf(Chain, e) >= 1).ToList();

    /// <summary>The environment before a target in the chain (tdd for uat, uat for prod); null for tdd or an unknown name.</summary>
    public static string? PreviousEnvironment(string target)
    {
        var i = Array.IndexOf(Chain, target);
        return i >= 1 ? Chain[i - 1] : null;
    }

    /// <summary>
    /// The environment key a release is promoted to next: the first of <paramref name="targets"/> where the release has no successful deployment.
    /// Null: promoted all the way, nothing to do. <paramref name="deploymentsByEnvironment"/> is keyed by lower-case environment name.
    /// </summary>
    public static string? NextTarget(IReadOnlyList<string> targets, Func<string, string> environmentName, IReadOnlyDictionary<string, EnvDeployment> deploymentsByEnvironment)
    {
        foreach (var t in targets)
            if (!(deploymentsByEnvironment.TryGetValue(environmentName(t).ToLowerInvariant(), out var d) && d.Succeeded)) return t;
        return null;
    }

    /// <summary>
    /// The newest qualifying release: newest by assembly time first, the first that qualifies (a blocked release is passed by and the train falls back
    /// to the one before it, delivery-speed.md section 12). Equal times keep the given order. Null when none qualifies.
    /// </summary>
    public static ReleaseCandidate? NewestQualifying(IEnumerable<ReleaseCandidate> candidates)
        => candidates.OrderByDescending(c => c.Assembled).FirstOrDefault(c => c.Qualifies);

    /// <summary>
    /// Get-Delivery's pick of the release that carries a merge: the release whose commit is the merge commit, else the newest by assembly time when
    /// its commit is ahead of (or identical to) the merge (<paramref name="newestIsAheadOfMerge"/>: GitHub's compare of merge...newest is 'ahead' or
    /// 'identical'; false when it could not be compared).
    /// </summary>
    public static T? CarryingRelease<T>(IReadOnlyList<T> releases, Func<T, string?> commit, Func<T, DateTimeOffset> assembled, string mergeSha, Func<T, bool> newestIsAheadOfMerge)
        where T : class
    {
        foreach (var r in releases) if (commit(r) == mergeSha.ToLowerInvariant()) return r;
        if (releases.Count == 0) return null;
        var newest = releases.OrderByDescending(assembled).First();
        return !string.IsNullOrEmpty(commit(newest)) && newestIsAheadOfMerge(newest) ? newest : null;
    }
}
