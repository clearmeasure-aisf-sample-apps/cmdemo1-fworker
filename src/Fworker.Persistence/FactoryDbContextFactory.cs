using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fworker.Persistence;

/// <summary>
/// Only for `dotnet ef migrations add` (design time); the host builds its own options from FACTORY_DB_CONNECTION (W2.2). The connection string is
/// never contacted when a migration is scaffolded, so a placeholder is enough.
/// </summary>
public class FactoryDbContextFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("FACTORY_DB_CONNECTION") ?? "Server=localhost;Database=factory-design-time;Integrated Security=true;TrustServerCertificate=True";
        return new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);
    }
}
