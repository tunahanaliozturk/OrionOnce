namespace Moongazing.OrionOnce.Demo;

using System.Text;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// Demonstrates the full key lifecycle the middleware drives:
/// acquire + complete (success is cached and replayed), and acquire + release (a failed handler
/// releases the key so the client can safely retry, and the retry acquires the key cleanly).
/// </summary>
internal static class LifecycleDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("4. Lifecycle: complete on success, release on failure");

        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(10));
        var body = Encoding.UTF8.GetBytes("""{"sku":"WIDGET-9"}""");
        var fingerprint = RequestFingerprint.Compute("POST", "/orders", body);

        // --- Success path: acquire then complete. ---
        const string okKey = "key-order-success";
        DemoConsole.Step("Success path: acquire, run handler, complete");
        var okLease = await store.AcquireAsync(okKey, fingerprint);
        DemoConsole.Detail("acquire outcome", okLease.Outcome.ToString());

        await store.CompleteAsync(okKey, new CachedResponse
        {
            StatusCode = 200,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"sku":"WIDGET-9","status":"ok"}"""),
        });
        DemoConsole.Pass("Completed; the response is now cached for replay.");

        var afterComplete = await store.AcquireAsync(okKey, fingerprint);
        if (afterComplete.Outcome != IdempotencyOutcome.AlreadyCompleted)
        {
            throw new InvalidOperationException("A completed key must replay, not re-acquire.");
        }

        DemoConsole.Detail("retry outcome", afterComplete.Outcome.ToString());
        DemoConsole.Pass("Retry after completion replays the stored response.");

        // --- Failure path: acquire then release, so a retry can re-acquire. ---
        const string failKey = "key-order-failure";
        DemoConsole.Step("Failure path: acquire, handler throws, release the key");
        var failLease = await store.AcquireAsync(failKey, fingerprint);
        DemoConsole.Detail("acquire outcome", failLease.Outcome.ToString());

        // Simulate the middleware releasing a key when the handler throws or returns 5xx.
        await store.ReleaseAsync(failKey);
        DemoConsole.Pass("Handler failed; key released and nothing was cached.");

        DemoConsole.Step("Client retries the previously-failed request");
        var retry = await store.AcquireAsync(failKey, fingerprint);
        DemoConsole.Detail("retry outcome", retry.Outcome.ToString());

        if (retry.Outcome != IdempotencyOutcome.Acquired)
        {
            throw new InvalidOperationException("A released key must be re-acquirable.");
        }

        DemoConsole.Pass("Retry cleanly re-acquired the released key (no transient failure cached).");
    }
}
