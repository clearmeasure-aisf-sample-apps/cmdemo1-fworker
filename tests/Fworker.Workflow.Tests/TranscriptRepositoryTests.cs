using Fworker.Persistence;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.Transcripts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.8 on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one), the database built by the scripts: the transcripts of runtime sessions in factory.Transcripts (SqlTranscriptRepository), and the
/// journal in the item history (JournalHistory).
/// </summary>
public class TranscriptRepositoryTests : IAsyncLifetime
{
    static bool NoServer => !TestDatabase.Available;
    static readonly DateTimeOffset T0 = new(2026, 10, 29, 14, 20, 0, TimeSpan.Zero);
    TestDatabase? database;
    DateTimeOffset now = T0;

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);
    SqlTranscriptRepository Repo() => new(Open, () => now);

    public async Task InitializeAsync() { if (!NoServer) database = await TestDatabase.CreateAsync("fw_w98_"); }
    public async Task DisposeAsync() { if (database != null) await database.DisposeAsync(); }

    static TranscriptDoc Doc(string name, string? item, DateTimeOffset started, params string[] lines)
        => new(name, item, "octopus", "develop", started, started.AddMinutes(3), null, 4, lines.Length + 4, lines);

    [Fact]
    public async Task ASession_RoundTrips_AndASecondSaveReplacesIt()
    {
        if (NoServer) return;
        var repo = Repo();
        var lines = Enumerable.Range(0, 3000).Select(i => $"line {i} with \"quotes\" and ünïcode 日本").ToArray();
        await repo.Save(Doc("cae-1.dev", "acme/orders#3", T0, lines));
        var back = (await repo.Load("cae-1.dev"))!;
        Assert.Equal(("cae-1.dev", "acme/orders#3", "octopus", "develop", 4L, 3004L, null), (back.Name, back.ItemKey, back.Runtime, back.Phase, back.Dropped, back.Total, (object?)back.Ended));
        Assert.Equal(lines, back.Lines);
        Assert.Equal(T0, back.Started); Assert.Equal(T0.AddMinutes(3), back.LastLine);
        await repo.Save(Doc("cae-1.dev", "acme/orders#3", T0, "only", "two") with { Ended = T0.AddMinutes(9) });
        var again = (await repo.Load("cae-1.dev"))!;
        Assert.Equal(["only", "two"], again.Lines); Assert.Equal(T0.AddMinutes(9), again.Ended);
        await using var db = Open();
        var row = await db.Transcripts.SingleAsync();
        Assert.Equal((2, 9L), (row.Revision, row.Bytes));   // two saves; "only" and "two", each with a line break
        Assert.Null(await repo.Load("no-such-session"));
    }

    [Fact]
    public async Task TwoWritersOfOneSession_BothLand_TheLastOneWins()
    {
        if (NoServer) return;
        var repo = Repo();
        await repo.Save(Doc("race", "acme/orders#3", T0, "first"));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => repo.Save(Doc("race", "acme/orders#3", T0, "writer " + i))));
        await using var db = Open();
        var row = await db.Transcripts.SingleAsync(x => x.Name == "race");
        Assert.True(row.Revision >= 2, "revision " + row.Revision);   // a writer that lost the revision read again and wrote on the new state
        Assert.StartsWith("[\"writer ", row.LinesJson);
    }

    [Fact]
    public async Task ThePrune_RemovesTheSessionsOfTheDaysBeforeTheLastN_ByTheDayOfTheirStart()
    {
        if (NoServer) return;
        var repo = Repo();
        // now is 2026-10-29 14:20; 14 days back is the day 2026-10-15: a session that started on or after midnight of the 15th stays
        await repo.Save(Doc("old", "acme/orders#1", new DateTimeOffset(2026, 10, 14, 23, 59, 0, TimeSpan.Zero), "x"));
        await repo.Save(Doc("edge", "acme/orders#2", new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), "x"));
        await repo.Save(Doc("new", "acme/orders#3", T0, "x"));
        Assert.Equal(1, await repo.Prune(14));
        Assert.Null(await repo.Load("old")); Assert.NotNull(await repo.Load("edge")); Assert.NotNull(await repo.Load("new"));
        Assert.Equal(1, await repo.Prune(1));                                                // only today's stays
        Assert.Equal(0, await repo.Prune(0));                                                // 0 is 1, as the script clamps: nothing more to remove
        Assert.NotNull(await repo.Load("new"));
        now = T0.AddDays(400);
        Assert.Equal(1, await repo.Prune(9999));                                             // 365 at most
    }

    [Fact]
    public async Task TheSessionsOfAnItem_AreOneIndexedRead_NewestFirst()
    {
        if (NoServer) return;
        var repo = Repo();
        await repo.Save(Doc("a", "acme/orders#3", T0.AddHours(-2), "x"));
        await repo.Save(Doc("b", "acme/orders#3", T0, "x"));
        await repo.Save(Doc("c", "acme/orders#4", T0, "x"));
        await repo.Save(Doc("d", null, T0, "x"));
        Assert.Equal(["b", "a"], await repo.SessionsOf("acme/orders#3"));
    }

    [Fact]
    public async Task APhaseOrRuntimeLongerThanItsColumn_IsCut_NotRefused()
    {
        if (NoServer) return;
        var repo = Repo();
        await repo.Save(Doc("long", null, T0, "x") with { Phase = new string('p', 80), Runtime = new string('r', 80) });
        var back = (await repo.Load("long"))!;
        Assert.Equal((32, 32), (back.Phase.Length, back.Runtime.Length));
    }

    // ---- the journal in the item history ----
    [Fact]
    public async Task AJournalLine_IsRecordedOnce_AndReadBackInOrder()
    {
        if (NoServer) return;
        var history = new JournalHistory(Open);
        Assert.True(await history.Record(new("acme/orders#3", "assigned", "k1", true, "", false, T0)));
        Assert.True(await history.Record(new("acme/orders#3", "assigned", "k1", true, "", false, T0.AddMinutes(1))));   // the same line again: no second row
        Assert.True(await history.Record(new("acme/orders#3", "ci-released", "r9", false, "recorded", true, T0.AddMinutes(2))));
        Assert.True(await history.Record(new("acme/orders#3", "waiting", "k2", false, "rate-limited", false, T0.AddMinutes(3))));
        Assert.True(await history.Record(new("acme/orders#4", "assigned", "k1", true, "", false, T0)));                   // another item, the same key
        var lines = await history.LinesOf("acme/orders#3");
        Assert.Equal(["assigned", "ci-released", "waiting"], lines.Select(l => l.Event));
        Assert.Equal((true, false), (lines[0].Posted, lines[0].Recorded));
        Assert.Equal(("recorded", true), (lines[1].Reason, lines[1].Recorded));
        Assert.Equal(3, await database!.Count("SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = 'acme/orders#3' AND Type LIKE 'journal.%'"));
    }

    [Fact]
    public async Task AHistoryThatCannotBeWritten_FailsQuietly()
    {
        if (NoServer) return;
        var history = new JournalHistory(() => new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer("Server=127.0.0.1,1;Database=x;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=true").Options));
        Assert.False(await history.Record(new("acme/orders#3", "assigned", "k1", true, "", false, T0)));
    }
}
