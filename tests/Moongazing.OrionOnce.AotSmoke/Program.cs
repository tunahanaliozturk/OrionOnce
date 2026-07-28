// NativeAOT smoke test. Publishing this with PublishAot=true must produce zero trim/AOT warnings,
// and running it must exit 0 - OrionOnce's AOT exit criterion. Runtime checks, not a framework:
// the point is to prove the idempotency store (acquire → complete → replay → fingerprint mismatch)
// survives trimming in a real native binary.
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

var services = new ServiceCollection();
services.AddOrionOnce();

using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<IIdempotencyStore>();

// First acquire under a fresh key wins the lease.
var first = await store.AcquireAsync("key-1", "fingerprint-a");
Check(first.Outcome == IdempotencyOutcome.Acquired, $"first acquire should be Acquired, was {first.Outcome}");

// Complete it with a cached response.
byte[] body = [1, 2, 3];
await store.CompleteAsync("key-1", new CachedResponse
{
    StatusCode = 200,
    ContentType = "application/json",
    Body = body,
});

// A repeat with the same fingerprint replays the completed response.
var replay = await store.AcquireAsync("key-1", "fingerprint-a");
Check(replay.Outcome == IdempotencyOutcome.AlreadyCompleted, $"repeat should replay AlreadyCompleted, was {replay.Outcome}");
Check(replay.Response is { StatusCode: 200 }, "replayed response missing or wrong");

// A different fingerprint on the same key is a mismatch.
var mismatch = await store.AcquireAsync("key-1", "fingerprint-b");
Check(mismatch.Outcome == IdempotencyOutcome.FingerprintMismatch, $"different fingerprint should mismatch, was {mismatch.Outcome}");

Console.WriteLine("OrionOnce AOT smoke test passed.");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"AOT smoke test failed: {message}");
        Environment.Exit(1);
    }
}
