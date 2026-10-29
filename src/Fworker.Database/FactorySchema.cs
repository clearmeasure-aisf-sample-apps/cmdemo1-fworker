using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DbUp;
using Microsoft.Data.SqlClient;

namespace Fworker.Database;

/// <summary>
/// The schema of the gateway's database, built by DbUp from the plain .sql files of scripts/Update (docs/design/fworker-database.md), the way cmdemo1-workorders
/// does it: numbered files in ordinal order, the journal dbo.SchemaVersions, no DbUp transaction (each script owns its own). Different from workorders: the scripts are embedded
/// (the gateway is one container), every script is idempotent (so they run as no-ops on a database the EF migrations and Wolverine built), and a process-wide
/// application lock serialises the upgrade (two gateway processes overlap at every deploy). The gateway applies the scripts at start, before Wolverine, and then checks that
/// the catalog is what the scripts build.
/// </summary>
public static class FactorySchema
{
    static readonly Regex Number = new(@"^(\d+)_", RegexOptions.CultureInvariant);

    /// <summary>The embedded script names, in ordinal order.</summary>
    public static IReadOnlyList<string> Scripts { get; } =
        typeof(FactorySchema).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>The highest leading number of the scripts (11 for 011_...).</summary>
    public static int LatestVersion => Scripts.Count == 0 ? 0 : Scripts.Max(n => int.Parse(Number.Match(n).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Applies the pending scripts under the application lock. Does not compare the catalog.</summary>
    public static async Task<SchemaReport> UpgradeAsync(string connectionString, SchemaRunOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        return await Locked(connectionString, options, ct, async () =>
        {
            var applied = await Task.Run(() => Upgrade(connectionString, options, ct), ct);
            var (journaled, pending, ahead) = await ReadJournalAsync(connectionString, ct);
            return new SchemaReport { Applied = applied, Journaled = journaled, Pending = pending, Ahead = ahead, Duration = sw.Elapsed };
        });
    }

    /// <summary>Reads the journal and the catalog and compares them with the scripts and the manifest of this build. Never writes.</summary>
    public static async Task<SchemaReport> VerifyAsync(string connectionString, CancellationToken ct = default) => await VerifyAsync(connectionString, [], Stopwatch.StartNew(), ct);

    static async Task<SchemaReport> VerifyAsync(string connectionString, IReadOnlyList<string> applied, Stopwatch sw, CancellationToken ct)
    {
        var (journaled, pending, ahead) = await ReadJournalAsync(connectionString, ct);
        var actual = await SchemaManifest.ReadAsync(connectionString, ct);
        var (differences, extra) = actual.Compare(SchemaManifest.Expected);
        return new SchemaReport { Applied = applied, Journaled = journaled, Pending = pending, Ahead = ahead, Differences = differences, Extra = extra, Verified = true, Duration = sw.Elapsed };
    }

    /// <summary>The start of the gateway: lock, upgrade, verify, unlock. The report says whether the schema is good; the caller decides what a bad one means.</summary>
    public static async Task<SchemaReport> UpgradeAndVerifyAsync(string connectionString, SchemaRunOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        return await Locked(connectionString, options, ct, async () =>
        {
            var applied = await Task.Run(() => Upgrade(connectionString, options, ct), ct);
            return await VerifyAsync(connectionString, applied, sw, ct);
        });
    }

    // the application lock sits on a connection of its own for the whole upgrade; disposing the connection releases it
    static async Task<T> Locked<T>(string connectionString, SchemaRunOptions options, CancellationToken ct, Func<Task<T>> run)
    {
        // (the explicit log: the default of EnsureDatabase writes to the console, and a master connection string carries the password)
        if (options.CreateDatabase)
            await Task.Run(() => EnsureDatabase.For.SqlDatabase(connectionString, new QuietLog(options.Log), (int)options.ScriptTimeout.TotalSeconds, DbUp.SqlServer.AzureDatabaseEdition.None, null, null, false), ct);
        await using var lockConnection = new SqlConnection(connectionString);
        await lockConnection.OpenAsync(ct);
        await using (var cmd = new SqlCommand("sp_getapplock", lockConnection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 })
        {
            cmd.Parameters.AddWithValue("@Resource", SchemaNames.LockResource);
            cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", (int)options.LockTimeout.TotalMilliseconds);
            var result = cmd.Parameters.Add("@result", SqlDbType.Int); result.Direction = ParameterDirection.ReturnValue;
            await cmd.ExecuteNonQueryAsync(ct);
            if ((int)result.Value < 0) throw new SchemaLockTimeoutException();
        }
        try { return await run(); }
        finally
        {
            try
            {
                await using var release = new SqlCommand("sp_releaseapplock", lockConnection) { CommandType = CommandType.StoredProcedure };
                release.Parameters.AddWithValue("@Resource", SchemaNames.LockResource);
                release.Parameters.AddWithValue("@LockOwner", "Session");
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { /* the lock goes with the connection */ }
        }
    }

    // DbUp is synchronous: this runs on a worker thread
    static List<string> Upgrade(string connectionString, SchemaRunOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var engine = DeployChanges.To.SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(FactorySchema).Assembly, n => n.EndsWith(".sql", StringComparison.Ordinal))
            .JournalToSqlTable(SchemaNames.JournalSchema, SchemaNames.JournalTable)
            .WithExecutionTimeout(options.ScriptTimeout)
            .LogTo(new QuietLog(options.Log))
            .Build();
        var result = engine.PerformUpgrade();
        if (!result.Successful) throw new SchemaUpgradeException(result.ErrorScript?.Name, result.Error);
        return result.Scripts.Select(s => s.Name).ToList();
    }

    static async Task<(int Journaled, List<string> Pending, List<string> Ahead)> ReadJournalAsync(string connectionString, CancellationToken ct)
    {
        var names = new List<string>();
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using (var cmd = new SqlCommand($"IF OBJECT_ID(N'{SchemaNames.JournalSchema}.{SchemaNames.JournalTable}', N'U') IS NOT NULL SELECT ScriptName FROM {SchemaNames.JournalSchema}.{SchemaNames.JournalTable}", c) { CommandTimeout = 60 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        var pending = Scripts.Where(s => !names.Contains(s, StringComparer.Ordinal)).ToList();
        var ahead = names.Where(n => !Scripts.Contains(n, StringComparer.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return (names.Count, pending, ahead);
    }
}
