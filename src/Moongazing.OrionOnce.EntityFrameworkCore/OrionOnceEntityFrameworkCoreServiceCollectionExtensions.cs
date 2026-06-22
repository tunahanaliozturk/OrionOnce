namespace Moongazing.OrionOnce.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// Registration helpers that wire the EF Core idempotency store into the OrionOnce services.
/// </summary>
public static class OrionOnceEntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OrionOnceDbContext"/> through an <see cref="IDbContextFactory{TContext}"/>
    /// and use an <see cref="EntityFrameworkCoreIdempotencyStore{TContext}"/> over it as the
    /// application's <see cref="IIdempotencyStore"/>. Call this alongside
    /// <c>AddOrionOnce(...)</c>; because that method only adds the in-memory store when none is
    /// registered, this registration takes precedence regardless of call order. The retention window
    /// is taken from the registered <see cref="IdempotencyOptions"/> unless one is supplied here.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureDbContext">
    /// Configures the context's provider and connection (for example
    /// <c>o =&gt; o.UseNpgsql(connectionString)</c>). The caller chooses the provider; this package
    /// references only EF Core's relational surface.
    /// </param>
    /// <param name="retention">
    /// The retention window for entries. When null, the window from the registered
    /// <see cref="IdempotencyOptions"/> is used (defaulting to that type's own default when OrionOnce
    /// options were not configured).
    /// </param>
    public static IServiceCollection AddOrionOnceEntityFrameworkCoreStore(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        TimeSpan? retention = null) =>
        services.AddOrionOnceEntityFrameworkCoreStore<OrionOnceDbContext>(configureDbContext, retention);

    /// <summary>
    /// Register <typeparamref name="TContext"/> through an <see cref="IDbContextFactory{TContext}"/>
    /// and use an <see cref="EntityFrameworkCoreIdempotencyStore{TContext}"/> over it as the
    /// application's <see cref="IIdempotencyStore"/>. Use this overload when the idempotency entry is
    /// mapped into your own context (which must apply <see cref="IdempotencyEntryConfiguration"/>)
    /// rather than the bundled <see cref="OrionOnceDbContext"/>.
    /// </summary>
    /// <typeparam name="TContext">The context type that maps <see cref="IdempotencyEntry"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureDbContext">Configures the context's provider and connection.</param>
    /// <param name="retention">
    /// The retention window for entries. When null, the window from the registered
    /// <see cref="IdempotencyOptions"/> is used.
    /// </param>
    public static IServiceCollection AddOrionOnceEntityFrameworkCoreStore<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        TimeSpan? retention = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        services.AddDbContextFactory<TContext>(configureDbContext);

        // Use a plain registration (not TryAdd): calling this method is an explicit choice to back
        // OrionOnce with the durable store, so it replaces the in-memory default that AddOrionOnce
        // adds with TryAdd. Resolve the retention lazily so it can read the IdempotencyOptions that
        // AddOrionOnce registers, whichever order the two calls run in.
        services.AddSingleton<IIdempotencyStore>(sp =>
        {
            var ttl = retention
                ?? sp.GetService<IdempotencyOptions>()?.Retention
                ?? new IdempotencyOptions().Retention;

            var factory = sp.GetRequiredService<IDbContextFactory<TContext>>();
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;

            return new EntityFrameworkCoreIdempotencyStore<TContext>(factory, ttl, timeProvider);
        });

        return services;
    }
}
