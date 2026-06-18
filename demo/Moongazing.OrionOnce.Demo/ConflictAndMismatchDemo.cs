namespace Moongazing.OrionOnce.Demo;

using System.Text;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// Demonstrates the two rejection outcomes the store reports while a key is held:
/// an in-flight duplicate (same key, not yet completed) reports <c>InProgress</c> (the middleware
/// maps this to 409), and a key reused with a different body reports <c>FingerprintMismatch</c>
/// (mapped to 422).
/// </summary>
internal static class ConflictAndMismatchDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("3. In-flight conflict and fingerprint mismatch");

        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(10));

        const string key = "key-transfer-777";
        var body = Encoding.UTF8.GetBytes("""{"from":"A","to":"B","amount":50}""");
        var fingerprint = RequestFingerprint.Compute("POST", "/transfers", body);

        DemoConsole.Step("First request acquires the key (still in flight, not completed)");
        var first = await store.AcquireAsync(key, fingerprint);
        DemoConsole.Detail("outcome", first.Outcome.ToString());

        DemoConsole.Step("Duplicate arrives with the same key + same body while in flight");
        var inFlight = await store.AcquireAsync(key, fingerprint);
        DemoConsole.Detail("outcome", inFlight.Outcome.ToString());

        if (inFlight.Outcome != IdempotencyOutcome.InProgress)
        {
            throw new InvalidOperationException("An in-flight duplicate must report InProgress.");
        }

        DemoConsole.Pass("In-flight duplicate reported InProgress (middleware returns 409 Conflict).");

        DemoConsole.Step("Different request reuses the same key with a different body");
        var otherBody = Encoding.UTF8.GetBytes("""{"from":"A","to":"B","amount":5000}""");
        var otherFingerprint = RequestFingerprint.Compute("POST", "/transfers", otherBody);
        var mismatch = await store.AcquireAsync(key, otherFingerprint);
        DemoConsole.Detail("outcome", mismatch.Outcome.ToString());

        if (mismatch.Outcome != IdempotencyOutcome.FingerprintMismatch)
        {
            throw new InvalidOperationException("Key reuse with a different body must mismatch.");
        }

        DemoConsole.Pass("Key reuse with a different body reported FingerprintMismatch (422).");
    }
}
