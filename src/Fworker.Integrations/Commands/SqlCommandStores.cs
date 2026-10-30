using System.Data;
using Fworker.Contracts.Commands;
using Microsoft.Data.SqlClient;

namespace Fworker.Integrations.Commands;

// W3 follow-up (#481): the idempotency ledger and the Copilot reservations in the Wolverine database (schema "factory", beside the tables of Fworker.Persistence).
// The in-memory ones forget everything at a restart: a command replayed after a kill between its side effect and its recorded result would run the side effect
// again, and a Copilot launch whose reservation was lost could create a second task. Here a key and a reservation outlive the process. The tables are built by script 009 of
// Fworker.Database (DbUp), which the gateway applies at start before the Wolverine host and everything that reads them. Both interfaces
// are synchronous, as the in-memory ones are: a call is one short statement.

/// <summary>The stores a gateway with a database wires: the ledger and the reservations on the same connection string and schema step.</summary>
public sealed class SqlCommandStores
{
    public IIdempotencyLedger Ledger { get; }
    public ICopilotReservations Reservations { get; }

    /// <param name="owner">W9.10c: who holds the claims this process makes (node and the leader lease's epoch, read at each claim); the process and epoch 0 without one.</param>
    /// <param name="claimTtl">How long a claim holds a key before another handling may take it over (90 seconds without one).</param>
    public SqlCommandStores(string connectionString, Func<ClaimOwner>? owner = null, TimeSpan? claimTtl = null)
    {
        var schema = new SqlCommandSchema(connectionString);
        Ledger = new SqlIdempotencyLedger(schema, owner, claimTtl);
        Reservations = new SqlCopilotReservations(schema);
    }
}

/// <summary>Opens connections to the database. The tables (factory.IdempotencyLedger, factory.IdempotencyClaims and factory.CopilotReservations) are built by scripts 009 and 140 of Fworker.Database, which the gateway
/// applies at start before anything uses them; nothing here creates a table.</summary>
public sealed class SqlCommandSchema(string connectionString)
{
    public SqlConnection Open()
    {
        var c = new SqlConnection(connectionString);
        c.Open();
        return c;
    }
}

public sealed class SqlIdempotencyLedger : IIdempotencyLedger, IPurgeableLedger
{
    readonly SqlCommandSchema schema; readonly Func<ClaimOwner> owner; readonly TimeSpan ttl;

    public SqlIdempotencyLedger(SqlCommandSchema schema, Func<ClaimOwner>? owner = null, TimeSpan? claimTtl = null)
    {
        this.schema = schema;
        var self = ClaimOwner.ThisProcess();
        this.owner = owner ?? (() => self); ttl = claimTtl ?? InMemoryIdempotencyLedger.DefaultClaimTtl;
    }

    /// <summary>Forgets the successes older than the retention (W9.10o), in batches so that a long backlog does not hold a lock; the number forgotten.</summary>
    public int PurgeSuccesses(TimeSpan retention, DateTimeOffset now)
    {
        var total = 0;
        while (true)
        {
            using var c = schema.Open();
            using var cmd = new SqlCommand("DELETE TOP (1000) FROM factory.IdempotencyLedger WHERE Ok = 1 AND RecordedAt < @before", c);
            cmd.Parameters.Add("@before", SqlDbType.DateTime2).Value = (now - retention).UtcDateTime;
            var n = cmd.ExecuteNonQuery();
            total += n;
            if (n < 1000) return total;
        }
    }

