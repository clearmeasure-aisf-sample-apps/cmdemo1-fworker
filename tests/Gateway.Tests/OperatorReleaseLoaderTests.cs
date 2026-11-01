using System.Collections.Concurrent;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// gateway/Operator/OperatorReleaseLoader.cs on its own (C4b; the operator identity is a protected area): when there is nothing to read, the read
/// of the day's releases at start, its retries and its filter. The rule scripts are a fake call; the plant's record is a delegate.
/// </summary>
public sealed class OperatorReleaseLoaderTests
{
    sealed class Scripts
    {
        public readonly ConcurrentQueue<(string Verb, string Input, TimeSpan Limit)> Calls = new();
        public Func<int, (int, string)> Answer = _ => (0, "{\"ok\":true,\"releases\":[]}");   // by the call's number, from 1
        public Task<(int Code, string Output)> Run(string verb, string input, TimeSpan limit)
        {
            Calls.Enqueue((verb, input, limit));
            var (code, output) = Answer(Calls.Count);
            if (output == "throw") throw new InvalidOperationException("stub broke");
            return Task.FromResult((code, output));
        }
    }

    static (OperatorReleaseLoader Loader, Scripts Scripts, List<(DateTime At, int Pr, string Version)> Added) Make(bool operatorEnabled = true, bool revocationsOff = false, double pause = 0.05)
    {
        var scripts = new Scripts(); var added = new List<(DateTime, int, string)>();
        var l = new OperatorReleaseLoader(operatorEnabled, revocationsOff, scripts.Run, (at, pr, v) => { lock (added) added.Add((at, pr, v)); }) { PauseSeconds = pause };
        return (l, scripts, added);
    }

    static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("the condition did not hold in time"); await Task.Delay(20); }
    }

    [Fact]
    public void Day_IsTheUtcDateAsDigits() =>
        Assert.Equal("20261026", OperatorReleaseLoader.Day(new DateTime(2026, 10, 26, 23, 59, 59, DateTimeKind.Utc)));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OperatorOffOrTestSwitch_LoadedAtOnce_NothingRead(bool operatorEnabled, bool revocationsOff)
    {
        var (l, scripts, _) = Make(operatorEnabled, revocationsOff);
        Assert.True(l.IsLoaded);
        Assert.True(l.Loaded.IsCompletedSuccessfully);
        await l.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Empty(scripts.Calls);
    }

    [Fact]
    public async Task NotLoadedBeforeStart_ReadOnceAtStart_ValidRowsAdded()
    {
        var (l, scripts, added) = Make();
        scripts.Answer = _ => (0, "{\"ok\":true,\"releases\":[{\"pr\":5,\"version\":\"1.0.5\"},{\"pr\":0,\"version\":\"1.0.1\"},{\"pr\":-2,\"version\":\"1.0.1\"},{\"pr\":6,\"version\":\"v1\"},"
            + "{\"pr\":\"7\",\"version\":\"1.0.7\"},{\"pr\":8},{\"version\":\"1.0.9\"},null,{\"pr\":9,\"version\":\"2.1.3.4\"}]}");
        await Task.Delay(100);
        Assert.False(l.IsLoaded);
        Assert.Empty(scripts.Calls);
        var before = DateTime.UtcNow;
        await l.StartAsync(CancellationToken.None);
        await l.StartAsync(CancellationToken.None);   // a second start does not start a second read
        await l.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        var after = DateTime.UtcNow;
        var call = Assert.Single(scripts.Calls);
        Assert.Equal("operator-releases", call.Verb);
        Assert.Contains(call.Input, new[] { $"{{\"day\":\"{OperatorReleaseLoader.Day(before)}\"}}", $"{{\"day\":\"{OperatorReleaseLoader.Day(after)}\"}}" });
        Assert.Equal(TimeSpan.FromSeconds(120), call.Limit);
        Assert.Equal(new[] { (5, "1.0.5"), (9, "2.1.3.4") }, added.Select(a => (a.Pr, a.Version)).ToArray());
        // the rows are recorded at the moment of the read, the day the input names
        Assert.All(added, a => Assert.InRange(a.At, before, after));
        Assert.All(added, a => Assert.Equal(added[0].At, a.At));
    }

    [Fact]
    public async Task FailedRead_NoList_BrokenOutput_Thrown_AreRetried_UntilARead()
    {
        var (l, scripts, added) = Make();
        scripts.Answer = n => n switch
        {
            1 => (1, "{\"ok\":false,\"code\":\"github-error\"}"),   // a failed script
            2 => (0, "{\"ok\":true}"),                              // no list
            3 => (0, "{\"ok\":true,\"releases\":{}}"),             // not a list
            4 => (0, "not json"),                                    // the parse throws
            5 => (0, "throw"),                                       // the call throws
            _ => (0, "{\"ok\":true,\"releases\":[{\"pr\":3,\"version\":\"1.0.3\"}]}"),
        };
        await l.StartAsync(CancellationToken.None);
        await l.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(6, scripts.Calls.Count);
        Assert.All(scripts.Calls, c => Assert.Equal("operator-releases", c.Verb));
        Assert.Equal(new[] { 3 }, added.Select(a => a.Pr).ToArray());
        await Task.Delay(200);
        Assert.Equal(6, scripts.Calls.Count);   // a successful read is never repeated
    }

    [Fact]
    public async Task EmptyList_IsARead()
    {
        var (l, scripts, added) = Make();
        await l.StartAsync(CancellationToken.None);
        await l.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(scripts.Calls);
        Assert.Empty(added);
    }

    [Fact]
    public async Task Pause_BetweenFailedReads()
    {
        var (l, scripts, _) = Make(pause: 0.5);
        scripts.Answer = n => n == 1 ? (1, "{}") : (0, "{\"releases\":[]}");
        var started = DateTime.UtcNow;
        await l.StartAsync(CancellationToken.None);
        await Until(() => scripts.Calls.Count >= 1);
        await Task.Delay(200);
        Assert.False(l.IsLoaded);
        Assert.Single(scripts.Calls);
        await l.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(0.45));
        Assert.Equal(2, scripts.Calls.Count);
    }

    [Fact]
    public void DefaultPause_IsFiveSeconds() =>
        Assert.Equal(5.0, new OperatorReleaseLoader(true, false, (_, _, _) => Task.FromResult((0, "")), (_, _, _) => { }).PauseSeconds);

    [Fact]
    public async Task StopAsync_DoesNotWaitForTheRead()
    {
        var (l, scripts, _) = Make(pause: 0.3);
        scripts.Answer = n => n == 1 ? (1, "{}") : (0, "{\"releases\":[]}");
        await l.StartAsync(CancellationToken.None);
        await Until(() => scripts.Calls.Count >= 1);
        var stop = l.StopAsync(CancellationToken.None);
        Assert.True(stop.IsCompletedSuccessfully);
        Assert.False(l.IsLoaded);
        // as before the move, a stop does not end the read: it goes on and completes
        await l.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, scripts.Calls.Count);
    }

    [Fact]
    public async Task Dispose_EndsTheRead_NoCallAfterIt()
    {
        var (l, scripts, _) = Make(pause: 0.1);
        scripts.Answer = _ => (1, "{}");   // every read fails: without the dispose the loop would go on for ever
        await l.StartAsync(CancellationToken.None);
        await Until(() => scripts.Calls.Count >= 2);
        l.Dispose();
        await Task.Delay(300);   // a call in flight at the dispose may still finish
        var n = scripts.Calls.Count;
        await Task.Delay(500);
        Assert.Equal(n, scripts.Calls.Count);
        Assert.False(l.IsLoaded);
    }
}
