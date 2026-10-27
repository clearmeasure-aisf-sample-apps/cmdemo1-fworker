// The gateway's database setting (task W2.0, docs/design/fworker-wolverine-implementation.md 4): FACTORY_DB_CONNECTION is the SQL Server connection
// string of the Wolverine host. Unset or blank means no database: the gateway runs exactly as v1 and never opens a connection (the uat and prod gateways
// stay in this state; the kill switch FACTORY_V2_OFF leaves it alone). Nothing reads the string yet: the host is wired in W2.2. The value is a credential
// when it carries a password, so it is never logged, reported or put in an exception message.
sealed class DatabaseOptions
{
    public const string EnvironmentVariable = "FACTORY_DB_CONNECTION";

    DatabaseOptions(string? connection) { ConnectionString = connection; }

    /// <summary>A connection string is set.</summary>
    public bool Configured => ConnectionString != null;

    /// <summary>The connection string; null when none is set.</summary>
    public string? ConnectionString { get; }

    public static DatabaseOptions From(Func<string, string?> env)
    {
        var v = env(EnvironmentVariable)?.Trim();
        return new DatabaseOptions(string.IsNullOrEmpty(v) ? null : v);
    }

    public override string ToString() => Configured ? "DatabaseOptions(configured)" : "DatabaseOptions(none)";
}
