using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W8.2 on a real gateway host: where v2 does not act (v1, and the shadow run of tdd today, which registers the ingress inbox for the delivery train) the alert route answers 404 to a delivery
/// with the right secret, as if it were not mapped. Where v2 acts the route works (AlertWebhookTests drives the module with engineActs; EndpointModules passes Engine.V2Acts).
/// One gateway per class: the fixtures share the ports and the process environment.
/// </summary>
static class AlertRouteGateway
{
    public const string Secret = "azure-secret-for-tests";
    public const string Uri = "/webhooks/azure-monitor?deployable=ui&environment=tdd&secret=" + Secret;
}

/// <summary>A gateway on v1 (the default configuration) with the Azure Monitor secret set.</summary>
public sealed class V1AlertGateway : GatewayFixture
{
    protected override void Configure(GatewayEnvironment env) => env.Set("FACTORY_WEBHOOK_SECRET_AZURE_MONITOR", AlertRouteGateway.Secret);
}

public sealed class AlertRouteInV1Tests(V1AlertGateway gw) : IClassFixture<V1AlertGateway>
{
    [Fact]
    public async Task TheRouteAnswers404_WithTheRightSecret()
    {
        if (!StubPwsh.Supported) return;
        Assert.Equal(404, (await gw.Host.SendAsync("POST", AlertRouteGateway.Uri, GatewayEnvironment.HookPort, body: "{}")).Status);
    }
}
