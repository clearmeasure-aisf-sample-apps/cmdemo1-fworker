using Fworker.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// Script 011 of Fworker.Database builds Wolverine's message store. It is generated, never written by hand: the DDL is what WolverineFx 6.56.0 / Weasel 9.47.0 print for the gateway's own
/// options (WolverineHostService.BuildHostForSchemaExport, the code path of BuildHost). These tests keep the golden file, the script and Wolverine's model together:
/// - TheExportMatchesTheGolden (no database): a package bump or an option that changes the DDL fails here and shows the difference;
/// - TheScriptsBuildWolverinesStorage (SQL Server): a database built by the scripts is exactly what Weasel's full diff expects, which is too slow for the start of the gateway and cheap here.
/// FWORKER_WRITE_WOLVERINE_SQL=1 rewrites the golden and, while 011 is the only script that builds Wolverine's storage, 011 with its header.
/// A future version upgrade adds a delta script instead (docs/design/fworker-database.md, CLAUDE.md "Database").
/// </summary>
public sealed class WolverineStorageScriptTests
{
    const string ScriptName = "011_CreateWolverineMessageStore.sql";
    const string Header = "-- Generated from WolverineFx 6.56.0 / Weasel 9.47.0, schema wolverine, by Gateway.Tests WolverineStorageScriptTests (FWORKER_WRITE_WOLVERINE_SQL=1); do not edit.\n";

    static readonly string Root = RepoRoot();
    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    static string GoldenPath => Path.Combine(Root, "tests", "Gateway.Tests", "golden", "wolverine-storage.sql");
    static string ScriptPath => Path.Combine(Root, "src", "Fworker.Database", "scripts", "Update", ScriptName);

    /// <summary>The DDL of Wolverine's message store for the gateway's options: the solo or the balanced profile, with or without the runtime commands' queue.</summary>
    internal static string Export(bool balanced, bool runtimeCommands)
    {
        using var host = WolverineHostService.BuildHostForSchemaExport(balanced ? WolverineHostOptions.BalancedProfile : new WolverineHostOptions(), runtimeCommands);
        var db = (Weasel.Core.Migrations.IDatabase)host.Services.GetRequiredService<IMessageStore>();
        return db.ToDatabaseScript().Replace("\r\n", "\n");
    }

    static bool Write => Environment.GetEnvironmentVariable("FWORKER_WRITE_WOLVERINE_SQL") == "1";

    [Fact]
    public void TheExportMatchesTheGolden()
    {
        var export = Export(balanced: false, runtimeCommands: false);
        // the gateway's options do not change the DDL: every profile prints the same
        Assert.Equal(export, Export(balanced: true, runtimeCommands: false));
        Assert.Equal(export, Export(balanced: false, runtimeCommands: true));
        Assert.Equal(export, Export(balanced: true, runtimeCommands: true));
        // the script only creates: no DROP of a table, a column or an index
        Assert.DoesNotContain("DROP ", export, StringComparison.OrdinalIgnoreCase);
        if (Write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllText(GoldenPath, export, new System.Text.UTF8Encoding(false));
            if (FactorySchema.Scripts.Count(n => n.Contains("Wolverine", StringComparison.Ordinal)) <= 1)
                File.WriteAllText(ScriptPath, Header + export, new System.Text.UTF8Encoding(false));
        }
        Assert.True(File.Exists(GoldenPath), $"{GoldenPath} is missing: FWORKER_WRITE_WOLVERINE_SQL=1 dotnet test tests/Gateway.Tests --filter TheExportMatchesTheGolden");
        Assert.Equal(File.ReadAllText(GoldenPath).Replace("\r\n", "\n"), export);
    }

    [Fact]
    public void ScriptEleven_IsTheGoldenWithItsHeader()
    {
        var script = File.ReadAllText(ScriptPath).Replace("\r\n", "\n");
        Assert.StartsWith(Header, script);
        Assert.Equal(File.ReadAllText(GoldenPath).Replace("\r\n", "\n"), script[Header.Length..]);
        Assert.Contains(ScriptName, FactorySchema.Scripts);
    }

    [Fact]
    public void TheWolverineSchemaIsTheLiteralOfTheScripts()
    {
        Assert.Equal("wolverine", WolverineHostOptions.Schema);
        Assert.Equal(SchemaNames.Wolverine, WolverineHostOptions.Schema);
    }

    [Fact]
    public async Task TheScriptsBuildWolverinesStorage()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_wstore_");
        using var host = WolverineHostService.BuildHostForSchemaExport(new WolverineHostOptions(), runtimeCommands: false, db.ConnectionString);
        var store = (Weasel.Core.Migrations.IDatabase)host.Services.GetRequiredService<IMessageStore>();
        // Weasel's full diff of its model against the catalog: it throws when anything is missing or different
        await store.AssertDatabaseMatchesConfigurationAsync();
    }

    /// <summary>Generates the delta script of a Wolverine upgrade against a database built by the current scripts: FWORKER_WRITE_WOLVERINE_DELTA=&lt;file&gt;. Skipped unless the variable is set.</summary>
    [Fact]
    public async Task WriteWolverineDelta()
    {
        var file = Environment.GetEnvironmentVariable("FWORKER_WRITE_WOLVERINE_DELTA");
        if (string.IsNullOrWhiteSpace(file) || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_wdelta_");
        using var host = WolverineHostService.BuildHostForSchemaExport(new WolverineHostOptions(), runtimeCommands: false, db.ConnectionString);
        var store = (Weasel.Core.Migrations.IDatabase)host.Services.GetRequiredService<IMessageStore>();
        await store.WriteMigrationFileAsync(file);
    }
}
