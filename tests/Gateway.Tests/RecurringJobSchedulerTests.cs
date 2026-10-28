using Xunit;

public class RecurringJobSchedulerTests
{
    sealed class Clock : TimeProvider { public DateTimeOffset Now = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task DueSlotsWithAJobArePublished_OnlyWhileTheProcessMayAct()
    {
        var reg = new RecurringJobRegistry().Add(RecurringJobSlots.BoardReconciler, _ => Task.CompletedTask);   // the other slots have no job
        var sent = new List<string>(); var may = true; var time = new Clock();
        var s = new RecurringJobScheduler(RecurringJobSlots.Default(), reg, r => { sent.Add(r.Name); return Task.FromResult(true); }, () => may, time, TimeSpan.FromSeconds(10));
        await s.Tick(); Assert.Empty(sent);
        time.Now += TimeSpan.FromSeconds(11); await s.Tick(); Assert.Equal([RecurringJobSlots.BoardReconciler], sent);
        time.Now += TimeSpan.FromMinutes(4); await s.Tick(); Assert.Single(sent);
        may = false; time.Now += TimeSpan.FromMinutes(2); await s.Tick(); Assert.Single(sent);   // due, but not the leader: skipped, not queued
        may = true; time.Now += TimeSpan.FromMinutes(5); await s.Tick(); Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task ARunOfARunningJobIsDropped_AndAFailingJobDoesNotThrow()
    {
        var gate = new TaskCompletionSource(); var runs = 0;
        var reg = new RecurringJobRegistry().Add("a", async _ => { runs++; await gate.Task; }).Add("b", _ => throw new InvalidOperationException());
        var first = reg.Run("a", default); await reg.Run("a", default); await reg.Run("b", default);
        gate.SetResult(); await first;
        Assert.Equal(1, runs);
    }
}
