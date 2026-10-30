using Fworker.Persistence;
using Fworker.Workflows.Incident;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W8.0: the incident table on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one), built by script 100: the rows round-trip through the model, a stale writer loses on the revision, the store
/// finds the incident of a hotfix item while it is open, and the timeline of an incident goes to the item history once per message.
/// </summary>
public class IncidentTableTests : IAsyncLifetime
{
    static readonly string? ServerConnection = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
    TestDatabase? database;

    static bool NoServer => string.IsNullOrWhiteSpace(ServerConnection);

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        if (NoServer) return;
        database = await TestDatabase.CreateAsync("fw_w80_");
    }

    public async Task DisposeAsync()
    {
        if (database != null) await database.DisposeAsync();
    }

    static readonly DateTimeOffset T0 = IncidentRig.T0;

    [Fact]
    public async Task AnIncidentRow_RoundTrips_AndAStaleWriterLoses()
    {
        if (NoServer) return;
        await using (var db = Open())
        {
            db.Incidents.Add(new IncidentRow { Key = "orders/tdd", Generation = 2, Revision = 1, Status = "fixing", HotfixItem = "acme/work#42", StateJson = "{\"status\":\"fixing\"}", UpdatedAt = T0 });
            db.Incidents.Add(new IncidentRow { Key = "orders/uat", Generation = 0, Revision = 1, Status = "quiet", HotfixItem = null, StateJson = "{}", UpdatedAt = T0 });
            await db.SaveChangesAsync();
        }
        await using var a = Open();
        await using var b = Open();
        var first = await a.Incidents.SingleAsync(x => x.Key == "orders/tdd");
        var second = await b.Incidents.SingleAsync(x => x.Key == "orders/tdd");
        Assert.Equal((2, "fixing", "acme/work#42"), (first.Generation, first.Status, first.HotfixItem));
        first.Revision = 2; first.Status = "verifying";
        await a.SaveChangesAsync();
        second.Revision = 2; second.Status = "escalated";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        await using var c = Open();
        Assert.Equal(["orders/tdd"], await c.Incidents.Where(x => x.Status != "quiet").Select(x => x.Key).ToListAsync());
        Assert.Equal(["orders/tdd"], await c.Incidents.Where(x => x.HotfixItem == "acme/work#42").Select(x => x.Key).ToListAsync());
        Assert.Null((await c.Incidents.SingleAsync(x => x.Key == "orders/uat")).HotfixItem);
    }

    [Fact]
    public async Task TheStore_SavesByRevision_AndFindsTheOpenIncidentOfAHotfixItem()
    {
        if (NoServer) return;
        var store = new SqlIncidentStore(() => Open());
        var saga = new IncidentSaga(new IncidentSettings(new Dictionary<string, IncidentEnvironment> { ["tdd"] = new("tdd", RollbackMode.Automatic, false) }));
        var t = saga.Apply(null, new AlertReceived("orders/tdd", "a1", "http-5xx", "Sev1", "errors", T0), T0);
        Assert.True(await store.Save("orders/tdd", t.State!, 0, default));
        Assert.False(await store.Save("orders/tdd", t.State!, 0, default));   // (the key is taken)
        var rec = (await store.Load("orders/tdd", default))!;
        Assert.Equal(1, rec.Revision);
        Assert.Equal(t.State!.ToJson(), rec.State.ToJson());   // (the outbox, with its effects, survives the table)

        var fix = saga.Apply(rec.State, new TriageReported("orders/tdd", 1, "t1", "fix-forward", "a defect"), T0.AddMinutes(1));
        var created = saga.Apply(fix.State, new HotfixCreated("orders/tdd", 1, "acme/Work#42"), T0.AddMinutes(2));
        Assert.True(await store.Save("orders/tdd", created.State!, 1, default));
        Assert.False(await store.Save("orders/tdd", fix.State!, 1, default));   // (a stale writer)
        Assert.Equal(["orders/tdd"], await store.OpenOfHotfix("acme/work#42", default));
        Assert.Equal(["orders/tdd"], (await store.Open(default)).Select(s => s.Key));
        await using (var db = Open())
        {
            var row = await db.Incidents.AsNoTracking().SingleAsync(x => x.Key == "orders/tdd");
            Assert.Equal(("fixing", "acme/work#42", 1), (row.Status, row.HotfixItem, row.Generation));   // (the columns beside the document follow it)
        }

        // resolved: the pair is quiet and the item is no hotfix of an open incident
        var closed = saga.Apply(created.State, new OwnerDecided("orders/tdd", 1, "o1", "close", "x", ""), T0.AddMinutes(3));
        Assert.Equal(IncidentOutcome.Refused, closed.Result.Outcome);   // (it is not escalated)
        var escalated = saga.Apply(created.State, new HotfixProgress("orders/tdd", "acme/work#42", "parked", "", T0.AddMinutes(3)), T0.AddMinutes(3));
        var resolved = saga.Apply(escalated.State, new OwnerDecided("orders/tdd", 1, "o2", "close", "x", ""), T0.AddMinutes(4));
        Assert.True(await store.Save("orders/tdd", resolved.State!, 2, default));
        Assert.Empty(await store.OpenOfHotfix("acme/work#42", default));
        Assert.Empty(await store.Open(default));
    }

    [Fact]
    public async Task TheTimeline_GoesToTheItemHistory_OncePerMessage()
    {
        if (NoServer) return;
        var history = new IncidentHistory(() => Open());
        var decided = new IncidentDecided("orders/tdd", 3, 17, IncidentActions.Escalated, IncidentStatus.Escalated, ["no triage verdict within 30 minutes"], T0);
        var id = Guid.NewGuid();
        await history.Record(decided, id);
        await history.Record(decided, id);   // (the same message handled again)
        await history.Record(decided with { Sequence = 18, Action = IncidentActions.Resolved, Status = IncidentStatus.Quiet }, Guid.NewGuid());
        await using var db = Open();
        var rows = await db.ItemHistory.Where(x => x.ItemKey == "incident:orders/tdd").OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("event", "incident.decided", 3), (r.Kind, r.Type, r.Generation)));
        Assert.Contains("\"Action\":\"escalated\"", rows[0].Detail);
        Assert.Contains("no triage verdict within 30 minutes", rows[0].Detail);
        Assert.Equal(T0, rows[0].At);
    }

    [Fact]
    public async Task TheIndexesTheRoutingReadsBy_Exist()
    {
        if (NoServer) return;
        await using var db = Open();
        var names = await db.Database.SqlQueryRaw<string>("SELECT i.name AS Value FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'factory' AND t.name = 'Incidents' AND i.name IS NOT NULL").ToListAsync();
        Assert.Equal(["IX_Incidents_HotfixItem", "IX_Incidents_Status", "PK_Incidents"], names.OrderBy(x => x, StringComparer.Ordinal));
    }
}
