namespace Moongazing.OrionOnce.EntityFrameworkCore.Tests.Sqlite;

using Moongazing.OrionOnce.EntityFrameworkCore.Tests.Conformance;

/// <summary>
/// Runs the full <see cref="IdempotencyStoreConformanceTests"/> contract against the EF Core store
/// over a real file-based SQLite database, so the implementation is held to the same behavior as the
/// in-memory store under genuine relational constraints and transactions.
/// </summary>
public sealed class SqliteIdempotencyStoreConformanceTests : IdempotencyStoreConformanceTests
{
    protected override async Task<IStoreHarness> CreateHarnessAsync() =>
        await SqliteStoreHarness.CreateAsync();
}
