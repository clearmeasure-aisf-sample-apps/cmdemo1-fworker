using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The test seam POST /api/v1/_test/advance (gateway/Endpoints/TestAdvanceEndpoint.cs) exists only with FACTORY_TEST_HOOKS=1 against a GitHub that is not api.github.com. The deployed configuration sets neither: the route is not mapped.
/// </summary>
public sealed class TestAdvanceEndpointTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    static Func<string, string?> Env(params (string Name, string Value)[] vars) => name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Theory]
    [InlineData("1", "http://127.0.0.1:5000", true)]       // the fake of the suites
    [InlineData("1", "https://api.github.com", false)]      // the real GitHub
    [InlineData("1", "https://API.GITHUB.COM/", false)]
    [InlineData("1", "", false)]                            // no base: the real GitHub
    [InlineData("1", "not a url", false)]
    [InlineData("0", "http://127.0.0.1:5000", false)]
    [InlineData("true", "http://127.0.0.1:5000", false)]    // only 1
    [InlineData("", "http://127.0.0.1:5000", false)]
    public void TheSeamIsMapped_OnlyWithTheVariableAndAFakeGitHub(string hooks, string githubBase, bool mapped) =>
        Assert.Equal(mapped, TestAdvanceEndpoint.Enabled(Env(("FACTORY_TEST_HOOKS", hooks), ("GITHUB_API_URL", githubBase))));

    [Fact]
    public void TheDeployedEnvironment_MapsNothing_AndTheDeployedConfigurationSetsNeitherVariable()
    {
        Assert.False(TestAdvanceEndpoint.Enabled(_ => null));
        // the image's configuration is config/instance.json with config/deploy.overrides.json over it: neither file names the variable or the hook
        var root = FakeGitHubRig.Root();
        foreach (var file in new[] { "config/instance.json", "config/deploy.overrides.json", "Dockerfile" })
            Assert.DoesNotContain("FACTORY_TEST_HOOKS", File.ReadAllText(Path.Combine(root, file)));
    }

    [Fact]
    public async Task TheRoute_Answers404_WhereTheVariableIsNotSet()
    {
        if (!StubPwsh.Supported) return;
        Assert.Null(Environment.GetEnvironmentVariable("FACTORY_TEST_HOOKS"));
        var a = await gw.Host.SendAsync("POST", "/api/v1/_test/advance", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken, """{"item":1}""");
        Assert.Equal(404, a.Status);
    }
}
