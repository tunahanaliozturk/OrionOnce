namespace Moongazing.OrionOnce.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionOnce.Storage;

/// <summary>
/// Measures whole key lifecycles end to end against <see cref="InMemoryIdempotencyStore"/>: the
/// success path (acquire then complete, which a request runs exactly once) and the failure path
/// (acquire then release, which a request takes when the handler throws or returns 5xx so the key
/// is freed for retry). Each iteration uses a unique key so the store does the dictionary insert,
/// update, or removal the real flow performs rather than re-reading a warm entry.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class LifecycleBenchmarks
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private InMemoryIdempotencyStore store = null!;
    private CachedResponse response = null!;
    private int counter;

    [GlobalSetup]
    public void Setup()
    {
        store = new InMemoryIdempotencyStore(TimeSpan.FromHours(24));
        response = new CachedResponse
        {
            StatusCode = 201,
            ContentType = "application/json",
            Body = new byte[256],
        };
    }

    /// <summary>Acquire a fresh key then cache its response: the once-only success path.</summary>
    [Benchmark]
    public async Task<IdempotencyLease> AcquireThenComplete()
    {
        var key = "ok-" + Interlocked.Increment(ref counter);
        var lease = await store.AcquireAsync(key, Fingerprint).ConfigureAwait(false);
        await store.CompleteAsync(key, response).ConfigureAwait(false);
        return lease;
    }

    /// <summary>Acquire a fresh key then release it: the handler-failed retry path.</summary>
    [Benchmark]
    public async Task<IdempotencyLease> AcquireThenRelease()
    {
        var key = "fail-" + Interlocked.Increment(ref counter);
        var lease = await store.AcquireAsync(key, Fingerprint).ConfigureAwait(false);
        await store.ReleaseAsync(key).ConfigureAwait(false);
        return lease;
    }
}
