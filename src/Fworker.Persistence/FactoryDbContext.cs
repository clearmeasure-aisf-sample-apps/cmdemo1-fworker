using Microsoft.EntityFrameworkCore;

namespace Fworker.Persistence;

/// <summary>
/// The gateway's database (W2.0: a stub; docs/design/fworker-wolverine-implementation.md section 4). W2.1 adds the tables (item history,
/// correlation map, open-instance index, projection versions, session mailbox, the catalog's last good copy) and their migrations.
/// Nothing creates it yet: the host is wired in W2.2, and only when a connection string is set (FACTORY_DB_CONNECTION).
/// </summary>
public class FactoryDbContext(DbContextOptions<FactoryDbContext> options) : DbContext(options)
{
}
