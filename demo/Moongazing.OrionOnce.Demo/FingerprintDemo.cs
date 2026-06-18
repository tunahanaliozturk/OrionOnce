namespace Moongazing.OrionOnce.Demo;

using System.Text;

using Moongazing.OrionOnce;

/// <summary>
/// Demonstrates <see cref="RequestFingerprint.Compute"/>: the SHA-256 that binds an idempotency
/// key to the identity of a request (method, path, body). Same request yields the same fingerprint;
/// any change to method, path, or body yields a different one.
/// </summary>
internal static class FingerprintDemo
{
    public static void Run()
    {
        DemoConsole.Section("1. RequestFingerprint.Compute (request identity)");

        var body = Encoding.UTF8.GetBytes("""{"amount":4200,"currency":"EUR"}""");

        var first = RequestFingerprint.Compute("POST", "/payments", body);
        var repeat = RequestFingerprint.Compute("POST", "/payments", body);

        DemoConsole.Step("Same method + path + body computed twice");
        DemoConsole.Detail("fingerprint #1", first);
        DemoConsole.Detail("fingerprint #2", repeat);

        if (!string.Equals(first, repeat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Identical requests must share a fingerprint.");
        }

        DemoConsole.Pass("Identical requests produce an identical fingerprint (replay is possible).");

        var differentBody = Encoding.UTF8.GetBytes("""{"amount":9900,"currency":"EUR"}""");
        var changed = RequestFingerprint.Compute("POST", "/payments", differentBody);

        DemoConsole.Step("Same key target, but a different body");
        DemoConsole.Detail("fingerprint (other)", changed);

        if (string.Equals(first, changed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different body must change the fingerprint.");
        }

        DemoConsole.Pass("A different body produces a different fingerprint (key reuse is detectable).");

        var differentPath = RequestFingerprint.Compute("POST", "/payments?expedite=1", body);
        if (string.Equals(first, differentPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different path/query must change the fingerprint.");
        }

        DemoConsole.Pass("A different path or query string also changes the fingerprint.");
    }
}
