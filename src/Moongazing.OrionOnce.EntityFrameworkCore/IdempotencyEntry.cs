namespace Moongazing.OrionOnce.EntityFrameworkCore;

/// <summary>
/// The persisted row backing a single idempotency key in <see cref="EntityFrameworkCoreIdempotencyStore{TContext}"/>.
/// The <see cref="Key"/> is the primary key, so the database's primary-key uniqueness is what makes
/// <see cref="EntityFrameworkCoreIdempotencyStore{TContext}.AcquireAsync"/> atomic: two concurrent
/// inserts for the same key cannot both succeed. A row starts in flight (<see cref="IsCompleted"/> is
/// <see langword="false"/>, no captured response) and becomes completed when the owner stores its
/// response.
/// </summary>
public sealed class IdempotencyEntry
{
    /// <summary>The idempotency key. Primary key, and therefore the unique claim that gates the lease.</summary>
    public required string Key { get; set; }

    /// <summary>
    /// The request fingerprint bound to the key, used to detect a key reused for a different request.
    /// </summary>
    public required string Fingerprint { get; set; }

    /// <summary>
    /// Whether the captured response has been stored. <see langword="false"/> while the owning request
    /// is still in flight; <see langword="true"/> once the response is captured and the entry is
    /// replayable.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>The captured HTTP status code, present only once <see cref="IsCompleted"/> is true.</summary>
    public int? StatusCode { get; set; }

    /// <summary>The captured response content type, or null when the response had no body.</summary>
    public string? ContentType { get; set; }

    /// <summary>The captured response body bytes, present only once <see cref="IsCompleted"/> is true.</summary>
    public byte[]? Body { get; set; }

    /// <summary>
    /// The instant the entry expires, as UTC ticks (<see cref="DateTimeOffset.UtcTicks"/>). Stored as a
    /// primitive integer rather than a <see cref="DateTimeOffset"/> so expiry comparison and the sweep
    /// translate to a plain integer predicate on every relational provider, free of provider-specific
    /// date handling.
    /// </summary>
    public long ExpiresAtTicks { get; set; }
}
