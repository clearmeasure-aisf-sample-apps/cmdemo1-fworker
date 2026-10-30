using System.Text.Json.Nodes;

namespace Fworker.Workflows.Incident;

// W8.0 (#348): the settings of the incident saga, from the "incidents" block of config/instance.json. There is no flag that turns incidents on (revision of 2026-10-26): the saga exists where the engine acts
// (Engine.V2Acts), and an environment is watched when it is listed here. The rollback policy is per environment and, by the owner's decision of 2026-10-26, only tdd is configured: an environment that is not listed
// opens no incident and is never rolled back by the factory.

/// <summary>What the factory may do about a failing release in an environment (the incident rollback policy, wolverine-factory.md 5.5).</summary>
public enum RollbackMode
{
    /// <summary>The factory never rolls the environment back: the incident is fixed forward, or the owner decides.</summary>
    Off,
    /// <summary>A rollback needs a person (a Sign-off): the incident goes to the owner with the rollback as the recommendation.</summary>
    Signoff,
    /// <summary>The factory rolls the environment back by itself when a release correlates with the failure.</summary>
    Automatic,
}

/// <summary>One watched environment: the rollback policy and whether a failed deployment there opens an incident.</summary>
public sealed record IncidentEnvironment(string Environment, RollbackMode Rollback, bool DeploymentFailureOpens);

/// <summary>How a health read counts: Good ends a streak of bad reads and counts towards the baseline, Bad is Unhealthy or unreachable, anything else (Degraded) is neither.</summary>
public enum HealthClass { Good, Bad, Neutral }

/// <summary>
/// The settings of the saga. <paramref name="ConfirmReads"/> bad reads in a row within <paramref name="ConfirmWindow"/> make an incident (the confirmation rule kept after the phantom incidents of 2026-09-13); a
/// release deployed less than <paramref name="RollbackWithin"/> before the incident correlates with it. The timeouts escalate to the owner: no verdict in <paramref name="TriageTimeout"/>, no new deployment in
/// <paramref name="RollbackTimeout"/>, no hotfix delivered in <paramref name="FixTimeout"/>, health not at baseline for <paramref name="VerifyHealthy"/> within <paramref name="VerifyTimeout"/>.
/// A deployment that shows no end after <paramref name="DeploymentMaxAge"/> no longer suppresses the health reads and the alerts (its end event was lost).
/// </summary>
public sealed record IncidentSettings(
    IReadOnlyDictionary<string, IncidentEnvironment> Environments,
    int ConfirmReads = 3,
    TimeSpan? ConfirmWindow = null,
    TimeSpan? RollbackWithin = null,
    TimeSpan? TriageTimeout = null,
    TimeSpan? RollbackTimeout = null,
    TimeSpan? FixTimeout = null,
    TimeSpan? VerifyHealthy = null,
    TimeSpan? VerifyTimeout = null,
    TimeSpan? DeploymentMaxAge = null,
    int MaxRelapses = 2,
    string Mention = "",
    string IssueLabel = "incident")
{
    public static readonly IncidentSettings None = new(new Dictionary<string, IncidentEnvironment>());

    public TimeSpan Window => ConfirmWindow is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(15);
    public TimeSpan Correlation => RollbackWithin is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(60);
    public TimeSpan Triage => TriageTimeout is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(30);
    public TimeSpan RollingBack => RollbackTimeout is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(30);
    public TimeSpan Fix => FixTimeout is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromHours(4);
    public TimeSpan Healthy => VerifyHealthy is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(10);
    public TimeSpan Verify => VerifyTimeout is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(60);
    public TimeSpan Deployment => DeploymentMaxAge is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(60);

    /// <summary>Whether incidents are handled for the environment (tdd, uat or prod).</summary>
    public bool Watches(string environment) => Environments.ContainsKey(environment.ToLowerInvariant());

    public IncidentEnvironment? Of(string environment) => Environments.TryGetValue(environment.ToLowerInvariant(), out var e) ? e : null;

    public RollbackMode RollbackOf(string environment) => Of(environment)?.Rollback ?? RollbackMode.Off;

    public static HealthClass Classify(string status)
    {
        var s = (status ?? "").Trim();
        if (s.Equals("Healthy", StringComparison.OrdinalIgnoreCase)) return HealthClass.Good;
        if (s.Length == 0 || s.Contains("unhealthy", StringComparison.OrdinalIgnoreCase) || s.Contains("unreachable", StringComparison.OrdinalIgnoreCase) || s.StartsWith("status ", StringComparison.OrdinalIgnoreCase))
            return HealthClass.Bad;
        return HealthClass.Neutral;
    }

    public static RollbackMode ParseMode(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "automatic" => RollbackMode.Automatic,
        "signoff" or "sign-off" => RollbackMode.Signoff,
        _ => RollbackMode.Off,
    };

    /// <summary>
    /// Reads the "incidents" block of the configuration root: <c>environments</c> (the environments of the application that are watched: "tdd", "uat", "prod"), <c>rollback</c> (the rollback policy by environment:
    /// "automatic", "signoff" or "off"; an environment that is not named is off) and <c>deploymentFailureOpens</c> (the environments where a failed deployment opens an incident); the confirmation rule
    /// (<c>confirmReads</c>, <c>confirmWithinMinutes</c>), the correlation window (<c>rollbackWithinMinutes</c>), the timeouts (<c>triageMinutes</c>, <c>rollbackMinutes</c>, <c>fixMinutes</c>,
    /// <c>verifyHealthyMinutes</c>, <c>verifyMinutes</c>), <c>deploymentMaxMinutes</c>, <c>maxRelapses</c> and <c>issueLabel</c>; the owner to mention is the root's <c>escalationMention</c>. A block that is missing or
    /// wrong watches nothing, which is the safe reading.
    /// </summary>
    public static IncidentSettings From(JsonNode? root)
    {
        JsonNode? block = null; try { block = root?["incidents"]; } catch { }
        var watched = Names(block, "environments"); var failing = Names(block, "deploymentFailureOpens");
        JsonNode? rollback = null; try { rollback = block?["rollback"]; } catch { }
        var environments = new Dictionary<string, IncidentEnvironment>(StringComparer.Ordinal);
        foreach (var name in watched)
            environments[name] = new IncidentEnvironment(name, ParseMode(Text(rollback, name)), failing.Contains(name));
        TimeSpan? Minutes(string name) => Int(block, name) is { } i and > 0 ? TimeSpan.FromMinutes(i) : null;
        return new IncidentSettings(environments, Math.Max(1, Int(block, "confirmReads") ?? 3), Minutes("confirmWithinMinutes"), Minutes("rollbackWithinMinutes"), Minutes("triageMinutes"), Minutes("rollbackMinutes"),
            Minutes("fixMinutes"), Minutes("verifyHealthyMinutes"), Minutes("verifyMinutes"), Minutes("deploymentMaxMinutes"), Math.Max(0, Int(block, "maxRelapses") ?? 2), Text(root, "escalationMention") ?? "",
            Text(block, "issueLabel") ?? "incident");
    }

    /// <summary>The environment names of an array (lower case; only tdd, uat and prod exist).</summary>
    static HashSet<string> Names(JsonNode? n, string name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (n?[name] is JsonArray a)
                foreach (var x in a)
                    if (x is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().ToLowerInvariant() is "tdd" or "uat" or "prod") set.Add(s.Trim().ToLowerInvariant());
        }
        catch { }
        return set;
    }

    static string? Text(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; } catch { return null; } }
    static int? Int(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<int>(out var i) && i >= 0 ? i : null; } catch { return null; } }
}
