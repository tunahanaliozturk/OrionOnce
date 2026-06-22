namespace Moongazing.OrionOnce.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionOnce.Storage;

/// <summary>
/// A durable <see cref="IIdempotencyStore"/> backed by Entity Framework Core, so idempotency holds
/// across process restarts and across instances sharing one database. It mirrors the semantics of
/// <see cref="InMemoryIdempotencyStore"/> exactly: an entry is live only while its retention window
/// has not elapsed, an expired entry is treated as absent and reclaimable, completing an entry
/// refreshes its window, and a release drops only a still-in-flight claim and never discards a
/// captured response.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AcquireAsync"/> is atomic without a read-then-write race: the first caller for a key
/// inserts the row and wins the lease, and because the key is the primary key, a concurrent second
/// insert for the same key is rejected by the database with a unique-constraint violation. That
/// violation surfaces as a <see cref="DbUpdateException"/>; the loser then re-reads the row and
/// reports the winner's state (in flight, completed, or fingerprint mismatch). The duplicate is
/// confirmed by re-querying the row rather than by inspecting a provider-specific SQL error code,
/// so the store stays provider-agnostic and a genuinely different failure (for example a missing
/// table) is rethrown instead of being mistaken for a duplicate.
/// </para>
/// <para>
/// Every operation uses a fresh <see cref="DbContext"/> from the injected
/// <see cref="IDbContextFactory{TContext}"/>, because a context is not safe for the concurrent calls
/// the store is built to handle and a short-lived context per operation keeps no claim state in
/// memory.
/// </para>
/// </remarks>
/// <typeparam name="TContext">
/// The context type that maps <see cref="IdempotencyEntry"/>. Use <see cref="OrionOnceDbContext"/>
/// for a dedicated context, or any context that applies <see cref="IdempotencyEntryConfiguration"/>.
/// </typeparam>
public sealed class EntityFrameworkCoreIdempotencyStore<TContext> : IIdempotencyStore
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> contextFactory;
    private readonly TimeSpan ttl;
    private readonly TimeProvider timeProvider;

    /// <summary>Create a store with a retention window, using the system clock.</summary>
    /// <param name="contextFactory">Supplies a fresh context per operation.</param>
    /// <param name="ttl">How long a completed or in-progress entry is retained.</param>
    public EntityFrameworkCoreIdempotencyStore(IDbContextFactory<TContext> contextFactory, TimeSpan ttl)
        : this(contextFactory, ttl, TimeProvider.System)
    {
    }

    /// <summary>Create a store with a retention window, reading time from a supplied provider.</summary>
    /// <param name="contextFactory">Supplies a fresh context per operation.</param>
    /// <param name="ttl">How long a completed or in-progress entry is retained.</param>
    /// <param name="timeProvider">
    /// The clock used for expiry; supply a fake to control time in tests, or
    /// <see cref="TimeProvider.System"/> in production.
    /// </param>
    public EntityFrameworkCoreIdempotencyStore(
        IDbContextFactory<TContext> contextFactory,
        TimeSpan ttl,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");
        }
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.contextFactory = contextFactory;
        this.ttl = ttl;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<IdempotencyLease> AcquireAsync(
        string key,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(fingerprint);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var nowTicks = timeProvider.GetUtcNow().UtcTicks;

        var existing = await context.Set<IdempotencyEntry>()
            .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null && existing.ExpiresAtTicks > nowTicks)
        {
            return Resolve(existing, fingerprint);
        }

        if (existing is not null)
        {
            // The row is present but expired: treat it as absent and reclaim it in place. Updating
            // the existing row (rather than delete-then-insert) keeps this a single primary-key-bound
            // write, so a concurrent reclaim still collides on the key and is resolved below.
            existing.Fingerprint = fingerprint;
            existing.IsCompleted = false;
            existing.StatusCode = null;
            existing.ContentType = null;
            existing.Body = null;
            existing.ExpiresAtTicks = nowTicks + ttl.Ticks;
        }
        else
        {
            context.Add(new IdempotencyEntry
            {
                Key = key,
                Fingerprint = fingerprint,
                IsCompleted = false,
                ExpiresAtTicks = nowTicks + ttl.Ticks,
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdempotencyLease.Acquired;
        }
        catch (DbUpdateException)
        {
            // A concurrent caller won the claim for this key first, so our insert hit the key's
            // unique constraint. Confirm that by re-reading the row on a clean context rather than
            // sniffing a provider error code (this package references no provider): if the row is
            // now present we lost the race and report the winner's state; if it is absent the
            // failure was something else (for example the table is missing) and must surface.
            return await ResolveLostRaceAsync(key, fingerprint, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        string key,
        CachedResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(response);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entry = await context.Set<IdempotencyEntry>()
            .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            .ConfigureAwait(false);

        // Mirror the in-memory store: only an existing claim is completed; a missing row is a no-op.
        if (entry is null)
        {
            return;
        }

        entry.IsCompleted = true;
        entry.StatusCode = response.StatusCode;
        entry.ContentType = response.ContentType;
        entry.Body = response.Body.ToArray();
        entry.ExpiresAtTicks = timeProvider.GetUtcNow().UtcTicks + ttl.Ticks;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Only drop a still-in-progress claim; never discard an already-captured response. Scoping
        // the delete to in-flight rows keeps this a single set-based statement and matches the
        // in-memory store, which removes a key only when its response is still null.
        await context.Set<IdempotencyEntry>()
            .Where(e => e.Key == key && !e.IsCompleted)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var nowTicks = timeProvider.GetUtcNow().UtcTicks;

        // Bulk-delete every entry whose retention window has elapsed, served by the expiry index,
        // without loading rows or touching the change tracker. The boundary matches the in-memory
        // store: an entry expires once now has reached its expiry instant (ExpiresAtTicks <= now).
        return await context.Set<IdempotencyEntry>()
            .Where(e => e.ExpiresAtTicks <= nowTicks)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IdempotencyLease> ResolveLostRaceAsync(
        string key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var nowTicks = timeProvider.GetUtcNow().UtcTicks;

        var winner = await context.Set<IdempotencyEntry>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (winner is null)
        {
            // No row exists, so the SaveChanges failure was not a duplicate-key collision. Surface
            // it: re-run the insert on a clean context so the genuine error propagates to the caller
            // instead of being swallowed as a phantom conflict.
            await using var retryContext = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            retryContext.Add(new IdempotencyEntry
            {
                Key = key,
                Fingerprint = fingerprint,
                IsCompleted = false,
                ExpiresAtTicks = timeProvider.GetUtcNow().UtcTicks + ttl.Ticks,
            });

            await retryContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdempotencyLease.Acquired;
        }

        // The winner's claim may itself have already expired by the time we re-read it; an expired
        // winner is treated as absent, which leaves the key free. Report InProgress so the caller
        // retries rather than racing a second reclaim here.
        if (winner.ExpiresAtTicks <= nowTicks)
        {
            return IdempotencyLease.InProgress;
        }

        return Resolve(winner, fingerprint);
    }

    private static IdempotencyLease Resolve(IdempotencyEntry entry, string fingerprint)
    {
        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return IdempotencyLease.FingerprintMismatch;
        }

        if (!entry.IsCompleted)
        {
            return IdempotencyLease.InProgress;
        }

        return IdempotencyLease.Completed(new CachedResponse
        {
            StatusCode = entry.StatusCode ?? 0,
            ContentType = entry.ContentType,
            Body = entry.Body ?? ReadOnlyMemory<byte>.Empty,
        });
    }
}
