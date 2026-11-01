using Xunit;

namespace Gateway.Tests;

/// <summary>GitHub refuses a request without a User-Agent (403). Every HttpClient of the gateway or of src that is built for api.github.com goes through a handler that sets one.</summary>
public class GitHubUserAgentWiringTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FactoryWorker.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("no FactoryWorker.sln above the test binaries");
    }

    [Fact]
    public void EveryGitHubClientIsBuiltWithAHandlerThatSendsAUserAgent()
    {
        var root = Root();
        var bad = new List<string>();
        var seen = 0;
        foreach (var dir in new[] { "gateway", "src" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var l = lines[i];
                    if (!l.Contains("new HttpClient(") && !l.Contains("new HttpClient {")) continue;
                    if (!l.Contains("api.github.com") && !l.Contains("GitHubAuthHandler") && !l.Contains("HttpGitHubApi")) continue;
                    seen++;
                    if (!l.Contains("GitHubAuthHandler") && !l.Contains("GitHubUserAgentHandler")) bad.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                }
            }
        Assert.True(seen >= 10, "the scan found too few GitHub clients; it no longer sees them");
        Assert.True(bad.Count == 0, "GitHub clients without the User-Agent handler: " + string.Join(", ", bad));
    }
}
