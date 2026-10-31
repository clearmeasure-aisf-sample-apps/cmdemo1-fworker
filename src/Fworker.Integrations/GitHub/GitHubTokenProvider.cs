using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Fworker.Integrations.GitHub;

/// <summary>Thrown when the App installation token cannot be minted. The message carries the HTTP status or the kind of failure, never a token, a key or a response body.</summary>
public sealed class GitHubTokenException(string message) : Exception(message);

/// <summary>
/// The bearer of the gateway's GitHub calls: GH_TOKEN when set (the vault placeholder "unset" counts as none), else an installation token of the author App,
/// minted as the rule scripts do (plugin/scripts/lib/Apps.ps1): a JWT (RS256) of FACTORY_APP_AUTHOR_ID signed with FACTORY_APP_AUTHOR_KEY, exchanged at
/// POST /app/installations/{FACTORY_APP_AUTHOR_INSTALLATION}/access_tokens. The token is cached and minted again <see cref="RefreshBefore"/> before it expires;
/// one mint at a time. Nothing here logs or prints a token or the key.
/// </summary>
public sealed class GitHubTokenProvider
{
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(5);

    readonly string? _fixed;
    readonly string? _appId, _installation, _pem;
    readonly HttpClient? _http;
    readonly Func<DateTimeOffset> _now;
    readonly SemaphoreSlim _gate = new(1, 1);
    string? _cached;
    DateTimeOffset _expires;

    /// <summary>The login of the App's bot ("slug[bot]") when the tokens are the App's; null for GH_TOKEN.</summary>
    public string? AppLogin { get; }

    GitHubTokenProvider(string? fixedToken, string? appId, string? installation, string? pem, string? slug, HttpClient? http, Func<DateTimeOffset>? now)
    {
        _fixed = fixedToken; _appId = appId; _installation = installation; _pem = pem; _http = http; _now = now ?? (() => DateTimeOffset.UtcNow);
        AppLogin = fixedToken is null && !string.IsNullOrWhiteSpace(slug) ? slug.Trim() + "[bot]" : null;
    }

    static bool Unset(string? v) => string.IsNullOrWhiteSpace(v) || v.Trim() == "unset";

    /// <summary>The provider for the environment, or null when it has neither GH_TOKEN nor the App's id, installation and key. <paramref name="http"/> mints the App token (tests); the default reaches GITHUB_API_URL or api.github.com.</summary>
    public static GitHubTokenProvider? FromEnvironment(Func<string, string?> env, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        var id = env("FACTORY_APP_AUTHOR_ID"); var inst = env("FACTORY_APP_AUTHOR_INSTALLATION"); var key = env("FACTORY_APP_AUTHOR_KEY");
        var appComplete = !Unset(id) && !Unset(inst) && !Unset(key);
        // FACTORY_AUTHOR_APP=1 names the App as the gateway's identity and GH_TOKEN is then not looked at (Get-GhToken, Get-ServerLogin in plugin/scripts/lib/Core.ps1; ServiceGitHub): the state comments it adopts and writes
        // are told apart by that login. With the App not configured completely, GH_TOKEN stays what it was.
        var appFirst = env("FACTORY_AUTHOR_APP") == "1" && appComplete;
        if (!appFirst && !Unset(env("GH_TOKEN"))) return new GitHubTokenProvider(env("GH_TOKEN")!.Trim(), null, null, null, null, null, now);
        if (!appComplete) return null;
        if (http is null)
        {
            var api = env("GITHUB_API_URL");
            http = new HttpClient { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(30) };
        }
        return new GitHubTokenProvider(null, id!.Trim(), inst!.Trim(), key!.Replace("\\n", "\n"), env("FACTORY_APP_AUTHOR_SLUG"), http, now);
    }

    /// <summary>A bearer token that is valid now.</summary>
    /// <exception cref="GitHubTokenException">The App token could not be minted.</exception>
    public async ValueTask<string> Token(CancellationToken ct = default)
    {
        if (_fixed is not null) return _fixed;
        if (Fresh() is { } t) return t;
        await _gate.WaitAsync(ct);
        try { return Fresh() ?? await Mint(ct); }
        finally { _gate.Release(); }
    }

    string? Fresh() => _cached is { } c && _now() < _expires - RefreshBefore ? c : null;

    async Task<string> Mint(CancellationToken ct)
    {
        var jwt = Jwt();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{Uri.EscapeDataString(_installation!)}/access_tokens") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
        HttpResponseMessage resp;
        try { resp = await _http!.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new GitHubTokenException("minting the GitHub App installation token failed: " + e.GetType().Name); }
        using (resp)
        {
            if ((int)resp.StatusCode != 201) throw new GitHubTokenException($"GitHub answered {(int)resp.StatusCode} when minting the App installation token");
            string? token = null; DateTimeOffset expires = default;
            try
            {
                var o = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                token = o?["token"]?.ToString();
                expires = DateTimeOffset.Parse(o?["expires_at"]?.ToString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { }
            if (string.IsNullOrEmpty(token) || expires == default) throw new GitHubTokenException("GitHub answered the App installation token request without a token and an expiry");
            _cached = token; _expires = expires;
            return token;
        }
    }

    string Jwt()
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _now().ToUnixTimeSeconds();
        var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var pay = B64(Encoding.UTF8.GetBytes(new JsonObject { ["iat"] = now - 60, ["exp"] = now + 540, ["iss"] = _appId }.ToJsonString()));
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_pem);
            return $"{head}.{pay}.{B64(rsa.SignData(Encoding.UTF8.GetBytes($"{head}.{pay}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { throw new GitHubTokenException("the GitHub App key cannot be used to sign"); }
    }
}

/// <summary>The credential source for the provider's token: the token is read at every <see cref="For"/>, so a reader that asks per request follows the refresh.</summary>
public sealed class ProviderCredentialSource(GitHubTokenProvider tokens, string label = "gateway") : IGitHubCredentialSource
{
    public async ValueTask<GitHubCredential?> For(string owner, CancellationToken ct = default) => new GitHubCredential(label, await tokens.Token(ct));
}

/// <summary>Sets the bearer of every request from the provider, for clients that keep one credential for their life.</summary>
public sealed class GitHubAuthHandler(GitHubTokenProvider tokens, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.Token(ct));
        return await base.SendAsync(request, ct);
    }
}
