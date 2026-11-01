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

    sealed class Recording : IEventIngress { public readonly List<IngressEvent> Got = []; public void Ingest(IngressEvent ev) { lock (Got) Got.Add(ev); } }

    sealed class OldOctopus : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var p = r.RequestUri!.PathAndQuery;
            var week = DateTimeOffset.UtcNow.AddDays(-7).ToString("o");   // (older than a 15 minute lookback, inside 30 days, whenever the test runs)
            // tdd's case: the releases waiting for promotion are days old when the gateway (restarted by its own deployment) polls for the first time
            var body = p.Contains("/releases?") ? $$"""{"Items":[{"Id":"Releases-5","Version":"2.4.1288","Assembled":"{{week}}","ReleaseNotes":"built 0123456789abcdef0123456789abcdef01234567"}]}"""
                : p.Contains("/deployments?") ? $$"""{"Items":[{"Id":"Deployments-9","TaskId":"ServerTasks-9","ReleaseId":"Releases-5","EnvironmentId":"Environments-1","Created":"{{week}}"}]}"""
                : p.Contains("/environments") ? """{"Items":[{"Id":"Environments-1","Name":"tdd"}]}"""
                : p.Contains("/tasks/") ? """{"State":"Success"}"""
                : p.Contains("/projects/ui") ? """{"Id":"Projects-1"}""" : "{}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    static async Task<List<IngressEvent>> FirstPoll()
    {
        var ingress = new Recording();
        var watch = OctopusWatchService.From(InstanceConfig.Load(WriteConfig(), _ => null), ingress, n => n == "OCTOPUS_API_KEY" ? "API-KEY" : null, () => true, new HttpClient(new OldOctopus()));
        await watch.StartAsync(default); await Task.Delay(1500); await watch.StopAsync(default);
        lock (ingress.Got) return ingress.Got.ToList();
    }

    [Fact] public async Task TheActingWatcherReplaysTheSameWindow_SoARestartLosesNothingOfAnOutageAndTheTrainOfTheCutoverStartsWithTheHistory()
    {
        var acting = await FirstPoll();
        Assert.Contains(acting, e => e is OctopusReleaseCreated { Version: "2.4.1288" });
        Assert.Contains(acting, e => e is OctopusDeploymentChanged { Environment: "tdd", Phase: OctopusDeploymentPhase.Succeeded });
        Assert.IsType<OctopusReplayBegan>(acting[0]);
        Assert.IsType<OctopusReplayComplete>(acting[^1]);
        Assert.Equal(TimeSpan.FromDays(30), OctopusWatchService.Lookback);
    }
}
