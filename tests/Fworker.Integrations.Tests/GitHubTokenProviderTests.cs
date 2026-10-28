using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

public sealed class GitHubTokenProviderTests
{
    // a throwaway key made at run time; no key is in the repository
    static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    sealed class Mint(Func<HttpRequestMessage, HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        public List<(string Path, string? Auth)> Calls { get; } = [];
        public int Count;
        public DateTimeOffset Expiry = DateTimeOffset.Parse("2030-01-01T01:00:00Z");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Calls) Calls.Add((r.RequestUri!.AbsolutePath, r.Headers.Authorization?.ToString()));
            var n = Interlocked.Increment(ref Count);
            if (answer != null) return Task.FromResult(answer(r));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(new JsonObject { ["token"] = "tok-" + n, ["expires_at"] = Expiry.ToString("o") }.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    static Func<string, string?> Env(params (string, string)[] kv) { var d = kv.ToDictionary(x => x.Item1, x => x.Item2); return k => d.GetValueOrDefault(k); }
    static Func<string, string?> AppEnv(string key) => Env(("FACTORY_APP_AUTHOR_ID", "123"), ("FACTORY_APP_AUTHOR_INSTALLATION", "456"), ("FACTORY_APP_AUTHOR_SLUG", "my-app"), ("FACTORY_APP_AUTHOR_KEY", key));
    static HttpClient Client(Mint m) => new(m) { BaseAddress = new Uri("http://github.test/") };
    static byte[] Unb64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

    [Fact]
    public async Task GhToken_Wins_AndNothingIsMinted()
    {
        var m = new Mint();
        var p = GitHubTokenProvider.FromEnvironment(Env(("GH_TOKEN", " " + "ghp_" + new string('A', 26) + " "), ("FACTORY_APP_AUTHOR_ID", "1"), ("FACTORY_APP_AUTHOR_INSTALLATION", "2"), ("FACTORY_APP_AUTHOR_KEY", Pem)), Client(m))!;
        Assert.Equal("ghp_" + new string('A', 26), await p.Token());
        Assert.Equal(0, m.Count);
        Assert.Null(p.AppLogin);
    }

    [Fact]
    public void NoIdentity_IsNull()
    {
        Assert.Null(GitHubTokenProvider.FromEnvironment(Env()));
        Assert.Null(GitHubTokenProvider.FromEnvironment(Env(("GH_TOKEN", "unset"))));
        Assert.Null(GitHubTokenProvider.FromEnvironment(Env(("GH_TOKEN", " "))));
        Assert.Null(GitHubTokenProvider.FromEnvironment(Env(("GH_TOKEN", "unset"), ("FACTORY_APP_AUTHOR_ID", "1"), ("FACTORY_APP_AUTHOR_INSTALLATION", "2"), ("FACTORY_APP_AUTHOR_KEY", "unset"))));
        Assert.Null(GitHubTokenProvider.FromEnvironment(Env(("FACTORY_APP_AUTHOR_ID", "1"), ("FACTORY_APP_AUTHOR_KEY", Pem))));   // no installation
    }

    [Fact]
    public async Task App_MintsWithASignedJwt_AndCaches()
    {
        var m = new Mint();
        var now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        var p = GitHubTokenProvider.FromEnvironment(AppEnv(Pem), Client(m), () => now)!;
        Assert.Equal("my-app[bot]", p.AppLogin);
        Assert.Equal("tok-1", await p.Token());
        Assert.Equal("tok-1", await p.Token());
        Assert.Equal(1, m.Count);
        Assert.Equal("/app/installations/456/access_tokens", m.Calls[0].Path);
        var parts = m.Calls[0].Auth!["Bearer ".Length..].Split('.');
        Assert.Equal("RS256", JsonNode.Parse(Unb64(parts[0]))!["alg"]!.ToString());
        var pay = JsonNode.Parse(Unb64(parts[1]))!;
        Assert.Equal("123", pay["iss"]!.ToString());
        Assert.Equal(now.ToUnixTimeSeconds() + 540, (long)pay["exp"]!);
        using var rsa = RSA.Create(); rsa.ImportFromPem(Pem);
        Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]), Unb64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task App_RefreshesFiveMinutesBeforeExpiry()
    {
        var m = new Mint();
        var now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");   // the token expires at 01:00
        var p = GitHubTokenProvider.FromEnvironment(AppEnv(Pem), Client(m), () => now)!;
        Assert.Equal("tok-1", await p.Token());
        now = DateTimeOffset.Parse("2030-01-01T00:54:59Z");
        Assert.Equal("tok-1", await p.Token());
        now = DateTimeOffset.Parse("2030-01-01T00:55:01Z");
        Assert.Equal("tok-2", await p.Token());
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public async Task App_ConcurrentCallers_MintOnce()
    {
        var m = new Mint();
        var p = GitHubTokenProvider.FromEnvironment(AppEnv(Pem), Client(m), () => DateTimeOffset.Parse("2030-01-01T00:00:00Z"))!;
        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () => await p.Token())));
        Assert.All(all, t => Assert.Equal("tok-1", t));
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public async Task KeyWithEscapedNewlines_IsAccepted()
    {
        var p = GitHubTokenProvider.FromEnvironment(AppEnv(Pem.Replace("\n", "\\n")), Client(new Mint()))!;
        Assert.Equal("tok-1", await p.Token());
    }

    [Fact]
    public async Task Failure_SurfacesTheStatus_NotTheBodyOrKey()
    {
        var secretBody = "ghs_" + new string('Z', 30);
        var m = new Mint(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(secretBody) });
        var p = GitHubTokenProvider.FromEnvironment(AppEnv(Pem), Client(m))!;
        var e = await Assert.ThrowsAsync<GitHubTokenException>(async () => await p.Token());
        Assert.Contains("401", e.Message);
        Assert.DoesNotContain(secretBody, e.ToString());
        Assert.DoesNotContain("PRIVATE KEY", e.ToString());
        await Assert.ThrowsAsync<GitHubTokenException>(async () => await p.Token());   // the next call tries again
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public async Task BadKey_AndEmptyAnswer_AreRefusedWithoutLeaking()
    {
        var bad = GitHubTokenProvider.FromEnvironment(AppEnv("not a key"), Client(new Mint()))!;
        var e = await Assert.ThrowsAsync<GitHubTokenException>(async () => await bad.Token());
        Assert.DoesNotContain("not a key", e.ToString());
        var empty = GitHubTokenProvider.FromEnvironment(AppEnv(Pem), Client(new Mint(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") })))!;
        await Assert.ThrowsAsync<GitHubTokenException>(async () => await empty.Token());
    }

    [Fact]
    public async Task Handler_SetsTheBearerPerRequest_AndFollowsTheRefresh()
    {
        var m = new Mint();
        var now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        var p = GitHubTokenProvider.FromEnvironment(AppEnv(Pem), Client(m), () => now)!;
        var seen = new List<string?>();
        var api = new HttpClient(new GitHubAuthHandler(p, new Mint(r => { lock (seen) seen.Add(r.Headers.Authorization?.Parameter); return new HttpResponseMessage(HttpStatusCode.OK); }))) { BaseAddress = new Uri("http://api.test/") };
        await api.GetAsync("x");
        now = now.AddMinutes(56);
        await api.GetAsync("x");
        Assert.Equal(["tok-1", "tok-2"], seen);
        var cred = await new ProviderCredentialSource(p).For("anyone");
        Assert.StartsWith("tok-", cred!.Token);   // minted again: the clock is inside the refresh window of the fixed expiry
    }
}
