namespace Moongazing.OrionOnce.AspNetCore;

using Microsoft.AspNetCore.Http;

using Moongazing.OrionOnce.Diagnostics;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// ASP.NET Core middleware implementing the <c>Idempotency-Key</c> pattern. For a guarded request
/// carrying a key, it replays the stored response if the key was already completed, rejects a
/// duplicate that is still in flight (409) or one that reuses a key with a different body (422),
/// and otherwise runs the handler once and caches its response for later replays.
/// </summary>
public sealed class IdempotencyMiddleware
{
    /// <summary>The header set on a replayed response so clients can tell it was not re-executed.</summary>
    public const string ReplayedHeader = "Idempotency-Replayed";

    private readonly RequestDelegate next;

    /// <summary>Create the middleware.</summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    public IdempotencyMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        this.next = next;
    }

    /// <summary>Process a request.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="store">The idempotency store.</param>
    /// <param name="options">The middleware options.</param>
    /// <param name="diagnostics">The metrics sink.</param>
    public async Task InvokeAsync(
        HttpContext context,
        IIdempotencyStore store,
        IdempotencyOptions options,
        IdempotencyDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        if (!options.Methods.Contains(request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!TryGetKey(context, options, out var key))
        {
            if (options.RequireKey)
            {
                diagnostics.Record("missing_key");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            diagnostics.Record("bypassed");
            await next(context).ConfigureAwait(false);
            return;
        }

        var ct = context.RequestAborted;
        var body = await ReadBodyAsync(request, options.MaxBodyBytes, ct).ConfigureAwait(false);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var fingerprint = RequestFingerprint.Compute(request.Method, request.Path + request.QueryString, body);
        var lease = await store.AcquireAsync(key, fingerprint, ct).ConfigureAwait(false);

        switch (lease.Outcome)
        {
            case IdempotencyOutcome.AlreadyCompleted:
                diagnostics.Record("replayed");
                await ReplayAsync(context, lease.Response!, ct).ConfigureAwait(false);
                return;

            case IdempotencyOutcome.InProgress:
                diagnostics.Record("in_progress");
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;

            case IdempotencyOutcome.FingerprintMismatch:
                diagnostics.Record("mismatch");
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return;

            case IdempotencyOutcome.Acquired:
            default:
                diagnostics.Record("acquired");
                await RunAndCaptureAsync(context, store, key, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task RunAndCaptureAsync(HttpContext context, IIdempotencyStore store, string key, CancellationToken ct)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // release the claim on ANY handler failure so the request can retry
        catch (Exception)
#pragma warning restore CA1031
        {
            context.Response.Body = originalBody;
            await store.ReleaseAsync(key, ct).ConfigureAwait(false);
            throw;
        }

        context.Response.Body = originalBody;
        var captured = buffer.ToArray();
        await originalBody.WriteAsync(captured, ct).ConfigureAwait(false);

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            // A server error is transient; do not cache it, let the client retry the same key.
            await store.ReleaseAsync(key, ct).ConfigureAwait(false);
            return;
        }

        await store.CompleteAsync(key, new CachedResponse
        {
            StatusCode = context.Response.StatusCode,
            ContentType = context.Response.ContentType,
            Body = captured,
        }, ct).ConfigureAwait(false);
    }

    private static async Task ReplayAsync(HttpContext context, CachedResponse cached, CancellationToken ct)
    {
        context.Response.StatusCode = cached.StatusCode;
        if (cached.ContentType is not null)
        {
            context.Response.ContentType = cached.ContentType;
        }
        context.Response.Headers[ReplayedHeader] = "true";
        await context.Response.Body.WriteAsync(cached.Body, ct).ConfigureAwait(false);
    }

    private static bool TryGetKey(HttpContext context, IdempotencyOptions options, out string key)
    {
        if (context.Request.Headers.TryGetValue(options.HeaderName, out var values))
        {
            var candidate = values.ToString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                key = candidate;
                return true;
            }
        }

        key = string.Empty;
        return false;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        request.EnableBuffering();
        using var buffer = new MemoryStream();

        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                request.Body.Position = 0;
                return null;
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        request.Body.Position = 0;
        return buffer.ToArray();
    }
}
