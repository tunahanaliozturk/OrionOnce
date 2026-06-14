namespace Moongazing.OrionOnce.Storage;

/// <summary>
/// Stores idempotency keys and the responses captured for them. The default
/// <see cref="InMemoryIdempotencyStore"/> is process-local; implement this interface over Redis,
/// a database, or another shared backing store to make idempotency hold across instances.
/// Implementations must make <see cref="AcquireAsync"/> atomic: two concurrent requests with the
/// same key must not both receive <see cref="IdempotencyOutcome.Acquired"/>.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claim a key for a request, or report that it is already completed, in flight,
    /// or in use for a different request body.
    /// </summary>
    /// <param name="key">The idempotency key from the request.</param>
    /// <param name="fingerprint">
    /// A hash of the request identity (method, path, body) used to detect a key reused for a
    /// different request.
    /// </param>
    /// <param name="cancellationToken">Cancels the store operation.</param>
    Task<IdempotencyLease> AcquireAsync(string key, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store the response for a key the caller previously acquired, so later requests replay it.
    /// </summary>
    /// <param name="key">The acquired key.</param>
    /// <param name="response">The response to cache.</param>
    /// <param name="cancellationToken">Cancels the store operation.</param>
    Task CompleteAsync(string key, CachedResponse response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Release a key the caller acquired but could not complete (the handler threw or produced a
    /// response that should not be cached), so the request can be safely retried.
    /// </summary>
    /// <param name="key">The acquired key.</param>
    /// <param name="cancellationToken">Cancels the store operation.</param>
    Task ReleaseAsync(string key, CancellationToken cancellationToken = default);
}
