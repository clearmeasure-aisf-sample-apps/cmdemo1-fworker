using System.Reflection;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// The User-Agent of every request the gateway sends to GitHub. GitHub answers a request without one 403 "Request forbidden by administrative rules.
/// Please make sure your request has a User-Agent header". The value is "factory-worker-gateway/&lt;version&gt;" (the rule scripts send the bare
/// "factory-worker-gateway", which the fake's write recorder tells apart from this one).
/// </summary>
public static class GitHubUserAgent
{
    public const string Product = "factory-worker-gateway";

    public static string Value { get; } = Product + "/" + Version();

    static string Version()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(GitHubUserAgent).Assembly;
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        if (string.IsNullOrWhiteSpace(v)) v = asm.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(v) ? "0.0.0" : v.Trim().Replace(' ', '_');
    }

    /// <summary>Sets the User-Agent unless the request carries one already.</summary>
    public static void Apply(HttpRequestMessage request)
    {
        if (request.Headers.UserAgent.Count == 0) request.Headers.TryAddWithoutValidation("User-Agent", Value);
    }
}

/// <summary>The one place that gives a GitHub client its User-Agent: every HttpClient that talks to api.github.com goes through this handler (GitHubAuthHandler does the same itself).</summary>
public sealed class GitHubUserAgentHandler(HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        GitHubUserAgent.Apply(request);
        return base.SendAsync(request, ct);
    }
}
