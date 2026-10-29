// The gateway's database setting (task W2.0, docs/design/fworker-wolverine-implementation.md 4): FACTORY_DB_CONNECTION is the SQL Server connection
// string of the Wolverine host. Unset or blank means no database: the gateway runs exactly as v1 and never opens a connection (the uat and prod gateways
// stay in this state; the kill switch FACTORY_V2_OFF leaves it alone). The Wolverine host (gateway/Composition/WolverineHostService.cs, W2.2) is registered only when it is set. The value is a credential
// when it carries a password, so it is never logged, reported or put in an exception message.
sealed class DatabaseOptions
{
    public const string EnvironmentVariable = "FACTORY_DB_CONNECTION";

    /// <summary>FACTORY_DB_CREATE=1 lets the gateway create the database when it is missing. Only tests/scenarios/run.ps1 -Engine v2 sets it; it is never set in config or overrides:
    /// the identity of a deployed gateway cannot CREATE DATABASE (bicep creates the database).</summary>
    public const string CreateEnvironmentVariable = "FACTORY_DB_CREATE";

    DatabaseOptions(string? connection, bool createIfMissing) { ConnectionString = connection; CreateIfMissing = createIfMissing; }

    /// <summary>The database is created when it is missing (tests only, see <see cref="CreateEnvironmentVariable"/>).</summary>
    public bool CreateIfMissing { get; }

    /// <summary>A connection string is set.</summary>
    public bool Configured => ConnectionString != null;

    /// <summary>The connection string; null when none is set.</summary>
    public string? ConnectionString { get; }

    public static DatabaseOptions From(Func<string, string?> env)
    {
        var v = env(EnvironmentVariable)?.Trim();
        return new DatabaseOptions(string.IsNullOrEmpty(v) ? null : v, env(CreateEnvironmentVariable)?.Trim() == "1");
    }

    public override string ToString() => Configured ? "DatabaseOptions(configured)" : "DatabaseOptions(none)";
}
