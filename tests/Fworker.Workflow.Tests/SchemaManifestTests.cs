using DbUp;
using DbUp.Helpers;
using Fworker.Database;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// The manifest of the schema (src/Fworker.Database/schema-manifest.json, embedded) is what the gateway compares the catalog with at every start. It is generated from a database built by the
/// scripts. FWORKER_UPDATE_SCHEMA_MANIFEST=1 rewrites the file. The SQL tests return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class SchemaManifestTests
{
    static string ManifestPath => Path.Combine(RepoPaths.Root, "src", "Fworker.Database", "schema-manifest.json");

    [SqlFact]
    public async Task TheManifestIsWhatTheScriptsBuild()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateEmptyAsync("fw_manifest_");
        var upgrade = await FactorySchema.UpgradeAsync(db.ConnectionString, new SchemaRunOptions());
        Assert.Equal(FactorySchema.Scripts, upgrade.Applied);
        var built = await SchemaManifest.ReadAsync(db.ConnectionString);
        if (Environment.GetEnvironmentVariable("FWORKER_UPDATE_SCHEMA_MANIFEST") == "1") File.WriteAllText(ManifestPath, built.ToJson(), new System.Text.UTF8Encoding(false));
        var (differences, extra) = built.Compare(SchemaManifest.FromJson(File.ReadAllText(ManifestPath)));
        Assert.True(differences.Count == 0 && extra == 0, "schema-manifest.json is not what the scripts build (FWORKER_UPDATE_SCHEMA_MANIFEST=1 rewrites it): " + string.Join("; ", differences.Select(d => d.Description)) + $" (extra {extra})");
        // the file the gateway embeds is the file on disk, and what is written is what is read
        Assert.Equal(File.ReadAllText(ManifestPath).Replace("\r\n", "\n"), built.ToJson());
        var embedded = SchemaManifest.Expected;
        Assert.Equal(built.Tables.Keys.Order(StringComparer.Ordinal), embedded.Tables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(built.Procedures.OrderBy(p => p.Key, StringComparer.Ordinal), embedded.Procedures.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void TheManifestKnowsTheTablesAndProceduresOfBothSchemas()
    {
        var m = SchemaManifest.Expected;
        foreach (var t in new[] { "factory.WorkItems", "factory.ItemHistory", "factory.IdempotencyLedger", "factory.IngressDedup", "wolverine.wolverine_incoming_envelopes", "wolverine.wolverine_nodes" })
            Assert.Contains(t, m.Tables.Keys);
        Assert.Equal(5, m.Procedures.Count);
        Assert.Contains("wolverine.uspMarkIncomingOwnership", m.Procedures.Keys);
        Assert.Equal(0, m.Tables["factory.WorkItems"].Columns.Count(c => c.Value.Default != null));
        Assert.Equal("CONVERT[bit],0", m.Tables["factory.DeliveryShadowComparisons"].Columns["Replay"].Default);   // ((0)) of the constraint EF left unnamed, normalised
        Assert.Equal(m.ToJson(), SchemaManifest.FromJson(m.ToJson()).ToJson());
    }

    [SqlFact]
    public async Task TheScriptsAreIdempotent_RunASecondTime_NoErrorAndTheManifestIsUnchanged()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_idem_");
        var before = (await SchemaManifest.ReadAsync(db.ConnectionString)).ToJson();
        // every script again, with no journal
        var result = DeployChanges.To.SqlDatabase(db.ConnectionString)
            .WithScriptsEmbeddedInAssembly(typeof(FactorySchema).Assembly, n => n.EndsWith(".sql", StringComparison.Ordinal))
            .JournalTo(new NullJournal())
            .WithExecutionTimeout(TimeSpan.FromSeconds(120))
            .LogToNowhere()
            .Build().PerformUpgrade();
        Assert.True(result.Successful, result.Error?.Message);
        Assert.Equal(FactorySchema.Scripts.Count, result.Scripts.Count());
        Assert.Equal(before, (await SchemaManifest.ReadAsync(db.ConnectionString)).ToJson());
        // and the next start applies nothing
        var second = await FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions());
        Assert.Empty(second.Applied);
        Assert.True(second.Good);
        Assert.Equal(FactorySchema.Scripts.Count, second.Journaled);
    }

    [SqlFact]
    public async Task VerifyFindsDrift_ADroppedColumnOrIndex_AnAlteredProcedure_ARemovedJournalRow_AndToleratesAnExtraTable()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_drift_");
        Assert.True((await FactorySchema.VerifyAsync(db.ConnectionString)).Good);

        await db.Execute("CREATE TABLE factory.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL PRIMARY KEY)");   // an object of an older or newer image
        await db.Execute("ALTER TABLE factory.WorkItems ADD ExtraColumn int NULL");
        var tolerated = await FactorySchema.VerifyAsync(db.ConnectionString);
        Assert.True(tolerated.Good);
        Assert.Equal(2, tolerated.Extra);

        await db.Execute("ALTER TABLE factory.CatalogCopies DROP COLUMN SourceSha");
        await db.Execute("DROP INDEX IX_WorkItems_Repository_Status ON factory.WorkItems");
        await db.Execute("ALTER TABLE factory.CapacityLedgers ALTER COLUMN StateJson nvarchar(400) NOT NULL");
        await db.Execute("ALTER PROCEDURE wolverine.uspMarkOutgoingOwnership @IDLIST wolverine.EnvelopeIdList READONLY, @owner INT AS UPDATE wolverine.wolverine_outgoing_envelopes SET owner_id = @owner + 1");
        await db.Execute("DROP TABLE factory.MergeTrains");
        await db.Execute("DELETE FROM dbo.SchemaVersions WHERE ScriptName = '010_AddIngressDedup.sql'");
        var bad = await FactorySchema.VerifyAsync(db.ConnectionString);
        Assert.False(bad.Good);
        var d = bad.Differences.Select(x => x.Description).ToList();
        Assert.Contains("missing column factory.CatalogCopies.SourceSha", d);
        Assert.Contains("missing index factory.WorkItems.IX_WorkItems_Repository_Status", d);
        Assert.Contains("column factory.CapacityLedgers.StateJson differs", d);
        Assert.Contains("procedure wolverine.uspMarkOutgoingOwnership differs", d);
        Assert.Contains("missing table factory.MergeTrains", d);
        Assert.Equal(["010_AddIngressDedup.sql"], bad.Pending);
        Assert.DoesNotContain(d, x => x.Contains("__EFMigrationsHistory") || x.Contains("ExtraColumn"));
        // names only, never values
        Assert.DoesNotContain("400", string.Join(" ", d));
    }

    [SqlFact]
    public async Task AJournalEntryThisBuildDoesNotShip_IsAheadAndNotAFailure()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_ahead_");
        await db.Execute("INSERT INTO dbo.SchemaVersions (ScriptName, Applied) VALUES ('999_FromANewerImage.sql', SYSUTCDATETIME())");
        var r = await FactorySchema.VerifyAsync(db.ConnectionString);
        Assert.True(r.Good);
        Assert.Equal(["999_FromANewerImage.sql"], r.Ahead);
        Assert.Equal(FactorySchema.Scripts.Count + 1, r.Journaled);
    }

    [SqlFact]
    public async Task TwoProcessesStartingTogether_BothSucceed_AndEachScriptIsJournaledOnce()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateEmptyAsync("fw_lock_");
        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions { LockTimeout = TimeSpan.FromSeconds(120) })));
        Assert.All(runs, r => Assert.True(r.Good));
        // the scripts were applied by exactly one of them
        Assert.Equal(FactorySchema.Scripts.Count, runs.Sum(r => r.Applied.Count));
        Assert.Equal(FactorySchema.Scripts.Count, await db.Count("SELECT COUNT(*) FROM dbo.SchemaVersions"));
        Assert.Equal(FactorySchema.Scripts.Count, await db.Count("SELECT COUNT(DISTINCT ScriptName) FROM dbo.SchemaVersions"));
    }

    [SqlFact]
    public async Task AFailingScript_IsNotJournaled_AndNamesItself()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateEmptyAsync("fw_fail_");
        // a table in the way of script 006: its CREATE finds the name taken, the guard skips it, and the index of the script then fails on the missing column
        await db.Execute("IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA factory')");
        await db.Execute("CREATE TABLE factory.WorkItems (ItemKey nvarchar(300) NOT NULL PRIMARY KEY)");
        var e = await Assert.ThrowsAsync<SchemaUpgradeException>(() => FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions()));
        Assert.Equal("006_AddWorkItems.sql", e.Script);
        Assert.Equal(5, await db.Count("SELECT COUNT(*) FROM dbo.SchemaVersions"));
        Assert.Equal(0, await db.Count("SELECT COUNT(*) FROM dbo.SchemaVersions WHERE ScriptName LIKE '006%'"));
        // the lock was released: a second run does not wait for it
        await Assert.ThrowsAsync<SchemaUpgradeException>(() => FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions { LockTimeout = TimeSpan.FromSeconds(2) }));
    }

    [SqlFact]
    public async Task TheLogHoldsScriptNamesAndNeverTheConnectionString()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateEmptyAsync("fw_log_");
        var lines = new List<string>();
        await FactorySchema.UpgradeAndVerifyAsync(db.ConnectionString, new SchemaRunOptions { Log = lines.Add });
        Assert.Contains(lines, l => l.Contains("001_CreateFactorySchema.sql"));
        Assert.All(lines, l => { Assert.Contains("\"gateway\":\"schema\"", l); Assert.DoesNotContain("Password", l, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(db.Name, l); });
    }

    [SqlFact]
    public async Task TheDatabaseIsCreatedOnlyWhenTheOptionSaysSo()
    {
        if (!TestDatabase.Available) return;
        var name = "fw_create_" + Guid.NewGuid().ToString("N")[..12];
        var cs = new SqlConnectionStringBuilder(TestDatabase.ServerConnection) { InitialCatalog = name, ConnectTimeout = 5 }.ConnectionString;
        try
        {
            // (another name: SqlClient's pool remembers a failed login for a few seconds)
            var missing = new SqlConnectionStringBuilder(cs) { InitialCatalog = name + "_x" }.ConnectionString;
            await Assert.ThrowsAnyAsync<SqlException>(() => FactorySchema.UpgradeAndVerifyAsync(missing, new SchemaRunOptions()));
            var lines = new List<string>();
            var report = await FactorySchema.UpgradeAndVerifyAsync(cs, new SchemaRunOptions { CreateDatabase = true, Log = lines.Add });
            Assert.True(report.Good);
            Assert.All(lines, l => Assert.DoesNotContain("Password", l, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await using var master = new SqlConnection(new SqlConnectionStringBuilder(TestDatabase.ServerConnection) { InitialCatalog = "master" }.ConnectionString); await master.OpenAsync(); await using var cmd = new SqlCommand($"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", master); await cmd.ExecuteNonQueryAsync(); } catch { }
        }
    }
}
