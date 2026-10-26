using Xunit;

namespace Gateway.Tests;

/// <summary>The gateway's environment variables (gateway/Configuration/GatewayOptions.cs): the required three in today's order, the defaults and the parses.</summary>
public sealed class GatewayOptionsTests
{
    // composed at run time: values that look like credentials never appear as literals
    static readonly string Token = "x" + new string('A', 20), Secret = "w" + new string('B', 30);

    static Func<string, string?> Env(params (string Name, string? Value)[] vars) { var d = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal); return n => d.TryGetValue(n, out var v) ? v : null; }
    static (string, string?)[] Required => new (string, string?)[] { ("FACTORY_PLUGIN_SCRIPTS", "/s/scripts"), ("FACTORY_DEV_TOKEN", Token), ("FACTORY_WEBHOOK_SECRET", Secret) };
    static GatewayOptions With(params (string Name, string? Value)[] more) => GatewayOptions.FromEnvironment(Env(Required.Concat(more).ToArray()));

    [Fact]
    public void RequiredVariables_RefusedInOrder()
    {
        Assert.Equal("FACTORY_PLUGIN_SCRIPTS is not set", Assert.Throws<InvalidOperationException>(() => GatewayOptions.FromEnvironment(Env())).Message);
        Assert.Equal("FACTORY_PLUGIN_SCRIPTS is not set", Assert.Throws<InvalidOperationException>(() => GatewayOptions.FromEnvironment(Env(("FACTORY_DEV_TOKEN", Token), ("FACTORY_WEBHOOK_SECRET", Secret)))).Message);
        Assert.Equal("FACTORY_DEV_TOKEN is not set", Assert.Throws<InvalidOperationException>(() => GatewayOptions.FromEnvironment(Env(("FACTORY_PLUGIN_SCRIPTS", "/s")))).Message);
        Assert.Equal("FACTORY_WEBHOOK_SECRET is not set", Assert.Throws<InvalidOperationException>(() => GatewayOptions.FromEnvironment(Env(("FACTORY_PLUGIN_SCRIPTS", "/s"), ("FACTORY_DEV_TOKEN", Token)))).Message);
    }

    [Fact]
    public void Defaults()
    {
        var o = With();
        Assert.Equal("/s/scripts", o.ScriptsDir);
        Assert.Equal(Path.Combine("/s/scripts", "factory.ps1"), o.Entry);
        Assert.Equal(Token, o.ApiToken);
        Assert.Equal(Secret, o.WebhookSecret);
        Assert.Equal(25, o.SsePingSeconds);
        Assert.Equal(180, o.SweepSeconds);
        Assert.False(o.OwnerMode);
        Assert.Equal("tdd", o.EnvironmentName);
        Assert.Equal("", o.PublicUrl);
        Assert.Equal("", o.TokenSecret);
        Assert.Equal(5100, o.ApiPort);
        Assert.Equal(5101, o.HookPort);
        Assert.Equal(0, o.SinglePort);
    }

    [Fact]
    public void Parses()
    {
        Assert.Equal(25, With(("FACTORY_SSE_PING_SECONDS", "0")).SsePingSeconds);
        Assert.Equal(25, With(("FACTORY_SSE_PING_SECONDS", "-3")).SsePingSeconds);
        Assert.Equal(7, With(("FACTORY_SSE_PING_SECONDS", "7")).SsePingSeconds);
        Assert.Equal(-5, With(("FACTORY_SWEEP_SECONDS", "-5")).SweepSeconds);
        Assert.Equal(0, With(("FACTORY_SWEEP_SECONDS", "0")).SweepSeconds);
        Assert.Equal(180, With(("FACTORY_SWEEP_SECONDS", "1.5")).SweepSeconds);
        Assert.True(With(("FACTORY_OWNER_MODE", "1")).OwnerMode);
        Assert.False(With(("FACTORY_OWNER_MODE", "true")).OwnerMode);
        Assert.Equal("uat", With(("FACTORY_ENVIRONMENT", "uat")).EnvironmentName);
        Assert.Equal("https://gw.example", With(("FACTORY_PUBLIC_URL", "https://gw.example")).PublicUrl);
        Assert.Equal(6000, With(("FACTORY_API_PORT", "6000")).ApiPort);
        Assert.Equal(6001, With(("FACTORY_WEBHOOK_PORT", "6001")).HookPort);
        Assert.Equal(5100, With(("FACTORY_API_PORT", "x")).ApiPort);
    }

    [Fact]
    public void SinglePort_Sources()
    {
        Assert.Equal(8080, With(("FACTORY_SINGLE_PORT", "8080")).SinglePort);
        Assert.Equal(8081, With(("ASPNETCORE_HTTP_PORTS", "8081")).SinglePort);
        Assert.Equal(8080, With(("FACTORY_SINGLE_PORT", "8080"), ("ASPNETCORE_HTTP_PORTS", "8081")).SinglePort);
        // FACTORY_SINGLE_PORT set but empty wins over ASPNETCORE_HTTP_PORTS (??), and does not parse: local mode, as before
        Assert.Equal(0, With(("FACTORY_SINGLE_PORT", ""), ("ASPNETCORE_HTTP_PORTS", "8081")).SinglePort);
        Assert.Equal(0, With(("ASPNETCORE_HTTP_PORTS", "8080;8081")).SinglePort);
    }

    [Fact]
    public void ValidateOwnerMode_NeedsSixteenCharacters()
    {
        var e = Assert.Throws<InvalidOperationException>(() => With(("FACTORY_OWNER_MODE", "1"), ("FACTORY_TOKEN_SECRET", new string('s', 15))).ValidateOwnerMode());
        Assert.Equal("FACTORY_TOKEN_SECRET (at least 16 characters) is required in owner mode", e.Message);
        Assert.Throws<InvalidOperationException>(() => With(("FACTORY_OWNER_MODE", "1")).ValidateOwnerMode());
        With(("FACTORY_OWNER_MODE", "1"), ("FACTORY_TOKEN_SECRET", new string('s', 16))).ValidateOwnerMode();
        With().ValidateOwnerMode();
        With(("FACTORY_TOKEN_SECRET", "short")).ValidateOwnerMode();
    }
}
