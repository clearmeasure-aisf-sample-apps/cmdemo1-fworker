using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Catalog;
using Xunit;

namespace Fworker.Integrations.Tests;

public class SystemCatalogLoaderTests
{
    // the image's instance.json, reduced to what the fallback reads; the home repository is sample-workorders
    const string Instance = """
        { "owner": "sample-org", "repo": "sample-workorders", "project": 12,
          "octopus": { "project": "cmdemo1-ui", "healthPath": "/_healthcheck/detailed" } }
        """;

    static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "system.sample.json"));

    static SystemCatalogLoader Loader(Func<CancellationToken, Task<string?>> read, string instance = Instance) => new(read, instance);

    [Fact]
    public void BeforeTheFirstRefreshTheCatalogIsTheInstanceEntry()
    {
        var c = Loader(_ => Task.FromResult<string?>(Fixture())).Current;
        Assert.Equal(CatalogSource.InstanceFallback, c.Source);
        Assert.Equal(new RepoId("sample-org", "sample-workorders"), c.Home);
        Assert.Equal(new BoardRef("sample-org", 12), c.Board);
        var repo = Assert.Single(c.Repositories);
        Assert.Equal(c.Home, repo.Id);
        Assert.Equal("ui", Assert.Single(repo.Deployables).Name);
    }

    [Fact]
    public async Task LoadsTheSampleSystemJson()
    {
        var loader = Loader(_ => Task.FromResult<string?>(Fixture()));
        var r = await loader.RefreshAsync();
        Assert.True(r.Succeeded, r.Error);
        Assert.Null(loader.LastError);
        var c = loader.Current;
        Assert.Equal(CatalogSource.SystemJson, c.Source);
        Assert.Equal("sample-org", c.Owner); // "Sample-Org" in the file, lowercased
        Assert.Equal(new RepoId("sample-org", "sample-workorders"), c.Home); // the file has no home: instance.json
        Assert.Equal(new BoardRef("sample-org", 12), c.Board);
        Assert.Equal(4, c.Deployables.Count());
        Assert.Equal(3, c.Repositories.Count);

        var ui = c.FindDeployable("UI")!;
        Assert.Equal(new RepoId("sample-org", "sample-workorders"), ui.Repository);
        Assert.Equal("/_healthcheck", ui.HealthPath);
        Assert.Equal("/_build", ui.BuildPath);
        Assert.Equal(8080, ui.Port);
        Assert.True(ui.PipelineSignOff);
        Assert.Equal("appservice", c.FindDeployable("app2-ui")!.Hosting);
        Assert.False(c.FindDeployable("app2-ui")!.PipelineSignOff);
        Assert.Null(c.FindDeployable("nope"));

        var gw = c.FindRepository(new RepoId("Sample-Org", "SAMPLE-GATEWAY"))!;
        Assert.Equal(["gateway", "gateway-worker"], gw.Deployables.Select(d => d.Name).ToArray());
    }

    [Fact]
    public async Task ReportsTheHomeEntryMismatchesWithoutChangingAnything()
    {
        var loader = Loader(_ => Task.FromResult<string?>(Fixture()));
        await loader.RefreshAsync();
        var m = loader.Current.Mismatches;
        Assert.Contains(m, x => x.Contains("healthPath") && x.Contains("/_healthcheck/detailed"));
        Assert.DoesNotContain(m, x => x.Contains("Octopus project")); // cmdemo1-ui derives from slug cmdemo1 and name ui: it matches
        Assert.Equal("/_healthcheck", loader.Current.FindDeployable("ui")!.HealthPath); // reported, not rewritten
    }

    [Fact]
    public async Task OctopusProjectMismatchIsReported()
    {
        var loader = Loader(_ => Task.FromResult<string?>(Fixture()), Instance.Replace("cmdemo1-ui", "sample-web"));
        await loader.RefreshAsync();
        Assert.Contains(loader.Current.Mismatches, x => x.Contains("cmdemo1-ui") && x.Contains("sample-web"));
    }

    [Fact]
    public async Task HomeAndBoardOfSystemJsonWinOverInstanceJson()
    {
        var text = Fixture().Replace("\"deployables\": [", "\"home\": \"Sample-Org/Sample-Gateway\", \"board\": { \"owner\": \"Other\", \"number\": 9 }, \"deployables\": [");
        var loader = Loader(_ => Task.FromResult<string?>(text));
        var r = await loader.RefreshAsync();
        Assert.True(r.Succeeded, r.Error);
        Assert.Equal(new RepoId("sample-org", "sample-gateway"), loader.Current.Home);
        Assert.Equal(new BoardRef("Other", 9), loader.Current.Board);
        Assert.Contains(loader.Current.Mismatches, x => x.StartsWith("home:"));
    }

    [Fact]
    public async Task AHomeWithNoDeployableIsStillInTheCatalog()
    {
        var text = Fixture().Replace("\"deployables\": [", "\"home\": \"sample-fworker\", \"deployables\": [");
        var loader = Loader(_ => Task.FromResult<string?>(text));
        await loader.RefreshAsync();
        var home = loader.Current.FindRepository(loader.Current.Home)!;
        Assert.Empty(home.Deployables);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "system": { "githubOrg": "o" } }""")]
    [InlineData("""{ "system": { "githubOrg": "o" }, "deployables": [ { "repository": "r" } ] }""")]
    [InlineData("""{ "system": { "githubOrg": "o" }, "deployables": [ { "name": "a" } ] }""")]
    [InlineData("""{ "system": { "githubOrg": "o" }, "deployables": [ { "name": "a", "repository": "r/x" } ] }""")]
    [InlineData("""{ "system": { "githubOrg": "o" }, "deployables": [ { "name": "a", "repository": "r" }, { "name": "A", "repository": "s" } ] }""")]
    [InlineData("""{ "system": { "githubOrg": "o" }, "board": { "number": 0 }, "deployables": [] }""")]
    [InlineData("""{ "system": { "githubOrg": "o" }, "home": "a/b/c", "deployables": [] }""")]
    public async Task InvalidSystemJsonKeepsTheLastGoodCopy(string bad)
    {
        string? next = Fixture();
        var loader = Loader(_ => Task.FromResult(next));
        await loader.RefreshAsync();
        var good = loader.Current;

        next = bad;
        var r = await loader.RefreshAsync();
        Assert.False(r.Succeeded);
        Assert.False(string.IsNullOrEmpty(r.Error));
        Assert.Equal(r.Error, loader.LastError);
        Assert.Same(good, loader.Current);
    }

    [Fact]
    public async Task AnInvalidFirstReadKeepsTheInstanceFallback()
    {
        var loader = Loader(_ => Task.FromResult<string?>("{}"));
        var first = loader.Current;
        await loader.RefreshAsync();
        Assert.Same(first, loader.Current);
        Assert.Equal(CatalogSource.InstanceFallback, loader.Current.Source);
    }

    [Fact]
    public async Task UnreachableOrMissingSourceNeverThrowsAndRecoversOnTheNextRefresh()
    {
        var mode = 0;
        var loader = Loader(_ => mode switch
        {
            0 => throw new HttpRequestException("503"),
            1 => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(Fixture()),
        });
        var r0 = await loader.RefreshAsync();
        Assert.False(r0.Succeeded);
        Assert.Contains("503", r0.Error);
        mode = 1;
        Assert.False((await loader.RefreshAsync()).Succeeded);
        Assert.Equal(CatalogSource.InstanceFallback, loader.Current.Source);
        mode = 2;
        Assert.True((await loader.RefreshAsync()).Succeeded);
        Assert.Null(loader.LastError);
        Assert.Equal(CatalogSource.SystemJson, loader.Current.Source);
    }

    [Fact]
    public async Task CancellationIsPassedOn()
    {
        var loader = Loader(ct => Task.FromCanceled<string?>(ct));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.RefreshAsync(cts.Token));
    }

    [Fact]
    public void InstanceWithoutOwnerAndRepoHasNoFallback() =>
        Assert.Throws<InvalidOperationException>(() => Loader(_ => Task.FromResult<string?>(null), "{}"));

    [Fact]
    public void LoadedAtComesFromTheTimeProvider()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(SystemCatalogLoader.Load(Fixture(), Instance, now, out var snap));
        Assert.Equal(now, snap!.LoadedAt);
    }
}
