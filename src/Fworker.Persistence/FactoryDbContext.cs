using Microsoft.EntityFrameworkCore;

namespace Fworker.Persistence;

/// <summary>
/// The gateway's database (docs/design/fworker-wolverine-implementation.md section 4): item history, correlation map, open-instance index,
/// projection versions, session mailbox and the catalog's last good copy. Saga rows join with the saga (W3). Nothing creates or migrates it yet:
/// the host is wired in W2.2, and only when a connection string is set (FACTORY_DB_CONNECTION).
/// Indexes are chosen for point reads and short ranges by key; none exists to support a scan by status, so nothing needs to poll (serverless-friendly).
/// </summary>
public class FactoryDbContext(DbContextOptions<FactoryDbContext> options) : DbContext(options)
{
    public const string Schema = "factory";

    public DbSet<ItemHistoryEntry> ItemHistory => Set<ItemHistoryEntry>();
    public DbSet<CorrelationEntry> Correlations => Set<CorrelationEntry>();
    public DbSet<OpenInstance> OpenInstances => Set<OpenInstance>();
    public DbSet<ProjectionVersion> ProjectionVersions => Set<ProjectionVersion>();
    public DbSet<SessionMailboxEntry> SessionMailbox => Set<SessionMailboxEntry>();
    public DbSet<CatalogCopy> CatalogCopies => Set<CatalogCopy>();
    public DbSet<MergedItem> MergedItems => Set<MergedItem>();
    public DbSet<DeliveredToEntry> DeliveredTo => Set<DeliveredToEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<ItemHistoryEntry>(e =>
        {
            e.ToTable("ItemHistory");
            e.HasKey(x => x.Id);
            e.Property(x => x.ItemKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Type).HasMaxLength(200).IsRequired();
            e.Property(x => x.SessionId).HasMaxLength(200);
            // "where is item X" and the timeline of one instance
            e.HasIndex(x => new { x.ItemKey, x.Generation, x.Id });
            // flow metrics over a period
            e.HasIndex(x => x.At);
            // a redelivered message writes its row once (rows without a message id are not constrained)
            e.HasIndex(x => new { x.MessageId, x.Kind }).IsUnique().HasFilter("[MessageId] IS NOT NULL");
        });

        b.Entity<CorrelationEntry>(e =>
        {
            e.ToTable("Correlations");
            e.HasKey(x => new { x.Kind, x.Key });
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Key).HasMaxLength(400);
            e.Property(x => x.ItemKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Source).HasMaxLength(64).IsRequired();
            // everything known about one item
            e.HasIndex(x => new { x.ItemKey, x.Generation });
        });

        b.Entity<OpenInstance>(e =>
        {
            e.ToTable("OpenInstances");
            e.HasKey(x => x.ItemKey);
            e.Property(x => x.ItemKey).HasMaxLength(300);
        });

        b.Entity<ProjectionVersion>(e =>
        {
            e.ToTable("ProjectionVersions");
            e.HasKey(x => new { x.ItemKey, x.Target });
            e.Property(x => x.ItemKey).HasMaxLength(300);
            e.Property(x => x.Target).HasMaxLength(64);
        });

        b.Entity<SessionMailboxEntry>(e =>
        {
            e.ToTable("SessionMailbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.SessionId).HasMaxLength(200).IsRequired();
            e.Property(x => x.ItemKey).HasMaxLength(300);
            e.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            // a long poll reads one session's rows in order, after the last id it has seen
            e.HasIndex(x => new { x.SessionId, x.Id });
            // the cleanup deletes by age
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<MergedItem>(e =>
        {
            e.ToTable("MergedItems");
            e.HasKey(x => new { x.ItemKey, x.Commit });
            e.Property(x => x.ItemKey).HasMaxLength(300);
            e.Property(x => x.Commit).HasMaxLength(40);
            e.Property(x => x.Source).HasMaxLength(16).IsRequired();
            e.Property(x => x.GatesJson).IsRequired();
            e.Property(x => x.DependenciesJson).IsRequired();
            // the train maps a commit to its items
            e.HasIndex(x => x.Commit);
        });

        b.Entity<DeliveredToEntry>(e =>
        {
            e.ToTable("DeliveredTo");
            e.HasKey(x => new { x.ItemKey, x.Environment });
            e.Property(x => x.ItemKey).HasMaxLength(300);
            e.Property(x => x.Environment).HasMaxLength(32);
            e.Property(x => x.Release).HasMaxLength(200).IsRequired();
        });

        b.Entity<CatalogCopy>(e =>
        {
            e.ToTable("CatalogCopies");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceSha).HasMaxLength(64);
        });
    }
}
