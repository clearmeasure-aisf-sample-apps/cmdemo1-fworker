using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Persistence;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W6.7: the views read the saga's table and the item history on a real SQL Server (FACTORY_TEST_SQL, as PersistenceTablesTests; each class run gets its own database, dropped at the end). Where it is unset the
/// database tests return at once; the parsing of a history row needs no server.
/// </summary>
public class WorkItemViewsTests : IAsyncLifetime
{
    static readonly string? ServerConnection = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    TestDatabase? database;
    string? dbConnection;

    static bool NoServer => string.IsNullOrWhiteSpace(ServerConnection);

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(dbConnection!).Options);

    public async Task InitializeAsync()
    {
        if (NoServer) return;
        database = await TestDatabase.CreateAsync("fw_views_");
        dbConnection = database.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (NoServer) return;
        if (database != null) await database.DisposeAsync();
    }

    static WorkItemState State(string item, string status = WorkItemStatus.Open, int generation = 1, bool parked = false, string reason = "", DateTimeOffset? completed = null) =>
        new() { Item = item, Generation = generation, Status = status, Plan = "docs", Step = StepKind.Develop, Parked = parked, ParkReason = reason, PullRequest = 41, StartedAt = T0.AddHours(-2), CompletedAt = completed, LeaseDue = T0.AddMinutes(5),
            Delivered = new() { ["tdd"] = "1.0.9" } };

    async Task Save(WorkItemState s, int expected = 0) => Assert.True(await new SqlWorkItemStore(Open).Save(s.Item, s, expected, default));

    async Task Decide(string item, int generation, long sequence, string action, DateTimeOffset at, params string[] reasons) =>
        await new WorkItemHistory(Open).Record(new WorkItemDecided(ItemId.Parse(item), generation, sequence, action, "docs", "Develop", $"{generation}.{sequence}", reasons, at), Guid.NewGuid());

    async Task Age(string item, DateTimeOffset at)
    {
        await using var db = Open();
        var row = await db.WorkItems.SingleAsync(x => x.ItemKey == item);
        row.UpdatedAt = at;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TheItems_AreRead_WithTheirNewestDecisions_OfTheirCurrentGenerationOnly()
    {
        if (NoServer) return;
        await Save(State("acme/orders#7", parked: true, reason: "waiting for the owner"));
        await Decide("acme/orders#7", 1, 1, "started", T0.AddHours(-2));
        await Decide("acme/orders#7", 1, 2, "step-entered", T0.AddHours(-1), "design recorded");
        await Decide("acme/orders#7", 1, 3, "parked", T0.AddMinutes(-5), "owner asked");
        await Decide("acme/orders#7", 0, 9, "belongs-to-another-generation", T0);   // (generation 0 is not the item's)
        await Decide("acme/orders#8", 1, 1, "started", T0);   // (no saga row: not an item of the views)

        var set = await new SqlWorkItemViewSource(Open).Read(new WorkItemViewQuery(T0.AddDays(-7), HistoryPerItem: 2), default);
        var item = Assert.Single(set.Items);
        Assert.False(set.Truncated);
        Assert.Equal(("acme/orders#7", 7, "acme/orders", 1, WorkItemStatus.Open, "docs", "Develop", true, "waiting for the owner", 41),
            (item.Item, item.Number, item.Repository, item.Generation, item.Status, item.Plan, item.Step, item.Parked, item.ParkReason, item.PullRequest));
        Assert.Equal("1.0.9", item.Delivered["tdd"]);
        Assert.Equal(T0.AddMinutes(5), item.LeaseDue);
        // the newest first, two of them, with the sequence, the step and the reasons the history recorded
        Assert.Equal(["parked", "step-entered"], item.History.Select(h => h.Action));
        Assert.Equal((3L, "Develop", "docs"), (item.History[0].Sequence, item.History[0].Step, item.History[0].Plan));
        Assert.Equal(["owner asked"], item.History[0].Reasons);
        Assert.Equal(T0.AddMinutes(-5), item.History[0].At);
    }

    [Fact]
    public async Task ACompletedItem_IsKeptForTheWindow_TheOpenOnesAlways_AndRepositoriesStayApart()
    {
        if (NoServer) return;
        await Save(State("acme/orders#1"));
        await Save(State("acme/billing#2", WorkItemStatus.Settling));
        await Save(State("acme/orders#3", WorkItemStatus.Completed, completed: T0.AddDays(-1)));
        await Save(State("acme/orders#4", WorkItemStatus.Completed, completed: T0.AddDays(-30)));
        await Save(State("acme/orders#5"));
        await Age("acme/orders#3", T0.AddDays(-1)); await Age("acme/orders#4", T0.AddDays(-30)); await Age("acme/orders#5", T0.AddDays(-60));   // (an open item is shown however long ago it moved)

        var set = await new SqlWorkItemViewSource(Open).Read(new WorkItemViewQuery(T0.AddDays(-7)), default);
        Assert.Equal(["acme/billing#2", "acme/orders#1", "acme/orders#3", "acme/orders#5"], set.Items.Select(i => i.Item).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(["acme/billing", "acme/orders"], set.Items.Select(i => i.Repository).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(set.Items, i => Assert.Empty(i.History));
    }

    [Fact]
    public async Task MoreThanTheMaximum_IsCutAtTheOldestCompleted_AndSaysSo()
    {
        if (NoServer) return;
        await Save(State("acme/orders#1")); await Save(State("acme/orders#2"));
        await Save(State("acme/orders#3", WorkItemStatus.Completed, completed: T0)); await Age("acme/orders#3", T0.AddHours(-1));
        await Save(State("acme/orders#4", WorkItemStatus.Completed, completed: T0)); await Age("acme/orders#4", T0.AddHours(-2));

        var set = await new SqlWorkItemViewSource(Open).Read(new WorkItemViewQuery(T0.AddDays(-7), MaxItems: 3), default);
        Assert.True(set.Truncated);
        Assert.Equal(["acme/orders#1", "acme/orders#2", "acme/orders#3"], set.Items.Select(i => i.Item).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AReopenedItem_ShowsTheHistoryOfItsNewGeneration_NotTheOldOne()
    {
        if (NoServer) return;
        await Save(State("acme/orders#7", generation: 2));
        await Decide("acme/orders#7", 1, 5, "delivered-earlier", T0.AddDays(-3));
        await Decide("acme/orders#7", 2, 1, "reopened", T0.AddMinutes(-3));
        var item = Assert.Single((await new SqlWorkItemViewSource(Open).Read(new WorkItemViewQuery(T0.AddDays(-7)), default)).Items);
        Assert.Equal(["reopened"], item.History.Select(h => h.Action));
    }

    [Fact]
    public async Task ADamagedStateDocument_DoesNotTakeTheOtherItemsDown()
    {
        if (NoServer) return;
        await Save(State("acme/orders#1"));
        await Save(State("acme/orders#2"));
        await using (var db = Open()) { (await db.WorkItems.SingleAsync(x => x.ItemKey == "acme/orders#2")).StateJson = "{ not json"; await db.SaveChangesAsync(); }
        var set = await new SqlWorkItemViewSource(Open).Read(new WorkItemViewQuery(T0.AddDays(-7)), default);
        Assert.Equal(2, set.Items.Count);
        var damaged = set.Items.Single(i => i.Number == 2);
        Assert.Equal((WorkItemStatus.Open, "docs", "Develop"), (damaged.Status, damaged.Plan, damaged.Step));   // the columns beside the document still say where the item is
    }

    [Fact]
    public async Task NothingInTheTable_IsAnEmptyAnswer()
    {
        if (NoServer) return;
        var set = await new SqlWorkItemViewSource(Open).Read(new WorkItemViewQuery(T0.AddDays(-7)), default);
        Assert.Empty(set.Items); Assert.False(set.Truncated);
    }
}
