namespace Moongazing.OrionOnce.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// Measures the two combined paths the middleware drives per guarded request: fingerprint the
/// request then claim a fresh key (first call), and fingerprint then resolve a completed key
/// (retry that replays). This pairs the SHA-256 cost with the store lookup the way production does,
/// so it shows the per-request floor rather than either step in isolation.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class RequestFlowBenchmarks
{
    private const string ReplayKey = "replay-key";

    private InMemoryIdempotencyStore store = null!;
    private byte[] body = [];
    private string replayFingerprint = string.Empty;
    private int counter;

    [GlobalSetup]
    public void Setup()
    {
        store = new InMemoryIdempotencyStore(TimeSpan.FromHours(24));
        body = new byte[512];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i & 0xFF);
        }

        replayFingerprint = RequestFingerprint.Compute("POST", "/payments", body);
        _ = store.AcquireAsync(ReplayKey, replayFingerprint).GetAwaiter().GetResult();
        store.CompleteAsync(ReplayKey, new CachedResponse
        {
            StatusCode = 201,
            ContentType = "application/json",
            Body = new byte[64],
        }).GetAwaiter().GetResult();
    }

    /// <summary>Fingerprint the request and claim a brand-new key: the first-call path.</summary>
    [Benchmark]
    public IdempotencyLease FingerprintAndAcquireFresh()
    {
        var fingerprint = RequestFingerprint.Compute("POST", "/payments", body);
        var key = "flow-" + Interlocked.Increment(ref counter);
        return store.AcquireAsync(key, fingerprint).GetAwaiter().GetResult();
    }

    /// <summary>Fingerprint the request and hit a completed key: the replay path.</summary>
    [Benchmark]
    public IdempotencyLease FingerprintAndAcquireReplay()
    {
        var fingerprint = RequestFingerprint.Compute("POST", "/payments", body);
        return store.AcquireAsync(ReplayKey, fingerprint).GetAwaiter().GetResult();
    }
}
