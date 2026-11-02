using System.Text.Json.Nodes;
using Fworker.Database;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Gateway.Tests;

/// <summary>W9.10o: a FACTORY_DB_CONNECTION that is not a connection string is no database; the schema gate of /ready is only where v2 acts.</summary>
public sealed class DatabaseSettingFaultTests
{
    static Func<string, string?> Env(string? value) => n => n == "FACTORY_DB_CONNECTION" ? value : null;

    [Theory]
    [InlineData("not a connection string")]
    [InlineData("Server")]
    [InlineData("Bogus=1;Server=db")]
    [InlineData("Database=f;User Id=sa")]
    [InlineData("Server=;Database=f")]
    public void AMalformedString_IsNotConfigured_AndSaysSo_WithoutShowingTheValue(string value)
    {
        var d = DatabaseOptions.From(Env(value));
        Assert.False(d.Configured);
        Assert.True(d.Malformed);
        Assert.Null(d.ConnectionString);
        Assert.Equal("DatabaseOptions(malformed)", d.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void NothingSet_IsNotMalformed(string? value) => Assert.False(DatabaseOptions.From(Env(value)).Malformed);

    [Fact]
    public void AValidString_IsConfigured_AndTheUnreachableOneOfTheTestsToo()
    {
        Assert.True(DatabaseOptions.From(Env("Server=db;Database=f")).Configured);
        Assert.True(DatabaseOptions.From(Env(WolverineHostTestSupport.Unreachable)).Configured);
        Assert.False(DatabaseOptions.From(Env("Server=db;Database=f")).Malformed);
    }

    [Fact]
    public void TheSchemaIsRequiredOnlyWhereV2Acts_AndTrackedWhereTheHostRuns()
    {
        var shadow = new FactorySchemaStatus(required: false, tracked: true);
        Assert.False(shadow.Required);
        Assert.True(shadow.Tracked);
        Assert.Null(shadow.Waiting());                    // /ready does not wait
        Assert.Equal("upgrading", (string?)shadow.Status()!["state"]);   // the status carries it
        var v2 = new FactorySchemaStatus(required: true);
        Assert.True(v2.Required && v2.Tracked);
        Assert.NotNull(v2.Waiting());
        var none = new FactorySchemaStatus(required: false);
        Assert.Null(none.Status());
        Assert.False(none.Tracked);
    }
}

/// <summary>W9.10o: the v2 block of /api/v1/status, from a fake source and a clock the test moves; no database.</summary>
public sealed class V2HealthTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    sealed class Source : IV2HealthSource
    {
        public Func<V2Facts> Facts = () => Empty();
        public Exception? Fail;
        public int Reads;
        public Task<V2Facts> Read(CancellationToken ct)
        {
            Reads++;
            if (Fail != null) throw Fail;
            return Task.FromResult(Facts());
        }
    }

    sealed class Clock { public DateTimeOffset Now = T0; }

    static V2Facts Empty() => new([], 0, new EnvelopeFacts(0, 0, []), new ScheduledFacts(0, 0, null, null), new EnvelopeFacts(0, 0, []), null, [], [], new LedgerFacts(0, 0, null), []);

    static FactorySchemaStatus Good()
    {
        var s = new FactorySchemaStatus(required: true);
        s.Good(new SchemaReport { Verified = true });
        return s;
    }

    static (V2Health Health, Source Source, Clock Clock) Make(FactorySchemaStatus? schema = null, bool acts = true, RecurringJobRegistry? jobs = null, WorkItemRearm? rearm = null)
    {
        var source = new Source(); var clock = new Clock();
        var engine = new EngineOptions { Environment = "tdd", Configured = acts ? EngineMode.V2 : EngineMode.Shadow };
        return (new V2Health(source, schema ?? Good(), engine, () => jobs, () => rearm, () => clock.Now), source, clock);
    }

    [Fact]
    public async Task W99b_TheAdoptionBlock_IsThereWhereTheEngineActs_EvenWhileTheSchemaIsNotGood_AndNowhereElse()
    {
        var engine = new EngineOptions { Environment = "tdd", Configured = EngineMode.V2 };
        var block = new JsonObject { ["state"] = "running", ["last"] = new JsonObject { ["adopted"] = 2, ["upserted"] = 2, ["skipped"] = 1, ["skippedBy"] = new JsonObject { ["settled"] = 1 } } };
        var acting = new V2Health(new Source(), new FactorySchemaStatus(required: true), engine, () => null, () => null, adoption: () => block);
        var o = await acting.Block(default);
        Assert.Equal((false, 2, 1), ((bool)o["available"]!, (int)o["adoption"]!["last"]!["adopted"]!, (int)o["adoption"]!["last"]!["skippedBy"]!["settled"]!));
        var shadow = new V2Health(new Source(), Good(), new EngineOptions { Environment = "tdd", Configured = EngineMode.Shadow }, () => null, () => null, adoption: () => block);
        Assert.False((await shadow.Block(default)).ContainsKey("adoption"));
        var none = new V2Health(new Source(), Good(), engine, () => null, () => null, adoption: () => null);
        Assert.False((await none.Block(default)).ContainsKey("adoption"));
    }

    [Fact]
    public async Task WhileTheSchemaIsNotGood_TheBlockSaysItCannotRead_AndTouchesNothing()
    {
        var (health, source, _) = Make(new FactorySchemaStatus(required: true));
        var o = await health.Block(default);
        Assert.Equal((false, "not-good", true), ((bool)o["available"]!, (string)o["schema"]!, (bool)o["acts"]!));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task TheDeadLettersAreCountedByType_WithTheOldestFailure_AndNoMessageText()
    {
        var (health, source, clock) = Make();
        source.Facts = () => Empty() with
        {
            DeadLetterTotal = 7,
            DeadLetters = [new DeadLetterGroup("workitem.merge-requested", "Fworker.Workflows.WorkItem.WorkItemConflictException", 5, T0.AddHours(-2)), new DeadLetterGroup("delivery.train-wake", "System.TimeoutException", 2, T0.AddMinutes(-5))],
        };
        var o = await health.Block(default);
        var d = (JsonObject)o["deadLetters"]!;
        Assert.Equal(7, (int)d["total"]!);
        var first = (JsonObject)d["byType"]![0]!;
        Assert.Equal(("workitem.merge-requested", "Fworker.Workflows.WorkItem.WorkItemConflictException", 5), ((string)first["messageType"]!, (string)first["exceptionType"]!, (int)first["count"]!));
        Assert.Equal("delivery.train-wake", (string)d["byType"]![1]!["messageType"]!);
        Assert.False((bool)d["truncated"]!);
        Assert.Equal(clock.Now.ToString("o"), (string)o["readAt"]!);
    }

    [Fact]
    public async Task TheEnvelopes_ShowCounts_ThePersistentOnesAgeFromTheirFirstSighting_AndAScheduledOneIsOverdueFromItsTime()
    {
        var (health, source, clock) = Make();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        source.Facts = () => Empty() with
        {
            Incoming = new EnvelopeFacts(2, 3, [a, b]), Outgoing = new EnvelopeFacts(1, 1, [Guid.Parse("00000000-0000-0000-0000-000000000009")]),
            Scheduled = new ScheduledFacts(4, 2, T0.AddMinutes(-9), T0.AddHours(1),
            [
                new ScheduledGroup("Fworker.Workflows.Delivery.TrainWake", 3, 2, T0.AddMinutes(-9), T0.AddHours(1)),
                new ScheduledGroup("Fworker.Workflows.Merge.SomethingElse", 1, 0, null, T0.AddHours(2)),
            ], 1, 2),
        };
        var first = await health.Block(default);
        Assert.Equal((2, 3, 0L), ((int)first["incoming"]!["count"]!, (int)first["incoming"]!["maxAttempts"]!, (long)first["incoming"]!["oldestSeenSeconds"]!));
        Assert.Equal((4, 2, 540L), ((int)first["scheduled"]!["count"]!, (int)first["scheduled"]!["overdue"]!, (long)first["scheduled"]!["oldestOverdueSeconds"]!));
        Assert.Equal(T0.AddHours(1).ToString("o"), (string)first["scheduled"]!["nextDue"]!);
        // a batch of timers hides behind a single nextDue: the due-soon counts show it
        Assert.Equal((1, 2), ((int)first["scheduled"]!["dueIn5Minutes"]!, (int)first["scheduled"]!["dueIn30Minutes"]!));
        // by message type, the biggest first: what a backlog of timers is made of
        var types = (JsonArray)first["scheduled"]!["byType"]!;
        Assert.Equal(2, types.Count);
        Assert.Equal(("Fworker.Workflows.Delivery.TrainWake", 3, 2, 540L), ((string)types[0]!["messageType"]!, (int)types[0]!["count"]!, (int)types[0]!["overdue"]!, (long)types[0]!["oldestOverdueSeconds"]!));
        Assert.Null(types[1]!["oldestOverdueSeconds"]);
        // 3 minutes later (past the 10 second cache): the same envelopes are still there, so they are 3 minutes old as far as this process can say
        clock.Now = T0.AddMinutes(3);
        var later = await health.Block(default);
        Assert.Equal(180L, (long)later["incoming"]!["oldestSeenSeconds"]!);
        Assert.Equal(180L, (long)later["outgoing"]!["oldestSeenSeconds"]!);
        // one incoming envelope is handled, the other stays: the age is the remaining one's
        source.Facts = () => Empty() with { Incoming = new EnvelopeFacts(1, 3, [b]) };
        clock.Now = T0.AddMinutes(4);
        Assert.Equal(240L, (long)(await health.Block(default))["incoming"]!["oldestSeenSeconds"]!);
        // all gone: no age
        source.Facts = () => Empty();
        clock.Now = T0.AddMinutes(5);
        Assert.Null((await health.Block(default))["incoming"]!["oldestSeenSeconds"]);
    }

    [Fact]
    public async Task TheJobs_ShowTheLastRunOfEachRecurringJob_WithTheTypeOfAFailure()
    {
        var jobs = new RecurringJobRegistry();
        jobs.Add("fine", _ => Task.CompletedTask).Add("broken", _ => throw new TimeoutException("a message that must not be shown")).Add("never-ran", _ => Task.CompletedTask);
        await jobs.Run("fine", default); await jobs.Run("broken", default);
        var (health, _, _) = Make(jobs: jobs);
        var j = (JsonObject)(await health.Block(default))["jobs"]!;
        Assert.Equal(1, (int)j["fine"]!["runs"]!);
        Assert.Null(j["fine"]!["lastError"]);
        Assert.NotNull(j["fine"]!["lastStartedAt"]);
        Assert.Equal("TimeoutException", (string)j["broken"]!["lastError"]!);
        Assert.Equal(0, (int)j["broken"]!["runs"]!);
        Assert.Equal(0, (int)j["never-ran"]!["runs"]!);
        Assert.Null(j["never-ran"]!["lastStartedAt"]);
        Assert.DoesNotContain("must not be shown", j.ToJsonString());
    }

    [Fact]
    public async Task InShadow_ThereAreNoJobs_AndTheBlockSaysTheEngineDoesNotAct()
    {
        var (health, source, _) = Make(acts: false);
        var o = await health.Block(default);
        Assert.Equal((false, true), ((bool)o["acts"]!, (bool)o["available"]!));
        Assert.Empty((JsonObject)o["jobs"]!);
        Assert.Equal(0, (int)o["sagas"]!["open"]!);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task TheSagasAreCountedByStep_WithTheOldestAge_AndTheRearmIsShown()
    {
        var rearm = new WorkItemRearm(new InMemoryWorkItemStore(), WorkItemPolicy.Default, _ => Task.FromResult(true), () => T0);
        await rearm.Run(default);
        var (health, source, _) = Make(rearm: rearm);
        source.Facts = () => Empty() with
        {
            Sagas = [new SagaGroup("docs", "Develop", WorkItemStatus.Open, 3, T0.AddHours(-2)), new SagaGroup("docs", "Design", WorkItemStatus.Open, 1, T0.AddMinutes(-30)), new SagaGroup("feature", "Deliver", WorkItemStatus.Settling, 2, T0.AddMinutes(-5))],
        };
        var s = (JsonObject)(await health.Block(default))["sagas"]!;
        Assert.Equal((4, 2), ((int)s["open"]!, (int)s["settling"]!));
        var steps = s["byStep"]!.AsArray();
        Assert.Equal(["Design", "Develop", "Deliver"], steps.Select(x => (string)x!["step"]!));   // open ones first (by plan and step), then settling
        Assert.Equal((3, 7200L), ((int)steps[1]!["count"]!, (long)steps[1]!["oldestAgeSeconds"]!));
        Assert.Equal((0, 0, 0), ((int)s["rearm"]!["open"]!, (int)s["rearm"]!["timersSent"]!, (int)s["rearm"]!["failed"]!));
    }

    [Fact]
    public async Task TheLedgerAndTheTrains_AreSummarisedAsCountsAndCodes()
    {
        var capacity = new CapacityState();
        capacity.Slots["s1"] = new LedgerSlot { SlotId = "s1", Item = "acme/orders#7" };
        capacity.Queue["acme/orders#8"] = new WaitingRequest { Item = "acme/orders#8", RequestedAt = T0.AddMinutes(-20) };
        capacity.InFlight["acme/orders#7"] = new InFlightItem { Item = "acme/orders#7" };
        var train = MergeTrainState.New("acme/orders");
        train.Line.Add(new MergeTrainEntry { Item = "acme/orders#7", EnqueuedAt = T0.AddMinutes(-3), Granted = true });
        var delivery = TrainState.New("orders/uat");
        delivery.Releases["1.0.2"] = new TrainRelease(); delivery.Requested["1.0.2"] = T0.AddMinutes(-4); delivery.FirstRequested["1.0.2"] = T0.AddMinutes(-9); delivery.Held = true;
        var (health, source, _) = Make();
        source.Facts = () => Empty() with
        {
            Capacity = new CapacityLedgerFacts(capacity, T0.AddMinutes(-1)), MergeTrains = [V2Health.MergeTrainLine("acme/orders", train, T0)], DeliveryTrains = [V2Health.DeliveryTrainLine("orders/uat", delivery, T0)],
            IdempotencyLedger = new LedgerFacts(12, 2, T0.AddDays(-3)),
        };
        var o = await health.Block(default);
        var cap = (JsonObject)o["capacity"]!;
        Assert.Equal((1, 1, 1, 1200L), ((int)cap["slots"]!, (int)cap["queued"]!, (int)cap["inFlight"]!, (long)cap["oldestWaitingSeconds"]!));
        var m = (JsonObject)o["mergeTrains"]![0]!;
        Assert.Equal(("acme/orders", 1, 1, 180L), ((string)m["key"]!, (int)m["line"]!, (int)m["granted"]!, (long)m["oldestWaitSeconds"]!));
        var t = (JsonObject)o["deliveryTrains"]![0]!;
        Assert.Equal(("orders/uat", 1, 1, true, 540L), ((string)t["key"]!, (int)t["releases"]!, (int)t["requested"]!, (bool)t["held"]!, (long)t["oldestRequestSeconds"]!));
        var l = (JsonObject)o["idempotencyLedger"]!;
        Assert.Equal((12, 2, 259200L), ((int)l["entries"]!, (int)l["failures"]!, (long)l["oldestAgeSeconds"]!));
    }

    static (V2Health Health, Source Source) MakeWith(TimeSpan? hostReportMax = null)
    {
        var source = new Source();
        return (new V2Health(source, Good(), new EngineOptions(), () => null, () => null, () => T0, hostReportMax: hostReportMax), source);
    }

    [Fact]
    public async Task TheCapacityBlock_NamesTheWaitingReasonsAndTheAgeOfEachHostReport()
    {
        var capacity = new CapacityState();
        capacity.Hosts["omarchy"] = new LedgerHost { Name = "omarchy", Runtime = "octopus", ReportedAt = T0.AddMinutes(-45) };
        capacity.Hosts["cloud-1"] = new LedgerHost { Name = "cloud-1", Runtime = "octopus", ReportedAt = T0.AddMinutes(-12) };
        capacity.Queue["acme/orders#1"] = new WaitingRequest { Item = "acme/orders#1", RequestedAt = T0.AddMinutes(-30), Announced = "host-headroom no host reported" };
        capacity.Queue["acme/orders#2"] = new WaitingRequest { Item = "acme/orders#2", RequestedAt = T0.AddMinutes(-10), Announced = "host-headroom" };
        capacity.Queue["acme/orders#3"] = new WaitingRequest { Item = "acme/orders#3", RequestedAt = T0.AddMinutes(-5), Announced = "runtime-budget" };
        capacity.Queue["acme/orders#4"] = new WaitingRequest { Item = "acme/orders#4", RequestedAt = T0.AddMinutes(-1) };
        var (health, source) = MakeWith();
        source.Facts = () => Empty() with { Capacity = new CapacityLedgerFacts(capacity, T0) };
        var cap = (JsonObject)(await health.Block(default))["capacity"]!;
        var waiting = cap["waiting"]!.AsArray();
        Assert.Equal([("host-headroom", 2, 1800L), ("unjudged", 1, 60L), ("runtime-budget", 1, 300L)], waiting.Select(w => ((string)w!["code"]!, (int)w["count"]!, (long)w["oldestWaitingSeconds"]!)));
        var r = (JsonObject)cap["hostReports"]!;
        Assert.Equal(10, (int)r["hostReportMaxMinutes"]!);
        var hosts = r["hosts"]!.AsArray();
        Assert.Equal([("cloud-1", 720L, "stale"), ("omarchy", 2700L, "gone")], hosts.Select(h => ((string)h!["name"]!, (long)h["reportAgeSeconds"]!, (string)h["state"]!)));
        Assert.Equal(720L, (long)r["freshestAgeSeconds"]!);
        Assert.False((bool)r["noHostReported"]!);   // cloud-1 reported 12 minutes ago: not 30
    }

    [Fact]
    public async Task NoHostReportedForThirtyMinutes_IsFlagged_AndAnEmptyHostListIsNot()
    {
        var capacity = new CapacityState();
        capacity.Hosts["omarchy"] = new LedgerHost { Name = "omarchy", Runtime = "octopus", ReportedAt = T0.AddMinutes(-31) };
        var (health, source) = MakeWith(TimeSpan.FromMinutes(5));
        source.Facts = () => Empty() with { Capacity = new CapacityLedgerFacts(capacity, T0) };
        var r = (JsonObject)(await health.Block(default))["capacity"]!["hostReports"]!;
        Assert.Equal((true, 1860L, 30, 5), ((bool)r["noHostReported"]!, (long)r["freshestAgeSeconds"]!, (int)r["alertAfterMinutes"]!, (int)r["hostReportMaxMinutes"]!));
        capacity.Hosts["omarchy"].ReportedAt = T0.AddMinutes(-30);   // exactly 30: not yet
        Assert.False((bool)((JsonObject)(await MakeFresh(capacity).Block(default))["capacity"]!["hostReports"]!)["noHostReported"]!);
        var none = (JsonObject)(await MakeFresh(new CapacityState()).Block(default))["capacity"]!["hostReports"]!;
        Assert.False((bool)none["noHostReported"]!);
        Assert.Null(none["freshestAgeSeconds"]);
    }

    static V2Health MakeFresh(CapacityState s)
    {
        var (h, src) = MakeWith();
        src.Facts = () => Empty() with { Capacity = new CapacityLedgerFacts(s, T0) };
        return h;
    }

    [Fact]
    public async Task TheWatch_WritesOneLine_OnlyWhenNoHostReported_AndOnlyOnTheProcessThatMayAct()
    {
        var capacity = new CapacityState();
        capacity.Hosts["omarchy"] = new LedgerHost { Name = "omarchy", Runtime = "octopus", ReportedAt = T0.AddMinutes(-40) };
        var lines = new List<string>();
        var may = true;
        var watch = new HostSilenceWatch(MakeFresh(capacity), () => may, write: lines.Add);
        Assert.True(await watch.Pass(default));
        var line = (JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(lines))!;
        Assert.Equal(("none", 2400L, 30), ((string)line["hostReports"]!, (long)line["freshestAgeSeconds"]!, (int)line["alertAfterMinutes"]!));
        may = false;
        Assert.False(await watch.Pass(default));
        may = true;
        capacity.Hosts["omarchy"].ReportedAt = T0.AddMinutes(-1);
        Assert.False(await new HostSilenceWatch(MakeFresh(capacity), () => true, write: lines.Add).Pass(default));
        Assert.Single(lines);
    }

    [Fact]
    public async Task ASharedRead_IsServedFor10Seconds_AFailedReadServesTheLastGoodOneMarkedStale_AndAFirstFailureIsUnavailable()
    {
        var (health, source, clock) = Make();
        source.Fail = new TimeoutException("password=secret");
        var none = await health.Block(default);
        Assert.Equal((false, "TimeoutException"), ((bool)none["available"]!, (string)none["error"]!));
        Assert.DoesNotContain("secret", none.ToJsonString());
        source.Fail = null;
        var ok = await health.Block(default);
        Assert.True((bool)ok["available"]!);
        Assert.False((bool)ok["stale"]!);
        var reads = source.Reads;
        clock.Now = T0.AddSeconds(5);
        await health.Block(default);
        Assert.Equal(reads, source.Reads);   // within the 10 seconds: no new read
        clock.Now = T0.AddSeconds(30);
        source.Fail = new InvalidOperationException("boom");
        var stale = await health.Block(default);
        Assert.Equal((true, true, "InvalidOperationException"), ((bool)stale["available"]!, (bool)stale["stale"]!, (string)stale["error"]!));
        Assert.Equal(T0.ToString("o"), (string)stale["readAt"]!);   // (the answer of the earlier read)
    }
}

/// <summary>The recurring jobs remember how their latest run ended.</summary>
public sealed class RecurringJobLastRunTests
{
    [Fact]
    public async Task ARunIsRemembered_WithItsOutcome_AndALaterRunReplacesIt()
    {
        var registry = new RecurringJobRegistry(); var fail = true;
        registry.Add("job", _ => fail ? throw new InvalidOperationException("no") : Task.CompletedTask);
        await registry.Run("job", default);
        Assert.Equal("InvalidOperationException", registry.LastRuns["job"].Error);
        Assert.Equal(0, registry.RunCount("job"));
        fail = false;
        await registry.Run("job", default);
        Assert.Null(registry.LastRuns["job"].Error);
        Assert.Equal(1, registry.RunCount("job"));
        Assert.Equal(["job"], registry.Names);
    }

    [Fact]
    public void TheDefaultSlots_IncludeTheRearmEvery5Minutes_AndTheLedgerPurgeEvery6Hours()
    {
        var slots = RecurringJobSlots.Default().Slots;
        Assert.Equal(TimeSpan.FromMinutes(5), slots.Single(s => s.Name == RecurringJobSlots.SagaRearm).Every);
        Assert.Equal(TimeSpan.FromHours(6), slots.Single(s => s.Name == RecurringJobSlots.LedgerPurge).Every);
    }
}

/// <summary>The queries of the health block, against a real SQL Server (FACTORY_TEST_SQL; the test returns where it is not set).</summary>
public sealed class SqlV2HealthSourceTests
{
    static string With(string cs, string database) => new SqlConnectionStringBuilder(cs) { InitialCatalog = database }.ConnectionString;

    static async Task Exec(string cs, string sql, params (string, object)[] ps)
    {
        await using var c = new SqlConnection(cs); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task TheFactsAreReadFromTheTablesOfTheScripts()
    {
        var sql = WolverineHostTestSupport.Sql;
        if (sql == null) return;
        var db = "fw_w910o_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master"); var cs = With(sql, db);
        await Exec(master, $"CREATE DATABASE [{db}]");
        try
        {
            var report = await FactorySchema.UpgradeAndVerifyAsync(cs, new SchemaRunOptions { CreateDatabase = false });
            Assert.True(report.Good);
            var now = DateTimeOffset.UtcNow;
            // two dead letters of one kind, one of another, one archived (not counted)
            foreach (var (type, exception, archived) in new[] { ("workitem.start", "WorkItemConflictException", false), ("workitem.start", "WorkItemConflictException", false), ("delivery.wake", "TimeoutException", false), ("old", "Old", true) })
                await Exec(cs, "INSERT wolverine.wolverine_dead_letters (id, body, message_type, received_at, exception_type, failed_at, archived_at) VALUES (NEWID(), 0x00, @t, 'local://x', @e, @f, @a)",
                    ("@t", type), ("@e", exception), ("@f", now.AddMinutes(-30)), ("@a", archived ? now : DBNull.Value));
            await Exec(cs, "INSERT wolverine.wolverine_incoming_envelopes (id, status, owner_id, body, message_type, received_at, attempts) VALUES (NEWID(), 'Incoming', 0, 0x00, 'x', 'local://x', 4)");
            await Exec(cs, "INSERT wolverine.wolverine_incoming_envelopes (id, status, owner_id, execution_time, body, message_type, received_at) VALUES (NEWID(), 'Scheduled', 0, @t, 0x00, 'x', 'local://x'), (NEWID(), 'Scheduled', 0, @u, 0x00, 'y', 'local://x'), (NEWID(), 'Scheduled', 0, @u, 0x00, 'y', 'local://x'), (NEWID(), 'Handled', 0, NULL, 0x00, 'x', 'local://x')",
                ("@t", now.AddMinutes(-10)), ("@u", now.AddMinutes(10)));
            await Exec(cs, "INSERT wolverine.wolverine_outgoing_envelopes (id, owner_id, destination, body, message_type) VALUES (NEWID(), 0, 'local://y', 0x00, 'x'), (NEWID(), 0, 'local://y', 0x00, 'x')");
            await Exec(cs, "INSERT factory.IdempotencyLedger ([Key], Ok, Failure, Error, Execution, Url, RecordedAt) VALUES ('a', 1, 0, '', '', '', SYSUTCDATETIME()), ('b', 0, 2, 'no', '', '', DATEADD(day, -2, SYSUTCDATETIME()))");
            var item = new Fworker.Workflows.WorkItem.WorkItemState { Item = "acme/orders#7", Generation = 1, Plan = "docs", Step = StepKind.Develop };
            await Exec(cs, "INSERT factory.WorkItems (ItemKey, Repository, Generation, Revision, [Status], [Plan], [Step], StateJson, UpdatedAt) VALUES ('acme/orders#7', 'acme/orders', 1, 1, 'open', 'docs', 'Develop', @j, @u), ('acme/orders#8', 'acme/orders', 1, 1, 'completed', 'docs', 'Deliver', '{}', SYSUTCDATETIME())",
                ("@j", item.ToJson()), ("@u", now.AddHours(-1)));
            await Exec(cs, "INSERT factory.CapacityLedgers ([Key], Revision, StateJson, UpdatedAt) VALUES ('factory', 1, @j, SYSUTCDATETIME())", ("@j", new CapacityState().ToJson()));
            var train = MergeTrainState.New("acme/orders"); train.Line.Add(new MergeTrainEntry { Item = "acme/orders#7", EnqueuedAt = now.AddMinutes(-2) });
            await Exec(cs, "INSERT factory.MergeTrains ([Key], Revision, StateJson, UpdatedAt) VALUES ('acme/orders', 1, @j, SYSUTCDATETIME())", ("@j", train.ToJson()));
            await Exec(cs, "INSERT factory.DeliveryTrains ([Key], Revision, StateJson, UpdatedAt) VALUES ('orders/uat', 1, @j, SYSUTCDATETIME())", ("@j", TrainState.New("orders/uat").ToJson()));

            var facts = await new SqlV2HealthSource(() => new SqlConnection(cs)).Read(default);
            Assert.Equal(3, facts.DeadLetterTotal);
            Assert.Equal([2, 1], facts.DeadLetters.Select(g => g.Count));
            Assert.Equal(("workitem.start", "WorkItemConflictException"), (facts.DeadLetters[0].MessageType, facts.DeadLetters[0].ExceptionType));
            Assert.Equal((1, 4), (facts.Incoming.Count, facts.Incoming.MaxAttempts));
            Assert.Equal(1, facts.Incoming.Ids.Count);
            Assert.Equal((3, 1), (facts.Scheduled.Count, facts.Scheduled.Overdue));
            Assert.Equal((0, 2), (facts.Scheduled.DueIn5Minutes, facts.Scheduled.DueIn30Minutes));
            Assert.Equal([("y", 2, 0), ("x", 1, 1)], facts.Scheduled.ByType!.Select(g => (g.MessageType, g.Count, g.Overdue)));
            Assert.True(facts.Scheduled.OldestDue < now);
            Assert.True(facts.Scheduled.NextDue > now);
            Assert.Equal(2, facts.Outgoing.Count);
            Assert.Equal((2, 1), (facts.IdempotencyLedger.Entries, facts.IdempotencyLedger.Failures));
            var group = Assert.Single(facts.Sagas);
            Assert.Equal(("docs", "Develop", "open", 1), (group.Plan, group.Step, group.Status, group.Count));
            Assert.NotNull(facts.Capacity);
            Assert.Single(facts.MergeTrains);
            Assert.Single(facts.DeliveryTrains);

            // and through the block
            var schema = new FactorySchemaStatus(required: true); schema.Good(report);
            var health = new V2Health(new SqlV2HealthSource(() => new SqlConnection(cs)), schema, new EngineOptions { Environment = "tdd", Configured = EngineMode.V2 }, () => null, () => null);
            var block = await health.Block(default);
            Assert.True((bool)block["available"]!);
            Assert.Equal(3, (int)block["deadLetters"]!["total"]!);
            Assert.Equal(1, (int)block["sagas"]!["open"]!);
            Assert.Equal("y", (string)block["scheduled"]!["byType"]![0]!["messageType"]!);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Exec(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }
}
