using System.Diagnostics;
using System.Text.Json.Nodes;

// The leadership's start, the hand-over and the stop (Leader.cs, issue 94; gateway-composition.md 3 E). GatewayLeader holds the SQL lock and its loops; SnapshotCoordinator holds the plant in the database; this
// service decides when each runs:
// - at the host's start: without a lease, the one read of the plant (and the process acts at once); with it, once the revoked credentials are read, the SQL application lock is taken (the newest process asks
//   the holder to make room), the plant is restored from the database, the lock confirmed, then the standby loop and the snapshot of every change start;
// - when this process stands down (a newer process asked for the lock, or its own lock was lost): the Wolverine node leaves (W9.10a), the final snapshot is written to the database, the streams end with
//   stream.replaced, and only then is the lock let go (GatewayLeader.SqlStepDownAsync);
// - when a standby takes the lock again: the restore from the database, then the node starts (GatewayLeader.OnActing);
// - at the stop (ApplicationStopping, RegisterStopping): the node leaves, then the final snapshot, then the lock is let go; without a lease the plant is written.
// Nothing here is stopped with the host: the loops end at ApplicationStopping, as before.
sealed class LeadershipService : IHostedService
{
    readonly GatewayLeader leader;
    readonly SnapshotCoordinator snapshots;
    readonly IRevocationService revocations;
    readonly IHostApplicationLifetime lifetime;
    int stoppingRegistered;
    readonly Runner runner;

    // how long a superseded process lets the running rule scripts that act on the board finish before it ends them (delivery-speed.md 18.1.1): a sync-board can run 280 s
    public static readonly TimeSpan DrainFenced = TimeSpan.FromSeconds(45);
    // how long the stop of the holder waits for the Wolverine node to leave before the final snapshot (W9.10a)
    public static readonly TimeSpan NodeLeaves = TimeSpan.FromSeconds(10);

