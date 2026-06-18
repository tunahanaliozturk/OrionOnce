namespace Moongazing.OrionOnce.Demo;

using System.Text;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// Demonstrates the happy path of the store: a fresh key is acquired, the response is completed and
/// cached, and a retry with the same key + same fingerprint replays the stored response instead of
/// running the handler again.
/// </summary>
internal static class AcquireAndReplayDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("2. Acquire then replay (run once, replay thereafter)");

        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(10));

        const string key = "key-checkout-001";
        var body = Encoding.UTF8.GetBytes("""{"orderId":"A-100"}""");
        var fingerprint = RequestFingerprint.Compute("POST", "/checkout", body);

        DemoConsole.Step("First request acquires the key");
        var lease = await store.AcquireAsync(key, fingerprint);
        DemoConsole.Detail("outcome", lease.Outcome.ToString());

        if (lease.Outcome != IdempotencyOutcome.Acquired)
        {
            throw new InvalidOperationException("A fresh key must be acquired.");
        }

        DemoConsole.Pass("Fresh key acquired; this caller owns it and runs the handler.");

        var captured = new CachedResponse
        {
            StatusCode = 201,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"orderId":"A-100","status":"created"}"""),
        };

        await store.CompleteAsync(key, captured);
        DemoConsole.Pass("Handler ran; response captured and cached via CompleteAsync.");

        DemoConsole.Step("Retry with the same key + same fingerprint");
        var replay = await store.AcquireAsync(key, fingerprint);
        DemoConsole.Detail("outcome", replay.Outcome.ToString());

        if (replay.Outcome != IdempotencyOutcome.AlreadyCompleted || replay.Response is null)
        {
            throw new InvalidOperationException("A completed key must replay the stored response.");
        }

        DemoConsole.Detail("replayed status", replay.Response.StatusCode.ToString());
        DemoConsole.Detail("replayed body", Encoding.UTF8.GetString(replay.Response.Body.Span));
        DemoConsole.Pass("Retry replayed the stored response; the handler was not run again.");
    }
}
