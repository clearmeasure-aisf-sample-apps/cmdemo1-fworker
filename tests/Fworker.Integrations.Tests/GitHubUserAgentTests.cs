using System.Net;
using Fworker.Contracts.Ids;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>GitHub answers a request without a User-Agent 403 ("Request forbidden by administrative rules"): every client that reaches it sends one.</summary>
public class GitHubUserAgentTests
{
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<string> Agents = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Agents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    sealed class Source : IGitHubCredentialSource
    {
        public async ValueTask<GitHubCredential?> For(string owner, CancellationToken ct = default) { await Task.Yield(); return new GitHubCredential("t", "tok"); }
    }

    static readonly RepoId Repo = new("acme", "orders");

    [Fact]
    public void TheValueNamesTheProductAndAVersion() =>
        Assert.Matches(@"^factory-worker-gateway/\S+$", GitHubUserAgent.Value);

    [Fact]
    public async Task TheRepoClientOfAClientWithTheHandlerSendsOne()
    {
        var rec = new Recorder();
        var client = await new GitHubClientFactory(new HttpClient(new GitHubUserAgentHandler(rec)) { BaseAddress = new Uri("http://x/") }, new Source(), new GitHubBudget()).For(Repo);
        (await client.Send(HttpMethod.Get, "pulls/1")).Dispose();
        Assert.Equal([GitHubUserAgent.Value], rec.Agents);
    }

    [Fact]
    public async Task TheUserAgentHandlerAddsOneAndKeepsOneThatIsSet()
    {
        var rec = new Recorder();
        using var http = new HttpClient(new GitHubUserAgentHandler(rec)) { BaseAddress = new Uri("http://x/") };
        (await http.GetAsync("a")).Dispose();
        using var own = new HttpRequestMessage(HttpMethod.Get, "b");
        own.Headers.TryAddWithoutValidation("User-Agent", "factory-worker-gateway");
        (await http.SendAsync(own)).Dispose();
        Assert.Equal([GitHubUserAgent.Value, "factory-worker-gateway"], rec.Agents);
    }

    [Fact]
    public async Task TheAuthHandlerSendsOne()
    {
        var rec = new Recorder();
        var tokens = GitHubTokenProvider.FromEnvironment(n => n == "GH_TOKEN" ? "tok" : null)!;
        using var http = new HttpClient(new GitHubAuthHandler(tokens, rec)) { BaseAddress = new Uri("http://x/") };
        (await http.GetAsync("a")).Dispose();
        Assert.Equal([GitHubUserAgent.Value], rec.Agents);
    }
}
