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

    [Fact]
    public async Task A_serialize_failure_releases_the_claim_so_a_retry_can_rerun()
    {
        // The operation succeeds, but the codec throws while capturing its result. The claim must be
        // released so the key is not stuck in-flight until its TTL, and a retry can rerun.
        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var executor = new IdempotentExecutor(store);
        var throwingCodec = new DelegateResultCodec<int>(
            serialize: _ => throw new FormatException("cannot serialize"),
            deserialize: payload => BinaryPrimitives.ReadInt32BigEndian(payload));
        var calls = 0;

        await Assert.ThrowsAsync<FormatException>(() =>
            executor.ExecuteAsync("k1", "fp", _ =>
            {
                calls++;
                return Task.FromResult(1);
            }, throwingCodec));

        // The key was released: a retry with a working codec acquires it fresh and completes.
        var result = await executor.ExecuteAsync("k1", "fp", _ =>
        {
            calls++;
            return Task.FromResult(99);
        }, IntCodec);

        Assert.Equal(99, result);
        Assert.Equal(2, calls); // both the failed attempt and the retry ran the operation
    }

    [Fact]
    public async Task A_complete_failure_releases_the_claim_so_a_retry_can_rerun()
    {
        // The operation succeeds and the result serializes, but the store throws while writing the
        // completed entry. The claim must still be released so a retry can rerun under the same key.
        var inner = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var store = new ControllableStore(inner) { FailNextComplete = true };
        var executor = new IdempotentExecutor(store);

        await Assert.ThrowsAsync<IOException>(() =>
            executor.ExecuteAsync("k1", "fp", _ => Task.FromResult(1), IntCodec));

        Assert.True(store.ReleaseCalled); // cleanup released the claim
        store.FailNextComplete = false;

        // The key is free again, so the retry acquires it fresh and succeeds.
        var result = await executor.ExecuteAsync("k1", "fp", _ => Task.FromResult(42), IntCodec);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task A_failing_operation_releases_with_a_live_token_even_when_the_caller_token_is_canceled()
    {
        // The operation fails because it observed the caller's canceled token. A store that honors
        // cancellation would skip a release issued on that same canceled token, leaving the key
        // in-flight. The executor must release with a live token, so the release is recorded.
        var inner = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var store = new ControllableStore(inner) { SkipReleaseWhenTokenCanceled = true };
        var executor = new IdempotentExecutor(store);

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync<int>("k1", "fp", token =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(1);
            }, IntCodec, cts.Token));

        // The release was issued on an uncancelable token, so the cancellation-honoring store ran it.
        Assert.True(store.ReleaseCalled);
        Assert.False(store.ReleaseSkippedDueToCancellation);

        // And the key is genuinely free: a fresh call (new token) acquires and completes.
        var result = await executor.ExecuteAsync("k1", "fp", _ => Task.FromResult(7), IntCodec);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task A_throwing_release_does_not_mask_the_original_operation_failure()
    {
        // Release cleanup is best-effort: if ReleaseAsync throws on the failure path, the original
        // operation exception must still propagate unchanged, not be replaced by the release fault.
        var inner = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var store = new ControllableStore(inner) { FailNextRelease = true };
        var executor = new IdempotentExecutor(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync<int>("k1", "fp", _ => throw new InvalidOperationException("boom"), IntCodec));

        Assert.Equal("boom", ex.Message); // the original failure, not the release fault
        Assert.True(store.ReleaseCalled); // the best-effort release was attempted
    }

    [Fact]
    public async Task A_throwing_release_does_not_mask_a_capture_failure()
    {
        // Same best-effort guarantee on the capture path: a serialize failure must surface even when
        // the subsequent release cleanup itself throws.
        var inner = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5));
        var store = new ControllableStore(inner) { FailNextRelease = true };
        var executor = new IdempotentExecutor(store);
        var throwingCodec = new DelegateResultCodec<int>(
            serialize: _ => throw new FormatException("cannot serialize"),
            deserialize: payload => BinaryPrimitives.ReadInt32BigEndian(payload));

        var ex = await Assert.ThrowsAsync<FormatException>(() =>
            executor.ExecuteAsync("k1", "fp", _ => Task.FromResult(1), throwingCodec));

        Assert.Equal("cannot serialize", ex.Message);
        Assert.True(store.ReleaseCalled);
    }

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by) => Now += by;
    }

    // A decorator over a real store that lets a test inject faults and observe how the executor cleans
    // up. It also models a store that honors cancellation: when asked, it skips a release whose token
    // is already canceled, which is exactly the behavior the live-token cleanup must defeat.
    private sealed class ControllableStore(IIdempotencyStore inner) : IIdempotencyStore
    {
        public bool FailNextComplete { get; set; }

        public bool FailNextRelease { get; set; }

        public bool SkipReleaseWhenTokenCanceled { get; set; }

        public bool ReleaseCalled { get; private set; }

        public bool ReleaseSkippedDueToCancellation { get; private set; }

        public Task<IdempotencyLease> AcquireAsync(string key, string fingerprint, CancellationToken cancellationToken = default) =>
            inner.AcquireAsync(key, fingerprint, cancellationToken);

        public Task CompleteAsync(string key, CachedResponse response, CancellationToken cancellationToken = default)
        {
            if (FailNextComplete)
            {
                throw new IOException("store write failed");
            }

            return inner.CompleteAsync(key, response, cancellationToken);
        }

        public Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
        {
            ReleaseCalled = true;

            if (SkipReleaseWhenTokenCanceled && cancellationToken.IsCancellationRequested)
            {
                // A cancellation-honoring store would not perform the release on a canceled token.
                ReleaseSkippedDueToCancellation = true;
                return Task.CompletedTask;
            }

            if (FailNextRelease)
            {
                throw new InvalidOperationException("release failed");
            }

            return inner.ReleaseAsync(key, cancellationToken);
        }
    }
}
