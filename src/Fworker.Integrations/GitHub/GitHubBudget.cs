using System.Globalization;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// The rate-limit budget of v2's GitHub calls, keyed by (credential, resource) and not by resource alone (hook H6 of docs/design/system-gateway.md 3): an
/// installation token of each owner has its own limit, so one owner running low must not hold back another. The credential is a label (an installation or
/// App name, never the token). Same rules as the gateway's v1 budget (gateway/GitHubBudget.cs): a lower remaining of the same reset wins over an older reading,
/// a reading whose reset has passed counts as full, and below the reserve the resource is low.
/// </summary>
public sealed class GitHubBudget
{
    sealed record Reading(int? Limit, int Remaining, long Reset, DateTimeOffset At);
    readonly object _gate = new();
    readonly Dictionary<(string Credential, string Resource), Reading> _res = new();
    readonly TimeProvider _time;
    public int Reserve { get; }

    public GitHubBudget(int reserve = 300, TimeProvider? time = null) { Reserve = Math.Max(0, reserve); _time = time ?? TimeProvider.System; }

    /// <summary>Records one answer's x-ratelimit-* headers (<paramref name="resource"/> is "core", "graphql", ...). Values that do not parse are ignored.</summary>
    public void Observe(string credential, string resource, string? remaining, string? reset, string? limit = null)
    {
        if (!int.TryParse(remaining, NumberStyles.None, CultureInfo.InvariantCulture, out var rem)) return;
        var rs = long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0;
        int? lim = int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null;
        lock (_gate)
        {
            var key = (credential, resource);
            if (_res.TryGetValue(key, out var cur) && (cur.Reset > rs || (cur.Reset == rs && cur.Remaining <= rem))) return;
            _res[key] = new Reading(lim, rem, rs, _time.GetUtcNow());
        }
    }

    /// <summary>Is this credential's resource below the reserve with its window still open? ResetAt is when to try again.</summary>
    public (bool Low, DateTimeOffset ResetAt) Low(string credential, string resource)
    {
        lock (_gate)
        {
            if (!_res.TryGetValue((credential, resource), out var r)) return (false, default);
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            return r.Remaining < Reserve && r.Reset > now ? (true, DateTimeOffset.FromUnixTimeSeconds(r.Reset)) : (false, default);
        }
    }

    /// <summary>The remaining calls last reported, or null when nothing was reported or the window ended (then the resource is full again).</summary>
    public int? Remaining(string credential, string resource)
    {
        lock (_gate)
            return _res.TryGetValue((credential, resource), out var r) && r.Reset > _time.GetUtcNow().ToUnixTimeSeconds() ? r.Remaining : null;
    }
}
