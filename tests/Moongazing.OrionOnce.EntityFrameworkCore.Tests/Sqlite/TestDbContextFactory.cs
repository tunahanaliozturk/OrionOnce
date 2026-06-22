namespace Moongazing.OrionOnce.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionOnce.EntityFrameworkCore;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> that builds a fresh
/// <see cref="OrionOnceDbContext"/> from fixed options on each call. The store creates one context
/// per operation, so handing out a new instance every time matches how it is used in production and
/// keeps concurrent operations on independent contexts.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<OrionOnceDbContext>
{
    private readonly DbContextOptions<OrionOnceDbContext> options;

    public TestDbContextFactory(DbContextOptions<OrionOnceDbContext> options) => this.options = options;

    public OrionOnceDbContext CreateDbContext() => new(options);

    // The async creation path is an extension method on IDbContextFactory<T> in EF Core; declaring it
    // directly here lets the harness call it on the concrete factory type as well as through the
    // interface, and there is no asynchronous setup to await for an in-process options object.
    public Task<OrionOnceDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
