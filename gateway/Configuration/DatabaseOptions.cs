using Microsoft.Data.SqlClient;

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

    DatabaseOptions(string? connection, bool createIfMissing, bool malformed = false) { ConnectionString = connection; CreateIfMissing = createIfMissing; Malformed = malformed; }

    /// <summary>The variable is set and is not a connection string SqlClient can parse (W9.10o). It is then treated as not configured: the gateway runs as v1 alone, with no host and no database call, instead of
    /// throwing at the first use of the string; /api/v1/status says so (never the value, which can carry a password).</summary>
    public bool Malformed { get; }

    /// <summary>The database is created when it is missing (tests only, see <see cref="CreateEnvironmentVariable"/>).</summary>
    public bool CreateIfMissing { get; }

    /// <summary>A connection string is set.</summary>
    public bool Configured => ConnectionString != null;

    /// <summary>The connection string; null when none is set.</summary>
    public string? ConnectionString { get; }

    public static DatabaseOptions From(Func<string, string?> env)
    {
        var v = env(EnvironmentVariable)?.Trim();
        var create = env(CreateEnvironmentVariable)?.Trim() == "1";
        if (string.IsNullOrEmpty(v)) return new DatabaseOptions(null, create);
        return Parses(v) ? new DatabaseOptions(v, create) : new DatabaseOptions(null, create, malformed: true);
    }

    // SqlConnectionStringBuilder throws ArgumentException for a string that is not "key=value;..." pairs or names an unknown keyword; a string that parses but names no server cannot be opened either
    static bool Parses(string value)
    {
        try { return new SqlConnectionStringBuilder(value).DataSource.Length > 0; }
        catch (Exception) { return false; }
    }

    public override string ToString() => Configured ? "DatabaseOptions(configured)" : Malformed ? "DatabaseOptions(malformed)" : "DatabaseOptions(none)";
}
