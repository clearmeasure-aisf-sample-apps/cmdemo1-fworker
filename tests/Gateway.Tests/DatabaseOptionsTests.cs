using Xunit;

namespace Gateway.Tests;

/// <summary>The database setting FACTORY_DB_CONNECTION (task W2.0, gateway/Configuration/DatabaseOptions.cs): unset means v1 alone.</summary>
public sealed class DatabaseOptionsTests
{
    static Func<string, string?> Env(string? value) => n => n == "FACTORY_DB_CONNECTION" ? value : null;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetOrBlank_NoDatabase(string? value)
    {
        var d = DatabaseOptions.From(Env(value));
        Assert.False(d.Configured);
        Assert.Null(d.ConnectionString);
    }

    [Fact]
    public void Set_IsConfigured_AndTrimmed()
    {
        var d = DatabaseOptions.From(Env("  Server=db;Database=f;  "));
        Assert.True(d.Configured);
        Assert.Equal("Server=db;Database=f;", d.ConnectionString);
    }

    [Fact]
    public void ToString_NeverShowsTheConnectionString()
    {
        var secret = "Server=db;Password=" + new string('x', 12);
        Assert.DoesNotContain("Password", DatabaseOptions.From(Env(secret)).ToString());
    }

    [Fact]
    public void TheComposition_UsesTheProcessEnvironment_AndShipsWithNoDatabase()
    {
        // the tests run without FACTORY_DB_CONNECTION: the gateway is v1 alone (the CI job that has SQL Server sets FACTORY_TEST_SQL, a different name)
        Assert.Null(Environment.GetEnvironmentVariable("FACTORY_DB_CONNECTION"));
        Assert.False(DatabaseOptions.From(Environment.GetEnvironmentVariable).Configured);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    public void TheDatabaseIsCreatedOnlyWhenFactoryDbCreateIsOne(string? value, bool expected)
    {
        var d = DatabaseOptions.From(n => n == "FACTORY_DB_CREATE" ? value : n == "FACTORY_DB_CONNECTION" ? "Server=db;Database=f" : null);
        Assert.Equal(expected, d.CreateIfMissing);
    }

    [Fact]
    public void TheDeployedConfigurationNeverSetsFactoryDbCreate()
    {
        // tdd's identity cannot CREATE DATABASE and bicep creates it: the variable is for tests/scenarios/run.ps1 only
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "Dockerfile"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        foreach (var f in new[] { "config/instance.json", "config/deploy.overrides.json", "Dockerfile" })
            Assert.DoesNotContain("FACTORY_DB_CREATE", File.ReadAllText(Path.Combine(root!, f)));
    }
}
