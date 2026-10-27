using Fworker.Contracts.Ids;

namespace Fworker.Contracts.Catalog;

// The system catalog (hook H2, docs/design/system-gateway.md 3): the repositories and deployables of the system, from cmdemo1-system's system.json,
// with the image's instance.json single-repository entry as the fallback. Read-only for the gateway; the loader is Fworker.Integrations.Catalog.

/// <summary>The project board of the system: <c>{owner, number}</c>.</summary>
public sealed record BoardRef(string Owner, int Number);

/// <summary>One deployable of <c>system.json</c>, as the gateway needs it. Fields the file lacks are null.</summary>
public sealed record DeployableEntry(
    string Name,
    RepoId Repository,
    string? HealthPath,
    string? BuildPath,
    int? Port,
    string? Hosting,
    bool PipelineSignOff);

/// <summary>A repository of the system with the deployables built from it (none for a repository with no deployable).</summary>
public sealed record RepositoryEntry(RepoId Id, IReadOnlyList<DeployableEntry> Deployables);

/// <summary>Where a snapshot came from.</summary>
public enum CatalogSource { InstanceFallback, SystemJson }

/// <summary>One validated, immutable view of the catalog.</summary>
public sealed record CatalogSnapshot(
    CatalogSource Source,
    DateTimeOffset LoadedAt,
    string Owner,
    RepoId Home,
    BoardRef? Board,
    IReadOnlyList<RepositoryEntry> Repositories,
    IReadOnlyList<string> Mismatches)
{
    public IEnumerable<DeployableEntry> Deployables => Repositories.SelectMany(r => r.Deployables);

    public RepositoryEntry? FindRepository(RepoId id) => Repositories.FirstOrDefault(r => r.Id == id);

    public DeployableEntry? FindDeployable(string name) =>
        Deployables.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a refresh did. A failed refresh keeps the last good snapshot: an unreachable or invalid <c>system.json</c> never stops the gateway.</summary>
public sealed record CatalogRefreshResult(bool Succeeded, string? Error, CatalogSnapshot Snapshot);

public interface ISystemCatalog
{
    /// <summary>The last good snapshot; never null (before the first refresh it is the instance.json fallback).</summary>
    CatalogSnapshot Current { get; }

    /// <summary>The error of the last refresh, or null when it succeeded.</summary>
    string? LastError { get; }

    /// <summary>Read the source again, validate it and swap the snapshot; on failure keep the last good one.</summary>
    Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default);
}
