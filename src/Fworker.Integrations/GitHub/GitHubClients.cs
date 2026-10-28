using Fworker.Contracts.Ids;

namespace Fworker.Integrations.GitHub;

/// <summary>The credential for an owner: <see cref="Label"/> names it for the budget and the logs (never the token); <see cref="Token"/> is the bearer.</summary>
public sealed record GitHubCredential(string Label, string Token);

/// <summary>Resolves the credential of an owner: one installation per owner (hook H6 of docs/design/system-gateway.md 3). The App-installation exchange is another implementation of this.</summary>
public interface IGitHubCredentialSource
{
    /// <summary>The credential for the owner, or null when the factory has no installation there.</summary>
    ValueTask<GitHubCredential?> For(string owner, CancellationToken ct = default);
}

/// <summary>A fixed table of credentials by owner (case-insensitive), with an optional default; for tests and for a gateway with a token per owner.</summary>
public sealed class StaticCredentialSource : IGitHubCredentialSource
{
    readonly Dictionary<string, GitHubCredential> _byOwner;
    readonly GitHubCredential? _default;
    public StaticCredentialSource(IEnumerable<KeyValuePair<string, GitHubCredential>> byOwner, GitHubCredential? fallback = null)
    {
        _byOwner = new(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in byOwner) _byOwner[kv.Key] = kv.Value;
        _default = fallback;
    }
    public ValueTask<GitHubCredential?> For(string owner, CancellationToken ct = default) =>
        ValueTask.FromResult<GitHubCredential?>(_byOwner.TryGetValue(owner, out var c) ? c : _default);
}

/// <summary>Thrown when the owner of a repository has no installation: a permanent condition, not a retry.</summary>
public sealed class NoInstallationException(string owner) : Exception($"no GitHub installation for owner '{owner}'");

/// <summary>
/// The GitHub client of one repository (hook H6): every call goes to <c>repos/{owner}/{repo}/...</c> of that repository with the credential of its owner, and reports
/// the answer's rate-limit headers to the shared <see cref="GitHubBudget"/> under (credential, resource). It carries no state but the repository: the
/// <see cref="GitHubClientFactory"/> builds one per repository.
/// </summary>
public sealed class GitHubRepoClient
{
    readonly HttpClient _http;
    readonly GitHubCredential _credential;
    readonly GitHubBudget _budget;
    public RepoId Repo { get; }
    public string CredentialLabel => _credential.Label;

    internal GitHubRepoClient(HttpClient http, RepoId repo, GitHubCredential credential, GitHubBudget budget)
    { _http = http; Repo = repo; _credential = credential; _budget = budget; }

    /// <summary>A request path under the repository: <c>Path("issues/5/comments")</c> is <c>repos/o/r/issues/5/comments</c>.</summary>
    public string Path(string relative) => $"repos/{Repo.Owner}/{Repo.Repo}/{relative.TrimStart('/')}";

    /// <summary>Sends a request to a path relative to the repository (see <see cref="Path"/>). Rate-limit headers are recorded. The caller classifies the answer.</summary>
    public async Task<HttpResponseMessage> Send(HttpMethod method, string relative, HttpContent? content = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, Path(relative)) { Content = content };
        req.Headers.Authorization = new("Bearer", _credential.Token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Report(resp);
        return resp;
    }

    void Report(HttpResponseMessage resp)
    {
        static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
        if (H(resp, "x-ratelimit-resource") is { } resource)
            _budget.Observe(_credential.Label, resource, H(resp, "x-ratelimit-remaining"), H(resp, "x-ratelimit-reset"), H(resp, "x-ratelimit-limit"));
    }

    /// <summary>Is the budget of this client's credential low for the resource?</summary>
    public (bool Low, DateTimeOffset ResetAt) Low(string resource = "core") => _budget.Low(_credential.Label, resource);
}

/// <summary>Builds the client of a repository: resolves the installation of its owner, so two repositories of one owner share a credential and two owners do not. The HttpClient is shared.</summary>
public sealed class GitHubClientFactory
{
    readonly HttpClient _http;
    readonly IGitHubCredentialSource _credentials;
    public GitHubBudget Budget { get; }

    public GitHubClientFactory(HttpClient http, IGitHubCredentialSource credentials, GitHubBudget budget)
    { _http = http; _credentials = credentials; Budget = budget; }

    /// <exception cref="NoInstallationException">The owner has no installation.</exception>
    public async Task<GitHubRepoClient> For(RepoId repo, CancellationToken ct = default)
    {
        var cred = await _credentials.For(repo.Owner, ct) ?? throw new NoInstallationException(repo.Owner);
        return new GitHubRepoClient(_http, repo, cred, Budget);
    }
}
