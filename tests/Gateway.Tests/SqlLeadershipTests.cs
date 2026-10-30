using System.Diagnostics;
using Fworker.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10b: the leader lease as a SQL application lock (gateway/Leadership/SqlLease.cs, GatewayLeader). The scenarios run twice with the same code: against a fake lock server (always) and against a real SQL
/// Server (where FACTORY_TEST_SQL is set; the tests return at once without one), two leases on one database as two gateways would hold them.
/// </summary>
internal static class SqlLeadershipSupport
{
    public static GatewayLeader NewLeader(ISqlLease lease, bool fences, double checkSeconds = 0.05)
    {
        var leader = new GatewayLeader(new Runner(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "factory.ps1")), new LeaderSettings { On = true, SqlCheckSeconds = checkSeconds }, "0.0.0");
        leader.UseSqlLease(lease, fences);
        return leader;
    }

    public static async Task Until(Func<bool> c, int seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (!c() && sw.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(10);
        Assert.True(c());
    }

    /// <summary>Ends the loops a test started (declared after the source, so it is disposed first).</summary>
    public sealed class CancelOnDispose(CancellationTokenSource source) : IDisposable
    {
        public void Dispose() { try { source.Cancel(); } catch { } }
    }

    /// <summary>A node that records when it starts and stops, and how many run at once.</summary>
    public sealed class Nodes
    {
        public readonly List<string> Log = new();
        int running; public int MaxRunning;
        public IHost Make(string name) => new Node(this, name);
        sealed class Node(Nodes owner, string name) : IHost
        {
            public IServiceProvider Services => throw new NotSupportedException();
            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                lock (owner.Log) { owner.Log.Add(name + ":start"); owner.MaxRunning = Math.Max(owner.MaxRunning, ++owner.running); }
                return Task.CompletedTask;
            }
            public Task StopAsync(CancellationToken cancellationToken = default) { lock (owner.Log) { owner.Log.Add(name + ":stop"); owner.running--; } return Task.CompletedTask; }
            public void Dispose() { }
        }
        public string[] Snapshot() { lock (Log) return Log.ToArray(); }
    }

    public static WolverineHostService Service(GatewayLeader leader, Nodes nodes, string name) =>
        new(DatabaseOptions.From(n => n == "FACTORY_DB_CONNECTION" ? WolverineHostTestSupport.Unreachable : null), WolverineHostTestSupport.Fast(), leader)
        {
            HostFactory = (_, _) => nodes.Make(name),
            Schema = (_, _) => Task.FromResult(new SchemaReport { Verified = true, Journaled = FactorySchema.Scripts.Count }),
        };

    /// <summary>What LeadershipService registers as OnSuperseded, reduced to its order: the node leaves first, then the final snapshot (logged), then the SQL lock is let go by the leader.</summary>
    public static void HandOver(GatewayLeader leader, Nodes nodes, string name) =>
        leader.OnSuperseded = async _ => { await leader.RunBeforeHandOverAsync("superseded"); lock (nodes.Log) nodes.Log.Add(name + ":snapshot"); };
}

/// <summary>A lock server in memory that behaves as the application lock and the leadership row do.</summary>
internal sealed class FakeLockServer
{
    readonly object gate = new();
    FakeLease? owner; string? lastHolder, requestedBy; long epoch;
    public bool Down;
    public int Attempts;
    public FakeLease NewLease() => new(this);

    /// <summary>The holder's connection drops: the server releases the lock; the holder does not know until it looks.</summary>
    public void Kill(FakeLease lease) { lock (gate) { if (owner == lease) owner = null; } }

    public sealed class FakeLease(FakeLockServer s) : ISqlLease
    {
        bool held; long myEpoch; bool requested, conceded;
        public bool Held { get { lock (s.gate) return held; } }
        public long Epoch { get { lock (s.gate) return myEpoch; } }

