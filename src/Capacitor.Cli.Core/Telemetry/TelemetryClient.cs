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
        List<TelemetryEvent> pending;
        lock (_queue) {
            pending = [.. spool.DrainAll(), .. _queue];
            _queue.Clear();
        }

        if (pending.Count == 0) return true;

        var body = PostHogPayload.Build(pending, token, distinctId, orgGroup);

        try {
            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = budget };
            using var cts  = new CancellationTokenSource(budget);
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await http.PostAsync($"{endpoint.TrimEnd('/')}/batch/", content, cts.Token);

            if (!response.IsSuccessStatusCode) {
                spool.Append(pending);
                return false;
            }

            spool.Clear();
            return true;
        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException) {
            spool.Append(pending);
            return false;
        }
    }
}
