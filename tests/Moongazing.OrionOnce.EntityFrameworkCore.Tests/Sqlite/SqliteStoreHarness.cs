namespace Moongazing.OrionOnce.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionOnce.EntityFrameworkCore;
using Moongazing.OrionOnce.EntityFrameworkCore.Tests.Conformance;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// A conformance harness backed by a real, file-based SQLite database. A file (not EF's in-memory
/// provider, and not shared-cache memory) is used so the store runs against genuine relational
/// constraints, transactions, and the actual unique-key enforcement the atomic claim depends on.
/// Each harness owns a unique database file under the temp directory and deletes it on disposal.
/// </summary>
internal sealed class SqliteStoreHarness : IdempotencyStoreConformanceTests.IStoreHarness
{
    private readonly string databasePath;
    private readonly MutableTimeProvider timeProvider;
    private readonly TestDbContextFactory factory;

    private SqliteStoreHarness(string databasePath, MutableTimeProvider timeProvider, TestDbContextFactory factory)
    {
        this.databasePath = databasePath;
        this.timeProvider = timeProvider;
        this.factory = factory;
        Store = new EntityFrameworkCoreIdempotencyStore<OrionOnceDbContext>(
            factory,
            IdempotencyStoreConformanceTests.Ttl,
            timeProvider);
    }

    public IIdempotencyStore Store { get; }

    /// <summary>The context factory the store draws from, exposed for the concurrency test.</summary>
    public IDbContextFactory<OrionOnceDbContext> Factory => factory;

    public void Advance(TimeSpan by) => timeProvider.Advance(by);

    /// <summary>Create the harness and its schema. The returned store is ready to use.</summary>
    public static async Task<SqliteStoreHarness> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"oriononce-efcore-{Guid.NewGuid():N}.db");

        // Busy timeout lets a writer wait for a concurrent writer's lock instead of failing fast with
        // SQLITE_BUSY, which keeps the parallel-acquire test honest (one row wins the key) rather than
        // flaky. The store still sees real constraint enforcement; only lock contention is smoothed.
        var connectionString = $"Data Source={databasePath};Default Timeout=30";

        var options = new DbContextOptionsBuilder<OrionOnceDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var factory = new TestDbContextFactory(options);

        await using (var context = await factory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        return new SqliteStoreHarness(databasePath, new MutableTimeProvider(SeedTime), factory);
    }

    public ValueTask DisposeAsync()
    {
        // Drop any pooled connections to the file before deleting it; SQLite keeps a handle open while
        // a connection is pooled, which would block the delete on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
        catch (IOException)
        {
            // Best effort: a stray handle on a CI agent should not fail an otherwise green test. The
            // temp file is named per test and will be reclaimed with the temp directory.
        }

        return ValueTask.CompletedTask;
    }

    // A fixed, far-from-epoch seed so 'now' is well clear of DateTimeOffset.MinValue and the TTL
    // arithmetic in the store cannot underflow when the test rewinds nothing and only advances.
    private static DateTimeOffset SeedTime { get; } =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
