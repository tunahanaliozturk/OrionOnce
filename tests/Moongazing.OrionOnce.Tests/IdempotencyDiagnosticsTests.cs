namespace Moongazing.OrionOnce.Tests;

using System.Diagnostics.Metrics;

using Moongazing.OrionOnce.Diagnostics;

using Xunit;

/// <summary>
/// Serializes every test class that emits to the process-global
/// <c>Moongazing.OrionOnce</c> meter. The meter is named by a shared constant, so under
/// xUnit's default per-class parallelism a <see cref="MeterListener"/> filtering by meter
/// name observes measurements emitted by sibling classes running concurrently. Membership in
/// this non-parallel collection guarantees no foreign emitter is active during the
/// listener-based assertions.
/// </summary>
[CollectionDefinition(nameof(MeterSerial), DisableParallelization = true)]
public sealed class MeterSerial
{
}

[Collection(nameof(MeterSerial))]
public sealed class IdempotencyDiagnosticsTests
{
    [Fact]
    public void The_meter_name_is_stable()
    {
        // OpenTelemetry consumers subscribe by this exact name; a change breaks their wiring.
        Assert.Equal("Moongazing.OrionOnce", IdempotencyDiagnostics.MeterName);
    }

    [Fact]
    public void Record_does_not_throw_when_no_listener_is_attached()
    {
        using var diagnostics = new IdempotencyDiagnostics();

        var exception = Record.Exception(() => diagnostics.Record("acquired"));

        Assert.Null(exception);
    }

    [Fact]
    public void The_requests_counter_is_exposed()
    {
        using var diagnostics = new IdempotencyDiagnostics();

        Assert.NotNull(diagnostics.Requests);
    }

    [Fact]
    public void Record_emits_a_measurement_tagged_with_the_outcome()
    {
        using var diagnostics = new IdempotencyDiagnostics();

        var observed = new List<(long Value, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Bind to the exact instrument instance under test, not the meter name. The meter
            // name is a shared process-global constant, so a name match would also enable
            // measurements from a sibling test class's IdempotencyDiagnostics instance.
            if (ReferenceEquals(instrument, diagnostics.Requests))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcome = tag.Value as string;
                }
            }
            observed.Add((measurement, outcome));
        });
        listener.Start();

        diagnostics.Record("replayed");
        listener.RecordObservableInstruments();

        Assert.Contains(observed, m => m.Value == 1 && m.Outcome == "replayed");
    }

    [Fact]
    public void Dispose_can_be_called_more_than_once()
    {
        var diagnostics = new IdempotencyDiagnostics();
        diagnostics.Dispose();

        var exception = Record.Exception(diagnostics.Dispose);

        Assert.Null(exception);
    }
}
