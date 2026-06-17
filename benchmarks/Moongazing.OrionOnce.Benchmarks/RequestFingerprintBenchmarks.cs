namespace Moongazing.OrionOnce.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionOnce;

/// <summary>
/// Measures <see cref="RequestFingerprint.Compute"/>, the SHA-256 over method, path, and body that
/// binds an idempotency key to a specific request. This runs on every guarded request, so its cost
/// scales with body size and is on the hot path before the store is even consulted.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class RequestFingerprintBenchmarks
{
    private byte[] body = [];

    /// <summary>Body sizes in bytes: a tiny JSON payload, a typical request, and a large one.</summary>
    [Params(64, 4096, 262144)]
    public int BodyBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        body = new byte[BodyBytes];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i & 0xFF);
        }
    }

    [Benchmark]
    public string Compute() => RequestFingerprint.Compute("POST", "/payments", body);
}