        public Task<SqlLeaseAcquire> TryAcquireAsync(string holder, bool request, CancellationToken ct)
        {
            lock (s.gate)
            {
                s.Attempts++;
                if (s.Down) throw new InvalidOperationException("the database does not answer");
                if (held && s.owner == this) return Task.FromResult(new SqlLeaseAcquire(true, myEpoch));
                if (s.owner != null)
                {
                    if (request && !conceded)
                    {
                        if (!requested || s.requestedBy == holder) { s.requestedBy = holder; requested = true; }
                        else conceded = true;
                    }
                    return Task.FromResult(new SqlLeaseAcquire(false, Conceded: conceded));
                }
                if (s.requestedBy != null && s.requestedBy != holder) return Task.FromResult(new SqlLeaseAcquire(false));   // a newer process asked first
                var previous = s.lastHolder;
                s.owner = this; s.epoch++; s.lastHolder = holder; s.requestedBy = null; held = true; myEpoch = s.epoch; requested = conceded = false;
                return Task.FromResult(new SqlLeaseAcquire(true, myEpoch, previous));
            }
        }

        public Task<SqlLeaseCheck> CheckAsync(string holder, CancellationToken ct)
        {
            lock (s.gate)
            {
                if (s.Down || !held || s.owner != this) { held = false; return Task.FromResult(new SqlLeaseCheck(SqlLeaseState.Lost, Failure: "the lock is gone")); }
                if (s.requestedBy != null && s.requestedBy != holder) return Task.FromResult(new SqlLeaseCheck(SqlLeaseState.TakeoverRequested, s.requestedBy));
                return Task.FromResult(new SqlLeaseCheck(SqlLeaseState.Held));
            }
        }

        public Task<SqlLeasePeek> PeekAsync(CancellationToken ct)
        {
            lock (s.gate) { if (s.Down) throw new InvalidOperationException("down"); return Task.FromResult(new SqlLeasePeek(s.owner == null, s.lastHolder, s.epoch)); }
        }

        public Task ReleaseAsync() { lock (s.gate) { if (s.owner == this) s.owner = null; held = false; requested = conceded = false; } return Task.CompletedTask; }
    }
}

