namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Moongazing.OrionOnce.Storage;

using Xunit;

public sealed class InMemoryIdempotencyStoreExtraTests
{
    private static CachedResponse Response(int status = 200) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Body = Encoding.UTF8.GetBytes("{\"ok\":true}"),
    };

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Acquire_rejects_a_null_or_empty_key(string? key)
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AcquireAsync(key!, "fp"));
    }

    [Fact]
    public async Task Acquire_rejects_a_null_fingerprint()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AcquireAsync("k1", null!));
    }

    [Fact]
    public async Task Acquire_accepts_an_empty_fingerprint()
    {
        // Only null is rejected; an empty fingerprint is a valid (if degenerate) value.
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));

        var lease = await store.AcquireAsync("k1", string.Empty);

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Complete_rejects_a_null_or_empty_key(string? key)
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.CompleteAsync(key!, Response()));
    }

    [Fact]
    public async Task Complete_rejects_a_null_response()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp");

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CompleteAsync("k1", null!));
    }

    [Fact]
    public async Task Complete_on_an_unknown_key_is_a_no_op()
    {
        // CompleteAsync only writes when an entry already exists; an unknown key must not create one.
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.CompleteAsync("ghost", Response(201));

        var lease = await store.AcquireAsync("ghost", "fp");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Release_rejects_a_null_or_empty_key(string? key)
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReleaseAsync(key!));
    }

    [Fact]
    public async Task Release_on_an_unknown_key_is_a_no_op()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));

        // Should not throw and should not create an entry.
        await store.ReleaseAsync("ghost");

        var lease = await store.AcquireAsync("ghost", "fp");
        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Fact]
    public async Task A_completed_response_is_replayed_with_its_full_payload()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp");
        await store.CompleteAsync("k1", Response(201));

        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(201, lease.Response!.StatusCode);
        Assert.Equal("application/json", lease.Response.ContentType);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(lease.Response.Body.Span));
    }

    [Fact]
    public async Task A_fingerprint_mismatch_outranks_an_in_progress_claim()
    {
        // A still-in-flight entry with a different fingerprint is a mismatch, not a conflict.
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp-a");

        var lease = await store.AcquireAsync("k1", "fp-b");

        Assert.Equal(IdempotencyOutcome.FingerprintMismatch, lease.Outcome);
    }

    [Fact]
    public async Task A_fingerprint_mismatch_outranks_a_completed_response()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp-a");
        await store.CompleteAsync("k1", Response());

        var lease = await store.AcquireAsync("k1", "fp-b");

        Assert.Equal(IdempotencyOutcome.FingerprintMismatch, lease.Outcome);
        Assert.Null(lease.Response);
    }

    [Fact]
    public async Task An_expired_in_progress_claim_is_re_acquired_as_new()
    {
        // Expiry applies to in-flight entries too, not just completed ones.
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(31));
        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Fact]
    public async Task An_entry_just_before_the_ttl_boundary_is_still_live()
    {
        // The liveness check is ExpiresAt > now, so the entry survives right up to (but not at) ttl.
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.InProgress, lease.Outcome);
    }

    [Fact]
    public async Task An_entry_exactly_at_the_ttl_boundary_has_expired()
    {
        // ExpiresAt > now is strict: at exactly now == ExpiresAt the entry is considered expired
        // and the key is re-acquired as new. This documents the exclusive upper bound.
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(30));
        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Fact]
    public async Task Complete_extends_the_retention_window_from_the_completion_time()
    {
        // CompleteAsync resets ExpiresAt to now + ttl, so the clock for replay starts at completion.
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(20));
        await store.CompleteAsync("k1", Response());

        // 25s after acquire (5s after completion) the response is still replayable.
        clock.Advance(TimeSpan.FromSeconds(5));
        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.AlreadyCompleted, lease.Outcome);
    }

    [Fact]
    public async Task Distinct_keys_are_tracked_independently()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp");
        await store.CompleteAsync("k1", Response(201));

        var other = await store.AcquireAsync("k2", "fp");

        Assert.Equal(IdempotencyOutcome.Acquired, other.Outcome);
    }

    [Fact]
    public async Task A_re_acquired_expired_key_can_use_a_fresh_fingerprint()
    {
        // After expiry the slot is wiped, so a new fingerprint is accepted rather than a mismatch.
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp-old");
        await store.CompleteAsync("k1", Response());

        clock.Advance(TimeSpan.FromSeconds(31));
        var lease = await store.AcquireAsync("k1", "fp-new");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Fact]
    public async Task Concurrent_acquires_of_the_same_key_yield_exactly_one_winner()
    {
        // The lock around the claim section must let only one caller acquire; the rest see InProgress.
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        const int racers = 64;

        using var start = new ManualResetEventSlim(false);
        var tasks = new Task<IdempotencyLease>[racers];
        for (var i = 0; i < racers; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                start.Wait();
                return await store.AcquireAsync("k1", "fp");
            });
        }

        start.Set();
        var leases = await Task.WhenAll(tasks);

        Assert.Equal(1, leases.Count(l => l.Outcome == IdempotencyOutcome.Acquired));
        Assert.Equal(racers - 1, leases.Count(l => l.Outcome == IdempotencyOutcome.InProgress));
    }

    [Fact]
    public async Task A_pre_cancelled_token_does_not_prevent_a_synchronous_acquire()
    {
        // The in-memory store completes synchronously and does not observe cancellation; document that.
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var lease = await store.AcquireAsync("k1", "fp", cts.Token);

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by) => Now += by;
    }
}
