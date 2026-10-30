using System.Text;
using System.Text.Json.Nodes;
using Fworker.Integrations.Alerts;
using Fworker.Integrations.Correlation;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W8.2: an Azure Monitor common-schema alert as the incident trigger event of the ingress inbox.</summary>
public sealed class AzureMonitorAlertsTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    const string AlertId = "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.AlertsManagement/alerts/aaaaaaaa-1111-2222-3333-444444444444";

    static byte[] Alert(string condition = "Fired", string severity = "Sev2", string? fired = "2026-10-29T11:58:24.3713213Z", string? resolved = null, string id = AlertId, string schema = "azureMonitorCommonAlertSchema", string rule = "tdd-ui-5xx")
    {
        var essentials = new JsonObject
        {
            ["alertId"] = id, ["alertRule"] = rule, ["severity"] = severity, ["signalType"] = "Metric", ["monitorCondition"] = condition, ["monitoringService"] = "Platform",
            ["alertTargetIDs"] = new JsonArray("/subscriptions/s/resourcegroups/rg/providers/microsoft.app/containerapps/ca-cmdemo1-tdd-ui"),
            ["firedDateTime"] = fired, ["resolvedDateTime"] = resolved, ["description"] = "5xx above the threshold", ["essentialsVersion"] = "1.0", ["alertContextVersion"] = "1.0",
        };
        return Encoding.UTF8.GetBytes(new JsonObject { ["schemaId"] = schema, ["data"] = new JsonObject { ["essentials"] = essentials, ["alertContext"] = new JsonObject { ["big"] = new string('x', 5000) } } }.ToJsonString());
    }

    [Fact]
    public void AFiredAlertIsAnIncidentTriggerOfItsDeployableAndEnvironment()
    {
        var r = AzureMonitorAlerts.Parse(Alert(), "ui", "tdd", Now);
        Assert.True(r.Ok);
        var e = r.Event!;
        Assert.Equal("incident.alert", (string?)e["event"]);
        Assert.Equal("azure-monitor", (string?)e["provider"]);
        Assert.Equal(("ui", "tdd"), ((string?)e["deployable"], (string?)e["environment"]));
        Assert.Equal(($"{AlertId}|Fired", "tdd-ui-5xx", "Sev2", 2), ((string?)e["ref"], (string?)e["rule"], (string?)e["severity"], (int?)e["severityLevel"]));
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T11:58:24.3713213Z").ToString("o"), (string?)e["firedAt"]);
        Assert.Equal("", (string?)e["resolvedAt"]);
        Assert.Single(e["targets"]!.AsArray());
        Assert.DoesNotContain("xxxxx", e.ToJsonString());   // the alert's own context is not carried
    }

    [Fact]
    public void AResolvedAlertIsTheResolutionEventWithItsOwnRef_SoItIsNotADuplicateOfTheFiredOne()
    {
        var fired = AzureMonitorAlerts.Parse(Alert(), "ui", "tdd", Now).Event!;
        var resolved = AzureMonitorAlerts.Parse(Alert("Resolved", resolved: "2026-10-29T12:03:00Z"), "ui", "tdd", Now).Event!;
        Assert.Equal("incident.alert.resolved", (string?)resolved["event"]);
        Assert.NotEqual((string?)fired["ref"], (string?)resolved["ref"]);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T12:03:00Z").ToString("o"), (string?)resolved["resolvedAt"]);
    }

    [Fact]
    public void ADeliveryAzureSendsAgainHasTheRefOfTheFirst_SoTheInboxDropsIt()
    {
        var first = AzureMonitorAlerts.Parse(Alert(), "ui", "tdd", Now).Event!;
        var again = AzureMonitorAlerts.Parse(Alert(), "ui", "tdd", Now.AddSeconds(20)).Event!;
        var p = new IngressInboxProcessor(new InMemoryIngressDedup(), new InMemoryCorrelationMap(), new Fworker.Contracts.Ids.RepoId("acme", "orders"));
        Assert.True(p.Process(new IngressInboxMessage(AzureMonitorAlerts.InboxSource, first.ToJsonString()), Guid.NewGuid()).Accepted);
        Assert.Equal("duplicate-delivery", p.Process(new IngressInboxMessage(AzureMonitorAlerts.InboxSource, again.ToJsonString()), Guid.NewGuid()).Reason);
        var other = AzureMonitorAlerts.Parse(Alert(id: AlertId + "-2"), "ui", "tdd", Now).Event!;   // another alert of the same rule is another delivery
        Assert.True(p.Process(new IngressInboxMessage(AzureMonitorAlerts.InboxSource, other.ToJsonString()), Guid.NewGuid()).Accepted);
    }

    [Theory]
    [InlineData("not json at all", "not-json")]
    [InlineData("[1,2]", "not-json")]
    [InlineData("{\"schemaId\":\"other\"}", "unsupported-schema")]
    [InlineData("{\"schemaId\":\"azureMonitorCommonAlertSchema\",\"data\":{}}", "no-essentials")]
    [InlineData("{\"schemaId\":\"azureMonitorCommonAlertSchema\",\"data\":{\"essentials\":{\"alertRule\":\"r\",\"monitorCondition\":\"Fired\"}}}", "no-alert-id")]
    [InlineData("{\"schemaId\":\"azureMonitorCommonAlertSchema\",\"data\":{\"essentials\":{\"alertId\":\"a\",\"monitorCondition\":\"Fired\"}}}", "no-alert-rule")]
    [InlineData("{\"schemaId\":\"azureMonitorCommonAlertSchema\",\"data\":{\"essentials\":{\"alertId\":\"a\",\"alertRule\":\"r\",\"monitorCondition\":\"Maybe\"}}}", "unknown-condition")]
    public void ABodyThatIsNotAnAlertIsRefusedWithItsReason(string body, string reason)
    {
        var r = AzureMonitorAlerts.Parse(Encoding.UTF8.GetBytes(body), "ui", "tdd", Now);
        Assert.False(r.Ok); Assert.Equal(reason, r.Reason); Assert.Null(r.Event);
    }

    [Fact]
    public void LongTextIsBoundedAndTheTargetsAreCapped()
    {
        var essentials = new JsonObject { ["alertId"] = "a", ["alertRule"] = new string('r', 900), ["monitorCondition"] = "Fired", ["description"] = new string('d', 9000), ["alertTargetIDs"] = new JsonArray(Enumerable.Range(0, 40).Select(i => (JsonNode)JsonValue.Create("t" + i)!).ToArray()) };
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["schemaId"] = "azureMonitorCommonAlertSchema", ["data"] = new JsonObject { ["essentials"] = essentials } }.ToJsonString());
        var e = AzureMonitorAlerts.Parse(body, "ui", "tdd", Now).Event!;
        Assert.Equal(200, ((string)e["rule"]!).Length);
        Assert.Equal(AzureMonitorAlerts.MaxText, ((string)e["description"]!).Length);
        Assert.Equal(AzureMonitorAlerts.MaxTargets, e["targets"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("Sev0", 0)] [InlineData("sev4", 4)] [InlineData("Sev5", -1)] [InlineData("Critical", -1)] [InlineData("", -1)]
    public void SeverityIsSev0ToSev4(string severity, int level) => Assert.Equal(level, AzureMonitorAlerts.Level(severity));

    [Theory]
    [InlineData("ui", "ui")] [InlineData(" TDD ", "tdd")] [InlineData("a.b_c-1", "a.b_c-1")]
    [InlineData("", null)] [InlineData(null, null)] [InlineData("a b", null)] [InlineData("-x", null)] [InlineData("ui/../x", null)] [InlineData("a\"b", null)]
    public void ASubjectIsALowerCaseName(string? value, string? expected) => Assert.Equal(expected, AzureMonitorAlerts.Subject(value));
}
