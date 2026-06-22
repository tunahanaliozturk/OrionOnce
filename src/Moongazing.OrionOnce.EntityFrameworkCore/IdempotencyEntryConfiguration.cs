namespace Moongazing.OrionOnce.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Maps <see cref="IdempotencyEntry"/> for relational providers. Apply it from a host
/// <see cref="DbContext.OnModelCreating"/> (via <c>builder.ApplyConfiguration(new IdempotencyEntryConfiguration())</c>)
/// to fold the idempotency table into an existing context instead of using
/// <see cref="OrionOnceDbContext"/>. The mapping keys the table by <see cref="IdempotencyEntry.Key"/>,
/// which supplies the unique constraint the store relies on, and indexes the expiry column so the
/// sweep can be served from the index.
/// </summary>
public sealed class IdempotencyEntryConfiguration : IEntityTypeConfiguration<IdempotencyEntry>
{
    /// <summary>The default table name used for the idempotency entries.</summary>
    public const string DefaultTableName = "OrionOnceIdempotencyEntries";

    private readonly string tableName;

    /// <summary>Configure the entity against the default table name.</summary>
    public IdempotencyEntryConfiguration()
        : this(DefaultTableName)
    {
    }

    /// <summary>Configure the entity against a caller-supplied table name.</summary>
    /// <param name="tableName">The table the entries are stored in.</param>
    public IdempotencyEntryConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        this.tableName = tableName;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdempotencyEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);

        // The key is the primary key: this is the unique constraint that makes the concurrent
        // claim atomic (insert-wins). A length cap keeps the column index-friendly on providers
        // that will not index an unbounded string.
        builder.HasKey(e => e.Key);
        builder.Property(e => e.Key).HasMaxLength(256);

        builder.Property(e => e.Fingerprint).HasMaxLength(256).IsRequired();
        builder.Property(e => e.IsCompleted).IsRequired();
        builder.Property(e => e.ExpiresAtTicks).IsRequired();

        // Serves the expiry comparison in AcquireAsync and the bulk delete in SweepAsync.
        builder.HasIndex(e => e.ExpiresAtTicks);
    }
}
