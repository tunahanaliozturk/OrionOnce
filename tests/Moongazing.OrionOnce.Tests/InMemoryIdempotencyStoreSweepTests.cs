namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Moongazing.OrionOnce.Storage;

using Xunit;

public sealed class InMemoryIdempotencyStoreSweepTests
{
    private static CachedResponse Response(int status = 200) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Body = Encoding.UTF8.GetBytes("{\"ok\":true}"),
    };

    [Fact]
    public async Task Sweep_on_an_empty_store_removes_nothing()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));

        var removed = await store.SweepAsync();

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task Sweep_does_not_remove_a_live_entry()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(10));
        var removed = await store.SweepAsync();

        Assert.Equal(0, removed);
        Assert.Equal(IdempotencyOutcome.InProgress, (await store.AcquireAsync("k1", "fp")).Outcome);
    }

    [Fact]
    public async Task Sweep_removes_only_expired_entries()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);

        // k1 acquired at t0, k2 acquired 25s later; sweep at t0+31s expires only k1.
        await store.AcquireAsync("k1", "fp");
        await store.CompleteAsync("k1", Response(201));

        clock.Advance(TimeSpan.FromSeconds(25));
        await store.AcquireAsync("k2", "fp");

        clock.Advance(TimeSpan.FromSeconds(6)); // now t0+31s: k1 (exp t0+30s) gone, k2 (exp t0+55s) live
        var removed = await store.SweepAsync();

        Assert.Equal(1, removed);
        // k1 is gone: re-acquired as new with a fresh fingerprint.
        Assert.Equal(IdempotencyOutcome.Acquired, (await store.AcquireAsync("k1", "other")).Outcome);
        // k2 survived: still in flight.
        Assert.Equal(IdempotencyOutcome.InProgress, (await store.AcquireAsync("k2", "fp")).Outcome);
    }

    [Fact]
    public async Task Sweep_removes_an_entry_exactly_at_the_ttl_boundary()
    {
        // Liveness is ExpiresAt > now; at exactly ExpiresAt the entry is expired, so sweep removes it.
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(30));
        var removed = await store.SweepAsync();

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task Sweep_keeps_an_entry_one_tick_before_the_boundary()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        var removed = await store.SweepAsync();

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task Sweep_removes_completed_entries_too()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");
        await store.CompleteAsync("k1", Response());

        clock.Advance(TimeSpan.FromSeconds(31));
        var removed = await store.SweepAsync();

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task Sweep_is_idempotent_across_repeated_calls()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await store.SweepAsync());
        Assert.Equal(0, await store.SweepAsync());
    }

    [Fact]
    public async Task The_default_interface_sweep_is_a_no_op()
    {
        // A custom store that does not override SweepAsync inherits the zero-returning default.
        IIdempotencyStore store = new NoopStore();

        Assert.Equal(0, await store.SweepAsync());
    }

    [Fact]
    public async Task Expiry_is_driven_by_the_supplied_time_provider()
    {
        // Exercise the public TimeProvider constructor: advancing the provider expires the entry.
        var provider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), provider);
        await store.AcquireAsync("k1", "fp");

        provider.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(1, await store.SweepAsync());
        Assert.Equal(IdempotencyOutcome.Acquired, (await store.AcquireAsync("k1", "fp")).Outcome);
    }

    [Fact]
    public void A_null_time_provider_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5), (TimeProvider)null!));
    }

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class NoopStore : IIdempotencyStore
    {
        public Task<IdempotencyLease> AcquireAsync(string key, string fingerprint, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompleteAsync(string key, CachedResponse response, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
