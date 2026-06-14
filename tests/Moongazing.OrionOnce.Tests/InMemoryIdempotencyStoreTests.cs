namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Moongazing.OrionOnce.Storage;

using Xunit;

public sealed class InMemoryIdempotencyStoreTests
{
    private static CachedResponse Response(int status = 200) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Body = Encoding.UTF8.GetBytes("{\"ok\":true}"),
    };

    [Fact]
    public async Task First_acquire_of_a_key_is_acquired()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Fact]
    public async Task Second_acquire_while_in_progress_reports_in_progress()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp");

        var second = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.InProgress, second.Outcome);
    }

    [Fact]
    public async Task Acquire_after_complete_replays_the_response()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp");
        await store.CompleteAsync("k1", Response(201));

        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.AlreadyCompleted, lease.Outcome);
        Assert.Equal(201, lease.Response!.StatusCode);
    }

    [Fact]
    public async Task Same_key_with_a_different_fingerprint_is_a_mismatch()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp-a");

        var lease = await store.AcquireAsync("k1", "fp-b");

        Assert.Equal(IdempotencyOutcome.FingerprintMismatch, lease.Outcome);
    }

    [Fact]
    public async Task Release_lets_an_in_progress_key_be_re_acquired()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp");
        await store.ReleaseAsync("k1");

        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Fact]
    public async Task Release_does_not_discard_a_completed_response()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        await store.AcquireAsync("k1", "fp");
        await store.CompleteAsync("k1", Response());
        await store.ReleaseAsync("k1");

        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.AlreadyCompleted, lease.Outcome);
    }

    [Fact]
    public async Task An_expired_entry_is_re_acquired_as_new()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        await store.AcquireAsync("k1", "fp");
        await store.CompleteAsync("k1", Response());

        clock.Advance(TimeSpan.FromSeconds(31));
        var lease = await store.AcquireAsync("k1", "fp");

        Assert.Equal(IdempotencyOutcome.Acquired, lease.Outcome);
    }

    [Fact]
    public void Non_positive_ttl_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryIdempotencyStore(TimeSpan.Zero));
    }

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by) => Now += by;
    }
}
