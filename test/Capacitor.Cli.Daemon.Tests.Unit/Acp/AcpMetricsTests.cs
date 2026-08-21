using System.Diagnostics.Metrics;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// A light smoke test that <see cref="AcpMetrics"/>'s
/// <see cref="Meter"/>/<see cref="Counter{T}"/> construction and every increment call site are
/// AOT-safe (no throw) and actually publish a measurement observable via
/// <see cref="MeterListener"/> — the same mechanism <c>dotnet-counters</c> uses. No reconnect
/// counters are covered here — none exist yet (a later phase).
/// </summary>
public class AcpMetricsTests {
    [Test]
    public async Task AllCounters_CanBeIncremented_WithoutThrowing() =>
        await Assert.That(() => {
            AcpMetrics.Launches.Add(1);
            AcpMetrics.SessionsStarted.Add(1);
            AcpMetrics.RecordBlockingRequest("permission");
            AcpMetrics.RecordBlockingRequest("elicitation");
            AcpMetrics.RecordFailure("handshake");
            AcpMetrics.RecordElicitationUnrenderable("malformed_schema");
        }).ThrowsNothing();

    [Test]
    public async Task RecordBlockingRequest_PublishesAMeasurement_TaggedWithKind() {
        long?   observedValue = null;
        string? observedKind  = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => {
            if (instrument.Meter.Name == "Capacitor.Cli.Daemon.Acp" && instrument.Name == "acp.blocking_requests")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) => {
            observedValue = measurement;

            foreach (var tag in tags) {
                if (tag.Key == "kind")
                    observedKind = tag.Value?.ToString();
            }
        });
        listener.Start();

        AcpMetrics.RecordBlockingRequest("elicitation");

        await Assert.That(observedValue).IsEqualTo(1L);
        await Assert.That(observedKind).IsEqualTo("elicitation");
    }

    [Test]
    public async Task RecordFailure_PublishesAMeasurement_TaggedWithStage() {
        long?   observedValue = null;
        string? observedStage = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => {
            if (instrument.Meter.Name == "Capacitor.Cli.Daemon.Acp" && instrument.Name == "acp.failures")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) => {
            observedValue = measurement;

            foreach (var tag in tags) {
                if (tag.Key == "stage")
                    observedStage = tag.Value?.ToString();
            }
        });
        listener.Start();

        AcpMetrics.RecordFailure("handshake");

        await Assert.That(observedValue).IsEqualTo(1L);
        await Assert.That(observedStage).IsEqualTo("handshake");
    }

    [Test]
    public async Task RecordElicitationUnrenderable_PublishesAMeasurement_TaggedWithReason() {
        // A unique reason token so concurrent tests incrementing the same static counter with
        // real reasons can't satisfy (or pollute) this assertion.
        const string uniqueReason = "test_reason_metrics_pin";
        long? observedValue = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => {
            if (instrument.Meter.Name == "Capacitor.Cli.Daemon.Acp" && instrument.Name == "acp.elicitation_unrenderable")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) => {
            foreach (var tag in tags) {
                if (tag.Key == "reason" && (tag.Value?.ToString()) == uniqueReason)
                    observedValue = measurement;
            }
        });
        listener.Start();

        AcpMetrics.RecordElicitationUnrenderable(uniqueReason);

        await Assert.That(observedValue).IsEqualTo(1);
    }
}
