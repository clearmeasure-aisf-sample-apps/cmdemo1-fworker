using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.0: the two tables the rest of W9 needs, on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one), built by scripts 012 and 013: the rows round-trip through the model, a stale writer loses
/// on the revision, and the indexes the later items read by exist.
/// </summary>
public class QcAndTranscriptTableTests : IAsyncLifetime
{
    static readonly string? ServerConnection = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
    TestDatabase? database;

    static bool NoServer => string.IsNullOrWhiteSpace(ServerConnection);

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        if (NoServer) return;
        database = await TestDatabase.CreateAsync("fw_w90_");
    }

    public async Task DisposeAsync()
    {
        if (database != null) await database.DisposeAsync();
    }

    static readonly DateTimeOffset T0 = new(2026, 10, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AQcEnvironmentRow_RoundTrips_AndAStaleWriterLoses()
    {
        if (NoServer) return;
        await using (var db = Open())
        {
            db.QcEnvironments.Add(new QcEnvironmentRow { PullRequestKey = "o/r!7", Repository = "o/r", ItemKey = "o/r#3", Revision = 1, Status = "assigned", StateJson = "{\"attempt\":1}", UpdatedAt = T0 });
            db.QcEnvironments.Add(new QcEnvironmentRow { PullRequestKey = "o/r!8", Repository = "o/r", ItemKey = null, Revision = 1, Status = "torn-down", StateJson = "{}", UpdatedAt = T0 });
            await db.SaveChangesAsync();
        }
        await using var a = Open();
        await using var b = Open();
        var first = await a.QcEnvironments.SingleAsync(x => x.PullRequestKey == "o/r!7");
        var second = await b.QcEnvironments.SingleAsync(x => x.PullRequestKey == "o/r!7");
        Assert.Equal(("o/r", "o/r#3", "assigned"), (first.Repository, first.ItemKey, first.Status));
        first.Revision = 2; first.Status = "ready";
        await a.SaveChangesAsync();
        second.Revision = 2; second.Status = "failed";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        await using var c = Open();
        Assert.Equal(["o/r!7"], await c.QcEnvironments.Where(x => x.Repository == "o/r" && x.Status != "torn-down").Select(x => x.PullRequestKey).ToListAsync());
        Assert.Null((await c.QcEnvironments.SingleAsync(x => x.PullRequestKey == "o/r!8")).ItemKey);
    }

    [Fact]
    public async Task ATranscriptRow_RoundTrips_WithItsLines_AndAStaleWriterLoses()
    {
        if (NoServer) return;
        var lines = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 2000).Select(i => "line " + i));
        await using (var db = Open())
        {
            db.Transcripts.Add(new TranscriptRow { Name = "cae-1.dev", ItemKey = "o/r#3", Runtime = "copilot", Phase = "develop", StartedAt = T0, LastLineAt = T0.AddMinutes(5), EndedAt = null, Revision = 1, Bytes = 15000, LinesTotal = 2100, LinesDropped = 100, LinesJson = lines, UpdatedAt = T0 });
            db.Transcripts.Add(new TranscriptRow { Name = "cae-2", ItemKey = null, Runtime = "cursor", Phase = "", StartedAt = T0.AddHours(-30), LastLineAt = T0.AddHours(-30), EndedAt = T0.AddHours(-29), Revision = 1, UpdatedAt = T0 });
            await db.SaveChangesAsync();
        }
        await using var a = Open();
        await using var b = Open();
        var row = await a.Transcripts.SingleAsync(x => x.Name == "cae-1.dev");
        Assert.Equal((2100L, 100L, 15000L, null), (row.LinesTotal, row.LinesDropped, row.Bytes, (object?)row.EndedAt));
        Assert.Equal(lines, row.LinesJson);
        var stale = await b.Transcripts.SingleAsync(x => x.Name == "cae-1.dev");
        row.Revision = 2; row.EndedAt = T0.AddMinutes(6);
        await a.SaveChangesAsync();
        stale.Revision = 2; stale.Phase = "other";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        await using var c = Open();
        Assert.Equal(["cae-1.dev"], await c.Transcripts.Where(x => x.ItemKey == "o/r#3").Select(x => x.Name).ToListAsync());
        Assert.Equal(["cae-2"], await c.Transcripts.Where(x => x.StartedAt < T0.AddHours(-24)).Select(x => x.Name).ToListAsync());   // the prune by age
        Assert.Equal("[]", (await c.Transcripts.SingleAsync(x => x.Name == "cae-2")).LinesJson);
    }

    [Fact]
    public async Task TheIndexesTheLaterItemsReadBy_Exist()
    {
        if (NoServer) return;
        await using var db = Open();
        var names = await db.Database.SqlQueryRaw<string>("SELECT i.name AS Value FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'factory' AND t.name IN ('QcEnvironments','Transcripts') AND i.name IS NOT NULL").ToListAsync();
        Assert.Equal(["IX_QcEnvironments_Repository_Status", "IX_Transcripts_ItemKey_StartedAt", "IX_Transcripts_StartedAt", "PK_QcEnvironments", "PK_Transcripts"], names.OrderBy(x => x, StringComparer.Ordinal));
    }
}
