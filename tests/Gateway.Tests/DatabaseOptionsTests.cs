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
}
