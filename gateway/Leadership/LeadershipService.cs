using System.Diagnostics;
using System.Text.Json.Nodes;

// The leader lease's start, the hand-over and the stop (Leader.cs, issue 94; gateway-composition.md 3 E). Moved out of Program.cs unchanged (C4d).
// GatewayLeader holds the lease and its loop; SnapshotCoordinator holds the plant snapshot; this service decides when each runs:
// - at the host's start: without the lease, the one read of the snapshot (and the process acts at once); with it, once the revoked credentials are
//   read (W9.10b: and, where there is a database and the engine acts, the SQL application lock is taken first, SqlLease.cs), the lease is taken, the plant restored from the predecessor, the lease confirmed, then the lease's loop and the snapshot of every change;
// - when a newer process takes the lease (superseded): the Wolverine node leaves (W9.10a), the final snapshot, then the streams end with stream.replaced;
// - when a standby takes the lease again: the restore from the releaser's snapshot, then the node starts (GatewayLeader.OnActing);
// - at the stop (ApplicationStopping, RegisterStopping): the node leaves, then the hand-over, or without the lease the write of the plant.
// Nothing here is stopped with the host: the loops end at ApplicationStopping, as before.
sealed class LeadershipService : IHostedService
{
    readonly GatewayLeader leader;
    readonly SnapshotCoordinator snapshots;
    readonly IRevocationService revocations;
    readonly IHostApplicationLifetime lifetime;
    int stoppingRegistered;
    readonly Runner runner;

    // how long a superseded process lets the running rule scripts that act on the board finish before it ends them (delivery-speed.md 18.1.1): a sync-board
    // can run 280 s, the successor waits leaderHandoverSeconds (90) for the final snapshot
    public static readonly TimeSpan DrainFenced = TimeSpan.FromSeconds(45);
    // how long the stop of the holder waits for the Wolverine node to leave before the final snapshot (W9.10a)
    public static readonly TimeSpan NodeLeaves = TimeSpan.FromSeconds(10);

    public LeadershipService(GatewayLeader leader, SnapshotCoordinator snapshots, IRevocationService revocations, Plant plant, IHostApplicationLifetime lifetime, Runner runner)
    {
        this.leader = leader; this.snapshots = snapshots; this.revocations = revocations; this.lifetime = lifetime; this.runner = runner;
        // the plant plans and starts runtime sessions only while this process holds the lease (Plant.Plan, RuntimeManager.Provision; W0.1)
        plant.MayAct = () => leader.MayAct;
        if (!leader.On) return;
        snapshots.MarkLeader();
        // superseded: nothing acts any more (the sweeper and the ticks ask MayAct); the running rule scripts that act on the board stop at their next write
        // (they find the lease lost); the final snapshot, then the streams end with stream.replaced, then the scripts are awaited for at most 45 s and ended
        leader.OnSuperseded = async cur =>
        {
            // W9.10a: the Wolverine node leaves before anything else of the hand-over: it drains and releases its rows while this process still holds the plant,
            // so the successor's node (which clears the node table and releases every row of "node 1" when it starts) finds nothing of a running node
            await leader.RunBeforeHandOverAsync("superseded");
            // the final snapshot and the hand-over come NEXT: the successor waits for them, and a SIGTERM during a drain would skip them. The running
            // fenced scripts refuse their own writes (they find the lease lost); they are drained in the background and ended after 45 s.
            var saved = snapshots.On && snapshots.Restored.IsCompleted && await snapshots.SaveAsync(true, TimeSpan.FromSeconds(30));
            var n = plant.HandOver("a newer gateway process took over the plant; connect again");
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "handed-over", ["epoch"] = leader.Epoch, ["finalSnapshot"] = saved, ["streamsEnded"] = n, ["to"] = cur?["holder"]?.DeepClone() }.ToJsonString());
            _ = Task.Run(async () =>
            {
                try
                {
                    var (waitedMs, killed) = await runner.DrainFencedAsync(DrainFenced);
                    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "fenced-drain", ["epoch"] = leader.Epoch, ["waitedMs"] = waitedMs, ["killed"] = killed }.ToJsonString());
                }
                catch (Exception e) { Console.Error.WriteLine($"fenced drain: {e.Message}"); }
            });
        };
        // a standby that takes the lease again restores from the releaser's snapshot and writes one of its own epoch at once
        leader.OnReacquired = async pred => { await snapshots.RestoreFromPredecessorAsync(pred, "reacquired"); snapshots.ResetCadence(); };
    }

    // The one ApplicationStopping callback of the lease and the snapshot. Program.cs calls this after the transcripts' final flush is registered:
    // callbacks run last-registered first, so the hand-over runs before the flush. Once, however often it is called.
    public void RegisterStopping()
    {
        if (Interlocked.Exchange(ref stoppingRegistered, 1) != 0) return;
        if (snapshots.On && !leader.On) lifetime.ApplicationStopping.Register(snapshots.SaveAtStop);
        // At SIGTERM the holder hands over: nothing acts, the final snapshot (only of a plant that was restored: one stopped during its start has
        // nothing of its own), then the lease is released naming whose snapshot holds the plant. Bounded by the platform's grace (30 s).
        if (leader.On) lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                var was = leader.BeginStop();
                if (was is not (GatewayLeader.Role.Acting or GatewayLeader.Role.Starting))
                {   // superseded: its final snapshot was written when it stood down; one still being written is given its time
                    snapshots.WaitForWrite(TimeSpan.FromSeconds(10));
                    try { leader.ReleaseStandbyAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); } catch (TimeoutException) { }   // (W9.10b) a standby that holds nothing (or a lock still held, or a ref record it wrote without the lock) lets go
                    runner.DrainFencedAsync(TimeSpan.Zero).GetAwaiter().GetResult();   // (W0.1) a fenced script still running must not hold the stop up: it refuses its writes anyway
                    Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"plant-snapshot\",\"skipped\":\"{was.ToString().ToLowerInvariant()}\"}}"); return;
                }
                var sw = Stopwatch.StartNew();
                // W9.10a: the node first (bounded: the platform's grace is 30 s, the snapshot and the release below take up to 22), then the snapshot and the lease
                try { leader.RunBeforeHandOverAsync("stop").WaitAsync(NodeLeaves).GetAwaiter().GetResult(); } catch (TimeoutException) { Console.Error.WriteLine("leader stop: the node did not leave in time"); }
                if (was == GatewayLeader.Role.Acting && snapshots.On && snapshots.Restored.IsCompleted) snapshots.SaveAsync(true, TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
                leader.ReleaseAsync(snapshots.HeldEpoch, TimeSpan.FromSeconds(Math.Max(5, 22 - sw.Elapsed.TotalSeconds))).GetAwaiter().GetResult();
                // W9.10b: the SQL lock last (after the ref's release, which names whose snapshot holds the plant): the successor takes the lock first and the ref next
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
                // W9.10b: where there is a database, the SQL lock first when the engine acts (the newest process asks the holder to stop its node, save the plant and release, then takes the lock and the
                // next epoch) and the ref only with the lock held (GatewayLeader.AcquireAllAsync); in the shadow run v1 acts at once and the lock, taken in the background, gates the node only
                if (leader.HasSqlLease && !leader.SqlFences) leader.StartSqlLoop(ct);
                var pred = await leader.AcquireAllAsync(ct);
                await snapshots.RestoreFromPredecessorAsync(pred, "start");
                // superseded while it restored (two starts close together): it stands by instead of acting
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
