using System.Net.Http.Headers;
using System.Text;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Queues events and ships them to PostHog's <c>/batch/</c> endpoint under a wall-clock budget.
/// A flush that fails for any reason spills to the spool instead of retrying inline — the
/// caller is on a user's command path and must not wait on a broken network.
/// </summary>
public sealed class TelemetryClient(
        HttpMessageHandler handler, TelemetrySpool spool, string token, string endpoint) {
    readonly List<TelemetryEvent> _queue = [];

    public void Enqueue(TelemetryEvent e) {
        lock (_queue) _queue.Add(e);
    }

    /// <summary>Ships queued + previously spooled events. Returns false when nothing reached
    /// PostHog, in which case everything has been spooled for a later attempt.</summary>
    public async Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) {
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

            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = budget };
            using var cts  = new CancellationTokenSource(budget);
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
