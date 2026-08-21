using System.Net.Http.Headers;
using System.Text;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Queues events and ships them to PostHog's <c>/batch/</c> endpoint under a wall-clock budget.
/// A flush that fails for any reason spills to the spool instead of retrying inline — the
/// caller is on a user's command path and must not wait on a broken network.
/// </summary>
public sealed class TelemetryClient(
        HttpMessageHandler handler, TelemetrySpool spool, string token, string endpoint,
        TimeProvider? timeProvider = null) {
    readonly List<TelemetryEvent> _queue = [];

    // Test seam only: production always uses the default (real) clock. Lets a test simulate a
    // slow drain/serialize phase deterministically instead of racing real wall-clock timing.
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public void Enqueue(TelemetryEvent e) {
        lock (_queue) _queue.Add(e);
    }

    /// <summary>Ships queued + previously spooled events. Returns false when nothing reached
    /// PostHog, in which case everything has been spooled for a later attempt.
    ///
    /// <paramref name="budget"/> bounds this WHOLE call, not just the HTTP phase: timing starts
    /// here, before <see cref="TelemetrySpool.DrainAll"/> (disk I/O, up to 2000 spooled lines)
    /// and <see cref="PostHogPayload.Build"/> (serializing the whole batch) — both of which run
    /// synchronously on the caller's command path (<c>CliTelemetry.CaptureNow</c>, the
    /// ProcessExit handler). Without that, a slow disk with a large spool could stall a command
    /// well past the intended budget before the HTTP phase even started timing.</summary>
    public async Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) {
        var start = _clock.GetTimestamp();

        List<TelemetryEvent> queued;
        lock (_queue) {
            queued = [.. _queue];
            _queue.Clear();
        }

        try {
            // Drained OUTSIDE the lock: it is file I/O, and blocking a concurrent Enqueue on disk
            // is not what the queue lock is for. Moved inside try as belt-and-braces: even if
            // TelemetrySpool.DrainAll now catches broadly, a pathological config path can still
            // escape a third category of exception, and we must never throw to the NativeAOT runtime.
            //
            // Spooled and queued events are kept apart deliberately. DrainAll is a read, not a take,
            // so re-appending the spooled ones on failure would duplicate them on every retry, and
            // the eventual success would ship the duplicates. Only the queued ones need spilling.
            var spooled = spool.DrainAll();
            var pending = new List<TelemetryEvent>(spooled.Count + queued.Count);
            pending.AddRange(spooled);   // spool first: previously-failed events keep their place in the funnel
            pending.AddRange(queued);

            if (pending.Count == 0) return true;

            var body = PostHogPayload.Build(pending, token, distinctId, orgGroup);

            // Whatever the drain + build above already spent comes out of the budget before the
            // HTTP phase gets any of it. If that alone exhausted it, a request could not finish
            // in time regardless of how it's timed out — spill instead of starting one that has
            // no chance of completing within budget.
            var remaining = budget - _clock.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero) {
                spool.Append(queued);
                return false;
            }

            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = remaining };
            using var cts  = new CancellationTokenSource(remaining);
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await http.PostAsync($"{endpoint.TrimEnd('/')}/batch/", content, cts.Token);

            if (!response.IsSuccessStatusCode) {
                spool.Append(queued);
                return false;
            }

            spool.Clear();
            return true;
        } catch {
            // Telemetry code must NEVER throw: under NativeAOT this becomes SIGABRT. Any exception —
            // whether from draining the spool, building the payload, setting the budget, network/serialization errors, or
            // anything else — spills the queued events for replay, exactly like a failed POST.
            spool.Append(queued);
            return false;
        }
    }
}
