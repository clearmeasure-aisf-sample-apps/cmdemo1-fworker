using System.Data;
using System.Text.Json.Nodes;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;

// W9.10o (docs/design/fworker-wolverine-implementation.md W9.10o, owner default of 2026-10-30 instead of #462 and #463): the health of the v2 engine as a block of /api/v1/status ("v2"), so that running the cutover does not
// need a database client. Where the Wolverine host runs (shadow or v2) and the schema is good it carries:
//  - deadLetters: the messages that ended in the dead letters, by message type and exception type, with the oldest failure; the count that must be 0 at the cutover gate;
//  - incoming, scheduled, outgoing: the durable envelopes of the host. Wolverine stores no creation time for an incoming or outgoing envelope, so their "oldestSeenSeconds" is since this process first saw the
//    envelope still there; a scheduled envelope has its due time, so its oldest overdue age is exact;
//  - jobs: the latest run of each recurring job (when, how long, how it ended) and the re-arm of the sagas (what it looked at and sent);
//  - capacity, mergeTrains, deliveryTrains, idempotencyLedger: summaries of the ledger of slots, the merge lines, the delivery trains and the idempotency ledger;
//  - sagas: the open work items by plan and step with the oldest age of each.
// Read only, counts and codes only (no text a person wrote, no connection string, no exception message). A read that fails is tried again by the next ask; the last good answer stands in for it and says so
// (stale, error), as V2ItemViews does. Concurrent asks share one read. In shadow the engine does not act: the block says "acts":false and the saga and job parts are empty.

/// <summary>The facts of the database the block shows: the rows already grouped by SQL, so a large backlog costs a few small answers.</summary>
sealed record V2Facts(
    IReadOnlyList<DeadLetterGroup> DeadLetters, int DeadLetterTotal,
    EnvelopeFacts Incoming, ScheduledFacts Scheduled, EnvelopeFacts Outgoing,
    CapacityLedgerFacts? Capacity, IReadOnlyList<string> MergeTrains, IReadOnlyList<string> DeliveryTrains,
    LedgerFacts IdempotencyLedger, IReadOnlyList<SagaGroup> Sagas);

sealed record DeadLetterGroup(string MessageType, string ExceptionType, int Count, DateTimeOffset? OldestFailedAt);
/// <summary>Incoming (not yet handled) or outgoing (not yet sent) envelopes: how many, the most attempts one took, and their ids (to see how long this process has seen each).</summary>
sealed record EnvelopeFacts(int Count, int MaxAttempts, IReadOnlyList<Guid> Ids);
sealed record ScheduledFacts(int Count, int Overdue, DateTimeOffset? OldestDue, DateTimeOffset? NextDue);
sealed record LedgerFacts(int Entries, int Failures, DateTimeOffset? Oldest);
sealed record SagaGroup(string Plan, string Step, string Status, int Count, DateTimeOffset Oldest);
sealed record CapacityLedgerFacts(CapacityState State, DateTimeOffset UpdatedAt);

interface IV2HealthSource
{
    Task<V2Facts> Read(CancellationToken ct);
}

