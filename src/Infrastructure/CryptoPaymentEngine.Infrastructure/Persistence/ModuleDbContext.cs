using CryptoPaymentEngine.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Infrastructure.Persistence;

/// <summary>
/// Base for every module's DbContext. Each module owns exactly one schema, its own migrations
/// history table, and its own outbox table — so a module can later be lifted into its own
/// database/service without untangling shared tables (§4.5, §7.5).
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    private static readonly OutboxSaveChangesInterceptor OutboxInterceptor = new();

    /// <summary>The SQL Server schema this module owns. Must be unique per module.</summary>
    public abstract string Schema { get; }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Attached here rather than at each registration site: an integration event that skips the
    /// outbox is an event that can be lost after the business change commits. A module must not be
    /// able to opt out by forgetting a line of DI.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(OutboxInterceptor);
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