/// <summary>The leader's rules around the SQL lock, on the fake server: who runs the node, in which order, and what a process without the lock does.</summary>
public sealed class SqlLeadershipRulesTests
{
    [Fact]
    public async Task TheNewestProcessAsksTheHolder_TheNodeLeavesAndThePlantIsSaved_ThenTheNewcomerTakesTheLockAndStartsItsNode()
    {
        var server = new FakeLockServer(); var nodes = new SqlLeadershipSupport.Nodes(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); using var stop = new SqlLeadershipSupport.CancelOnDispose(cts);
        var a = SqlLeadershipSupport.NewLeader(server.NewLease(), fences: true); var b = SqlLeadershipSupport.NewLeader(server.NewLease(), fences: true);
        var svcA = SqlLeadershipSupport.Service(a, nodes, "A"); var svcB = SqlLeadershipSupport.Service(b, nodes, "B");
        SqlLeadershipSupport.HandOver(a, nodes, "A"); SqlLeadershipSupport.HandOver(b, nodes, "B");
        await svcA.StartAsync(CancellationToken.None); await svcB.StartAsync(CancellationToken.None);

        await a.AcquireSqlAsync(cts.Token);
        a.StartActing();
        _ = Task.Run(() => a.SqlLoopAsync(cts.Token));
        await SqlLeadershipSupport.Until(() => svcA.State == WolverineHostService.Phase.Running);
        Assert.True(a.MayAct); Assert.True(a.MayRunNode); Assert.Equal(1, a.SqlEpoch);

        // B starts: it asks, and does not act while A holds the lock
        var taking = Task.Run(async () => { await b.AcquireSqlAsync(cts.Token); b.StartActing(); _ = Task.Run(() => b.SqlLoopAsync(cts.Token)); }, cts.Token);
        await SqlLeadershipSupport.Until(() => svcB.State == WolverineHostService.Phase.Running);
        await taking;

        Assert.Equal(new[] { "A:start", "A:stop", "A:snapshot", "B:start" }, nodes.Snapshot());   // the old node left, the plant was saved, only then the new node started
        Assert.Equal(1, nodes.MaxRunning);                                                      // never two nodes at once
        Assert.False(a.MayAct); Assert.False(a.MayRunNode); Assert.Equal(GatewayLeader.Role.Superseded, a.State);
        Assert.True(b.MayAct); Assert.Equal(2, b.SqlEpoch);                                      // the newcomer's term is the next epoch
        Assert.Equal(true, (bool?)b.Status()["sql"]!["held"]); Assert.Equal(false, (bool?)a.Status()["sql"]!["held"]);
        await svcA.StopAsync(CancellationToken.None); await svcB.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheHoldersConnectionDrops_ItActsNoMore_AndTheOtherTakesTheLockWithTheNextEpoch()
    {
        var server = new FakeLockServer(); var nodes = new SqlLeadershipSupport.Nodes(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); using var stop = new SqlLeadershipSupport.CancelOnDispose(cts);
        var leaseA = server.NewLease();
        var a = SqlLeadershipSupport.NewLeader(leaseA, fences: true); var b = SqlLeadershipSupport.NewLeader(server.NewLease(), fences: true);
        var svcA = SqlLeadershipSupport.Service(a, nodes, "A"); var svcB = SqlLeadershipSupport.Service(b, nodes, "B");
        SqlLeadershipSupport.HandOver(a, nodes, "A"); SqlLeadershipSupport.HandOver(b, nodes, "B");
        await svcA.StartAsync(CancellationToken.None); await svcB.StartAsync(CancellationToken.None);
        await a.AcquireSqlAsync(cts.Token); a.StartActing(); _ = Task.Run(() => a.SqlLoopAsync(cts.Token));
        await SqlLeadershipSupport.Until(() => svcA.State == WolverineHostService.Phase.Running);

        server.Kill(leaseA);                                   // A is gone: its session ended, the server released the lock
        await b.AcquireSqlAsync(cts.Token); b.StartActing();   // B takes it at once, without the holder's help
        await SqlLeadershipSupport.Until(() => svcB.State == WolverineHostService.Phase.Running);
        await SqlLeadershipSupport.Until(() => !a.MayAct);     // A finds the lock gone at its next look and acts no more
        await SqlLeadershipSupport.Until(() => svcA.State == WolverineHostService.Phase.Stopped);
        Assert.Equal(2, b.SqlEpoch);
        Assert.True(b.MayAct); Assert.False(a.MayAct);
        Assert.Equal(1L, (long?)a.Status()["sql"]!["lost"]);
        Assert.Equal(a.Holder, b.Status()["sql"]!["previousHolder"]!.GetValue<string>());
        await svcA.StopAsync(CancellationToken.None); await svcB.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APassiveStandbyYieldsToANewerProcessThatAsked_AndTheTermClearsTheRequest()
    {
        var server = new FakeLockServer(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); using var stop = new SqlLeadershipSupport.CancelOnDispose(cts);
        var a = server.NewLease(); var b = server.NewLease();
        Assert.True((await a.TryAcquireAsync("A", true, cts.Token)).Acquired);
        Assert.False((await b.TryAcquireAsync("B", true, cts.Token)).Acquired);
        Assert.Equal(SqlLeaseState.TakeoverRequested, (await a.CheckAsync("A", cts.Token)).State);
        await a.ReleaseAsync();
        var third = server.NewLease();
        Assert.False((await third.TryAcquireAsync("C", false, cts.Token)).Acquired);   // a passive standby yields to the newer process that asked
        var got = await b.TryAcquireAsync("B", true, cts.Token);
        Assert.True(got.Acquired); Assert.Equal("A", got.PreviousHolder); Assert.Equal(2, got.Epoch);
        Assert.Equal(SqlLeaseState.Held, (await b.CheckAsync("B", cts.Token)).State);   // the request is cleared by the term
    }

    [Fact]
    public async Task InTheShadowRun_V1ActsWithoutTheLock_AndTheLockGatesTheNodeOnly_ADatabaseOutageNeverStopsV1()
    {
        var server = new FakeLockServer { Down = true }; var nodes = new SqlLeadershipSupport.Nodes(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); using var stop = new SqlLeadershipSupport.CancelOnDispose(cts);
        var lease = server.NewLease();
        var a = SqlLeadershipSupport.NewLeader(lease, fences: false);
        var svcA = SqlLeadershipSupport.Service(a, nodes, "A"); SqlLeadershipSupport.HandOver(a, nodes, "A");
        await svcA.StartAsync(CancellationToken.None);
        _ = Task.Run(() => a.SqlLoopAsync(cts.Token));
        a.StartActing();                                          // the ref lease is taken: v1 acts
        Assert.True(a.MayAct);                                    // ... with the database down
        await Task.Delay(300);
        Assert.Equal(WolverineHostService.Phase.Idle, svcA.State); // ... but no node without the lock
        Assert.False(a.MayRunNode);
        Assert.True((long?)a.Status()["sql"]!["failures"] > 0);
        server.Down = false;                                      // the database is back: the lock is taken in the background and the node starts
        await SqlLeadershipSupport.Until(() => svcA.State == WolverineHostService.Phase.Running);
        Assert.True(a.MayAct && a.MayRunNode);
        server.Kill(lease);                                       // the connection drops: the node leaves, v1 keeps acting
        await SqlLeadershipSupport.Until(() => nodes.Snapshot().Contains("A:stop"));
        Assert.True(a.MayAct);
        await SqlLeadershipSupport.Until(() => nodes.Snapshot().Count(x => x == "A:start") == 2);   // and the lock, taken again, brings the node back
        await svcA.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhereTheEngineActs_TheLockIsPartOfActing_AProcessThatHasTheRefButNotTheLockDoesNotAct()
    {
        var server = new FakeLockServer(); var nodes = new SqlLeadershipSupport.Nodes();
        var leader = SqlLeadershipSupport.NewLeader(server.NewLease(), fences: true);
        var svc = SqlLeadershipSupport.Service(leader, nodes, "A");
        await svc.StartAsync(CancellationToken.None);
        leader.StartActing();                              // the ref says acting; the lock was never taken
        Assert.False(leader.MayAct); Assert.False(leader.MayRunNode);
        await Task.Delay(200);
        Assert.Equal(WolverineHostService.Phase.Idle, svc.State);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var stop = new SqlLeadershipSupport.CancelOnDispose(cts);
        await leader.AcquireSqlAsync(cts.Token);           // the lock comes: now it acts and the node starts
        await SqlLeadershipSupport.Until(() => svc.State == WolverineHostService.Phase.Running);
        Assert.True(leader.MayAct);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhereTheEngineActs_AProcessThatActsOnTheRefWithoutTheLock_StandsDown()
    {
        var server = new FakeLockServer(); var nodes = new SqlLeadershipSupport.Nodes(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); using var stop = new SqlLeadershipSupport.CancelOnDispose(cts);
        var leader = SqlLeadershipSupport.NewLeader(server.NewLease(), fences: true);
        SqlLeadershipSupport.HandOver(leader, nodes, "A");
        leader.StartActing();   // the lock was let go between the take of the lock and of the ref
        _ = Task.Run(() => leader.SqlLoopAsync(cts.Token));
        await SqlLeadershipSupport.Until(() => leader.State == GatewayLeader.Role.Superseded);
        Assert.False(leader.MayAct);
        Assert.Contains("A:snapshot", nodes.Snapshot());
    }

    [Fact]
    public async Task AProcessWithoutADatabase_HasNoSqlLease_AndActsOnTheRefAlone()
    {
        var leader = new GatewayLeader(new Runner(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "factory.ps1")), new LeaderSettings { On = true }, "0.0.0");
        var fired = 0; leader.OnActing += () => fired++;
        Assert.False(leader.HasSqlLease); Assert.False(leader.SqlFences);
        Assert.False(leader.MayAct);
        leader.StartActing();
        Assert.True(leader.MayAct); Assert.True(leader.MayRunNode); Assert.Equal(1, fired);
        await leader.AcquireSqlAsync(CancellationToken.None);   // nothing to take
        await leader.SqlLoopAsync(CancellationToken.None);      // nothing to loop
        Assert.Equal(0, leader.SqlEpoch);
        Assert.Null(leader.Status()["sql"]);
        Assert.Equal(leader.Epoch, leader.ClaimEpoch);
    }
}

/// <summary>The same scenarios on a real SQL Server (FACTORY_TEST_SQL): two leases on one database, the lock a session application lock, the death of the holder a killed session.</summary>
public sealed class SqlLeaseDatabaseTests
{
    static SqlLease NewLease(TestDatabase db) => new(db.ConnectionString, _ => Task.CompletedTask, requestFreshSeconds: 20);

    // the session that holds the lock, killed: what the death of the holder's process (or its network) does to its connection
    static async Task KillHolder(TestDatabase db)
    {
        var spid = await db.Count("SELECT TOP 1 request_session_id FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'GRANT' AND resource_database_id = DB_ID()");
        await db.Execute($"KILL {spid}");
    }

    [Fact]
    public async Task TwoLeasesOnOneDatabase_OnlyOneHoldsTheLock_AndTheEpochIsOnTheRow()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lease1_");
        var a = NewLease(db); var b = NewLease(db);
        try
        {
            var first = await a.TryAcquireAsync("A", true, default);
            Assert.True(first.Acquired); Assert.Equal(1, first.Epoch); Assert.Null(first.PreviousHolder);
            var second = await b.TryAcquireAsync("B", false, default);
            Assert.False(second.Acquired);
            Assert.True(a.Held); Assert.False(b.Held);
            Assert.Equal(SqlLeaseState.Held, (await a.CheckAsync("A", default)).State);
            Assert.Equal(1L, await db.Count("SELECT Epoch FROM factory.Leadership WHERE Id = 1 AND Holder = N'A'"));
            Assert.Equal(1L, await db.Count("SELECT COUNT(*) FROM factory.Leadership"));
            Assert.False((await b.PeekAsync(default)).LockFree);
        }
        finally { await a.ReleaseAsync(); await b.ReleaseAsync(); }
    }

    [Fact]
    public async Task TheHoldersConnectionDrops_TheLockIsFree_TheHolderSeesItLost_AndTheOtherTakesItWithTheNextEpoch()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lease2_");
        var a = NewLease(db); var b = NewLease(db);
        try
        {
            Assert.True((await a.TryAcquireAsync("A", true, default)).Acquired);
            Assert.False((await b.TryAcquireAsync("B", false, default)).Acquired);
            await KillHolder(db);
            var peek = await b.PeekAsync(default);
            Assert.True(peek.LockFree); Assert.Equal("A", peek.LastHolder);                     // the lock is free, and the row still names the dead holder
            var got = await b.TryAcquireAsync("B", false, default);
            Assert.True(got.Acquired); Assert.Equal(2, got.Epoch); Assert.Equal("A", got.PreviousHolder);
            var lost = await a.CheckAsync("A", default);                                         // the dead holder looks: gone
            Assert.Equal(SqlLeaseState.Lost, lost.State); Assert.False(a.Held);
            Assert.Equal(SqlLeaseState.Held, (await b.CheckAsync("B", default)).State);
        }
        finally { await a.ReleaseAsync(); await b.ReleaseAsync(); }
    }

    [Fact]
    public async Task TheNewestProcessAsks_TheHolderSeesTheRequestAndReleases_ANeutralStandbyYields_AndTheAskerTakesIt()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lease3_");
        var a = NewLease(db); var b = NewLease(db); var c = NewLease(db);
        try
        {
            Assert.True((await a.TryAcquireAsync("A", true, default)).Acquired);
            Assert.False((await b.TryAcquireAsync("B", true, default)).Acquired);
            var check = await a.CheckAsync("A", default);
            Assert.Equal(SqlLeaseState.TakeoverRequested, check.State); Assert.Equal("B", check.RequestedBy);
            await a.ReleaseAsync();
            Assert.False(a.Held);
            Assert.False((await c.TryAcquireAsync("C", false, default)).Acquired);               // a passive standby does not jump the newer process that asked
            Assert.False(c.Held);
            var got = await b.TryAcquireAsync("B", true, default);
            Assert.True(got.Acquired); Assert.Equal(2, got.Epoch); Assert.Equal("A", got.PreviousHolder);
            Assert.Equal(0L, await db.Count("SELECT COUNT(*) FROM factory.Leadership WHERE TakeoverBy IS NOT NULL"));   // the term cleared the request
        }
        finally { await a.ReleaseAsync(); await b.ReleaseAsync(); await c.ReleaseAsync(); }
    }