    public CommandResult? TryGet(string key)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand("SELECT Ok, Failure, Error, Execution, Url FROM factory.IdempotencyLedger WHERE [Key] = @k", c);
        cmd.Parameters.Add("@k", SqlDbType.NVarChar, 400).Value = key;
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    static CommandResult Read(SqlDataReader r) => new(r.GetBoolean(0), (FailureKind)r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4));

    /// <summary>The first result of a key stays (as the in-memory ledger's TryAdd): a second record of the key changes nothing. The key's claim ends in the same transaction, so a handling that
    /// takes the claim afterwards finds the result. The claim is touched before the ledger, as in <see cref="Claim"/>, so the two never wait for each other's locks in a circle.</summary>
    public void Record(string key, CommandResult result)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand("""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM factory.IdempotencyClaims WHERE [Key] = @k;
            INSERT INTO factory.IdempotencyLedger ([Key], Ok, Failure, Error, Execution, Url, RecordedAt)
            SELECT @k, @ok, @f, @e, @x, @u, SYSUTCDATETIME()
            WHERE NOT EXISTS (SELECT 1 FROM factory.IdempotencyLedger WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = @k);
            COMMIT TRANSACTION;
            """, c);
        cmd.Parameters.Add("@k", SqlDbType.NVarChar, 400).Value = key;
        cmd.Parameters.Add("@ok", SqlDbType.Bit).Value = result.Ok;
        cmd.Parameters.Add("@f", SqlDbType.Int).Value = (int)result.Failure;
        cmd.Parameters.Add("@e", SqlDbType.NVarChar, -1).Value = result.Error ?? "";
        cmd.Parameters.Add("@x", SqlDbType.NVarChar, -1).Value = result.Execution ?? "";
        cmd.Parameters.Add("@u", SqlDbType.NVarChar, -1).Value = result.Url ?? "";
        try { cmd.ExecuteNonQuery(); }
        catch (SqlException e) when (e.Number is 2627 or 2601) { /* a concurrent record of the same key won */ }
    }

    /// <summary>
    /// One batch, one transaction: the claim is taken (an expired one is overwritten with this owner, a missing one inserted under a range lock) and only then is the ledger read, because a handling that
    /// records deletes its claim in the same transaction as it inserts its result: whoever gets the claim afterwards sees the result. The outcome is the first column: 1 claimed, 2 taken over,
    /// 0 a live claim of another (owner and expiry follow), 3 a result is recorded (its columns follow).
    /// </summary>
    public ClaimOutcome Claim(string key)
    {
        var me = owner();
        using var c = schema.Open();
        using var cmd = new SqlCommand("""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @now datetime2 = SYSUTCDATETIME();
            DECLARE @state int;
            UPDATE factory.IdempotencyClaims WITH (UPDLOCK, HOLDLOCK) SET OwnerNode = @n, OwnerEpoch = @e, ClaimedAt = @now, ExpiresAt = DATEADD(MILLISECOND, @ttl, @now)
                WHERE [Key] = @k AND ExpiresAt <= @now;
            IF @@ROWCOUNT > 0 SET @state = 2;
            ELSE
            BEGIN
                INSERT INTO factory.IdempotencyClaims ([Key], OwnerNode, OwnerEpoch, ClaimedAt, ExpiresAt)
                SELECT @k, @n, @e, @now, DATEADD(MILLISECOND, @ttl, @now)
                WHERE NOT EXISTS (SELECT 1 FROM factory.IdempotencyClaims WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = @k);
                IF @@ROWCOUNT > 0 SET @state = 1; ELSE SET @state = 0;
            END
            IF @state IN (1, 2) AND EXISTS (SELECT 1 FROM factory.IdempotencyLedger WHERE [Key] = @k)
            BEGIN
                DELETE FROM factory.IdempotencyClaims WHERE [Key] = @k;
                SET @state = 3;
            END
            COMMIT TRANSACTION;
            IF @state = 3 SELECT 3, Ok, Failure, Error, Execution, Url FROM factory.IdempotencyLedger WHERE [Key] = @k;
            ELSE IF @state = 0 SELECT 0, OwnerNode, OwnerEpoch, ExpiresAt, NULL, NULL FROM factory.IdempotencyClaims WHERE [Key] = @k;
            ELSE SELECT @state, NULL, NULL, NULL, NULL, NULL;
            """, c);
        cmd.Parameters.Add("@k", SqlDbType.NVarChar, 400).Value = key;
        cmd.Parameters.Add("@n", SqlDbType.NVarChar, 200).Value = me.Node;
        cmd.Parameters.Add("@e", SqlDbType.BigInt).Value = me.Epoch;
        cmd.Parameters.Add("@ttl", SqlDbType.Int).Value = (int)ttl.TotalMilliseconds;
        try
        {
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new(ClaimState.Pending);   // the claim was released between the two statements: ask again
            switch (r.GetInt32(0))
            {
                case 1: return new(ClaimState.Claimed);
                case 2: return new(ClaimState.TakenOver);
                case 3: return new(ClaimState.Done, new CommandResult(r.GetBoolean(1), (FailureKind)r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5)));
                default: return new(ClaimState.Pending, Holder: new ClaimOwner(r.GetString(1), r.GetInt64(2)), ExpiresAt: DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc));
            }
        }
        catch (SqlException e) when (e.Number is 2627 or 2601) { return new(ClaimState.Pending); /* a concurrent insert of the same key won */ }
    }

    public void ReleaseClaim(string key) => Exec("DELETE FROM factory.IdempotencyClaims WHERE [Key] = @k AND OwnerNode = @n AND OwnerEpoch = @e", key);

    public void ExpireClaim(string key) => Exec("UPDATE factory.IdempotencyClaims SET ExpiresAt = SYSUTCDATETIME() WHERE [Key] = @k AND OwnerNode = @n AND OwnerEpoch = @e", key);

    void Exec(string sql, string key)
    {
        var me = owner();
        using var c = schema.Open();
        using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.Add("@k", SqlDbType.NVarChar, 400).Value = key;
        cmd.Parameters.Add("@n", SqlDbType.NVarChar, 200).Value = me.Node;
        cmd.Parameters.Add("@e", SqlDbType.BigInt).Value = me.Epoch;
        cmd.ExecuteNonQuery();
    }
}

public sealed class SqlCopilotReservations(SqlCommandSchema schema) : ICopilotReservations
{
    const string Columns = "Item, Phase, Worker, State, At, TaskId, Url, Mode";

