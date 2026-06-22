namespace Moongazing.OrionOnce.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// A ready-made <see cref="DbContext"/> holding only the OrionOnce idempotency table, for
/// applications that keep the idempotency store in its own context and database. Applications that
/// would rather fold the table into an existing context can skip this type and apply
/// <see cref="IdempotencyEntryConfiguration"/> from their own context instead, then point the store
/// at that context.
/// </summary>
public class OrionOnceDbContext : DbContext
{
    /// <summary>Create the context with externally supplied options (provider, connection, ...).</summary>
    /// <param name="options">The options that select the provider and connection.</param>
    public OrionOnceDbContext(DbContextOptions<OrionOnceDbContext> options)
        : base(options)
    {
    }

    /// <summary>The persisted idempotency entries.</summary>
    public DbSet<IdempotencyEntry> IdempotencyEntries => Set<IdempotencyEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new IdempotencyEntryConfiguration());
    }
}
