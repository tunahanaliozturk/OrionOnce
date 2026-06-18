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
public sealed class IdempotencyMiddlewareExtraTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(IdempotencyOptions? options = null, IIdempotencyStore? store = null)
        {
            Options = options ?? new IdempotencyOptions();
            Store = store ?? new InMemoryIdempotencyStore(Options.Retention);
        }

        public IdempotencyOptions Options { get; }

        public IIdempotencyStore Store { get; }

        public IdempotencyDiagnostics Diagnostics { get; } = new();

        public int HandlerCalls { get; private set; }

        public async Task<(int Status, string Body, bool Replayed, string? ContentType)> SendAsync(
            string method,
            string path,
            string? key,
            string requestBody,
            string? queryString = null,
            Func<HttpContext, Task>? handler = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            if (queryString is not null)
            {
                context.Request.QueryString = new QueryString(queryString);
            }
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
            return (
                context.Response.StatusCode,
                Encoding.UTF8.GetString(responseBody.ToArray()),
                replayed,
                context.Response.ContentType);
        }

        public void Dispose() => Diagnostics.Dispose();
    }

    [Fact]
    public async Task The_method_filter_is_case_insensitive()
    {
        // "post" must be guarded just like "POST"; otherwise a lowercase verb would skip dedup.
        using var h = new Harness();

        await h.SendAsync("post", "/orders", "k1", "{\"a\":1}");
        var second = await h.SendAsync("post", "/orders", "k1", "{\"a\":1}");

        Assert.True(second.Replayed);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_custom_header_name_is_honoured()
    {
        using var h = new Harness(new IdempotencyOptions { HeaderName = "X-Idem" });

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.True(second.Replayed);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_whitespace_only_key_is_treated_as_absent_and_bypasses()
    {
        // TryGetKey rejects whitespace via IsNullOrWhiteSpace, so a blank key bypasses by default.
        using var h = new Harness();

        var (status, _, _, _) = await h.SendAsync("POST", "/orders", "   ", "{\"a\":1}");

        Assert.Equal(200, status);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_whitespace_only_key_is_rejected_when_a_key_is_required()
    {
        using var h = new Harness(new IdempotencyOptions { RequireKey = true });

        var (status, _, _, _) = await h.SendAsync("POST", "/orders", "   ", "{\"a\":1}");

        Assert.Equal(400, status);
        Assert.Equal(0, h.HandlerCalls);
    }

    [Fact]
    public async Task The_replayed_response_carries_the_replay_header_and_content_type()
    {
        using var h = new Harness();

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.True(second.Replayed);
        Assert.Equal("application/json", second.ContentType);
        Assert.Equal("{\"handled\":true}", second.Body);
    }

    [Fact]
    public async Task A_replayed_response_preserves_the_original_status_code()
    {
        using var h = new Harness();

        Func<HttpContext, Task> created = async ctx =>
        {
            ctx.Response.StatusCode = 201;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"id\":7}");
        };

        var first = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", handler: created);
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", handler: created);

        Assert.Equal(201, first.Status);
        Assert.Equal(201, second.Status);
        Assert.True(second.Replayed);
        Assert.Equal("{\"id\":7}", second.Body);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_client_error_response_is_cached_and_replayed()
    {
        // Only 5xx is treated as transient; a 4xx is a deterministic outcome and is cached.
        using var h = new Harness();

        Func<HttpContext, Task> badRequest = async ctx =>
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("nope");
        };

        var first = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", handler: badRequest);
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.Equal(400, first.Status);
        Assert.Equal(400, second.Status);
        Assert.True(second.Replayed);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task The_exact_500_boundary_is_not_cached()
    {
        // The cache-skip predicate is >= 500, so 500 itself is transient.
        using var h = new Harness();

        Func<HttpContext, Task> serverError = async ctx =>
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("boom");
        };

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", handler: serverError);
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.False(second.Replayed);
        Assert.Equal(200, second.Status);
        Assert.Equal(2, h.HandlerCalls);
    }

    [Fact]
    public async Task A_body_exactly_at_the_limit_is_allowed()
    {
        // The reject predicate is length + read > max, so a body equal to the limit passes.
        using var h = new Harness(new IdempotencyOptions { MaxBodyBytes = 8 });

        var (status, _, _, _) = await h.SendAsync("POST", "/orders", "k1", "12345678");

        Assert.Equal(200, status);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_body_one_byte_over_the_limit_is_rejected()
    {
        using var h = new Harness(new IdempotencyOptions { MaxBodyBytes = 8 });

        var (status, _, _, _) = await h.SendAsync("POST", "/orders", "k1", "123456789");

        Assert.Equal(413, status);
        Assert.Equal(0, h.HandlerCalls);
    }

    [Fact]
    public async Task An_empty_body_is_a_valid_guarded_request()
    {
        using var h = new Harness();

        var first = await h.SendAsync("POST", "/orders", "k1", string.Empty);
        var second = await h.SendAsync("POST", "/orders", "k1", string.Empty);

        Assert.Equal(200, first.Status);
        Assert.True(second.Replayed);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task The_query_string_is_part_of_the_request_identity()
    {
        // Same key and path but different query is a different request, hence a fingerprint mismatch.
        using var h = new Harness();

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", queryString: "?page=1");
        var (status, _, _, _) = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", queryString: "?page=2");

        Assert.Equal(422, status);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task The_same_path_and_query_replays()
    {
        using var h = new Harness();

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", queryString: "?page=1");
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", queryString: "?page=1");

        Assert.True(second.Replayed);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task Different_keys_on_the_same_request_each_run_the_handler()
    {
        using var h = new Harness();

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");
        var other = await h.SendAsync("POST", "/orders", "k2", "{\"a\":1}");

        Assert.Equal(200, other.Status);
        Assert.False(other.Replayed);
        Assert.Equal(2, h.HandlerCalls);
    }

    [Fact]
    public async Task A_response_with_no_content_type_replays_without_one()
    {
        using var h = new Harness();

        Func<HttpContext, Task> noContentType = ctx =>
        {
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        };

        await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}", handler: noContentType);
        var second = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.Equal(204, second.Status);
        Assert.True(second.Replayed);
        Assert.Equal(string.Empty, second.Body);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task A_handler_failure_propagates_the_exception_to_the_caller()
    {
        using var h = new Harness();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.SendAsync("POST", "/orders", "k1", "{\"a\":1}",
                handler: _ => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task A_non_guarded_method_with_no_key_still_passes_through()
    {
        using var h = new Harness();

        var (status, _, _, _) = await h.SendAsync("GET", "/orders", key: null, requestBody: string.Empty);

        Assert.Equal(200, status);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task An_in_progress_duplicate_does_not_call_the_handler()
    {
        using var h = new Harness();
        var fingerprint = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("{\"a\":1}"));
        await h.Store.AcquireAsync("k1", fingerprint);

        var (status, _, _, _) = await h.SendAsync("POST", "/orders", "k1", "{\"a\":1}");

        Assert.Equal(409, status);
        Assert.Equal(0, h.HandlerCalls);
    }

    [Fact]
    public void The_replayed_header_name_is_stable()
    {
        // Clients depend on this exact header to tell a replay from a fresh response.
        Assert.Equal("Idempotency-Replayed", IdempotencyMiddleware.ReplayedHeader);
    }

    [Fact]
    public void The_middleware_rejects_a_null_next_delegate()
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotencyMiddleware(null!));
    }
}
