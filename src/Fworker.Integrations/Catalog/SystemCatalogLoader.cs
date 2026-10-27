using System.Text.Json;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;

namespace Fworker.Integrations.Catalog;

/// <summary>
/// The loader of the system catalog as a library (task W1.1; hook H2 of docs/design/system-gateway.md 3). It reads the text of cmdemo1-system's
/// <c>system.json</c> through a delegate (the GitHub API call is wired by W1.2, so this class does no network and touches no gateway composition),
/// validates it and keeps the <b>last good copy</b>. The first copy is the image's <c>instance.json</c> single-repository entry. A field
/// <c>system.json</c> lacks falls back to <c>instance.json</c>: <c>home</c> and <c>board</c> are optional keys of the file. Differences between the two
/// on the home entry are reported in <see cref="CatalogSnapshot.Mismatches"/>; they change no behaviour.
/// </summary>
public sealed class SystemCatalogLoader : ISystemCatalog
{
    readonly Func<CancellationToken, Task<string?>> _readSystemJson;
    readonly JsonElement _instance;
    readonly TimeProvider _time;
    readonly object _gate = new();
    CatalogSnapshot _current;
    string? _lastError;

    /// <param name="readSystemJson">Returns the text of system.json, or null when it is not available; may throw (a failure is kept as the error).</param>
    /// <param name="instanceJson">The text of config/instance.json: the fallback and the source of the fields system.json lacks.</param>
    public SystemCatalogLoader(Func<CancellationToken, Task<string?>> readSystemJson, string instanceJson, TimeProvider? time = null)
    {
        _readSystemJson = readSystemJson;
        _time = time ?? TimeProvider.System;
        using var doc = JsonDocument.Parse(instanceJson);
        _instance = doc.RootElement.Clone();
        _current = FromInstance(_instance, _time.GetUtcNow());
    }

    public CatalogSnapshot Current { get { lock (_gate) return _current; } }

    public string? LastError { get { lock (_gate) return _lastError; } }

    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        CatalogSnapshot? loaded = null;
        string? error;
        try
        {
            var text = await _readSystemJson(ct).ConfigureAwait(false);
            error = text is null ? "system.json was not available" : Load(text, _instance, _time.GetUtcNow(), out loaded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { error = $"system.json could not be read: {e.Message}"; }

        lock (_gate)
        {
            _lastError = error;
            if (error is null) _current = loaded!;
            return new CatalogRefreshResult(error is null, error, _current);
        }
    }

    /// <summary>Parse and validate one system.json. Returns the error, or null with the snapshot. Public so a caller can validate a file without a loader.</summary>
    public static string? Load(string systemJson, string instanceJson, DateTimeOffset now, out CatalogSnapshot? snapshot)
    {
        using var inst = JsonDocument.Parse(instanceJson);
        return Load(systemJson, inst.RootElement, now, out snapshot);
    }

    static string? Load(string systemJson, JsonElement instance, DateTimeOffset now, out CatalogSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            using var doc = JsonDocument.Parse(systemJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "system.json is not a JSON object";

            var slug = Str(Obj(root, "system"), "slug");
            var owner = Str(Obj(root, "system"), "githubOrg");
            if (owner is null) return "system.json has no system.githubOrg";
            if (!ItemId.IsValidName(owner)) return $"system.githubOrg '{owner}' is not a GitHub owner name";

            if (!root.TryGetProperty("deployables", out var depsEl) || depsEl.ValueKind != JsonValueKind.Array) return "system.json has no deployables array";
            var deployables = new List<DeployableEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var d in depsEl.EnumerateArray())
            {
                var at = $"deployables[{i++}]";
                if (d.ValueKind != JsonValueKind.Object) return $"{at} is not an object";
                var name = Str(d, "name");
                if (string.IsNullOrWhiteSpace(name)) return $"{at} has no name";
                if (!names.Add(name)) return $"{at}: the deployable name '{name}' is used twice";
                var repoName = Str(d, "repository");
                if (repoName is null) return $"deployable '{name}' has no repository";
                if (!ItemId.IsValidName(repoName)) return $"deployable '{name}': '{repoName}' is not a GitHub repository name";
                int? port = d.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pn) ? pn : null;
                var signOff = d.TryGetProperty("pipelineSignOff", out var so) && so.ValueKind == JsonValueKind.True;
                deployables.Add(new DeployableEntry(name, new RepoId(owner, repoName), Str(d, "healthPath"), Str(d, "buildPath"), port, Str(d, "hosting"), signOff));
            }

            // home: system.json's own key when it has one ("owner/repo" or just the repository name), else instance.json
            RepoId home;
            var homeText = Str(root, "home");
            if (homeText is not null)
            {
                if (homeText.Contains('/') ? !RepoId.TryParse(homeText, out home) : !TryRepo(owner, homeText, out home)) return $"home '{homeText}' is not a repository";
            }
            else if (!InstanceHome(instance, out home)) return "system.json has no home and instance.json has no owner and repo";

            BoardRef? board = null;
            if (root.TryGetProperty("board", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                var bo = Str(b, "owner") ?? owner;
                if (!b.TryGetProperty("number", out var bn) || !bn.TryGetInt32(out var number) || number < 1) return "board.number is not a project number";
                board = new BoardRef(bo, number);
            }
            else board = InstanceBoard(instance);

            var repositories = deployables.GroupBy(d => d.Repository)
                .Select(g => new RepositoryEntry(g.Key, g.ToList())).ToList();
            // the home repository is in the catalog even when no deployable is built from it
            if (repositories.All(r => r.Id != home)) repositories.Insert(0, new RepositoryEntry(home, []));

            snapshot = new CatalogSnapshot(CatalogSource.SystemJson, now, owner.ToLowerInvariant(), home, board, repositories, Mismatches(instance, home, slug, deployables));
            return null;
        }
        catch (JsonException e) { return $"system.json is not valid JSON: {e.Message}"; }
    }

