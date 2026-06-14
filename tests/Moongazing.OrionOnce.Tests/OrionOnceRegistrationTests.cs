namespace Moongazing.OrionOnce.Tests;

using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

using Xunit;

public sealed class OrionOnceRegistrationTests
{
    [Fact]
    public void AddOrionOnce_registers_the_in_memory_store_by_default()
    {
        var services = new ServiceCollection();
        services.AddOrionOnce();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryIdempotencyStore>(provider.GetService<IIdempotencyStore>());
    }

    [Fact]
    public void AddOrionOnce_honours_configured_options()
    {
        var services = new ServiceCollection();
        services.AddOrionOnce(o => o.HeaderName = "X-Idem");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("X-Idem", provider.GetRequiredService<IdempotencyOptions>().HeaderName);
    }

    [Fact]
    public void AddOrionOnce_keeps_a_custom_store_registered_first()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIdempotencyStore>(new InMemoryIdempotencyStore(TimeSpan.FromMinutes(1)));
        services.AddOrionOnce();

        using var provider = services.BuildServiceProvider();
        // TryAdd must not replace the pre-registered store.
        Assert.Single(services, d => d.ServiceType == typeof(IIdempotencyStore));
    }

    [Fact]
    public void AddOrionOnce_rejects_invalid_options_eagerly()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddOrionOnce(o => o.Retention = TimeSpan.Zero));
    }
}
