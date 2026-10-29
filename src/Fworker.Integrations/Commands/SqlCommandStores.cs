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

    public SqlCommandStores(string connectionString)
    {
        var schema = new SqlCommandSchema(connectionString);
        Ledger = new SqlIdempotencyLedger(schema);
        Reservations = new SqlCopilotReservations(schema);
    }
}

/// <summary>Opens connections to the database. The two tables (factory.IdempotencyLedger and factory.CopilotReservations) are built by script 009 of Fworker.Database, which the gateway
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

public sealed class SqlIdempotencyLedger(SqlCommandSchema schema) : IIdempotencyLedger
{
    public CommandResult? TryGet(string key)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand("SELECT Ok, Failure, Error, Execution, Url FROM factory.IdempotencyLedger WHERE [Key] = @k", c);
        cmd.Parameters.Add("@k", SqlDbType.NVarChar, 400).Value = key;
        using var r = cmd.ExecuteReader();
        return r.Read() ? new CommandResult(r.GetBoolean(0), (FailureKind)r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4)) : null;
    }

    /// <summary>The first result of a key stays (as the in-memory ledger's TryAdd): a second record of the key changes nothing.</summary>
    public void Record(string key, CommandResult result)
    {
        using var c = schema.Open();
        using var cmd = new SqlCommand("""
            INSERT INTO factory.IdempotencyLedger ([Key], Ok, Failure, Error, Execution, Url, RecordedAt)
            SELECT @k, @ok, @f, @e, @x, @u, SYSUTCDATETIME()
            WHERE NOT EXISTS (SELECT 1 FROM factory.IdempotencyLedger WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = @k)
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
}

public sealed class SqlCopilotReservations(SqlCommandSchema schema) : ICopilotReservations
{
    const string Columns = "Item, Phase, Worker, State, At, TaskId, Url";

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
            UPDATE factory.CopilotReservations WITH (UPDLOCK, HOLDLOCK) SET Worker = @w, State = @s, At = @a, TaskId = @t, Url = @u WHERE Item = @i AND Phase = @p;
            IF @@ROWCOUNT = 0 INSERT INTO factory.CopilotReservations (Item, Phase, Worker, State, At, TaskId, Url) VALUES (@i, @p, @w, @s, @a, @t, @u);
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
            UPDATE factory.CopilotReservations WITH (UPDLOCK, HOLDLOCK) SET Worker = @w, State = @s, At = @a, TaskId = @t, Url = @u WHERE Item = @i AND Phase = @p AND State <> @reserved;
            IF @@ROWCOUNT > 0 SELECT 1;
            ELSE IF NOT EXISTS (SELECT 1 FROM factory.CopilotReservations WITH (UPDLOCK, HOLDLOCK) WHERE Item = @i AND Phase = @p)
            BEGIN INSERT INTO factory.CopilotReservations (Item, Phase, Worker, State, At, TaskId, Url) VALUES (@i, @p, @w, @s, @a, @t, @u); SELECT 1; END
            ELSE SELECT 0;
            """, c);
        Fill(cmd, r);
        cmd.Parameters.Add("@reserved", SqlDbType.Int).Value = (int)ReservationState.Reserved;
        try { return Convert.ToInt32(cmd.ExecuteScalar()) == 1; }
        catch (SqlException e) when (e.Number is 2627 or 2601) { return false; }
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
    }

    static CopilotReservation Read(SqlDataReader r) =>
        new(r.GetInt32(0), r.GetString(1), r.GetString(2), (ReservationState)r.GetInt32(3), DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.GetString(5), r.GetString(6));
}
