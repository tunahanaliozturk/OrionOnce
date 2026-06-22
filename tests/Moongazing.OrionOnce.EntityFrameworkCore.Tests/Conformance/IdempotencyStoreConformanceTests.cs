namespace Moongazing.OrionOnce.EntityFrameworkCore.Tests.Conformance;

using System.Text;

using Moongazing.OrionOnce.Storage;

using Xunit;

/// <summary>
/// A reusable contract test for any <see cref="IIdempotencyStore"/>. Derive a concrete class per
/// implementation and supply a store through <see cref="CreateHarnessAsync"/>; the cases below pin
/// the behavior every store must share: the first acquire wins, a duplicate of a completed key
/// replays the stored response, a duplicate of an in-flight key is reported in progress, a key
/// reused with a different fingerprint is a mismatch, a released in-flight claim frees the key while
/// a completed entry survives release, and expiry plus the sweep reclaim old entries.
/// </summary>
/// <remarks>
/// The harness exposes a clock the test advances, so expiry is driven deterministically rather than
/// by wall-clock waits. Implementations that cannot honor a test clock should override the expiry
/// and sweep facts; the EF Core implementation honors it through its <see cref="TimeProvider"/>.
/// </remarks>
public abstract class IdempotencyStoreConformanceTests
{
    /// <summary>The retention window the harness must configure its store with.</summary>
    public static TimeSpan Ttl { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Build a fresh, isolated store for one test, together with a clock the test can advance.
    /// Each call must return a store over its own backing state so cases do not interfere.
    /// </summary>
    protected abstract Task<IStoreHarness> CreateHarnessAsync();

    [Fact]
    public async Task first_acquire_on_an_unseen_key_wins()
    {
        await using var harness = await CreateHarnessAsync();

        var lease = await harness.Store.AcquireAsync("key-1", "fp-1");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
        Assert.Null(lease.Response);
    }

    [Fact]
    public async Task second_acquire_while_in_flight_is_reported_in_progress()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        var second = await harness.Store.AcquireAsync("key-1", "fp-1");

        Assert.Equal(IdempotencyOutcome.InProgress, second.Outcome);
        Assert.Null(second.Response);
    }

    [Fact]
    public async Task acquire_after_completion_replays_the_stored_response()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        var stored = new CachedResponse
        {
            StatusCode = 201,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"ok\":true}"),
        };
        await harness.Store.CompleteAsync("key-1", stored);

        var replay = await harness.Store.AcquireAsync("key-1", "fp-1");

        Assert.Equal(IdempotencyOutcome.AlreadyCompleted, replay.Outcome);
        Assert.NotNull(replay.Response);
        Assert.Equal(stored.StatusCode, replay.Response!.StatusCode);
        Assert.Equal(stored.ContentType, replay.Response.ContentType);
        Assert.Equal(stored.Body.ToArray(), replay.Response.Body.ToArray());
    }

    [Fact]
    public async Task acquire_with_a_different_fingerprint_is_a_mismatch()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        var mismatch = await harness.Store.AcquireAsync("key-1", "fp-different");

        Assert.Equal(IdempotencyOutcome.FingerprintMismatch, mismatch.Outcome);
        Assert.Null(mismatch.Response);
    }

    [Fact]
    public async Task a_completed_key_still_mismatches_a_different_fingerprint()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        await harness.Store.CompleteAsync("key-1", Response(200));

        var mismatch = await harness.Store.AcquireAsync("key-1", "fp-different");

        Assert.Equal(IdempotencyOutcome.FingerprintMismatch, mismatch.Outcome);
    }

    [Fact]
    public async Task releasing_an_in_flight_claim_frees_the_key()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        await harness.Store.ReleaseAsync("key-1");

        var reacquired = await harness.Store.AcquireAsync("key-1", "fp-1");

        Assert.Equal(IdempotencyOutcome.Acquired, reacquired.Outcome);
    }

    [Fact]
    public async Task releasing_a_completed_key_does_not_discard_the_response()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        await harness.Store.CompleteAsync("key-1", Response(200));

        // Release must only drop a still-in-flight claim; a completed entry must survive so its
        // response keeps replaying.
        await harness.Store.ReleaseAsync("key-1");

        var replay = await harness.Store.AcquireAsync("key-1", "fp-1");
        Assert.Equal(IdempotencyOutcome.AlreadyCompleted, replay.Outcome);
    }

    [Fact]
    public async Task an_expired_completed_entry_can_be_reacquired()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        await harness.Store.CompleteAsync("key-1", Response(200));

        harness.Advance(Ttl + TimeSpan.FromMinutes(1));

        var reacquired = await harness.Store.AcquireAsync("key-1", "fp-1");
        Assert.Equal(IdempotencyOutcome.Acquired, reacquired.Outcome);
    }

    [Fact]
    public async Task sweep_removes_expired_entries_and_reports_the_count()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-1", "fp-1");
        await harness.Store.AcquireAsync("key-2", "fp-2");
        await harness.Store.CompleteAsync("key-2", Response(200));

        harness.Advance(Ttl + TimeSpan.FromMinutes(1));

        var removed = await harness.Store.SweepAsync();

        Assert.Equal(2, removed);
    }

    [Fact]
    public async Task sweep_leaves_live_entries_untouched()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.AcquireAsync("key-live", "fp-1");

        var removed = await harness.Store.SweepAsync();

        Assert.Equal(0, removed);
        var stillInFlight = await harness.Store.AcquireAsync("key-live", "fp-1");
        Assert.Equal(IdempotencyOutcome.InProgress, stillInFlight.Outcome);
    }

    private static CachedResponse Response(int statusCode) => new()
    {
        StatusCode = statusCode,
        ContentType = "text/plain",
        Body = Encoding.UTF8.GetBytes("body"),
    };

    /// <summary>
    /// One test's store plus the clock that drives its expiry. Disposed at the end of the test, which
    /// also tears down any backing resources (for example a SQLite connection).
    /// </summary>
    public interface IStoreHarness : IAsyncDisposable
    {
        /// <summary>The store under test.</summary>
        IIdempotencyStore Store { get; }

        /// <summary>Advance the store's clock so retention windows elapse without waiting.</summary>
        /// <param name="by">How far to move time forward.</param>
        void Advance(TimeSpan by);
    }
}
