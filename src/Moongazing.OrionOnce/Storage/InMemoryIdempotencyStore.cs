namespace Moongazing.OrionOnce.Storage;

/// <summary>
/// A process-local <see cref="IIdempotencyStore"/> backed by a dictionary. Suitable for a single
/// instance or for tests; use a shared-store implementation for a multi-instance deployment.
/// Atomicity is provided by a single lock around the claim/complete critical section, and expired
/// entries are evicted lazily on access.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly TimeSpan ttl;
    private readonly Func<DateTimeOffset> now;

    /// <summary>Create a store with a retention window.</summary>
    /// <param name="ttl">How long a completed or in-progress entry is retained.</param>
    public InMemoryIdempotencyStore(TimeSpan ttl)
        : this(ttl, () => DateTimeOffset.UtcNow)
    {
    }

    internal InMemoryIdempotencyStore(TimeSpan ttl, Func<DateTimeOffset> now)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");
        }
        ArgumentNullException.ThrowIfNull(now);
        this.ttl = ttl;
        this.now = now;
    }

    /// <inheritdoc />
    public Task<IdempotencyLease> AcquireAsync(string key, string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(fingerprint);

        lock (gate)
        {
            var timestamp = now();
            if (entries.TryGetValue(key, out var existing) && existing.ExpiresAt > timestamp)
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Task.FromResult(IdempotencyLease.FingerprintMismatch);
                }

                return Task.FromResult(existing.Response is { } response
                    ? IdempotencyLease.Completed(response)
                    : IdempotencyLease.InProgress);
            }

            entries[key] = new Entry(fingerprint, Response: null, ExpiresAt: timestamp + ttl);
            return Task.FromResult(IdempotencyLease.Acquired);
        }
    }

    /// <inheritdoc />
    public Task CompleteAsync(string key, CachedResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(response);

        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                entries[key] = existing with { Response = response, ExpiresAt = now() + ttl };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (gate)
        {
            // Only drop a still-in-progress claim; never discard an already-cached response.
            if (entries.TryGetValue(key, out var existing) && existing.Response is null)
            {
                entries.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    private sealed record Entry(string Fingerprint, CachedResponse? Response, DateTimeOffset ExpiresAt);
}
