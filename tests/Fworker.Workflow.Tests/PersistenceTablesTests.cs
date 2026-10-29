using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Fworker.Persistence;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W2.1: the scripts of Fworker.Database build the tables on a real SQL Server (DbUp) and every table round-trips a row. FACTORY_TEST_SQL (the service container of tests.yml) names the
/// server; each test class run gets its own database, dropped at the end. Where FACTORY_TEST_SQL is unset (a developer machine) the database tests
/// return at once. That the scripts build what the EF model maps is SchemaMatchesEfModelTests.
/// </summary>
public class PersistenceTablesTests : IAsyncLifetime
{
    static readonly string? ServerConnection = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
    static readonly DateTimeOffset T0 = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
    TestDatabase? database;
    string? dbConnection;

    static bool NoServer => string.IsNullOrWhiteSpace(ServerConnection);

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(dbConnection!).Options);

    public async Task InitializeAsync()
    {
        if (NoServer) return;
        database = await TestDatabase.CreateAsync("fw_persist_");
        dbConnection = database.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (NoServer) return;
        if (database != null) await database.DisposeAsync();
    }

    [Fact]
    public async Task ItemHistory_RoundTrips_AndOrdersByInstance()
    {
        if (NoServer) return;
        var msg = Guid.NewGuid();
        await using (var db = Open())
        {
            db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = "acme/shop#7", Generation = 1, At = T0, Kind = "event", Type = "PullRequestOpened", Detail = "{\"pr\":9}", SessionId = "s1", MessageId = msg });
            db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = "acme/shop#7", Generation = 1, At = T0.AddMinutes(1), Kind = "command", Type = "MoveCard" });
            db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = "acme/shop#7", Generation = 2, At = T0.AddHours(1), Kind = "event", Type = "Reopened" });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var rows = await db.ItemHistory.Where(x => x.ItemKey == "acme/shop#7" && x.Generation == 1).OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(["PullRequestOpened", "MoveCard"], rows.Select(r => r.Type));
            Assert.Equal(("{\"pr\":9}", "s1", msg, T0), (rows[0].Detail, rows[0].SessionId, rows[0].MessageId, rows[0].At));
            Assert.Null(rows[1].MessageId);
        }
    }

    [Fact]
    public async Task ItemHistory_AMessageIdIsWrittenOncePerKind()
    {
        if (NoServer) return;
        var msg = Guid.NewGuid();
        await using var db = Open();
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = "acme/shop#7", At = T0, Kind = "event", Type = "A", MessageId = msg });
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = "acme/shop#7", At = T0, Kind = "command", Type = "B", MessageId = msg });
        await db.SaveChangesAsync();
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = "acme/shop#7", At = T0, Kind = "event", Type = "A", MessageId = msg });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Correlations_RoundTrip_ByKindAndKey_AndByItem()
    {
        if (NoServer) return;
        await using (var db = Open())
        {
            db.Correlations.Add(new CorrelationEntry { Kind = "pr", Key = "acme/shop!9", ItemKey = "acme/shop#7", Generation = 2, Source = "verb", CreatedAt = T0 });
            db.Correlations.Add(new CorrelationEntry { Kind = "branch", Key = "acme/shop:copilot/fix-7", ItemKey = "acme/shop#7", Generation = 2, Source = "convention", CreatedAt = T0 });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var pr = await db.Correlations.SingleAsync(x => x.Kind == "pr" && x.Key == "acme/shop!9");
            Assert.Equal(("acme/shop#7", 2, "verb", T0), (pr.ItemKey, pr.Generation, pr.Source, pr.CreatedAt));
            Assert.Equal(2, await db.Correlations.CountAsync(x => x.ItemKey == "acme/shop#7" && x.Generation == 2));
        }
        await using (var db = Open())
        {
            db.Correlations.Add(new CorrelationEntry { Kind = "pr", Key = "acme/shop!9", ItemKey = "acme/shop#8", Source = "verb", CreatedAt = T0 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task OpenInstances_RoundTrip_AndCloseKeepsTheRow()
    {
        if (NoServer) return;
        await using (var db = Open())
        {
            db.OpenInstances.Add(new OpenInstance { ItemKey = "acme/shop#7", Generation = 1, OpenedAt = T0 });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var row = await db.OpenInstances.SingleAsync(x => x.ItemKey == "acme/shop#7");
            Assert.Null(row.ClosedAt);
            row.ClosedAt = T0.AddDays(1);
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var row = await db.OpenInstances.SingleAsync(x => x.ItemKey == "acme/shop#7");
            Assert.Equal((1, T0, (DateTimeOffset?)T0.AddDays(1)), (row.Generation, row.OpenedAt, row.ClosedAt));
        }
    }

    [Fact]
    public async Task ProjectionVersions_RoundTrip_PerItemAndTarget()
    {
        if (NoServer) return;
        await using (var db = Open())
        {
            db.ProjectionVersions.Add(new ProjectionVersion { ItemKey = "acme/shop#7", Target = "card", AppliedVersion = 5, AppliedAt = T0 });
            db.ProjectionVersions.Add(new ProjectionVersion { ItemKey = "acme/shop#7", Target = "labels", AppliedVersion = 3, AppliedAt = T0 });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var card = await db.ProjectionVersions.SingleAsync(x => x.ItemKey == "acme/shop#7" && x.Target == "card");
            Assert.Equal(5, card.AppliedVersion);
            card.AppliedVersion = 6;
            await db.SaveChangesAsync();
            Assert.Equal(new long[] { 3, 6 }, await db.ProjectionVersions.Where(x => x.ItemKey == "acme/shop#7").OrderBy(x => x.AppliedVersion).Select(x => x.AppliedVersion).ToArrayAsync());
        }
    }

    [Fact]
    public async Task SessionMailbox_RoundTrips_InOrderPerSession()
    {
        if (NoServer) return;
        await using (var db = Open())
        {
            db.SessionMailbox.Add(new SessionMailboxEntry { SessionId = "s1", ItemKey = "acme/shop#7", Kind = "checks.failed", Payload = "{\"pr\":9}", CreatedAt = T0, ExpiresAt = T0.AddHours(1) });
            db.SessionMailbox.Add(new SessionMailboxEntry { SessionId = "s2", Kind = "base.updated", CreatedAt = T0, ExpiresAt = T0.AddHours(1) });
            db.SessionMailbox.Add(new SessionMailboxEntry { SessionId = "s1", Kind = "session.end", CreatedAt = T0.AddMinutes(1), ExpiresAt = T0.AddHours(1) });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var rows = await db.SessionMailbox.Where(x => x.SessionId == "s1").OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(["checks.failed", "session.end"], rows.Select(r => r.Kind));
            Assert.Equal(("{\"pr\":9}", "acme/shop#7", (DateTimeOffset?)null), (rows[0].Payload, rows[0].ItemKey, rows[0].DeliveredAt));
            rows[0].DeliveredAt = T0.AddSeconds(5);
            await db.SaveChangesAsync();
            var after = await db.SessionMailbox.Where(x => x.SessionId == "s1" && x.Id > rows[0].Id).ToListAsync();
            Assert.Single(after);
            Assert.Equal(3, await db.SessionMailbox.CountAsync(x => x.ExpiresAt < T0.AddHours(2)));
        }
    }

    [Fact]
    public async Task CatalogCopies_RoundTrip_AndReplaceTheLastGoodCopy()
    {
        if (NoServer) return;
        await using (var db = Open())
        {
            db.CatalogCopies.Add(new CatalogCopy { Name = "system-catalog", Json = "{\"deployables\":[]}", ContentHash = new string('a', 64), SourceSha = "abc123", FetchedAt = T0 });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var copy = await db.CatalogCopies.SingleAsync(x => x.Name == "system-catalog");
            Assert.Equal(("{\"deployables\":[]}", "abc123", T0), (copy.Json, copy.SourceSha, copy.FetchedAt));
            copy.Json = "{\"deployables\":[{\"name\":\"ui\"}]}";
            copy.ContentHash = new string('b', 64);
            copy.FetchedAt = T0.AddMinutes(5);
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            Assert.Equal(1, await db.CatalogCopies.CountAsync());
            Assert.Equal(new string('b', 64), (await db.CatalogCopies.SingleAsync()).ContentHash);
        }
    }
}
