namespace Moongazing.OrionOnce.Demo;

/// <summary>
/// Runnable console walkthrough of OrionOnce's core idempotency services, driven directly without
/// starting a web server. It exercises the request fingerprint, a fresh acquire, replay of a
/// completed response, the in-flight conflict and fingerprint-mismatch rejections, and the full
/// acquire/complete and acquire/release lifecycle. The program runs to completion and exits.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("OrionOnce demo - HTTP idempotency core services");
        Console.WriteLine("Driving RequestFingerprint and InMemoryIdempotencyStore directly (no Kestrel).");

        try
        {
            FingerprintDemo.Run();
            await AcquireAndReplayDemo.RunAsync();
            await ConflictAndMismatchDemo.RunAsync();
            await LifecycleDemo.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Demo FAILED: {ex.Message}");
            return 1;
        }

        DemoConsole.Section("Done");
        DemoConsole.Note("All idempotency outcomes behaved as expected. Exiting.");
        return 0;
    }
}