    public CopilotReservation? Get(int item, string phase)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand($"SELECT {Columns} FROM factory.CopilotReservations WHERE Item = @i AND Phase = @p", c);
        Key(cmd, item, phase);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public void Put(CopilotReservation r)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand("""
            UPDATE factory.CopilotReservations WITH (UPDLOCK, HOLDLOCK) SET Worker = @w, State = @s, At = @a, TaskId = @t, Url = @u, Mode = @m WHERE Item = @i AND Phase = @p;
            IF @@ROWCOUNT = 0 INSERT INTO factory.CopilotReservations (Item, Phase, Worker, State, At, TaskId, Url, Mode) VALUES (@i, @p, @w, @s, @a, @t, @u, @m);
            """, c);
        Fill(cmd, r);
        try { cmd.ExecuteNonQuery(); }
        catch (SqlException e) when (e.Number is 2627 or 2601) { cmd.ExecuteNonQuery(); /* a racing insert won: the update now finds its row */ }
    }

    /// <summary>Atomic: takes (item, phase) unless a Reserved one is there. The primary key decides a race between two inserts.</summary>
    public bool TryReserve(CopilotReservation r)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand("""
            UPDATE factory.CopilotReservations WITH (UPDLOCK, HOLDLOCK) SET Worker = @w, State = @s, At = @a, TaskId = @t, Url = @u, Mode = @m WHERE Item = @i AND Phase = @p AND State <> @reserved;
            IF @@ROWCOUNT > 0 SELECT 1;
            ELSE IF NOT EXISTS (SELECT 1 FROM factory.CopilotReservations WITH (UPDLOCK, HOLDLOCK) WHERE Item = @i AND Phase = @p)
            BEGIN INSERT INTO factory.CopilotReservations (Item, Phase, Worker, State, At, TaskId, Url, Mode) VALUES (@i, @p, @w, @s, @a, @t, @u, @m); SELECT 1; END
            ELSE SELECT 0;
            """, c);
        Fill(cmd, r);
        cmd.Parameters.Add("@reserved", SqlDbType.Int).Value = (int)ReservationState.Reserved;
        try { return Convert.ToInt32(cmd.ExecuteScalar()) == 1; }
        catch (SqlException e) when (e.Number is 2627 or 2601) { return false; }
    }

    /// <summary>W9.10c: conditional, in one statement: the reservation becomes <paramref name="next"/> only while it is still exactly <paramref name="expected"/> (Reserved, by the same worker, made at the same time, in the same mode).</summary>
    public bool Transition(CopilotReservation expected, CopilotReservation next)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand("""
            UPDATE factory.CopilotReservations WITH (UPDLOCK, HOLDLOCK) SET Worker = @w, State = @s, At = @a, TaskId = @t, Url = @u, Mode = @m
              WHERE Item = @i AND Phase = @p AND State = @xs AND Worker = @xw AND At = @xa AND Mode = @xm;
            SELECT @@ROWCOUNT;
            """, c);
        Fill(cmd, next);
        cmd.Parameters.Add("@xs", SqlDbType.Int).Value = (int)expected.State;
        cmd.Parameters.Add("@xw", SqlDbType.NVarChar, 200).Value = expected.Worker;
        cmd.Parameters.Add("@xa", SqlDbType.DateTime2).Value = expected.At.ToUniversalTime();
        cmd.Parameters.Add("@xm", SqlDbType.NVarChar, 20).Value = expected.Mode ?? "";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<CopilotReservation> Reserved()
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand($"SELECT {Columns} FROM factory.CopilotReservations WHERE State = @s", c);
        cmd.Parameters.Add("@s", SqlDbType.Int).Value = (int)ReservationState.Reserved;
        using var r = cmd.ExecuteReader();
        var list = new List<CopilotReservation>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    static void Key(SqlCommand cmd, int item, string phase)
    {
        cmd.Parameters.Add("@i", SqlDbType.Int).Value = item;
        cmd.Parameters.Add("@p", SqlDbType.NVarChar, 100).Value = phase;
    }

    static void Fill(SqlCommand cmd, CopilotReservation r)
    {
        Key(cmd, r.Item, r.Phase);
        cmd.Parameters.Add("@w", SqlDbType.NVarChar, 200).Value = r.Worker;
        cmd.Parameters.Add("@s", SqlDbType.Int).Value = (int)r.State;
        cmd.Parameters.Add("@a", SqlDbType.DateTime2).Value = r.At.ToUniversalTime();
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 200).Value = r.TaskId ?? "";
        cmd.Parameters.Add("@u", SqlDbType.NVarChar, -1).Value = r.Url ?? "";
        cmd.Parameters.Add("@m", SqlDbType.NVarChar, 20).Value = r.Mode ?? "";
    }

    static CopilotReservation Read(SqlDataReader r) =>
        new(r.GetInt32(0), r.GetString(1), r.GetString(2), (ReservationState)r.GetInt32(3), DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.GetString(5), r.GetString(6), r.GetString(7));
}