    public LeadershipService(GatewayLeader leader, SnapshotCoordinator snapshots, IRevocationService revocations, Plant plant, IHostApplicationLifetime lifetime, Runner runner)
    {
        this.leader = leader; this.snapshots = snapshots; this.revocations = revocations; this.lifetime = lifetime; this.runner = runner;
        // the plant plans and starts runtime sessions only while this process holds the lock (Plant.Plan, RuntimeManager.Provision; W0.1)
        plant.MayAct = () => leader.MayAct;
        if (!leader.On) return;
        snapshots.MarkLeader();
        // superseded: nothing acts any more (the sweeper and the ticks ask MayAct); the node leaves, the final snapshot is written to the database, then the streams end with stream.replaced, then the running
        // rule scripts are awaited for at most 45 s and ended
        leader.OnSuperseded = async cur =>
        {
            // W9.10a: the Wolverine node leaves before anything else of the hand-over: it drains and releases its rows while this process still holds the plant,
            // so the successor's node (which clears the node table and releases every row of "node 1" when it starts) finds nothing of a running node
            await leader.RunBeforeHandOverAsync("superseded");
            // the final snapshot and the release of the lock come NEXT: the successor waits for them, and a SIGTERM during a drain would skip them
            var saved = snapshots.On && snapshots.Restored.IsCompleted && await snapshots.SaveAsync(true, TimeSpan.FromSeconds(30));
            // a write that failed (the database hiccuped) is tried once more before the lock goes: the successor restores what is there when it takes the lock
            if (!saved && snapshots.On && snapshots.Restored.IsCompleted) saved = await snapshots.SaveAsync(true, TimeSpan.FromSeconds(20));
            // the rule scripts that act on the board are ended now, before the lock goes: they carry no lease any more, so a script left running would write beside the successor
            var (waitedMs, killed) = await runner.DrainFencedAsync(TimeSpan.Zero);
            var n = plant.HandOver("a newer gateway process took over the plant; connect again");
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "handed-over", ["epoch"] = leader.Epoch, ["finalSnapshot"] = saved, ["streamsEnded"] = n, ["to"] = cur?["holder"]?.DeepClone() }.ToJsonString());
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "fenced-drain", ["epoch"] = leader.Epoch, ["waitedMs"] = waitedMs, ["killed"] = killed }.ToJsonString());
        };
        // a standby that takes the lock again restores from the database and writes one of its own epoch at once
        leader.OnReacquired = async () => { await snapshots.RestoreAsync("reacquired"); snapshots.ResetCadence(); };
    }

    // The one ApplicationStopping callback of the lease and the snapshot. Program.cs calls this after the transcripts' final flush is registered:
    // callbacks run last-registered first, so the hand-over runs before the flush. Once, however often it is called.
    public void RegisterStopping()
    {
        if (Interlocked.Exchange(ref stoppingRegistered, 1) != 0) return;
        if (snapshots.On && !leader.On) lifetime.ApplicationStopping.Register(snapshots.SaveAtStop);
        // At SIGTERM the holder hands over: nothing acts, the node leaves, the final snapshot (only of a plant that was restored: one stopped during its start has nothing of its own), then the lock is
        // let go. Bounded by the platform's grace (30 s).
        if (leader.On) lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                var was = leader.BeginStop();
                if (was is not (GatewayLeader.Role.Acting or GatewayLeader.Role.Starting))
                {   // superseded: its final snapshot is written when it stands down; a step-down still running (node leaving, final plant) is given its time before the lock goes
                    leader.WaitForHandOverAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    snapshots.WaitForWrite(TimeSpan.FromSeconds(10));
                    try { leader.ReleaseStandbyAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); } catch (TimeoutException) { }   // a standby that holds nothing (or a lock still held, or a takeover request it wrote) lets go
                    runner.DrainFencedAsync(TimeSpan.Zero).GetAwaiter().GetResult();   // (W0.1) a fenced script still running must not hold the stop up: it refuses its writes anyway
                    Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"plant-snapshot\",\"skipped\":\"{was.ToString().ToLowerInvariant()}\"}}"); return;
                }
                // W9.10a: the node first (bounded: the platform's grace is 30 s, the snapshot and the release below take up to 17), then the snapshot and the lock
                try { leader.RunBeforeHandOverAsync("stop").WaitAsync(NodeLeaves).GetAwaiter().GetResult(); } catch (TimeoutException) { Console.Error.WriteLine("leader stop: the node did not leave in time"); }
                runner.DrainFencedAsync(TimeSpan.Zero).GetAwaiter().GetResult();   // rule scripts that act on the board end before the lock goes (they carry no lease)
                if (was == GatewayLeader.Role.Acting && snapshots.On && snapshots.Restored.IsCompleted) snapshots.SaveAsync(true, TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
                // the SQL lock last (after the final snapshot): the successor takes the lock and restores from the database
                try { leader.ReleaseSqlAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } catch (TimeoutException) { Console.Error.WriteLine("leader stop: the SQL lock was not released in time"); }
            }
            catch (Exception e) { try { Console.Error.WriteLine($"leader stop: {e.Message}"); } catch { } }
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!leader.On)
        {
            if (snapshots.On) _ = Task.Run(snapshots.LoadOnceAsync);
            leader.StartActing();
            // the plant in the database is written on every change here too (a process without a lease writes it at the stop as well)
            if (snapshots.On) _ = Task.Run(() => snapshots.LoopAsync(lifetime.ApplicationStopping));
            return Task.CompletedTask;
        }
        _ = Task.Run(async () =>
        {
            var ct = lifetime.ApplicationStopping;
            try
            {
                // not before this process can serve sessions: a start that never gets ready (the revoked credentials unreadable) must not take the
                // plant from a healthy predecessor
                await revocations.Loaded.WaitAsync(ct);
                // the SQL lock first (the newest process asks the holder to stop its node, save the plant and release, then takes the lock and the next epoch)
                await leader.AcquireAsync(ct);
                await snapshots.RestoreAsync("start");
                // lost while it restored (a newer process asked): it stands by instead of acting
                if (await leader.ConfirmAsync()) leader.StartActing();
                _ = Task.Run(() => leader.LoopAsync(ct));
                if (snapshots.On) _ = Task.Run(() => snapshots.LoopAsync(ct));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine($"leader start: {e.Message}"); }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
