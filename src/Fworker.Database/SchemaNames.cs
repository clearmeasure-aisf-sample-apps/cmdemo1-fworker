namespace Fworker.Database;

/// <summary>The names the scripts and the checks share. The scripts write the schemas as literals, so none of these can be configured.</summary>
public static class SchemaNames
{
    /// <summary>The gateway's own tables (what EF Core maps).</summary>
    public const string Factory = "factory";
    /// <summary>Wolverine's message store (inbox, outbox, nodes, dead letters), built by script 011 and never by Wolverine.</summary>
    public const string Wolverine = "wolverine";
    /// <summary>The journal of DbUp: which scripts have run. The fworker database is dedicated, so dbo is free.</summary>
    public const string JournalSchema = "dbo";
    public const string JournalTable = "SchemaVersions";
    /// <summary>The application lock that serialises the upgrade: two gateway processes overlap at every deploy (lease hand-over).</summary>
    public const string LockResource = "fworker-schema-upgrade";
}
