namespace Fworker.Database;

/// <summary>What the database differs in from what the scripts build: object names only, never values.</summary>
public sealed record SchemaDifference(string Description);

/// <summary>Options of one run. The log receives JSON lines and never the connection string.</summary>
public sealed class SchemaRunOptions
{
    /// <summary>Creates the database when it is missing (EnsureDatabase). Tests only: the identity of a deployed gateway cannot CREATE DATABASE, and bicep creates the database.</summary>
    public bool CreateDatabase { get; init; }
    /// <summary>The command timeout of every script.</summary>
    public TimeSpan ScriptTimeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>How long a second process waits for the application lock of the upgrade.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public Action<string>? Log { get; init; }
}

/// <summary>The result of an upgrade and/or a verification.</summary>
public sealed class SchemaReport
{
    /// <summary>The scripts this call ran.</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];
    /// <summary>The number of entries in the journal.</summary>
    public int Journaled { get; init; }
    /// <summary>The scripts this build ships that the journal does not hold.</summary>
    public IReadOnlyList<string> Pending { get; init; } = [];
    /// <summary>Journal entries this build does not ship (an older image started after a newer one): reported, not a failure, because scripts are expand-only.</summary>
    public IReadOnlyList<string> Ahead { get; init; } = [];
    /// <summary>What is missing or different against the manifest of this build.</summary>
    public IReadOnlyList<SchemaDifference> Differences { get; init; } = [];
    /// <summary>Tables, columns, indexes and procedures the manifest does not know (tolerated: __EFMigrationsHistory, objects of a newer image).</summary>
    public int Extra { get; init; }
    /// <summary>The catalog was compared with the manifest (an upgrade alone does not do it).</summary>
    public bool Verified { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>The journal is complete and the catalog matches the manifest.</summary>
    public bool Good => Verified && Pending.Count == 0 && Differences.Count == 0;
}

/// <summary>A script failed. Carries the script name, which is not a secret; the inner exception may carry the server's text and is not logged.</summary>
public sealed class SchemaUpgradeException(string? script, Exception? inner) : Exception("the schema upgrade failed" + (script == null ? "" : " in " + script), inner)
{
    public string? Script { get; } = script;
}

/// <summary>Another process held the application lock for the whole wait.</summary>
public sealed class SchemaLockTimeoutException() : Exception("the schema upgrade lock was not free in time");

/// <summary>The scripts ran and the catalog still differs from the manifest: it does not heal on retry.</summary>
public sealed class SchemaNotGoodException(SchemaReport report) : Exception("the schema is not what the scripts build")
{
    public SchemaReport Report { get; } = report;
}
