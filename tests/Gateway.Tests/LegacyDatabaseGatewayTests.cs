using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Database;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Xunit;
using Xunit.Abstractions;

namespace Gateway.Tests;

/// <summary>
/// The first start of this change on tdd, rehearsed with the whole gateway: a database built the old way (the 8 EF migrations as they ran, the old runtime DDL of one table, Wolverine's tables built by Weasel,
/// rows in them) and a gateway started on it. The scripts run as no-ops, the two tables the old way never made are created, no row is touched, the check passes, /ready turns 200 and Wolverine starts. The
/// times are written to the test output (the start of Wolverine used to read the whole schema).
/// </summary>
public sealed class LegacyDatabaseGatewayTests(ITestOutputHelper output)
{
    const string OldIngressDedupDdl = """
        IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory]');
        IF OBJECT_ID(N'factory.IngressDedup', N'U') IS NULL
            CREATE TABLE factory.IngressDedup (
                Layer nvarchar(16) NOT NULL, [Key] nvarchar(400) NOT NULL, MessageId uniqueidentifier NOT NULL, ClaimedAt datetime2 NOT NULL,
                CONSTRAINT PK_IngressDedup PRIMARY KEY (Layer, [Key]));
        """;

    [Fact]
    public async Task AGatewayStartedOnTheDatabaseTheOldWayBuilt_AppliesTheScriptsAsNoOps_KeepsTheRows_AndStartsWolverine()
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateEmptyAsync("fw_legacygw_");
        foreach (var batch in Regex.Split(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ef-migrations-2026-10-29", "ef-migrations.sql")), @"^GO\s*$", RegexOptions.Multiline).Where(b => !string.IsNullOrWhiteSpace(b)))
            await db.Execute(batch);
        await db.Execute(OldIngressDedupDdl);

        // Wolverine's tables as the old start built them: Weasel's CreateOrUpdate of its own model (what it did at every start)
        var timeWeasel = Stopwatch.StartNew();
        using (var h = WolverineHostService.BuildHostForSchemaExport(new WolverineHostOptions(), runtimeCommands: false, db.ConnectionString))
            await ((IDatabase)h.Services.GetRequiredService<IMessageStore>()).ApplyAllConfiguredChangesToDatabaseAsync();
        output.WriteLine($"Weasel built Wolverine's tables in {timeWeasel.ElapsedMilliseconds} ms");
        // what the old start still did on top of it, at every start: the full diff of its model against the catalog
        var timeDiff = Stopwatch.StartNew();
        using (var h = WolverineHostService.BuildHostForSchemaExport(new WolverineHostOptions(), runtimeCommands: false, db.ConnectionString))
            await ((IDatabase)h.Services.GetRequiredService<IMessageStore>()).AssertDatabaseMatchesConfigurationAsync();
        output.WriteLine($"the old start's schema diff of Wolverine's model: {timeDiff.ElapsedMilliseconds} ms (on this machine; about 80 s on the S0 database of tdd)");

        await db.Execute("INSERT INTO factory.WorkItems (ItemKey, Repository, Generation, Revision, Status, [Plan], Step, StateJson, UpdatedAt) VALUES (N'acme/shop#7', N'acme/shop', 1, 3, N'open', N'feature', N'Develop', N'{}', SYSDATETIMEOFFSET())");
        await db.Execute("INSERT INTO factory.ItemHistory (ItemKey, Generation, At, Kind, Type) VALUES (N'acme/shop#7', 1, SYSDATETIMEOFFSET(), N'event', N'Started')");
        await db.Execute("INSERT INTO factory.IngressDedup (Layer, [Key], MessageId, ClaimedAt) VALUES (N'webhook', N'delivery-1', NEWID(), SYSUTCDATETIME())");
        await db.Execute("INSERT INTO wolverine.wolverine_incoming_envelopes (id, status, owner_id, body, message_type, received_at) VALUES (NEWID(), 'Handled', 0, 0x00, 'T', 'local://x')");
        Assert.Equal(0, await db.Count("SELECT COUNT(*) FROM sys.tables WHERE name IN ('IdempotencyLedger', 'CopilotReservations')"));
        SqlConnection.ClearAllPools();

        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", db.ConnectionString);
        WolverineHostTestSupport.Shadow(env);
        env.Apply();
        var host = new GatewayHost();
        try
        {
            var total = Stopwatch.StartNew();
            _ = host.Server;
            JsonNode? status = null; var ready = false;
            while (total.Elapsed < TimeSpan.FromSeconds(120))
            {
                if (!ready && (await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status == 200) { ready = true; output.WriteLine($"/ready 200 after {total.ElapsedMilliseconds} ms"); }
                status = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json;
                if ((string?)status?["wolverine"]?["state"] == "running") break;
                await Task.Delay(50);
            }
            output.WriteLine($"Wolverine running after {total.ElapsedMilliseconds} ms; schema: {status?["schema"]?.ToJsonString()}");
            Assert.True(ready);
            Assert.Equal("running", (string?)status?["wolverine"]?["state"]);
            var schema = status?["schema"];
            Assert.Equal("good", (string?)schema?["state"]);
            Assert.Empty(schema?["differences"]?.AsArray() ?? []);
            Assert.Equal(FactorySchema.Scripts.Count, schema?["applied"]?.AsArray().Count);   // all journaled by this start: they ran as no-ops
            Assert.Equal(FactorySchema.Scripts.Count, await db.Count("SELECT COUNT(*) FROM dbo.SchemaVersions"));
            Assert.Equal(2, await db.Count("SELECT COUNT(*) FROM sys.tables WHERE name IN ('IdempotencyLedger', 'CopilotReservations')"));
            Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.WorkItems WHERE ItemKey = N'acme/shop#7' AND Revision = 3"));
            Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.ItemHistory"));
            Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.IngressDedup"));
            Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes"));
            Assert.Equal(8, await db.Count("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        }
        finally { await host.DisposeAsync(); SqlConnection.ClearAllPools(); }
    }
}
