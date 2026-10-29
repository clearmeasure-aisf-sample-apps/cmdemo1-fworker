using Fworker.Database;
using Microsoft.Data.SqlClient;

namespace Gateway.Tests;

/// <summary>
/// A database of its own on the SQL Server of FACTORY_TEST_SQL, built the way the gateway builds its database: the scripts of Fworker.Database (DbUp), checked against the manifest. Nothing here
/// uses EF migrations or lets Wolverine build its tables. Disposing drops it, as the TearDown of cmdemo1-workorders' migration tests does.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public static readonly string? ServerConnection = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
    public static bool Available => !string.IsNullOrWhiteSpace(ServerConnection);

    TestDatabase(string name, string connectionString) { Name = name; ConnectionString = connectionString; }

    public string Name { get; }
    public string ConnectionString { get; }

    /// <summary>A new database with the scripts applied; the schema must be good.</summary>
    public static async Task<TestDatabase> CreateAsync(string prefix)
    {
        var db = await CreateEmptyAsync(prefix);
        try
        {
            var report = await FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions { CreateDatabase = false });
            if (!report.Good) throw new InvalidOperationException("the schema built by the scripts is not good: " + string.Join("; ", report.Differences.Select(d => d.Description)) + string.Join(", ", report.Pending));
        }
        catch { await db.DisposeAsync(); throw; }
        return db;
    }

    /// <summary>A new, empty database.</summary>
    public static async Task<TestDatabase> CreateEmptyAsync(string prefix)
    {
        var name = prefix + Guid.NewGuid().ToString("N")[..16];
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]", master);
            await create.ExecuteNonQueryAsync();
        }
        return new TestDatabase(name, new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = name }.ConnectionString);
    }

    public async Task<long> Count(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task Execute(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = "master" }.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"IF DB_ID('{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }
}
