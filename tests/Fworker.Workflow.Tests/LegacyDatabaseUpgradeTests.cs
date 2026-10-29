using Fworker.Database;
using JasperFx;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Wolverine;
using Wolverine.SqlServer;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// The rehearsal of tdd. The tdd database was built the old way: the 8 EF migrations (fixtures/ef-migrations-2026-10-29, the SQL they ran), the runtime DDL of the stores for the tables that were used,
/// and Wolverine's own tables built by Weasel at its start (CreateOrUpdate). It holds data. The scripts of Fworker.Database must run on it as no-ops, create what the old way never made, keep every row, and leave a
/// schema the manifest check accepts.
/// </summary>
public class LegacyDatabaseUpgradeTests
{
    // the DDL SqlIngressDedup ran on first use, copied from master before it was removed
    const string OldIngressDedupDdl = """
        IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory]');
        IF OBJECT_ID(N'factory.IngressDedup', N'U') IS NULL
            CREATE TABLE factory.IngressDedup (
                Layer nvarchar(16) NOT NULL, [Key] nvarchar(400) NOT NULL, MessageId uniqueidentifier NOT NULL, ClaimedAt datetime2 NOT NULL,
                CONSTRAINT PK_IngressDedup PRIMARY KEY (Layer, [Key]));
        """;

    static async Task RunBatches(TestDatabase db, string sql)
    {
        foreach (var batch in Regex.Split(sql, @"^GO\s*$", RegexOptions.Multiline).Where(b => !string.IsNullOrWhiteSpace(b))) await db.Execute(batch);
    }

    [Fact]
    public async Task TheScriptsRunAsNoOpsOnTheDatabaseTheOldWayBuilt_AndKeepItsRows()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateEmptyAsync("fw_legacy_");
        // 1. the EF migrations, as they ran
        await RunBatches(db, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ef-migrations-2026-10-29", "ef-migrations.sql")));
        Assert.Equal(8, await db.Count("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        // 2. the old runtime DDL of the one table that was used on tdd
        await db.Execute(OldIngressDedupDdl);
        // 3. Wolverine's tables, built by Weasel at the start of a host (CreateOrUpdate, the default), then stopped
        using (var host = await new HostBuilder().ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Critical)).UseWolverine(w =>
        {
            w.ServiceName = "legacy"; w.Discovery.DisableConventionalDiscovery(); w.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Static;
            w.PersistMessagesWithSqlServer(db.ConnectionString, "wolverine");
            w.Durability.Mode = DurabilityMode.Solo;
        }).StartAsync())
            await host.StopAsync();
        SqlConnection.ClearAllPools();
        Assert.Equal(0, await db.Count("SELECT COUNT(*) FROM sys.tables WHERE name IN ('IdempotencyLedger', 'CopilotReservations')"));   // v2 never acted on tdd
        // 4. data
        await db.Execute("INSERT INTO factory.WorkItems (ItemKey, Repository, Generation, Revision, Status, [Plan], Step, StateJson, UpdatedAt) VALUES (N'acme/shop#7', N'acme/shop', 1, 3, N'open', N'feature', N'Develop', N'{}', SYSDATETIMEOFFSET())");
        await db.Execute("INSERT INTO factory.ItemHistory (ItemKey, Generation, At, Kind, Type) VALUES (N'acme/shop#7', 1, SYSDATETIMEOFFSET(), N'event', N'Started'), (N'acme/shop#7', 1, SYSDATETIMEOFFSET(), N'event', N'Moved')");
        await db.Execute("INSERT INTO factory.IngressDedup (Layer, [Key], MessageId, ClaimedAt) VALUES (N'webhook', N'delivery-1', NEWID(), SYSUTCDATETIME())");
        await db.Execute("INSERT INTO wolverine.wolverine_incoming_envelopes (id, status, owner_id, body, message_type, received_at) VALUES (NEWID(), 'Handled', 0, 0x00, 'T', 'local://x')");

        // the old way left a schema that is not the scripts' yet: no journal, two tables missing
        var before = await FactorySchema.VerifyAsync(db.ConnectionString);
        Assert.False(before.Good);
        Assert.Equal(FactorySchema.Scripts.Count, before.Pending.Count);

        var report = await FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions());

        Assert.True(report.Good, string.Join("; ", report.Differences.Select(d => d.Description)));
        Assert.Equal(FactorySchema.Scripts, report.Applied);
        Assert.Equal(FactorySchema.Scripts.Count, report.Journaled);
        Assert.Equal(FactorySchema.Scripts.Count, await db.Count("SELECT COUNT(*) FROM dbo.SchemaVersions"));
        Assert.Equal(2, await db.Count("SELECT COUNT(*) FROM sys.tables WHERE name IN ('IdempotencyLedger', 'CopilotReservations')"));
        // every row is still there, and the EF history is untouched (a revert of the pull request finds all 8 migrations applied)
        Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.WorkItems WHERE ItemKey = N'acme/shop#7' AND Revision = 3"));
        Assert.Equal(2, await db.Count("SELECT COUNT(*) FROM factory.ItemHistory"));
        Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.IngressDedup"));
        Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes"));
        Assert.Equal(8, await db.Count("SELECT COUNT(*) FROM __EFMigrationsHistory"));

        // the schema equals the one the scripts build from nothing, apart from extra objects
        await using var fresh = await TestDatabase.CreateAsync("fw_legacy_fresh_");
        var (differences, _) = (await SchemaManifest.ReadAsync(db.ConnectionString)).Compare(await SchemaManifest.ReadAsync(fresh.ConnectionString));
        Assert.Empty(differences);
        // the next start applies nothing
        var again = await FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions());
        Assert.Empty(again.Applied);
        Assert.True(again.Good);
    }
}