    [Fact]
    public async Task AStaleRequest_IsIgnoredByTheHolder_AndAnOverwrittenRequestConcedes()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lease4_");
        var a = NewLease(db); var b = NewLease(db); var c = NewLease(db);
        try
        {
            Assert.True((await a.TryAcquireAsync("A", true, default)).Acquired);
            Assert.False((await b.TryAcquireAsync("B", true, default)).Acquired);
            await db.Execute("UPDATE factory.Leadership SET TakeoverAt = DATEADD(MINUTE, -10, SYSDATETIMEOFFSET()) WHERE Id = 1");   // B died ten minutes ago
            Assert.Equal(SqlLeaseState.Held, (await a.CheckAsync("A", default)).State);
            // a newer process asks: B's next try finds its request overwritten and concedes
            Assert.False((await c.TryAcquireAsync("C", true, default)).Acquired);
            var again = await b.TryAcquireAsync("B", true, default);
            Assert.False(again.Acquired); Assert.True(again.Conceded);
            Assert.Equal(SqlLeaseState.TakeoverRequested, (await a.CheckAsync("A", default)).State);
            Assert.Equal(1L, await db.Count("SELECT COUNT(*) FROM factory.Leadership WHERE TakeoverBy = N'C'"));
        }
        finally { await a.ReleaseAsync(); await b.ReleaseAsync(); await c.ReleaseAsync(); }
    }

    [Fact]
    public async Task TheSchemaIsPrepared_OnAnEmptyDatabase_BeforeTheLockIsFirstTried()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateEmptyAsync("fw_lease5_");
        var lease = new SqlLease(db.ConnectionString, ct => FactorySchema.UpgradeAsync(db.ConnectionString, new SchemaRunOptions(), ct), 20);
        try
        {
            var got = await lease.TryAcquireAsync("A", true, default);
            Assert.True(got.Acquired); Assert.Equal(1, got.Epoch);
            Assert.Equal(1L, await db.Count("SELECT COUNT(*) FROM factory.Leadership"));
        }
        finally { await lease.ReleaseAsync(); }
    }

    [Fact]
    public async Task TwoGatewaysOnOneDatabase_OnlyOneRunsTheNode_TheNewestTakesOver_AndTheHoldersDeathHandsTheLockOn()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lease6_");
        var nodes = new SqlLeadershipSupport.Nodes(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); using var stop = new SqlLeadershipSupport.CancelOnDispose(cts);
        var a = SqlLeadershipSupport.NewLeader(NewLease(db), fences: true, checkSeconds: 0.2); var b = SqlLeadershipSupport.NewLeader(NewLease(db), fences: true, checkSeconds: 0.2);
        var svcA = SqlLeadershipSupport.Service(a, nodes, "A"); var svcB = SqlLeadershipSupport.Service(b, nodes, "B");
        SqlLeadershipSupport.HandOver(a, nodes, "A"); SqlLeadershipSupport.HandOver(b, nodes, "B");
        await svcA.StartAsync(CancellationToken.None); await svcB.StartAsync(CancellationToken.None);
        try
        {
            await a.AcquireSqlAsync(cts.Token); a.StartActing(); _ = Task.Run(() => a.SqlLoopAsync(cts.Token));
            await SqlLeadershipSupport.Until(() => svcA.State == WolverineHostService.Phase.Running);
            Assert.Equal(1, a.SqlEpoch);

            // B is the newest: it asks, A's node leaves and the plant is saved, B takes the lock (epoch 2) and only then starts its node
            await b.AcquireSqlAsync(cts.Token); b.StartActing(); _ = Task.Run(() => b.SqlLoopAsync(cts.Token));
            await SqlLeadershipSupport.Until(() => svcB.State == WolverineHostService.Phase.Running);
            Assert.Equal(new[] { "A:start", "A:stop", "A:snapshot", "B:start" }, nodes.Snapshot());
            Assert.Equal(1, nodes.MaxRunning);
            Assert.False(a.MayAct); Assert.True(b.MayAct); Assert.Equal(2, b.SqlEpoch);
            Assert.Equal(2L, await db.Count("SELECT Epoch FROM factory.Leadership WHERE Id = 1"));

            // the holder dies (its session is killed): the lock is free at once, a third process takes it with the next epoch, and B finds its lock lost
            var c = SqlLeadershipSupport.NewLeader(NewLease(db), fences: true, checkSeconds: 0.2);
            var svcC = SqlLeadershipSupport.Service(c, nodes, "C"); SqlLeadershipSupport.HandOver(c, nodes, "C"); await svcC.StartAsync(CancellationToken.None);
            await KillHolder(db);
            await c.AcquireSqlAsync(cts.Token); c.StartActing(); _ = Task.Run(() => c.SqlLoopAsync(cts.Token));
            await SqlLeadershipSupport.Until(() => svcC.State == WolverineHostService.Phase.Running);
            await SqlLeadershipSupport.Until(() => !b.MayAct);
            await SqlLeadershipSupport.Until(() => svcB.State == WolverineHostService.Phase.Stopped);
            Assert.Equal(3, c.SqlEpoch);
            Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.Leadership WHERE Holder = N'" + c.Holder + "' AND Epoch = 3"));
            await svcC.StopAsync(CancellationToken.None);
        }
        finally
        {
            cts.Cancel();
            await svcA.StopAsync(CancellationToken.None); await svcB.StopAsync(CancellationToken.None);
            await a.ReleaseSqlAsync(); await b.ReleaseSqlAsync();
        }
    }
}

