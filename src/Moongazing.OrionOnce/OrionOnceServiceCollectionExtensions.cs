namespace Moongazing.OrionOnce;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Moongazing.OrionOnce.AspNetCore;
using Moongazing.OrionOnce.Diagnostics;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// Registration and pipeline helpers for OrionOnce.
/// </summary>
public static class OrionOnceServiceCollectionExtensions
{
    /// <summary>
    /// Register the idempotency options, diagnostics, and an <see cref="InMemoryIdempotencyStore"/>.
    /// To use a shared backing store, register your own <see cref="IIdempotencyStore"/> before or
    /// after this call and it will take precedence (this method only adds the in-memory store if
    /// none is registered).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    public static IServiceCollection AddOrionOnce(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new IdempotencyOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<IdempotencyDiagnostics>();
        services.TryAddSingleton<IIdempotencyStore>(_ => new InMemoryIdempotencyStore(options.Retention));

        return services;
    }

    /// <summary>
    /// Add the idempotency middleware to the request pipeline. Place it after routing and before
    /// the endpoints whose retries you want to deduplicate.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseOrionOnce(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
