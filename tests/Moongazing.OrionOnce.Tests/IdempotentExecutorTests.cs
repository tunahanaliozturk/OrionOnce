namespace Moongazing.OrionOnce.Tests;

using System.Buffers.Binary;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

using Xunit;

public sealed class IdempotentExecutorTests
{
    // A trivial int codec: four big-endian bytes. Keeps the tests free of a JSON dependency.
    private static readonly IIdempotentResultCodec<int> IntCodec = new DelegateResultCodec<int>(
        serialize: value =>
        {
            var bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        },
        deserialize: payload => BinaryPrimitives.ReadInt32BigEndian(payload),
        contentType: "application/octet-stream");

    [Fact]
    public async Task First_execution_runs_the_operation_and_returns_its_result()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);
        var calls = 0;

        var result = await executor.ExecuteAsync("k1", "fp", _ =>
        {
            calls++;
            return Task.FromResult(42);
        }, IntCodec);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_duplicate_call_replays_the_captured_result_without_re_running()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);
        var calls = 0;

        Task<int> Operation(CancellationToken _)
        {
            calls++;
            return Task.FromResult(7);
        }

        var first = await executor.ExecuteAsync("k1", "fp", Operation, IntCodec);
        var second = await executor.ExecuteAsync("k1", "fp", Operation, IntCodec);

        Assert.Equal(7, first);
        Assert.Equal(7, second); // replayed, same value
        Assert.Equal(1, calls); // operation ran exactly once
    }

    [Fact]
    public async Task An_expired_key_re_runs_the_operation()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromSeconds(30), () => clock.Now);
        var executor = new IdempotentExecutor(store);
        var calls = 0;

        Task<int> Operation(CancellationToken _) => Task.FromResult(++calls);

        var first = await executor.ExecuteAsync("k1", "fp", Operation, IntCodec);
        clock.Advance(TimeSpan.FromSeconds(31));
        var second = await executor.ExecuteAsync("k1", "fp", Operation, IntCodec);

        Assert.Equal(1, first);
        Assert.Equal(2, second); // re-executed after expiry, fresh value
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_different_fingerprint_for_a_live_key_throws_a_mismatch()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);
        await executor.ExecuteAsync("k1", "fp-a", _ => Task.FromResult(1), IntCodec);

        var ex = await Assert.ThrowsAsync<IdempotentExecutionException>(() =>
            executor.ExecuteAsync("k1", "fp-b", _ => Task.FromResult(2), IntCodec));

        Assert.Equal(IdempotencyOutcome.FingerprintMismatch, ex.Outcome);
    }

    [Fact]
    public async Task An_in_flight_key_throws_in_progress_for_a_second_caller()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);

        // Hold the first execution open while a second caller observes the in-flight claim.
        var release = new TaskCompletionSource();
        var first = executor.ExecuteAsync("k1", "fp", async _ =>
        {
            await release.Task;
            return 1;
        }, IntCodec);

        var ex = await Assert.ThrowsAsync<IdempotentExecutionException>(() =>
            executor.ExecuteAsync("k1", "fp", _ => Task.FromResult(2), IntCodec));

        Assert.Equal(IdempotencyOutcome.InProgress, ex.Outcome);

        release.SetResult();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task A_failed_operation_releases_the_key_so_a_retry_can_run()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync<int>("k1", "fp", _ => throw new InvalidOperationException("boom"), IntCodec));

        // The key was released, so the retry acquires it fresh and succeeds.
        var result = await executor.ExecuteAsync("k1", "fp", _ => Task.FromResult(99), IntCodec);

        Assert.Equal(99, result);
    }

    [Fact]
    public async Task Concurrent_duplicate_callers_yield_exactly_one_execution()
    {
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);
        var executions = 0;
        const int racers = 32;

        using var start = new ManualResetEventSlim(false);
        var tasks = new Task<int>[racers];
        for (var i = 0; i < racers; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                start.Wait();
                try
                {
                    return await executor.ExecuteAsync("k1", "fp", _ =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(123);
                    }, IntCodec);
                }
                catch (IdempotentExecutionException)
                {
                    // A concurrent loser sees the in-flight claim; that is the expected outcome.
                    return -1;
                }
            });
        }

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, executions); // the operation body ran exactly once
        Assert.Contains(123, results); // the single winner produced the result
    }

    [Fact]
    public void A_null_store_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotentExecutor(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Execute_rejects_a_null_or_empty_key(string? key)
    {
        var executor = new IdempotentExecutor(new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5)));

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            executor.ExecuteAsync(key!, "fp", _ => Task.FromResult(1), IntCodec));
    }

    [Fact]
    public async Task Execute_rejects_a_null_fingerprint()
    {
        var executor = new IdempotentExecutor(new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5)));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            executor.ExecuteAsync("k1", null!, _ => Task.FromResult(1), IntCodec));
    }

    [Fact]
    public async Task Execute_rejects_a_null_operation()
    {
        var executor = new IdempotentExecutor(new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5)));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            executor.ExecuteAsync<int>("k1", "fp", null!, IntCodec));
    }

    [Fact]
    public async Task Execute_rejects_a_null_codec()
    {
        var executor = new IdempotentExecutor(new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5)));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            executor.ExecuteAsync("k1", "fp", _ => Task.FromResult(1), null!));
    }

    [Fact]
    public async Task A_replayed_reference_result_round_trips_through_the_codec()
    {
        // Replay must reconstruct an equal value, not the same instance: the codec decodes bytes.
        var codec = new DelegateResultCodec<string>(
            serialize: s => System.Text.Encoding.UTF8.GetBytes(s),
            deserialize: payload => System.Text.Encoding.UTF8.GetString(payload));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);

        var first = await executor.ExecuteAsync("k1", "fp", _ => Task.FromResult("hello"), codec);
        var second = await executor.ExecuteAsync("k1", "fp", _ => Task.FromResult("world"), codec);

        Assert.Equal("hello", first);
        Assert.Equal("hello", second); // the captured value, not the second operation's "world"
    }

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by) => Now += by;
    }
}
