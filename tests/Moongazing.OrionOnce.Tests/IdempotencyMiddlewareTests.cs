namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Microsoft.AspNetCore.Http;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.AspNetCore;
using Moongazing.OrionOnce.Diagnostics;
using Moongazing.OrionOnce.Storage;

using Xunit;

// Emits to the shared Moongazing.OrionOnce meter via IdempotencyDiagnostics; serialize against
// IdempotencyDiagnosticsTests so its MeterListener does not observe these measurements.
[Collection(nameof(MeterSerial))]
public sealed class IdempotencyMiddlewareTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(IdempotencyOptions? options = null)
        {
            Options = options ?? new IdempotencyOptions();
            Store = new InMemoryIdempotencyStore(Options.Retention);
        }

        public IdempotencyOptions Options { get; }

        public InMemoryIdempotencyStore Store { get; }

        public IdempotencyDiagnostics Diagnostics { get; } = new();

        public int HandlerCalls { get; private set; }

        public async Task<(int Status, string Body, bool Replayed)> SendAsync(
            string method,
            string path,
            string? key,
            string requestBody,
            Func<HttpContext, Task>? handler = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));
            if (key is not null)
            {
                context.Request.Headers[Options.HeaderName] = key;
            }

            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            RequestDelegate next = async ctx =>
            {
                HandlerCalls++;
                if (handler is not null)
                {
                    await handler(ctx);
                }
                else
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync("{\"handled\":true}");
                }
            };

            var middleware = new IdempotencyMiddleware(next);
            await middleware.InvokeAsync(context, Store, Options, Diagnostics);

            var replayed = context.Response.Headers.TryGetValue(IdempotencyMiddleware.ReplayedHeader, out var v)
                && v.ToString() == "true";
            return (context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()), replayed);
        }

        public void Dispose() => Diagnostics.Dispose();
    }

    [Fact]
    public async Task A_non_guarded_method_passes_through()
    {
        using var h = new Harness();
        var (status, _, replayed) = await h.SendAsync("GET", "/orders", key: "k1", requestBody: "");

        Assert.Equal(200, status);
        Assert.Equal(1, h.HandlerCalls);
        Assert.False(replayed);
    }

    [Fact]
    public async Task A_request_without_a_key_bypasses_by_default()
    {
        using var h = new Harness();
        var (status, _, _) = await h.SendAsync("POST", "/orders", key: null, requestBody: "{}");

        Assert.Equal(200, status);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_missing_key_is_rejected_when_required()
    {
        using var h = new Harness(new IdempotencyOptions { RequireKey = true });
        var (status, _, _) = await h.SendAsync("POST", "/orders", key: null, requestBody: "{}");

        Assert.Equal(400, status);
        Assert.Equal(0, h.HandlerCalls);
    }

    [Fact]
    public async Task A_repeated_request_replays_the_stored_response_without_re_running()
    {
        using var h = new Harness();

        var first = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.Equal(1, h.HandlerCalls);
        Assert.Equal(200, second.Status);
        Assert.True(second.Replayed);
        Assert.Equal(first.Body, second.Body);
    }

    [Fact]
    public async Task A_key_reused_with_a_different_body_is_rejected()
    {
        using var h = new Harness();

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");
        var (status, _, _) = await h.SendAsync("POST", "/orders", "k1", "{\"a\":2}");

        Assert.Equal(422, status);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_duplicate_while_in_progress_is_a_conflict()
    {
        using var h = new Harness();
        // Pre-claim the key with the fingerprint the middleware will compute.
        var fingerprint = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("{\"a\":1}"));
        await h.Store.AcquireAsync("k1", fingerprint);

        var (status, _, _) = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.Equal(409, status);
        Assert.Equal(0, h.HandlerCalls);
    }

    [Fact]
    public async Task A_handler_failure_releases_the_key_for_retry()
    {
        using var h = new Harness();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.SendAsync("POST", "/orders", "k1", "{\"a\":1}",
                handler: _ => throw new InvalidOperationException("boom")));

        // The key is free again, so a retry runs the handler.
        var retry = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");
        Assert.Equal(200, retry.Status);
        Assert.Equal(2, h.HandlerCalls);
    }

    [Fact]
    public async Task A_server_error_is_not_cached()
    {
        using var h = new Harness();

        Func<HttpContext, Task> fail = async ctx =>
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("upstream down");
        };

        var first = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", fail);
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.Equal(503, first.Status);
        Assert.False(second.Replayed);
        Assert.Equal(200, second.Status);
        Assert.Equal(2, h.HandlerCalls);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_rejected()
    {
        using var h = new Harness(new IdempotencyOptions { MaxBodyBytes = 8 });

        var (status, _, _) = await h.SendAsync("POST", "/orders", "k1", "this body is definitely longer than eight bytes");

        Assert.Equal(413, status);
        Assert.Equal(0, h.HandlerCalls);
    }
}
