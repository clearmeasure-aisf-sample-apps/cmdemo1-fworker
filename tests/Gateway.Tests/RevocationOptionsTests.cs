using Xunit;

namespace Gateway.Tests;

/// <summary>The switches of the revocation read at start (gateway/Configuration/RevocationOptions.cs).</summary>
public sealed class RevocationOptionsTests
{
    static Func<string, string?> Env(params (string Name, string? Value)[] vars) { var d = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal); return n => d.TryGetValue(n, out var v) ? v : null; }
    static RevocationOptions With(params (string Name, string? Value)[] vars) => RevocationOptions.FromEnvironment(Env(vars));

    [Fact]
    public void Defaults()
    {
        var o = With();
        Assert.False(o.SkipRevocations);
        Assert.Equal(45, o.AttemptSeconds);
        Assert.Equal(180, o.AlarmSeconds);
        Assert.False(o.GitHubConfigured);
    }

    [Fact]
    public void SkipRevocations_OnlyOne()
    {
        Assert.True(With(("FACTORY_TEST_SKIP_REVOCATIONS", "1")).SkipRevocations);
        Assert.False(With(("FACTORY_TEST_SKIP_REVOCATIONS", "true")).SkipRevocations);
        Assert.False(With(("FACTORY_TEST_SKIP_REVOCATIONS", "0")).SkipRevocations);
    }

    [Theory]
    [InlineData("0", 1, 1)]
    [InlineData("0.5", 1, 1)]
    [InlineData("60.5", 60.5, 60.5)]
    [InlineData("500", 120, 500)]
    [InlineData("5000", 120, 3600)]
    [InlineData("-7", 1, 1)]
    [InlineData("x", 45, 180)]
    [InlineData("", 45, 180)]
    public void Seconds_Clamped(string value, double attempt, double alarm)
    {
        var o = With(("FACTORY_REVOCATIONS_ATTEMPT_SECONDS", value), ("FACTORY_REVOCATIONS_ALARM_SECONDS", value));
        Assert.Equal(attempt, o.AttemptSeconds);
        Assert.Equal(alarm, o.AlarmSeconds);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("unset", true)]
    [InlineData(" unset ", true)]
    [InlineData("UNSET", false)]
    [InlineData("x", false)]
    public void Unset_PlaceholderOrEmpty(string? value, bool expected) => Assert.Equal(expected, RevocationOptions.Unset(value));

    [Fact]
    public void GitHubConfigured_Matrix()
    {
        var token = "g" + new string('C', 24);
        // a token
        Assert.False(With().GitHubConfigured);
        Assert.False(With(("GH_TOKEN", "unset")).GitHubConfigured);
        Assert.False(With(("GH_TOKEN", " ")).GitHubConfigured);
        Assert.True(With(("GH_TOKEN", token)).GitHubConfigured);
        Assert.True(With(("FACTORY_AUTHOR_APP", "0"), ("GH_TOKEN", token)).GitHubConfigured);
        // the author App: fake apps, else the key (an absent key may be in a local keyring: configured); GH_TOKEN is not looked at
        Assert.True(With(("FACTORY_AUTHOR_APP", "1"), ("FACTORY_FAKE_APPS", "x"), ("FACTORY_APP_AUTHOR_KEY", "unset")).GitHubConfigured);
        Assert.True(With(("FACTORY_AUTHOR_APP", "1")).GitHubConfigured);
        Assert.True(With(("FACTORY_AUTHOR_APP", "1"), ("FACTORY_FAKE_APPS", "")).GitHubConfigured);
        Assert.False(With(("FACTORY_AUTHOR_APP", "1"), ("FACTORY_APP_AUTHOR_KEY", "unset"), ("GH_TOKEN", token)).GitHubConfigured);
        Assert.False(With(("FACTORY_AUTHOR_APP", "1"), ("FACTORY_APP_AUTHOR_KEY", "")).GitHubConfigured);
        Assert.True(With(("FACTORY_AUTHOR_APP", "1"), ("FACTORY_APP_AUTHOR_KEY", "k" + new string('D', 30))).GitHubConfigured);
    }
}
