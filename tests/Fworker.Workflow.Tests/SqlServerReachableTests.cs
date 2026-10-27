using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Fworker.Persistence;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// The CI smoke check that the SQL Server service container of tests.yml is reachable (W2.0). FACTORY_TEST_SQL holds its connection string in CI; where it
/// is unset (a developer machine) the test returns at once, so nothing needs a database locally.
/// </summary>
public class SqlServerReachableTests
{
    [Fact]
    public async Task TheSqlServerServiceContainer_AnswersAQuery()
    {
        var cs = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var db = new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);
        Assert.True(await db.Database.CanConnectAsync(cts.Token), "the SQL Server container did not answer");
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(cts.Token);
        await using var cmd = new SqlCommand("SELECT 1", conn);
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync(cts.Token)));
    }
}
