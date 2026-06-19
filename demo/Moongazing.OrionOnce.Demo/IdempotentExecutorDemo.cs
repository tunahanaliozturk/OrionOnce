namespace Moongazing.OrionOnce.Demo;

using System.Text;
using System.Text.Json;

using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

/// <summary>
/// Demonstrates <see cref="IdempotentExecutor"/>: the non-HTTP counterpart of the middleware that
/// runs an operation once per idempotency key and replays the captured typed result on a later
/// duplicate call. The first call runs the operation and stores its result through the shared
/// <see cref="IIdempotencyStore"/>; the second call with the same key and fingerprint replays the
/// stored result without running the operation again. It then sweeps expired entries from the store.
/// </summary>
internal static class IdempotentExecutorDemo
{
    private sealed record Receipt(string OrderId, decimal Amount);

    public static async Task RunAsync()
    {
        DemoConsole.Section("5. IdempotentExecutor: capture a typed result and replay it");

        var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(10));
        var executor = new IdempotentExecutor(store);

        // The library ships no serializer; the caller supplies a codec. Here System.Text.Json
        // turns the Receipt into bytes for storage and back on replay.
        var codec = new DelegateResultCodec<Receipt>(
            serialize: receipt => JsonSerializer.SerializeToUtf8Bytes(receipt),
            deserialize: payload => JsonSerializer.Deserialize<Receipt>(payload)!,
            contentType: "application/json");

        const string key = "key-charge-42";
        var body = Encoding.UTF8.GetBytes("""{"orderId":"A-42","amount":19.99}""");
        var fingerprint = RequestFingerprint.Compute("POST", "/charge", body);

        var operationRuns = 0;

        Task<Receipt> Charge(CancellationToken _)
        {
            operationRuns++;
            return Task.FromResult(new Receipt("A-42", 19.99m));
        }

        DemoConsole.Step("First call: operation runs and the result is captured");
        var first = await executor.ExecuteAsync(key, fingerprint, Charge, codec);
        DemoConsole.Detail("order id", first.OrderId);
        DemoConsole.Detail("operation runs", operationRuns.ToString());

        DemoConsole.Step("Second call, same key: stored result is replayed, operation skipped");
        var second = await executor.ExecuteAsync(key, fingerprint, Charge, codec);
        DemoConsole.Detail("order id", second.OrderId);
        DemoConsole.Detail("operation runs", operationRuns.ToString());

        if (operationRuns != 1)
        {
            throw new InvalidOperationException("The operation must run exactly once for a key.");
        }

        if (second != first)
        {
            throw new InvalidOperationException("The replayed result must equal the captured result.");
        }

        DemoConsole.Pass("Operation ran once; the duplicate call replayed the captured receipt.");

        DemoConsole.Step("A different fingerprint under the same key is rejected, not replayed");
        var otherFingerprint = RequestFingerprint.Compute("POST", "/charge", Encoding.UTF8.GetBytes("{}"));
        try
        {
            await executor.ExecuteAsync(key, otherFingerprint, Charge, codec);
            throw new InvalidOperationException("A fingerprint mismatch must throw.");
        }
        catch (IdempotentExecutionException ex)
        {
            DemoConsole.Detail("outcome", ex.Outcome.ToString());
            DemoConsole.Pass("Key reused for a different operation surfaced as IdempotentExecutionException.");
        }

        DemoConsole.Step("SweepAsync reclaims entries whose retention window has elapsed");
        var removed = await store.SweepAsync();
        DemoConsole.Detail("entries removed", removed.ToString());
        DemoConsole.Note("Nothing expired yet, so the live entry stays; sweep returns 0 here.");
    }
}
