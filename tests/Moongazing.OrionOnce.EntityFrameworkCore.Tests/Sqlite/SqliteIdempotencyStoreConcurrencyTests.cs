namespace Moongazing.OrionOnce.EntityFrameworkCore.Tests.Sqlite;

using System.Collections.Concurrent;

using Moongazing.OrionOnce.Storage;

using Xunit;

/// <summary>
/// Proves the atomic claim under genuine parallelism: many threads racing to acquire the same key
/// against a real SQLite database must produce exactly one winner, with every other caller told the
/// key is already taken. This is the test that would fail a read-then-write implementation, where two
/// callers could both read "absent" and both insert; here the database's unique key serializes the
/// inserts so only one survives and the rest resolve through the duplicate-violation path.
/// </summary>
public sealed class SqliteIdempotencyStoreConcurrencyTests
{
    [Fact]
    public async Task many_parallel_acquires_of_one_key_yield_exactly_one_winner()
    {
        const int callers = 64;

        await using var harness = await SqliteStoreHarness.CreateAsync();

        var outcomes = new ConcurrentBag<IdempotencyOutcome>();

        // A barrier so the tasks fire their AcquireAsync as close to simultaneously as possible,
        // maximizing the real contention on the key rather than letting them serialize by accident.
        using var ready = new Barrier(callers);

        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            ready.SignalAndWait();
            var lease = await harness.Store.AcquireAsync("hot-key", "fp-1");
            outcomes.Add(lease.Outcome);
        }));

        await Task.WhenAll(tasks);

        var results = outcomes.ToArray();
        Assert.Equal(callers, results.Length);

        var winners = results.Count(o => o == IdempotencyOutcome.Acquired);
        Assert.Equal(1, winners);

        // No caller completed the key, so every loser must see it as in flight. Nothing should leak
        // a mismatch (same fingerprint) or a phantom completion (no response was stored).
        Assert.Equal(callers - 1, results.Count(o => o == IdempotencyOutcome.InProgress));
        Assert.DoesNotContain(IdempotencyOutcome.AlreadyCompleted, results);
        Assert.DoesNotContain(IdempotencyOutcome.FingerprintMismatch, results);
    }

    [Fact]
    public async Task the_single_winner_is_the_only_caller_that_may_complete_and_be_replayed()
    {
        const int callers = 32;

        await using var harness = await SqliteStoreHarness.CreateAsync();

        var leases = new ConcurrentBag<IdempotencyLease>();
        using var ready = new Barrier(callers);

        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            ready.SignalAndWait();
            leases.Add(await harness.Store.AcquireAsync("hot-key", "fp-1"));
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(1, leases.Count(l => l.Outcome == IdempotencyOutcome.Acquired));

        // The winner completes; a fresh acquire then replays exactly that response, confirming the
        // race left the store in a coherent single-owner state.
        await harness.Store.CompleteAsync("hot-key", new CachedResponse
        {
            StatusCode = 201,
            ContentType = "text/plain",
            Body = "winner"u8.ToArray(),
        });

        var replay = await harness.Store.AcquireAsync("hot-key", "fp-1");
        Assert.Equal(IdempotencyOutcome.AlreadyCompleted, replay.Outcome);
        Assert.Equal(201, replay.Response!.StatusCode);
        Assert.Equal("winner"u8.ToArray(), replay.Response.Body.ToArray());
    }
}
