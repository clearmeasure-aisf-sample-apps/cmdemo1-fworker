using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W0.8 (delivery-speed.md 13): the metrics route hands the rule script's answer through as it is, so the per-stage durations and medians of the
/// script (<c>stages</c>, and <c>flow</c> per item) reach the caller with the existing fields untouched and the access rules as before. Through the
/// stub rule scripts (StubPwsh); the script's own arithmetic is checked by tests/smoke.ps1 and the Tier 1 scenarios.
/// </summary>
public sealed class MetricsFlowGatewayTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    const string Answer =
        "{\"ok\":true,\"since\":\"2026-01-01T00:00:00Z\",\"computedAt\":\"2026-01-02T00:00:00Z\",\"scanned\":1,\"unreadable\":[],"
        + "\"items\":[{\"item\":5,\"times\":{\"merged\":\"2026-01-01T01:40:00Z\"},\"durations\":{},\"columns\":{\"Development\":3600},"
        + "\"flow\":[{\"stage\":\"Technical Design\",\"at\":\"2026-01-01T00:00:00Z\",\"seconds\":600},{\"stage\":\"Development\",\"at\":\"2026-01-01T00:10:00Z\",\"seconds\":5400}]}],"
        + "\"aggregate\":{\"matchToAccept\":{\"count\":0}},\"columns\":{\"Development\":{\"count\":1,\"median\":3600,\"p90\":3600,\"min\":3600,\"max\":3600}},"
        + "\"stages\":{\"Technical Design\":{\"count\":1,\"median\":600,\"p90\":600,\"min\":600,\"max\":600},\"Development\":{\"count\":1,\"median\":5400,\"p90\":5400,\"min\":5400,\"max\":5400}},"
        + "\"unit\":\"seconds\"}";

    [Fact]
    public async Task Metrics_AnswersTheStageDurationsAndMediansOfTheScript_WithTheExistingFieldsAsTheyWere()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "metrics", Answer);
        var a = await gw.Host.SendAsync("GET", "/api/v1/metrics?since=3d&fresh=1", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        var j = (JsonObject)JsonNode.Parse(a.Body)!;
        foreach (var k in new[] { "ok", "since", "computedAt", "scanned", "unreadable", "items", "aggregate", "columns", "unit" }) Assert.True(j.ContainsKey(k), "missing " + k);
        Assert.Equal(5400, (int?)j["stages"]!["Development"]!["median"]);
        Assert.Equal(600, (int?)j["stages"]!["Technical Design"]!["median"]);
        Assert.Equal("Technical Design", (string?)j["items"]![0]!["flow"]![0]!["stage"]);
        Assert.Equal(3600, (int?)j["columns"]!["Development"]!["median"]);   // the existing per-column medians are unchanged
    }

    [Fact]
    public async Task Metrics_IsStillRefusedToACallerWithoutTheAdminOrOperatorToken_AndABadQueryIsStillAUsageError()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "metrics", Answer);
        var anon = await gw.Host.SendAsync("GET", "/api/v1/metrics?since=3d", GatewayEnvironment.ApiPort);
        Assert.Contains(anon.Status, new[] { 401, 403 });
        Assert.DoesNotContain("stages", anon.Body);
        var bad = await gw.Host.SendAsync("GET", "/api/v1/metrics?since=" + new string('9', 41), GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(400, bad.Status);
    }
}