/// <summary>The whole gateway with the lease on and a database (FACTORY_TEST_SQL): it takes the SQL lock, says so in the status, starts its node, and keeps a second holder out.</summary>
public sealed class SqlLeadershipGatewayTests
{
    [Fact]
    public async Task AGatewayWithADatabaseAndTheLeaseOn_HoldsTheSqlLock_StartsItsNode_AndAnotherProcessCannotTakeIt()
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_gwlease_");
        using var env = LeadershipGatewayTests.Environment(e => { e.Set("FACTORY_DB_CONNECTION", db.ConnectionString); WolverineHostTestSupport.Shadow(e); e.Set("FACTORY_TIMING_leaderSqlCheckSeconds", "1"); });
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await LeadershipGatewayTests.StatusUntil(host, x => LeadershipGatewayTests.Role(x) == "acting" && (bool?)x?["leader"]?["sql"]?["held"] == true && (string?)x?["wolverine"]?["state"] == "running", 60);
        Assert.Equal("acting", LeadershipGatewayTests.Role(s));
        Assert.Equal(true, (bool?)s?["leader"]?["sql"]?["held"]);
        Assert.Equal(false, (bool?)s?["leader"]?["sql"]?["fencesActing"]);   // the shadow run: v1 acts, the lock gates the node only
        Assert.Equal(1L, (long?)s?["leader"]?["sql"]?["epoch"]);
        Assert.Equal("running", (string?)s?["wolverine"]?["state"]);
        Assert.DoesNotContain("Password", s!.ToJsonString());
        Assert.Equal(1L, await db.Count("SELECT Epoch FROM factory.Leadership WHERE Id = 1"));
        // another process on the same database does not get the lock while this one holds it
        var other = new SqlLease(db.ConnectionString, _ => Task.CompletedTask, 20);
        try { Assert.False((await other.TryAcquireAsync("another-process", false, default)).Acquired); }
        finally { await other.ReleaseAsync(); }
    }
}
