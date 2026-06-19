namespace Moongazing.OrionOnce.Diagnostics;

using System.Diagnostics.Metrics;

/// <summary>
/// OpenTelemetry instrumentation for the idempotency middleware. Exposes a <see cref="Meter"/>
/// named <c>Moongazing.OrionOnce</c> with a single outcome-tagged counter. Registered as a
/// singleton; dispose it to release the meter.
/// </summary>
public sealed class IdempotencyDiagnostics : IDisposable
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionOnce";

    private readonly Meter meter;

    /// <summary>Create the meter and its instruments.</summary>
    public IdempotencyDiagnostics()
    {
        meter = new Meter(MeterName, "0.2.0");
        Requests = meter.CreateCounter<long>(
            "oriononce.requests",
            unit: "{request}",
            description: "Requests seen by the idempotency middleware, tagged outcome "
                + "(acquired/replayed/in_progress/mismatch/missing_key/bypassed).");
    }

    /// <summary>Counts requests by idempotency outcome.</summary>
    public Counter<long> Requests { get; }

    /// <summary>Record one request outcome.</summary>
    /// <param name="outcome">The outcome tag value.</param>
    public void Record(string outcome) =>
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <inheritdoc />
    public void Dispose() => meter.Dispose();
}
