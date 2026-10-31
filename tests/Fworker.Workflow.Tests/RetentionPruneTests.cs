using System.Text.Json.Nodes;
using Fworker.Persistence;
using Fworker.Workflows.Reconcilers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.10g: the daily retention prune on a real SQL Server (FACTORY_TEST_SQL; reported as skipped without one), the database built by the scripts.</summary>
public class RetentionPruneTests : IAsyncLifetime
{
    static bool NoServer => !TestDatabase.Available;
    static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    TestDatabase? database;

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);
    RetentionPrune Prune(RetentionSettings? s = null) => new(Open, s ?? new RetentionSettings(), () => T0);

    public async Task InitializeAsync() { if (!NoServer) database = await TestDatabase.CreateAsync("fw_ret_"); }
    public async Task DisposeAsync() { if (database != null) await database.DisposeAsync(); }

    async Task History(string item, string type, DateTimeOffset at, int count = 1)
    {
        await using var db = Open();
        for (var i = 0; i < count; i++) db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = item, Generation = 1, At = at, Kind = "event", Type = type });
        await db.SaveChangesAsync();
    }

    async Task WorkItem(string item, string status)
    {
        await using var db = Open();
        db.WorkItems.Add(new WorkItemRow { ItemKey = item, Repository = item.Split('#')[0], Generation = 1, Revision = 1, Status = status, Plan = "docs", Step = "design", StateJson = "{}", UpdatedAt = T0 });
        await db.SaveChangesAsync();
    }

    Task<long> Count(string table, string where = "1=1") => database!.Count($"SELECT COUNT(*) FROM factory.{table} WHERE {where}");

    [SqlFact]
    public async Task OldHistoryOfAnItemWithNoOpenInstanceGoes_TheHistoryOfAnOpenItemAndRecentRowsStay()
    {
        if (NoServer) return;
        var old = T0.AddDays(-200);
        await History("acme/orders#1", "work-item.decided", old, 3);     // over: goes
        await History("acme/orders#2", "work-item.decided", old, 2);     // open: stays whole
        await WorkItem("acme/orders#2", "open");
        await History("acme/orders#3", "work-item.decided", old);        // completed: goes
        await WorkItem("acme/orders#3", "completed");
        await History("acme/orders#4", "work-item.decided", old);        // settling counts as open
        await WorkItem("acme/orders#4", "settling");
        await History("acme/orders#1", "work-item.decided", T0.AddDays(-10), 2);   // recent: stays
        var report = await Prune().Run();
        Assert.Equal(4, report.ItemHistory);
        Assert.Equal(0, report.ShadowHistory);
        Assert.Equal(2, await Count("ItemHistory", "ItemKey = N'acme/orders#2'"));
        Assert.Equal(1, await Count("ItemHistory", "ItemKey = N'acme/orders#4'"));
        Assert.Equal(2, await Count("ItemHistory", "ItemKey = N'acme/orders#1'"));
        Assert.False(report.More);
    }

    [SqlFact]
    public async Task TheShadowRunsRecordsGoAfterTheirShortRetention_TheActingTrainsDecisionsDoNot()
    {
        if (NoServer) return;
        var aged = T0.AddDays(-45);   // older than shadowDays (30), younger than itemHistoryDays (180)
        await History("acme/orders#1", "ingress.inbox.webhook.pull_request", aged, 4);
        await History("acme/orders#1", "delivery.train.shadow-command", aged, 2);
        await History("acme/orders#1", "delivery.train.replay", aged);
        await History("acme/orders#1", "delivery.train.decided", aged, 2);       // the acting train's: stays
        await History("acme/orders#1", "delivery.item-merged", aged);            // stays
        await History("acme/orders#1", "ingress.inbox.webhook.pull_request", T0.AddDays(-5), 2);   // recent: stays
        await using (var db = Open())
        {
            db.DeliveryShadowComparisons.Add(new DeliveryShadowComparison { At = aged, Deployable = "app", Environment = "uat", Release = "1.0.1", Outcome = "match" });
            db.DeliveryShadowComparisons.Add(new DeliveryShadowComparison { At = T0.AddDays(-2), Deployable = "app", Environment = "uat", Release = "1.0.2", Outcome = "match" });
            await db.SaveChangesAsync();
        }
        var report = await Prune().Run();
        Assert.Equal(7, report.ShadowHistory);
        Assert.Equal(1, report.ShadowComparisons);
        Assert.Equal(0, report.ItemHistory);
        Assert.Equal(5, await Count("ItemHistory"));
        Assert.Equal(1, await Count("DeliveryShadowComparisons"));
    }

    [SqlFact]
    public async Task ARunRemovesInChunks_AndSaysThereIsMoreWhenItStopsAtItsLimit()
    {
        if (NoServer) return;
        await History("acme/orders#1", "ingress.inbox.webhook.push", T0.AddDays(-60), 25);
        var small = new RetentionSettings(ChunkRows: 10, MaxChunks: 2);
        var first = await Prune(small).Run();
        Assert.Equal(20, first.ShadowHistory); Assert.True(first.More);
        var second = await Prune(small).Run();
        Assert.Equal(5, second.ShadowHistory); Assert.False(second.More);
        Assert.Equal(0, await Count("ItemHistory"));
    }

    async Task Transcript(string name, DateTimeOffset started, long bytes, DateTimeOffset? ended, DateTimeOffset? lastLine = null)
    {
        await using var db = Open();
        db.Transcripts.Add(new TranscriptRow { Name = name, ItemKey = "acme/orders#1", Runtime = "octopus", Phase = "develop", StartedAt = started, LastLineAt = lastLine ?? started.AddMinutes(5), EndedAt = ended, Revision = 1, Bytes = bytes, LinesTotal = 1, LinesJson = "[]", UpdatedAt = started });
        await db.SaveChangesAsync();
    }

    [SqlFact]
    public async Task Transcripts_GoByAge_ThenTheOldestFinishedOnesGoUntilTheCapHolds_AndALiveSessionIsNeverCut()
    {
        if (NoServer) return;
        await Transcript("ancient", T0.AddDays(-30), 10, T0.AddDays(-30));     // older than the 14 days: goes by age
        await Transcript("a", T0.AddDays(-9), 400, T0.AddDays(-9));
        await Transcript("b", T0.AddDays(-8), 400, T0.AddDays(-8));
        await Transcript("c", T0.AddDays(-7), 400, T0.AddDays(-7));
        await Transcript("live-old", T0.AddDays(-6), 400, null, lastLine: T0.AddMinutes(-10));   // still writing: never cut
        await Transcript("d", T0.AddDays(-1), 400, T0.AddHours(-20));
        var report = await Prune(new RetentionSettings(TranscriptMaxBytes: 1000)).Run();
        Assert.Equal(1, report.Transcripts);
        // 2000 bytes stored, cap 1000: a, b and c go (1200 removed), the live session and d (800) stay
        Assert.Equal(3, report.TranscriptsOverCap);
        var names = new List<string>();
        await using (var db = Open()) names = await db.Transcripts.Select(x => x.Name).OrderBy(x => x).ToListAsync();
        Assert.Equal(["d", "live-old"], names);
    }

    [Fact]
    public void TheSettingsComeFromTheConfigurationBlock_WithTheirDefaults_AndLimits()
    {
        var d = RetentionSettings.From(null);
        Assert.Equal((180, 30, 14, 256L * 1024 * 1024), (d.ItemHistoryDays, d.ShadowDays, d.TranscriptDays, d.TranscriptMaxBytes));
        var s = RetentionSettings.From(JsonNode.Parse("""{"retention":{"itemHistoryDays":90,"shadowDays":7,"transcriptDays":3,"transcriptMaxMb":64}}"""));
        Assert.Equal((90, 7, 3, 64L * 1024 * 1024), (s.ItemHistoryDays, s.ShadowDays, s.TranscriptDays, s.TranscriptMaxBytes));
        var odd = RetentionSettings.From(JsonNode.Parse("""{"retention":{"itemHistoryDays":0,"shadowDays":"x","transcriptMaxMb":1}}"""));
        Assert.Equal(7, odd.ItemHistoryDays);     // never below a week
        Assert.Equal(30, odd.ShadowDays);         // not a number: the default
        Assert.Equal(16L * 1024 * 1024, odd.TranscriptMaxBytes);
    }
}
