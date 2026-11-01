using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Domain;

namespace Fworker.Workflows.Delivery;

// W5.1, hook H9 of system-gateway.md: the policy of a deployable's promotion, per environment. The deployable and its repository come from the system catalog (H2); what v1 keeps in
// delivery.autoPromote and octopus.* (the Octopus project, the environment names and health URLs, the health window, the Sign-off limit) is carried by DeliveryPolicySettings, one per
// deployable, and the promoter says who creates the deployments: the gateway, an external promoter (cmdemo1-system's ui-auto-promote, for cmdemo1-fworker itself), or the pipeline
// (tdd: the release's own lifecycle deploys it there, the train only observes).

/// <summary>Who creates the deployment to an environment.</summary>
public enum Promoter { Gateway, External, Pipeline }

/// <summary>One deployable's settings as the configuration carries them (v1: delivery.autoPromote, octopus.*). A missing entry is a deployable the gateway does not promote.</summary>
public sealed record DeliveryPolicySettings(
    string Project,
    Promoter Promoter = Promoter.Gateway,
    IReadOnlyList<string>? Environments = null,
    IReadOnlyDictionary<string, string>? EnvironmentNames = null,
    IReadOnlyDictionary<string, string>? EnvironmentUrls = null,
    string? HealthPath = null,
    HealthWindow? Window = null,
    double SignoffWaitHours = 2,
    bool DependenciesDelivered = false,
    IReadOnlyList<string>? ShadowEnvironments = null,
    IReadOnlyList<string>? MigrationPaths = null,
    int PollSeconds = 120,
    int MinReadGapSeconds = 30);

/// <summary>The policy of one train: a deployable in one environment (tdd, uat or prod).</summary>
public sealed record DeliveryPolicy(
    string Deployable, RepoId Repository, string Environment, Promoter Promoter, bool Enabled, string Project,
    IReadOnlyDictionary<string, string> EnvironmentNames, IReadOnlyDictionary<string, string> EnvironmentUrls,
    string HealthPath, HealthWindow Window, double SignoffWaitHours, bool DependenciesDelivered,
    int PollSeconds = 120, int MinReadGapSeconds = 30, bool Shadow = false, IReadOnlyList<string>? MigrationPaths = null)
{
    /// <summary>W8.3: the path fragments that mark a file as a database migration of the deployable (the rollback's precondition); the default is DbUp's scripts/Update.</summary>
    public IReadOnlyList<string> Migrations => MigrationPaths is { Count: > 0 } ? MigrationPaths : RollbackRules.DefaultMigrationPaths;

    /// <summary>The Octopus name of an environment key (the key itself when none is configured).</summary>
    public string NameOf(string envKey) => EnvironmentNames.TryGetValue(envKey, out var n) && n.Length > 0 ? n : envKey;

    /// <summary>The environment key an Octopus environment name stands for, or null when it is none of the chain's.</summary>
    public string? KeyOf(string octopusName)
    {
        foreach (var k in Chain) if (string.Equals(NameOf(k), octopusName, StringComparison.OrdinalIgnoreCase) || string.Equals(k, octopusName, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    public string? UrlOf(string envKey) => EnvironmentUrls.TryGetValue(envKey, out var u) && u.Length > 0 ? u.TrimEnd('/') : null;

    public static readonly string[] Chain = ["tdd", "uat", "prod"];

    /// <summary>The train promotes (creates deployments, answers Sign-offs) only for an enabled environment after tdd that the gateway promotes.</summary>
    public bool Acts => Enabled && Promoter == Promoter.Gateway && ReleaseChoice.PreviousEnvironment(Environment) is not null;

    // The Shadow parameter (W9.10p): the train decides here, in the engine that acts, but v1's AutoPromoter still promotes (the app's uat and prod until the train's recorded clean live window, W9.12): the decisions are published and
    // compared with v1's, and the commands it would send are wrapped in ShadowCommand, which the shadow records and nothing executes. False for every environment of the shadow run, whose commands
    // are all recorded by the shadow host's handlers anyway, and for an environment the train promotes for real.
}

public interface IDeliveryPolicies
{
    /// <summary>The policy of the train, or null when the catalog has no such deployable.</summary>
    DeliveryPolicy? For(string deployable, string environment);

    /// <summary>Every train: each deployable of the catalog in tdd, uat and prod.</summary>
    IReadOnlyList<DeliveryPolicy> All();

    /// <summary>The trains an Octopus event belongs to: those of the deployables that have an Octopus project. The events carry no project name (the watcher watches one project), so each of them reaches every such train.</summary>
    IReadOnlyList<DeliveryPolicy> ForOctopusEvents();

    /// <summary>The trains of the deployables built from <paramref name="repository"/>: where a merged item of the repository is a candidate.</summary>
    IReadOnlyList<DeliveryPolicy> ForRepository(RepoId repository);
}

/// <summary>The policies from the catalog's current snapshot and the per-deployable settings. Read at every call, so a catalog refresh reaches the next message.</summary>
public sealed class CatalogDeliveryPolicies(Func<CatalogSnapshot> catalog, Func<string, DeliveryPolicySettings?> settings) : IDeliveryPolicies
{
    public DeliveryPolicy? For(string deployable, string environment)
        => All().FirstOrDefault(p => string.Equals(p.Deployable, deployable, StringComparison.OrdinalIgnoreCase) && p.Environment == environment.ToLowerInvariant());

    public IReadOnlyList<DeliveryPolicy> All()
    {
        var list = new List<DeliveryPolicy>();
        foreach (var d in catalog().Deployables)
        {
            var s = settings(d.Name);
            foreach (var env in DeliveryPolicy.Chain)
            {
                // fail closed (v1: without delivery.autoPromote.enabled and the environment named nobody promotes); tdd is deployed by the release's lifecycle, never by the train
                var promoted = s?.Environments?.Any(e => string.Equals(e, env, StringComparison.OrdinalIgnoreCase)) == true;
                var shadowed = !promoted && s?.ShadowEnvironments?.Any(e => string.Equals(e, env, StringComparison.OrdinalIgnoreCase)) == true;   // (W9.10p: decided, recorded, not executed)
                var named = promoted || shadowed;
                var promoter = env == "tdd" ? Promoter.Pipeline : s?.Promoter ?? Promoter.External;
                list.Add(new DeliveryPolicy(d.Name.ToLowerInvariant(), d.Repository, env, promoter, named && env != "tdd", s?.Project ?? "",
                    s?.EnvironmentNames ?? new Dictionary<string, string>(), s?.EnvironmentUrls ?? new Dictionary<string, string>(),
                    EvidenceComposite.HealthPath(s?.HealthPath ?? d.HealthPath), s?.Window ?? new HealthWindow(5, 10), s?.SignoffWaitHours ?? 2, (s?.DependenciesDelivered ?? false) && !shadowed, Shadow: shadowed && env != "tdd", PollSeconds: s?.PollSeconds ?? 120, MinReadGapSeconds: s?.MinReadGapSeconds ?? 30, MigrationPaths: s?.MigrationPaths));
            }
        }
        return list;
    }

    public IReadOnlyList<DeliveryPolicy> ForOctopusEvents() => All().Where(p => p.Project.Length > 0).ToList();

    public IReadOnlyList<DeliveryPolicy> ForRepository(RepoId repository) => All().Where(p => p.Repository == repository).ToList();
}
