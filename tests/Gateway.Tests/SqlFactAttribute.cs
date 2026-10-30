using Xunit;

namespace Gateway.Tests;

/// <summary>
/// A test that needs the SQL Server of FACTORY_TEST_SQL (the service container of the CI jobs). Without it the test is reported as skipped, with the reason, where it used to return at once and be counted
/// as passed: a run on a machine with no SQL Server then says how many tests it did not run (W9.10d, #552). The early return in the body stays as it was (it also covers the platforms with no stub pwsh).
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_TEST_SQL"))) Skip = "FACTORY_TEST_SQL is not set: no SQL Server to run against";
    }
}
