using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace Gateway.Tests;

/// <summary>W4.0: a webhook secret per source (WebhookEndpoints.cs) and the recurring-job slots (Composition/RecurringJobSlots.cs).</summary>
public sealed class WebhookSecretsTests
{
    const string GhSecret = "gh-secret-for-tests", OctSecret = "oct-secret-for-tests";
    static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
    static WebhookSecrets Make(Dictionary<string, string?>? vars = null) { vars ??= new() { ["FACTORY_WEBHOOK_SECRET"] = GhSecret, ["FACTORY_WEBHOOK_SECRET_OCTOPUS"] = OctSecret }; return new(n => vars.GetValueOrDefault(n)); }
    static string Sign(string secret, byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    [Fact] public void AValidGitHubSignatureIsAccepted() => Assert.True(Make().Verify(WebhookSecrets.GitHub, Sign(GhSecret, Body), Body));
    [Fact] public void AnInvalidGitHubSignatureIsRefused() => Assert.False(Make().Verify(WebhookSecrets.GitHub, Sign("other", Body), Body));
    [Fact] public void ATamperedBodyIsRefused() => Assert.False(Make().Verify(WebhookSecrets.GitHub, Sign(GhSecret, Body), Encoding.UTF8.GetBytes("{}")));
    [Fact] public void AMissingSignatureIsRefused() => Assert.False(Make().Verify(WebhookSecrets.GitHub, null, Body));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ASourceWithoutASecretRefusesEvenAMatchingSignature(string? value)
    {
        var s = Make(new() { ["FACTORY_WEBHOOK_SECRET"] = value, ["FACTORY_WEBHOOK_SECRET_OCTOPUS"] = OctSecret });
        Assert.Null(s.For(WebhookSecrets.GitHub));
        Assert.False(s.Verify(WebhookSecrets.GitHub, Sign(value ?? "", Body), Body));
        Assert.True(s.Verify(WebhookSecrets.Octopus, OctSecret, Body));   // the other source is not affected
    }

    [Fact]
    public void ASecretIsOfItsOwnSourceOnly()
    {
        var s = Make();
        Assert.False(s.Verify(WebhookSecrets.GitHub, Sign(OctSecret, Body), Body));   // octopus' secret does not sign github deliveries
        Assert.False(s.Verify(WebhookSecrets.Octopus, GhSecret, Body));
        Assert.False(s.Verify(WebhookSecrets.Octopus, Sign(GhSecret, Body), Body));
        Assert.False(s.Verify("gitlab", Sign(GhSecret, Body), Body));   // an unknown source has no secret
    }

    [Fact] public void OctopusSendsItsSharedSecret() { Assert.True(Make().Verify(WebhookSecrets.Octopus, OctSecret, Body)); Assert.False(Make().Verify(WebhookSecrets.Octopus, OctSecret + "x", Body)); }

    [Fact]
    public void ARepeatedDeliveryIdIsRecognised()
    {
        var d = new WebhookDeduplicator();
        Assert.False(d.Repeated("id-1")); Assert.True(d.Repeated("id-1")); Assert.False(d.Repeated("id-2")); Assert.False(d.Repeated("")); Assert.False(d.Repeated(""));
    }

    [Fact]
    public void TheSlotsAreRegisteredInTheHostsServicesAndScheduleNothing()
    {
        var leader = new GatewayLeader(new Runner(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "factory.ps1")), new LeaderSettings { On = true }, "0.0.0");
        var host = new WolverineHostService(DatabaseOptions.From(n => n == "FACTORY_DB_CONNECTION" ? WolverineHostTestSupport.Unreachable : null), WolverineHostTestSupport.Fast(), leader);
        Assert.Null(host.RuntimeCommands);   // v1 and shadow: Attach is not called, so the host is as W2.3 left it
        var slots = RecurringJobSlots.Attach(host);
        Assert.Equal([RecurringJobSlots.BoardReconciler, RecurringJobSlots.CatalogReread, RecurringJobSlots.IngressDedupPurge, RecurringJobSlots.DocsIntake, RecurringJobSlots.FeatureChecks, RecurringJobSlots.SagaRearm, RecurringJobSlots.LedgerPurge, RecurringJobSlots.IncidentHealth, RecurringJobSlots.ItemSteps], slots.Slots.Select(x => x.Name).ToArray());
        var services = new ServiceCollection();
        host.RuntimeCommands!(services, new WolverineOptions());
        Assert.Same(slots, services.BuildServiceProvider().GetRequiredService<RecurringJobSlots>());
    }
}
