namespace Moongazing.OrionOnce.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionOnce.Storage;

/// <summary>
/// Measures the three steady-state outcomes of <see cref="InMemoryIdempotencyStore.AcquireAsync"/>
/// against a pre-populated store: a fresh key (claimed), a known key whose request has completed
/// (replayed), and a known key reused for a different body (mismatch). These are the branches the
/// middleware takes on every retried request, so the dictionary lookup plus lock cost here is the
/// dedup hot path.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class AcquireBenchmarks
{
    private const string CompletedKey = "completed-key";
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherFingerprint = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private InMemoryIdempotencyStore store = null!;
    private CachedResponse response = null!;
    private int freshCounter;

    [GlobalSetup]
    public void Setup()
    {
        store = new InMemoryIdempotencyStore(TimeSpan.FromHours(24));
        response = new CachedResponse
        {
            StatusCode = 201,
            ContentType = "application/json",
            Body = new byte[64],
        };

        // Seed one completed entry that the Replayed and Mismatch benchmarks resolve against.
        _ = store.AcquireAsync(CompletedKey, Fingerprint).GetAwaiter().GetResult();
        store.CompleteAsync(CompletedKey, response).GetAwaiter().GetResult();
    }

    /// <summary>A never-before-seen key: the claim path that runs once per genuine request.</summary>
    [Benchmark(Baseline = true)]
    public IdempotencyLease AcquireFresh()
    {
        var key = "fresh-" + Interlocked.Increment(ref freshCounter);
        return store.AcquireAsync(key, Fingerprint).GetAwaiter().GetResult();
    }

    /// <summary>A retried request whose first call already completed: the response is replayed.</summary>
    [Benchmark]
    public IdempotencyLease AcquireReplayed() =>
        store.AcquireAsync(CompletedKey, Fingerprint).GetAwaiter().GetResult();

    /// <summary>The same key reused for a different request body: rejected as a mismatch.</summary>
    [Benchmark]
    public IdempotencyLease AcquireMismatch() =>
        store.AcquireAsync(CompletedKey, OtherFingerprint).GetAwaiter().GetResult();
}