sealed class V2Health(IV2HealthSource source, FactorySchemaStatus schema, EngineOptions engine, Func<RecurringJobRegistry?> jobs, Func<WorkItemRearm?> rearm, Func<DateTimeOffset>? now = null, Func<JsonObject?>? reconciler = null, Func<JsonObject?>? adoption = null)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10), AttemptLimit = TimeSpan.FromSeconds(8);
    /// <summary>Envelopes remembered per kind: past this many ids a backlog is "at least" this old, not counted one by one.</summary>
    public const int MaxIds = 1000;

    readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
    readonly SemaphoreSlim reading = new(1, 1);
    readonly Dictionary<Guid, DateTimeOffset> seenIncoming = [], seenOutgoing = [];
    volatile Read? last;

    sealed record Read(V2Facts Facts, DateTimeOffset At);

    /// <summary>The block for /api/v1/status.</summary>
    public async Task<JsonObject> Block(CancellationToken ct)
    {
        var o = new JsonObject { ["acts"] = engine.V2Acts, ["schema"] = schema.IsGood ? "good" : "not-good" };
        if (engine.V2Acts && reconciler?.Invoke() is { } reconcilerBlock) o["reconciler"] = reconcilerBlock;   // W9.10g: what the board reconciler costs GitHub (passes and GraphQL requests of the last hour)
        if (engine.V2Acts && adoption?.Invoke() is { } adoptionBlock) o["adoption"] = adoptionBlock;   // W9.9b: where the adoption of v1's items stands and what the latest run did
        if (!schema.IsGood) { o["available"] = false; return o; }
        string? error = null; bool stale = false; Read? read;
        if (last is { } fresh && now() - fresh.At < Fresh) read = fresh;
        else if (last is { } old && reading.CurrentCount == 0) { read = old; stale = true; }
        else
        {
            await reading.WaitAsync(ct);
            try
            {
                if (last is { } shared && now() - shared.At < Fresh) read = shared;
                else
                {
                    try
                    {
                        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(AttemptLimit);
                        var at = now();
                        var facts = await source.Read(limit.Token);
                        Observe(facts, at);
                        read = last = new Read(facts, at);
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        error = e is OperationCanceledException ? "timeout" : e is SqlException se ? $"SqlException {se.Number}" : e.GetType().Name;
                        Console.Error.WriteLine($"v2 health: read failed: {error}");
                        read = last; stale = read is not null;
                    }
                }
            }
            finally { reading.Release(); }
        }
        o["available"] = read is not null; o["stale"] = stale;
        if (error is not null) o["error"] = error;
        if (read is null) return o;
        o["readAt"] = read.At.ToString("o");
        Render(o, read.Facts, read.At);
        return o;
    }

    // remembers when each envelope still in the tables was first seen, and forgets those that are gone
    void Observe(V2Facts f, DateTimeOffset at)
    {
        lock (seenIncoming) Track(seenIncoming, f.Incoming.Ids, at);
        lock (seenOutgoing) Track(seenOutgoing, f.Outgoing.Ids, at);
    }

    static void Track(Dictionary<Guid, DateTimeOffset> seen, IReadOnlyList<Guid> ids, DateTimeOffset at)
    {
        var present = ids.ToHashSet();
        foreach (var id in seen.Keys.Where(k => !present.Contains(k)).ToList()) seen.Remove(id);
        foreach (var id in present) seen.TryAdd(id, at);
    }

    void Render(JsonObject o, V2Facts f, DateTimeOffset at)
    {
        // dead letters: the first 20 groups by count, the total of all
        var groups = new JsonArray();
        foreach (var g in f.DeadLetters.OrderByDescending(g => g.Count).ThenBy(g => g.MessageType, StringComparer.Ordinal).Take(20))
            groups.Add(new JsonObject { ["messageType"] = g.MessageType, ["exceptionType"] = g.ExceptionType, ["count"] = g.Count, ["oldestFailedAt"] = g.OldestFailedAt?.ToString("o") });
        o["deadLetters"] = new JsonObject { ["total"] = f.DeadLetterTotal, ["byType"] = groups, ["truncated"] = f.DeadLetters.Count > 20 };
        o["incoming"] = Envelopes(f.Incoming, seenIncoming, at);
        o["scheduled"] = new JsonObject
        {
            ["count"] = f.Scheduled.Count, ["overdue"] = f.Scheduled.Overdue, ["oldestOverdueSeconds"] = f.Scheduled.OldestDue is { } due && due < at ? (long)(at - due).TotalSeconds : null,
            ["nextDue"] = f.Scheduled.NextDue?.ToString("o"),
        };
        o["outgoing"] = Envelopes(f.Outgoing, seenOutgoing, at);
        o["jobs"] = Jobs();
        o["sagas"] = Sagas(f.Sagas, at);
        if (f.Capacity is { } cap) o["capacity"] = Capacity(cap, at);
        o["mergeTrains"] = new JsonArray(f.MergeTrains.Select(t => (JsonNode?)JsonNode.Parse(t)).ToArray());
        o["deliveryTrains"] = new JsonArray(f.DeliveryTrains.Select(t => (JsonNode?)JsonNode.Parse(t)).ToArray());
        o["idempotencyLedger"] = new JsonObject { ["entries"] = f.IdempotencyLedger.Entries, ["failures"] = f.IdempotencyLedger.Failures, ["oldestAgeSeconds"] = Age(f.IdempotencyLedger.Oldest, at) };
    }

    JsonObject Envelopes(EnvelopeFacts e, Dictionary<Guid, DateTimeOffset> seen, DateTimeOffset at)
    {
        long? oldest = null;
        lock (seen) if (seen.Count > 0) oldest = (long)Math.Max(0, (at - seen.Values.Min()).TotalSeconds);
        return new JsonObject { ["count"] = e.Count, ["maxAttempts"] = e.MaxAttempts, ["oldestSeenSeconds"] = e.Count == 0 ? null : oldest };
    }

    JsonObject Jobs()
    {
        var o = new JsonObject();
        if (jobs() is not { } registry) return o;
        var runs = registry.LastRuns;
        foreach (var name in registry.Names.OrderBy(n => n, StringComparer.Ordinal))
            o[name] = runs.TryGetValue(name, out var r)
                ? new JsonObject { ["runs"] = registry.RunCount(name), ["lastStartedAt"] = r.StartedAt.ToString("o"), ["lastMilliseconds"] = r.Milliseconds, ["lastError"] = r.Error }
                : new JsonObject { ["runs"] = 0 };
        return o;
    }

    JsonObject Sagas(IReadOnlyList<SagaGroup> groups, DateTimeOffset at)
    {
        var steps = new JsonArray();
        foreach (var g in groups.OrderBy(g => g.Status, StringComparer.Ordinal).ThenBy(g => g.Plan, StringComparer.Ordinal).ThenBy(g => g.Step, StringComparer.Ordinal))
            steps.Add(new JsonObject { ["plan"] = g.Plan, ["step"] = g.Step, ["status"] = g.Status, ["count"] = g.Count, ["oldestAgeSeconds"] = Age(g.Oldest, at) });
        var o = new JsonObject { ["open"] = groups.Where(g => g.Status == WorkItemStatus.Open).Sum(g => g.Count), ["settling"] = groups.Where(g => g.Status == WorkItemStatus.Settling).Sum(g => g.Count), ["byStep"] = steps };
        if (rearm() is { Last: { } r })
            o["rearm"] = new JsonObject { ["at"] = r.At.ToString("o"), ["open"] = r.Open, ["timersSent"] = r.Timers, ["commandsSent"] = r.Commands, ["stepsWithoutLease"] = r.NoLease, ["failed"] = r.Failed, ["awaitingReadoption"] = r.Held };
        return o;
    }

    static JsonObject Capacity(CapacityLedgerFacts c, DateTimeOffset at)
    {
        var s = c.State;
        return new JsonObject
        {
            ["slots"] = s.Slots.Count, ["queued"] = s.Queue.Count, ["inFlight"] = s.InFlight.Count, ["holds"] = s.Holds.Count, ["hosts"] = s.Hosts.Count,
            ["oldestWaitingSeconds"] = s.Queue.Count == 0 ? null : (long)Math.Max(0, (at - s.Queue.Values.Min(q => q.RequestedAt)).TotalSeconds),
            ["wakeDue"] = s.WakeDue?.ToString("o"), ["updatedAt"] = c.UpdatedAt.ToString("o"),
        };
    }

    static long? Age(DateTimeOffset? from, DateTimeOffset at) => from is { } f ? (long)Math.Max(0, (at - f).TotalSeconds) : null;

    // ---- the summaries of the trains, as JSON text the source prints and Render reads back (a train's state is the database's JSON document) ---------------------------------------------------------------

    /// <summary>A line for one merge train: the repository, the entries in the line, those whose turn was granted and the oldest wait.</summary>
    public static string MergeTrainLine(string key, MergeTrainState s, DateTimeOffset at) =>
        new JsonObject { ["key"] = key, ["line"] = s.Line.Count, ["granted"] = s.Line.Count(e => e.Granted), ["oldestWaitSeconds"] = s.Line.Count == 0 ? null : (long)Math.Max(0, (at - s.Line.Min(e => e.EnqueuedAt)).TotalSeconds), ["merged"] = s.Merged.Count }.ToJsonString();

    /// <summary>A line for one delivery train: the releases it knows, the requests outstanding (with the oldest age), the Sign-offs waiting, the candidates, whether it is held and when it wakes next.</summary>
    public static string DeliveryTrainLine(string key, TrainState s, DateTimeOffset at) =>
        new JsonObject
        {
            ["key"] = key, ["releases"] = s.Releases.Count, ["candidates"] = s.Candidates.Count, ["requested"] = s.Requested.Count, ["signOffs"] = s.SignOffs.Count, ["held"] = s.Held, ["current"] = s.Current,
            ["oldestRequestSeconds"] = s.FirstRequested.Count == 0 ? null : (long)Math.Max(0, (at - s.FirstRequested.Values.Min()).TotalSeconds), ["wakeDue"] = s.WakeDue?.ToString("o"), ["replaying"] = s.ReplayingSince is not null,
        }.ToJsonString();
}