    /// <summary>Differences on the home entry that the loader reports and does not act on (the rule scripts keep reading instance.json until R9).</summary>
    static List<string> Mismatches(JsonElement instance, RepoId home, string? slug, List<DeployableEntry> deployables)
    {
        var list = new List<string>();
        if (InstanceHome(instance, out var instHome) && instHome != home)
            list.Add($"home: system.json says {home}, instance.json says {instHome}");
        var oct = Obj(instance, "octopus");
        var instProject = Str(oct, "project");
        var instHealth = Str(oct, "healthPath");
        foreach (var d in deployables.Where(d => d.Repository == home))
        {
            if (instHealth is not null && d.HealthPath is not null && d.HealthPath != instHealth)
                list.Add($"deployable {d.Name}: healthPath is {d.HealthPath} in system.json and {instHealth} in instance.json (octopus.healthPath)");
            var derived = slug is null ? null : $"{slug}-{d.Name}";
            if (instProject is not null && derived is not null && !string.Equals(derived, instProject, StringComparison.OrdinalIgnoreCase))
                list.Add($"deployable {d.Name}: the Octopus project derived from system.json is {derived}, instance.json (octopus.project) has {instProject}");
        }
        return list;
    }

    static CatalogSnapshot FromInstance(JsonElement instance, DateTimeOffset now)
    {
        if (!InstanceHome(instance, out var home)) throw new InvalidOperationException("instance.json has no owner and repo: the catalog has no fallback");
        var oct = Obj(instance, "octopus");
        var deployables = new List<DeployableEntry>();
        var project = Str(oct, "project");
        // "cmdemo1-ui" names the deployable "ui": the part after the system slug (the Octopus project is {slug}-{deployable})
        if (project is not null && project.IndexOf('-') is > 0 and var dash && dash < project.Length - 1)
            deployables.Add(new DeployableEntry(project[(dash + 1)..], home, Str(oct, "healthPath"), null, null, null, false));
        return new CatalogSnapshot(CatalogSource.InstanceFallback, now, home.Owner, home, InstanceBoard(instance), [new RepositoryEntry(home, deployables)], []);
    }

    static bool InstanceHome(JsonElement instance, out RepoId home)
    {
        home = default;
        var o = Str(instance, "owner");
        var r = Str(instance, "repo");
        return o is not null && r is not null && TryRepo(o, r, out home);
    }

    static BoardRef? InstanceBoard(JsonElement instance)
    {
        var o = Str(instance, "owner");
        return o is not null && instance.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n > 0 ? new BoardRef(o, n) : null;
    }

    static bool TryRepo(string owner, string repo, out RepoId id)
    {
        id = default;
        if (!ItemId.IsValidName(owner) || !ItemId.IsValidName(repo)) return false;
        id = new RepoId(owner, repo);
        return true;
    }

    static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
