using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The lifecycle of TranscriptService (gateway/Transcripts.cs, C4c): the final flush is an ApplicationStopping callback that runs after one
/// registered later (the leader's hand-over), the save loop writes only while the process may act and ends at ApplicationStopping, and the
/// stop returns promptly. The whole-gateway stop order is pinned by StopOrderTests.
/// </summary>
public sealed class TranscriptServiceTests
{
    sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        readonly CancellationTokenSource started = new(), stopping = new(), stopped = new();
        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() { started.Dispose(); stopping.Dispose(); stopped.Dispose(); }
    }

    sealed class Leadership : ILeadership
    {
        public volatile bool Acting;
        public bool On => true;
        public bool MayAct => Acting;
        public bool Replaced => false;
        public long Epoch => 1;
        public JsonObject Status() => new();
    }

    // a durable store whose rule-script calls are recorded in order (with the test's own events); saveCode is what transcript-save answers
    sealed class Rig : IDisposable
    {
        public readonly ConcurrentQueue<string> Events = new();
        public readonly Lifetime Lifetime = new();
        public readonly Leadership Leadership = new();
        public readonly TranscriptStore Store;
        public readonly TranscriptService Service;
        public Rig(int saveCode = 0, TimeSpan? tick = null)
        {
            Store = new TranscriptStore((verb, _, _) => { Events.Enqueue(verb); return Task.FromResult(verb == "transcript-save" ? (saveCode, "{}") : (0, "{\"found\":false}")); }, "transcript-service-tests-secret");
            Service = new TranscriptService(Store, Leadership, Lifetime) { Tick = tick ?? TimeSpan.FromHours(1) };
        }
        public int Saves => Events.Count(e => e == "transcript-save");
        public void Dispose() { Service.Dispose(); Lifetime.Dispose(); }
    }

    static async Task<bool> Until(Func<bool> cond, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < within) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    [Fact]
    public async Task FinalFlush_RunsAtApplicationStopping_AfterACallbackRegisteredLater()
    {
        using var rig = new Rig();
        Assert.True(rig.Store.Durable);
        rig.Service.RegisterFinalFlush();
        // the leader's hand-over is registered after the flush (Program.cs); last-registered first, so it runs first
        rig.Lifetime.ApplicationStopping.Register(() => rig.Events.Enqueue("hand-over"));
        await rig.Store.Append("alice", ["a", "b"]);
        Assert.Equal(0, rig.Saves);
        rig.Lifetime.StopApplication();
        var events = rig.Events.ToList();
        var handOver = events.IndexOf("hand-over");
        var save = events.IndexOf("transcript-save");
        Assert.True(handOver >= 0 && save > handOver, $"wanted the hand-over, then the transcript save; saw: {string.Join(", ", events)}");
        Assert.Equal(0, (int?)rig.Store.Status()["dirty"]);
    }

    [Fact]
    public async Task RegisterFinalFlush_Twice_FlushesOnce()
    {
        // saves refused: the session stays dirty, so a second registered flush would save it again
        using var rig = new Rig(saveCode: 1);
        rig.Service.RegisterFinalFlush();
        rig.Service.RegisterFinalFlush();
        await rig.Store.Append("alice", ["a"]);
        rig.Lifetime.StopApplication();
        Assert.Equal(1, rig.Saves);
    }

    [Fact]
    public async Task FinalFlush_NotRegistered_NothingSavedAtStop()
    {
        using var rig = new Rig();
        await rig.Store.Append("alice", ["a"]);
        rig.Lifetime.StopApplication();
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public async Task SaveLoop_SavesOnlyWhileMayAct()
    {
        using var rig = new Rig(tick: TimeSpan.FromMilliseconds(50));
        await rig.Service.StartAsync(CancellationToken.None);
        await rig.Store.Append("alice", ["a"]);
        // a standby: several ticks pass and nothing is written
        await Task.Delay(400);
        Assert.Equal(0, rig.Saves);
        rig.Leadership.Acting = true;
        Assert.True(await Until(() => rig.Saves == 1, TimeSpan.FromSeconds(5)), "the acting process saved nothing");
        Assert.Equal(0, (int?)rig.Store.Status()["dirty"]);
        await rig.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SaveLoop_EndsAtApplicationStopping_BeforeStopAsync()
    {
        using var rig = new Rig(tick: TimeSpan.FromMilliseconds(50));
        rig.Leadership.Acting = true;
        await rig.Service.StartAsync(CancellationToken.None);
        Assert.NotNull(rig.Service.ExecuteTask);
        Assert.False(rig.Service.ExecuteTask!.IsCompleted);
        rig.Lifetime.StopApplication();
        // the loop ends with ApplicationStopping (as the Task.Run it replaces did), before the host calls StopAsync
        Assert.True(await Until(() => rig.Service.ExecuteTask.IsCompleted, TimeSpan.FromSeconds(2)), "the save loop went on after ApplicationStopping");
        var saves = rig.Saves;
        await rig.Store.Append("alice", ["a"]);
        await Task.Delay(300);
        Assert.Equal(saves, rig.Saves);
        var sw = Stopwatch.StartNew();
        await rig.Service.StopAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync took {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task StopAsync_EndsTheLoopPromptly()
    {
        using var rig = new Rig();   // a tick of an hour: StopAsync must not wait for it
        await rig.Service.StartAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await rig.Service.StopAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync took {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(rig.Service.ExecuteTask!.IsCompleted);
        Assert.Equal(0, rig.Saves);
    }
}

/// <summary>The gateway's container: one TranscriptService, the same instance as hosted service, over the store the routes use.</summary>
public sealed class TranscriptServiceRegistrationTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public void RegisteredOnce_AsSingletonAndHostedService()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        var service = sp.GetRequiredService<TranscriptService>();
        Assert.Same(service, sp.GetRequiredService<TranscriptService>());
        Assert.Same(service, Assert.Single(sp.GetServices<IHostedService>().OfType<TranscriptService>()));
        Assert.Same(sp.GetRequiredService<TranscriptStore>(), sp.GetRequiredService<TranscriptStore>());
        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask!.IsCompleted);
    }
}
