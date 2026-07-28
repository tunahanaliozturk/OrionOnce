namespace Moongazing.OrionOnce.Diagnostics;

using System.Diagnostics.Metrics;

using Moongazing.Orion.Abstractions.Diagnostics;

/// <summary>
/// OpenTelemetry instrumentation for the idempotency middleware. Built on the Orion family's
/// <see cref="OrionInstrumentation"/> spine, so it shares the family's naming and static-tag
/// conventions: a <see cref="Meter"/> named <c>Moongazing.OrionOnce</c> (subscribe by that name)
/// exposing the outcome-tagged counter <c>orion.once.requests</c>. Multi-tenant / multi-region
/// labels configured through <see cref="OrionInstrumentation.SetStaticTags"/> are stamped onto every
/// measurement. Registered as a singleton; dispose it to release the meter.
/// </summary>
public sealed class IdempotencyDiagnostics : OrionInstrumentation
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionOnce";

    /// <summary>Create the meter and its instruments.</summary>
    public IdempotencyDiagnostics()
        : base(OrionTelemetry.ScopeName("OrionOnce"), MeterVersion.Value)
    {
        Requests = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("once", "requests"),
            unit: "{request}",
            description: "Requests seen by the idempotency middleware, tagged outcome "
                + "(acquired/replayed/in_progress/mismatch/missing_key/bypassed).");
    }

    /// <summary>Counts requests by idempotency outcome.</summary>
    public Counter<long> Requests { get; }

    /// <summary>Record one request outcome.</summary>
    /// <param name="outcome">The outcome tag value.</param>
    public void Record(string outcome) =>
        Requests.Add(1, Tag(new KeyValuePair<string, object?>(OrionTelemetry.Tags.Outcome, outcome)));
}
