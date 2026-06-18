namespace Moongazing.OrionOnce.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.AspNetCore;
using Moongazing.OrionOnce.Diagnostics;
using Moongazing.OrionOnce.Storage;

using Xunit;

public sealed class OrionOnceRegistrationExtraTests
{
    [Fact]
    public void AddOrionOnce_rejects_a_null_service_collection()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OrionOnceServiceCollectionExtensions.AddOrionOnce(null!));
    }

    [Fact]
    public void AddOrionOnce_returns_the_same_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var result = services.AddOrionOnce();

        Assert.Same(services, result);
    }

    [Fact]
    public void AddOrionOnce_registers_diagnostics_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddOrionOnce();

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IdempotencyDiagnostics>();
        var b = provider.GetRequiredService<IdempotencyDiagnostics>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddOrionOnce_registers_the_store_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddOrionOnce();

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IIdempotencyStore>();
        var b = provider.GetRequiredService<IIdempotencyStore>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddOrionOnce_registers_the_options_instance_it_validated()
    {
        var services = new ServiceCollection();
        services.AddOrionOnce(o => o.MaxBodyBytes = 4096);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IdempotencyOptions>();

        Assert.Equal(4096, options.MaxBodyBytes);
    }

    [Fact]
    public void AddOrionOnce_keeps_a_pre_registered_options_instance()
    {
        var custom = new IdempotencyOptions { HeaderName = "X-Pre" };
        var services = new ServiceCollection();
        services.AddSingleton(custom);
        services.AddOrionOnce(o => o.HeaderName = "X-Late");

        using var provider = services.BuildServiceProvider();
        // TryAddSingleton must not overwrite the already-registered options.
        Assert.Same(custom, provider.GetRequiredService<IdempotencyOptions>());
        Assert.Equal("X-Pre", provider.GetRequiredService<IdempotencyOptions>().HeaderName);
    }

    [Fact]
    public void AddOrionOnce_without_configuration_uses_the_defaults()
    {
        var services = new ServiceCollection();
        services.AddOrionOnce();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IdempotencyOptions>();

        Assert.Equal("Idempotency-Key", options.HeaderName);
        Assert.Equal(TimeSpan.FromHours(24), options.Retention);
    }

    [Fact]
    public void UseOrionOnce_rejects_a_null_application_builder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OrionOnceServiceCollectionExtensions.UseOrionOnce(null!));
    }

    [Fact]
    public void UseOrionOnce_returns_the_same_builder_for_chaining()
    {
        var services = new ServiceCollection();
        services.AddOrionOnce();
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        var result = app.UseOrionOnce();

        Assert.Same(app, result);
    }

    [Fact]
    public async Task UseOrionOnce_wires_the_middleware_into_the_pipeline()
    {
        // Build a minimal pipeline and confirm a guarded duplicate replays through the registered services.
        var services = new ServiceCollection();
        services.AddOrionOnce();
        using var provider = services.BuildServiceProvider();

        var handlerCalls = 0;
        var app = new ApplicationBuilder(provider);
        app.UseOrionOnce();
        app.Run(async ctx =>
        {
            handlerCalls++;
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok");
        });
        var pipeline = app.Build();

        await InvokeAsync(pipeline, "k1");
        var secondReplayed = await InvokeAsync(pipeline, "k1");

        Assert.Equal(1, handlerCalls);
        Assert.True(secondReplayed);

        static async Task<bool> InvokeAsync(RequestDelegate pipeline, string key)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/orders";
            context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"));
            context.Request.Headers["Idempotency-Key"] = key;
            context.Response.Body = new MemoryStream();

            await pipeline(context);

            return context.Response.Headers.TryGetValue(IdempotencyMiddleware.ReplayedHeader, out var v)
                && v.ToString() == "true";
        }
    }
}
