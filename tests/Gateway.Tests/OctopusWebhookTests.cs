using System.Text;
using Fworker.Contracts.Seams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>W4.2: the Octopus webhook route (OctopusWatch.cs): the shared secret in X-Octopus-Webhook-Secret, 404 unless the watcher is on.</summary>
public sealed class OctopusWebhookTests
{
    const string Secret = "oct-secret-for-tests";
    sealed class NoIngress : IEventIngress { public void Ingest(IngressEvent ev) { } }

    static WebhookSecrets Secrets(string? s = Secret) => new(n => n == "FACTORY_WEBHOOK_SECRET_OCTOPUS" ? s : null);

    static string WriteConfig()
    {
        var p = Path.Combine(Path.GetTempPath(), $"oct-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, """{"octopus":{"url":"http://127.0.0.1:1","space":"Spaces-1","project":"ui"}}""");
        return p;
    }

    static OctopusWatchService Active() => OctopusWatchService.From(InstanceConfig.Load(WriteConfig(), _ => null), new NoIngress(), n => n == "OCTOPUS_API_KEY" ? "API-KEY" : null, () => true);

    static async Task<int> Post(OctopusWatchService? watch, WebhookSecrets secrets, string? header)
    {
        var sc = new ServiceCollection().AddLogging(); if (watch != null) sc.AddSingleton(watch);
        var sp = sc.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Method = "POST"; ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}")); ctx.Response.Body = new MemoryStream();
        if (header != null) ctx.Request.Headers["X-Octopus-Webhook-Secret"] = header;
        var recv = typeof(OctopusWebhookEndpoints).GetMethod("Receive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = await (Task<IResult>)recv.Invoke(new OctopusWebhookEndpoints(sp, secrets), [ctx])!;
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    [Fact] public async Task ACorrectSecretIsAccepted() => Assert.Equal(202, await Post(Active(), Secrets(), Secret));
    [Fact] public async Task AWrongSecretIsRefused() => Assert.Equal(401, await Post(Active(), Secrets(), "nope"));
    [Fact] public async Task AMissingHeaderIsRefused() => Assert.Equal(401, await Post(Active(), Secrets(), null));
    [Fact] public async Task ASourceWithoutASecretRefusesEverything() => Assert.Equal(401, await Post(Active(), Secrets(""), ""));
    [Fact] public async Task WithoutTheWatcherTheRouteIsNotThere() => Assert.Equal(404, await Post(null, Secrets(), Secret));
    [Fact] public void WithoutAnApiKeyTheWatcherIsOff() => Assert.False(OctopusWatchService.From(InstanceConfig.Load(WriteConfig(), _ => null), new NoIngress(), _ => null, () => true).Enabled);
}