/// <summary>Reads the facts from the database with a handful of grouped queries on one connection.</summary>
sealed class SqlV2HealthSource(Func<SqlConnection> open, Func<DateTimeOffset>? now = null) : IV2HealthSource
{
    readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);

    public async Task<V2Facts> Read(CancellationToken ct)
    {
        await using var c = open();
        await c.OpenAsync(ct);
        var at = now();

        var dead = new List<DeadLetterGroup>();
        // (exception_type is varchar(max): its first 120 characters name the type; a dead letter that was archived is not counted)
        await foreach (var r in Rows(c, "SELECT TOP (50) ISNULL(message_type, ''), ISNULL(LEFT(exception_type, 120), ''), COUNT(*), MIN(failed_at) FROM wolverine.wolverine_dead_letters WHERE archived_at IS NULL GROUP BY message_type, LEFT(exception_type, 120) ORDER BY COUNT(*) DESC", ct))
        {
            dead.Add(new DeadLetterGroup(r.GetString(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetDateTimeOffset(3)));
        }
        var deadTotal = Convert.ToInt32(await Scalar(c, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters WHERE archived_at IS NULL", ct));

        var incoming = await Envelopes(c, "SELECT COUNT(*), ISNULL(MAX(attempts), 0) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Incoming'",
            $"SELECT TOP ({V2Health.MaxIds}) id FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Incoming'", ct);
        var outgoing = await Envelopes(c, "SELECT COUNT(*), ISNULL(MAX(attempts), 0) FROM wolverine.wolverine_outgoing_envelopes",
            $"SELECT TOP ({V2Health.MaxIds}) id FROM wolverine.wolverine_outgoing_envelopes", ct);

        ScheduledFacts scheduled;
        await using (var cmd = new SqlCommand("SELECT COUNT(*), SUM(CASE WHEN execution_time <= @now THEN 1 ELSE 0 END), MIN(CASE WHEN execution_time <= @now THEN execution_time END), MIN(CASE WHEN execution_time > @now THEN execution_time END) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled'", c))
        {
            cmd.Parameters.Add("@now", SqlDbType.DateTimeOffset).Value = at;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            scheduled = new ScheduledFacts(r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetDateTimeOffset(2), r.IsDBNull(3) ? null : r.GetDateTimeOffset(3));
        }

        CapacityLedgerFacts? capacity = null;
        await foreach (var r in Rows(c, "SELECT StateJson, UpdatedAt FROM factory.CapacityLedgers WHERE [Key] = 'factory'", ct))
            capacity = new CapacityLedgerFacts(CapacityState.FromJson(r.GetString(0)), r.GetDateTimeOffset(1));

        var merge = new List<string>();
        await foreach (var r in Rows(c, "SELECT TOP (50) [Key], StateJson FROM factory.MergeTrains ORDER BY [Key]", ct)) merge.Add(V2Health.MergeTrainLine(r.GetString(0), MergeTrainState.FromJson(r.GetString(1)), at));
        var delivery = new List<string>();
        await foreach (var r in Rows(c, "SELECT TOP (50) [Key], StateJson FROM factory.DeliveryTrains ORDER BY [Key]", ct)) delivery.Add(V2Health.DeliveryTrainLine(r.GetString(0), TrainState.FromJson(r.GetString(1)), at));

        LedgerFacts ledger;
        await using (var cmd = new SqlCommand("SELECT COUNT(*), ISNULL(SUM(CASE WHEN Ok = 0 THEN 1 ELSE 0 END), 0), MIN(RecordedAt) FROM factory.IdempotencyLedger", c))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            ledger = new LedgerFacts(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)));
        }

        var sagas = new List<SagaGroup>();
        await foreach (var r in Rows(c, "SELECT [Plan], [Step], [Status], COUNT(*), MIN(UpdatedAt) FROM factory.WorkItems WHERE [Status] <> 'completed' GROUP BY [Plan], [Step], [Status]", ct))
            sagas.Add(new SagaGroup(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetDateTimeOffset(4)));

        return new V2Facts(dead, deadTotal, incoming, scheduled, outgoing, capacity, merge, delivery, ledger, sagas);
    }

    static async IAsyncEnumerable<SqlDataReader> Rows(SqlConnection c, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) yield return r;
    }

    static async Task<object?> Scalar(SqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync(ct);
    }

    static async Task<EnvelopeFacts> Envelopes(SqlConnection c, string countSql, string idsSql, CancellationToken ct)
    {
        int count, max;
        await using (var cmd = new SqlCommand(countSql, c))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            count = r.GetInt32(0); max = r.GetInt32(1);
        }
        var ids = new List<Guid>();
        if (count > 0) await foreach (var r in Rows(c, idsSql, ct)) ids.Add(r.GetGuid(0));
        return new EnvelopeFacts(count, max, ids);
    }
}
