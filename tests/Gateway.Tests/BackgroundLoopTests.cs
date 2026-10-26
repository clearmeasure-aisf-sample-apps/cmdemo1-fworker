using Xunit;

namespace Gateway.Tests;

/// <summary>The two loops C5d moved out of Program.cs, alone: McpSessionSweeper's pass, its survival of an error and its stop; the manager tick's interval.</summary>
public sealed class McpSessionSweeperTests
{
    [Fact]
    public async Task Sweeper_PassesAtItsInterval_UntilStopped()
    {
        var passes = 0;
        var sweeper = new McpSessionSweeper(() => { Interlocked.Increment(ref passes); return null; }, TimeSpan.FromMilliseconds(50));
        await sweeper.StartAsync(CancellationToken.None);
        // the first pass comes after one interval, not at the start
        Assert.Equal(0, Volatile.Read(ref passes));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref passes) < 3 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(Volatile.Read(ref passes) >= 3, $"passes: {passes}");
        await sweeper.StopAsync(CancellationToken.None);
        Assert.True(sweeper.ExecuteTask!.IsCompletedSuccessfully);
        var after = Volatile.Read(ref passes);
        await Task.Delay(200);
        Assert.Equal(after, Volatile.Read(ref passes));
    }

    [Fact]
    public async Task Sweeper_KeepsGoing_AfterAPassFails()
    {
        var passes = 0;
        var sweeper = new McpSessionSweeper(() => { Interlocked.Increment(ref passes); throw new InvalidOperationException("boom"); }, TimeSpan.FromMilliseconds(30));
        await sweeper.StartAsync(CancellationToken.None);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref passes) < 3 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(Volatile.Read(ref passes) >= 3, $"passes: {passes}");
        Assert.False(sweeper.ExecuteTask!.IsCompleted);
        await sweeper.StopAsync(CancellationToken.None);
        Assert.True(sweeper.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Sweeper_StopsAtOnce_DuringItsWait()
    {
        // today's interval: the stop does not wait for the 30 s to pass (before C5d the loop never ended)
        var sweeper = new McpSessionSweeper(() => null, McpSessionSweeper.DefaultInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), sweeper.Interval);
        await sweeper.StartAsync(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sweeper.StopAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the stop took {sw.Elapsed.TotalSeconds:0.0} s");
        // completed, not necessarily successfully: a stop that comes before ExecuteAsync has begun leaves the task canceled (.NET 10's BackgroundService)
        Assert.True(sweeper.ExecuteTask!.IsCompleted);
    }
}

public sealed class ManagerCopilotServiceTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(4.9, 5)]
    [InlineData(5, 30)]
    [InlineData(30, 30)]
    [InlineData(120, 30)]
    public void Interval_IsFiveSecondsBelowAFiveSecondListInterval_ElseThirty(double listSeconds, double want) =>
        Assert.Equal(TimeSpan.FromSeconds(want), ManagerCopilotService.IntervalFor(listSeconds));
}
