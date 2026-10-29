using Fworker.Persistence;
using Fworker.Workflows.Capacity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.0: the scheduler's table on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one): the ledger round-trips, a stale writer loses, and a database that fails is a fault, never a lost
/// revision (the service would apply the input again on a state it never read, and a conflict is not retried the way a fault is).
/// </summary>
public class CapacityTableTests : IAsyncLifetime
{
    static readonly string? ServerConnection = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
    readonly string dbName = "fw_capacity_" + Guid.NewGuid().ToString("N");
    string? dbConnection;

    static bool NoServer => string.IsNullOrWhiteSpace(ServerConnection);

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(dbConnection!).Options);

    public async Task InitializeAsync()
    {
        if (NoServer) return;
        dbConnection = new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = dbName }.ConnectionString;
        await using var db = Open();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (NoServer) return;
        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = "master" }.ConnectionString);
        await conn.OpenAsync();
        SqlConnection.ClearAllPools();
        await using var cmd = new SqlCommand($"IF DB_ID('{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static CapacityState State(int slots) => new() { Day = "2026-10-28", SlotSeq = slots, ExecutionsToday = { ["octopus"] = slots } };

    [Fact]
    public async Task TheLedgerRow_RoundTripsItsState_AndTheMigrationCreatedTheTable()
    {
        if (NoServer) return;
        var store = new SqlCapacityStore(Open);
        Assert.Null(await store.Load(CapacityKeys.Factory, default));
        Assert.True(await store.Save(CapacityKeys.Factory, State(3), 0, default));
        var rec = (await store.Load(CapacityKeys.Factory, default))!;
        Assert.Equal(1, rec.Revision);
        Assert.Equal((3L, 3), (rec.State.SlotSeq, rec.State.ExecutionsToday["octopus"]));
        await using var db = Open();
        var row = await db.CapacityLedgers.AsNoTracking().SingleAsync();
        Assert.Equal(("factory", 1), (row.Key, row.Revision));
        Assert.Equal(State(3).ToJson(), row.StateJson);
    }

    [Fact]
    public async Task AStaleWriter_LosesTheRevision_AndAnInsertThatLostTheKey_Loses()
    {
        if (NoServer) return;
        var store = new SqlCapacityStore(Open);
        Assert.True(await store.Save(CapacityKeys.Factory, State(1), 0, default));
        Assert.False(await store.Save(CapacityKeys.Factory, State(9), 0, default));   // another writer inserted the row first
        Assert.True(await store.Save(CapacityKeys.Factory, State(2), 1, default));
        Assert.False(await store.Save(CapacityKeys.Factory, State(9), 1, default));   // it read revision 1, the row is at 2
        Assert.False(await store.Save("never-written", State(9), 4, default));        // nothing to update
        var rec = (await store.Load(CapacityKeys.Factory, default))!;
        Assert.Equal((2, 2L), (rec.Revision, rec.State.SlotSeq));
    }

    [Fact]
    public async Task TwoWritersAtOnce_OneWins_TheOtherIsToldSo_NeitherThrows()
    {
        if (NoServer) return;
        var store = new SqlCapacityStore(Open);
        Assert.True(await store.Save(CapacityKeys.Factory, State(1), 0, default));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => store.Save(CapacityKeys.Factory, State(10 + i), 1, default))));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(2, (await store.Load(CapacityKeys.Factory, default))!.Revision);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReached_IsAFault_NotAConflict()
    {
        if (NoServer) return;
        var unreachable = "Server=127.0.0.1,9;Database=fw;User Id=sa;Password=x;TrustServerCertificate=True;Encrypt=False;Connect Timeout=2;ConnectRetryCount=0";
        var store = new SqlCapacityStore(() => new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(unreachable).Options));
        await Assert.ThrowsAnyAsync<Exception>(() => store.Load(CapacityKeys.Factory, default));
        await Assert.ThrowsAnyAsync<Exception>(() => store.Save(CapacityKeys.Factory, State(1), 0, default));   // (not false: a false is a lost revision, and the service would read again and call it a conflict)
        await Assert.ThrowsAnyAsync<Exception>(() => store.Save(CapacityKeys.Factory, State(1), 3, default));
    }
}
