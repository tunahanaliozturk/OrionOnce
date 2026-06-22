namespace Moongazing.OrionOnce.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionOnce.EntityFrameworkCore;

using Xunit;

/// <summary>
/// Guards the duplicate-detection path: the store confirms a real duplicate by re-reading the row,
/// not by sniffing a provider error code, so a <see cref="DbUpdateException"/> that is not a
/// unique-key collision must surface instead of being mistaken for "already in flight". Without this,
/// a constraint fault on the insert would be silently swallowed as a phantom conflict and the caller
/// would never learn the write failed.
/// </summary>
public sealed class SqliteIdempotencyStoreFailureTests
{
    [Fact]
    public async Task a_non_duplicate_save_failure_is_surfaced_not_swallowed_as_a_conflict()
    {
        await using var harness = await SqliteStoreHarness.CreateAsync();

        // Recreate the table with every column the entity maps, so the initial read and the catch's
        // re-read both succeed (the row is genuinely absent), but with Body marked NOT NULL. A fresh
        // claim inserts Body as NULL, so the INSERT fails a NOT NULL constraint: a DbUpdateException
        // that is NOT a unique-key violation. The store's re-query finds no row and must therefore
        // surface the failure rather than report a false in-flight conflict.
        var table = IdempotencyEntryConfiguration.DefaultTableName;
        await using (var context = await harness.Factory.CreateDbContextAsync())
        {
            // EF1002 (interpolated) / EF1003 (concatenated): the only non-literal value is a
            // compile-time-constant table identifier, and SQL identifiers cannot be parameterized, so
            // there is no injection surface to protect against. EF Core 10 split these into two codes,
            // so both are suppressed for this DDL.
#pragma warning disable EF1002, EF1003
            await context.Database.ExecuteSqlRawAsync($"DROP TABLE {table};");
            await context.Database.ExecuteSqlRawAsync(
                $"CREATE TABLE {table} (" +
                "Key TEXT NOT NULL CONSTRAINT PK_entries PRIMARY KEY, " +
                "Fingerprint TEXT NOT NULL, " +
                "IsCompleted INTEGER NOT NULL, " +
                "StatusCode INTEGER NULL, " +
                "ContentType TEXT NULL, " +
                "Body BLOB NOT NULL, " +
                "ExpiresAtTicks INTEGER NOT NULL);");
#pragma warning restore EF1002, EF1003
        }

        await Assert.ThrowsAsync<DbUpdateException>(
            async () => await harness.Store.AcquireAsync("key-1", "fp-1"));
    }
}
