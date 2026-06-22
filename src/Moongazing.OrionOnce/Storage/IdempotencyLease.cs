namespace Moongazing.OrionOnce.Storage;

/// <summary>
/// The outcome of trying to claim an idempotency key for a request.
/// </summary>
public enum IdempotencyOutcome
{
    /// <summary>
    /// The key was not seen before and is now claimed by this request. The caller owns it and
    /// must call <see cref="IIdempotencyStore.CompleteAsync"/> on success or
    /// <see cref="IIdempotencyStore.ReleaseAsync"/> on failure.
    /// </summary>
    Acquired,

    /// <summary>
    /// The key was already completed by an earlier request. The stored response is replayed and
    /// the handler is skipped.
    /// </summary>
    AlreadyCompleted,

    /// <summary>
    /// Another request holding this key is still in flight. The duplicate is rejected (409)
    /// rather than executed a second time.
    /// </summary>
    InProgress,

    /// <summary>
    /// The key was seen before but with a different request fingerprint, which means a client
    /// reused a key for a different request. The request is rejected (422).
    /// </summary>
    FingerprintMismatch,
}

/// <summary>
/// The result of <see cref="IIdempotencyStore.AcquireAsync"/>: the outcome plus the cached
/// response when the outcome is <see cref="IdempotencyOutcome.AlreadyCompleted"/>.
/// </summary>
public sealed class IdempotencyLease
{
    private IdempotencyLease(IdempotencyOutcome outcome, CachedResponse? response)
    {
        Outcome = outcome;
        Response = response;
    }

    /// <summary>What happened when the key was claimed.</summary>
    public IdempotencyOutcome Outcome { get; }

    /// <summary>
    /// The response to replay, present only when <see cref="Outcome"/> is
    /// <see cref="IdempotencyOutcome.AlreadyCompleted"/>.
    /// </summary>
    public CachedResponse? Response { get; }

    /// <summary>
    /// The lease for a caller that just claimed a previously unseen key
    /// (<see cref="IdempotencyOutcome.Acquired"/>). Returned by an <see cref="IIdempotencyStore"/>
    /// implementation when this caller wins the claim.
    /// </summary>
    public static IdempotencyLease Acquired { get; } = new(IdempotencyOutcome.Acquired, null);

    /// <summary>
    /// The lease for a key another caller still holds (<see cref="IdempotencyOutcome.InProgress"/>).
    /// Returned by an <see cref="IIdempotencyStore"/> implementation for a duplicate that arrives
    /// while the first request is in flight.
    /// </summary>
    public static IdempotencyLease InProgress { get; } = new(IdempotencyOutcome.InProgress, null);

    /// <summary>
    /// The lease for a key reused with a different request fingerprint
    /// (<see cref="IdempotencyOutcome.FingerprintMismatch"/>). Returned by an
    /// <see cref="IIdempotencyStore"/> implementation when the stored fingerprint differs.
    /// </summary>
    public static IdempotencyLease FingerprintMismatch { get; } = new(IdempotencyOutcome.FingerprintMismatch, null);

    /// <summary>
    /// Build the lease for a key that already completed
    /// (<see cref="IdempotencyOutcome.AlreadyCompleted"/>), carrying the response to replay.
    /// Returned by an <see cref="IIdempotencyStore"/> implementation when the stored response is
    /// found.
    /// </summary>
    /// <param name="response">The captured response to replay.</param>
    public static IdempotencyLease Completed(CachedResponse response) =>
        new(IdempotencyOutcome.AlreadyCompleted, response);
}
