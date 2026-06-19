namespace Moongazing.OrionOnce;

using Moongazing.OrionOnce.Storage;

/// <summary>
/// Runs an operation under an idempotency key and captures its typed result so a later duplicate
/// call with the same key replays the stored result instead of running the operation again. This
/// is the non-HTTP counterpart of <see cref="AspNetCore.IdempotencyMiddleware"/>: it wires through
/// the same <see cref="IIdempotencyStore"/>, so capture, replay, conflict, mismatch, and expiry all
/// behave identically. The library stays serialization-agnostic; the caller supplies the codec that
/// turns a result into bytes and back.
/// </summary>
public sealed class IdempotentExecutor
{
    private readonly IIdempotencyStore store;

    /// <summary>Create an executor over a store.</summary>
    /// <param name="store">The store that holds claims and captured results.</param>
    public IdempotentExecutor(IIdempotencyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>
    /// Execute <paramref name="operation"/> exactly once for <paramref name="key"/> and return its
    /// result, replaying the captured result on any later call with the same key and fingerprint.
    /// The first caller acquires the key and runs the operation; its result is serialized with
    /// <paramref name="codec"/> and stored. A later caller whose entry is still present and whose
    /// fingerprint matches gets the deserialized stored result without running the operation. If
    /// the operation throws, or its result cannot be captured (serialization or the store's
    /// completion write fails), the key is released so the call can be retried, and the original
    /// exception is propagated unchanged. That release is best-effort and uses an uncancelable
    /// token, so it runs even when the caller's token is canceled and never masks the real failure.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="key">The idempotency key.</param>
    /// <param name="fingerprint">
    /// A hash of the operation identity used to detect a key reused for a different operation;
    /// compute one with <see cref="RequestFingerprint"/> or any stable per-operation string.
    /// </param>
    /// <param name="operation">The operation to run at most once for the key.</param>
    /// <param name="codec">Serializes the result for storage and reconstructs it on replay.</param>
    /// <param name="cancellationToken">Cancels the store calls and is passed to the operation.</param>
    /// <returns>The freshly produced result, or the captured result replayed from the store.</returns>
    /// <exception cref="IdempotentExecutionException">
    /// A concurrent caller still holds the key (<see cref="IdempotencyOutcome.InProgress"/>) or the
    /// key was reused with a different fingerprint (<see cref="IdempotencyOutcome.FingerprintMismatch"/>).
    /// </exception>
    public async Task<TResult> ExecuteAsync<TResult>(
        string key,
        string fingerprint,
        Func<CancellationToken, Task<TResult>> operation,
        IIdempotentResultCodec<TResult> codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(codec);

        var lease = await store.AcquireAsync(key, fingerprint, cancellationToken).ConfigureAwait(false);

        switch (lease.Outcome)
        {
            case IdempotencyOutcome.AlreadyCompleted:
                return codec.Deserialize(lease.Response!.Body.Span);

            case IdempotencyOutcome.InProgress:
            case IdempotencyOutcome.FingerprintMismatch:
                throw new IdempotentExecutionException(lease.Outcome);

            case IdempotencyOutcome.Acquired:
            default:
                return await RunAndCaptureAsync(key, operation, codec, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TResult> RunAndCaptureAsync<TResult>(
        string key,
        Func<CancellationToken, Task<TResult>> operation,
        IIdempotentResultCodec<TResult> codec,
        CancellationToken cancellationToken)
    {
        TResult result;
        try
        {
            result = await operation(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Release the claim on any failure so the operation can be retried under the same key.
            // The caller's token may already be canceled here (the operation may have failed because
            // it observed cancellation), and a store that honors cancellation would skip the release,
            // leaving the key in-flight until its TTL. Use an uncancelable token and swallow any fault
            // so this best-effort cleanup never masks the original operation exception.
            await ReleaseQuietlyAsync(key).ConfigureAwait(false);
            throw;
        }

        try
        {
            var captured = new CachedResponse
            {
                StatusCode = StatusCodes.Status200Ok,
                ContentType = codec.ContentType,
                Body = codec.Serialize(result),
            };

            await store.CompleteAsync(key, captured, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The operation succeeded but its result could not be captured: serialization threw, or
            // the store failed while writing the completed entry. Release the claim so the key does
            // not linger as an in-flight entry until its TTL and a retry can rerun the operation.
            // Same best-effort cleanup as above: uncancelable token, faults swallowed so the real
            // capture/complete failure propagates unchanged.
            await ReleaseQuietlyAsync(key).ConfigureAwait(false);
            throw;
        }

        return result;
    }

    /// <summary>
    /// Best-effort release of a claim during failure cleanup. Uses <see cref="CancellationToken.None"/>
    /// so a store that honors cancellation still performs the release even when the caller's token is
    /// already canceled, and swallows every fault so the cleanup never replaces the original failure.
    /// </summary>
    private async Task ReleaseQuietlyAsync(string key)
    {
        try
        {
            await store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Intentionally swallowed: this release is best-effort cleanup on a failure path, and its
            // fault (including cancellation) must not mask the original operation or capture exception.
        }
    }

    // Local mirror of the HTTP status used to mark a captured non-HTTP result, so this type carries
    // no dependency on ASP.NET Core. Replay reconstructs the result from the body, not this code.
    private static class StatusCodes
    {
        public const int Status200Ok = 200;
    }
}
