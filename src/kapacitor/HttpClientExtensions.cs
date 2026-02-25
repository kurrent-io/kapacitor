using System.Diagnostics;
using System.Text.Json;

namespace kapacitor;

static class HttpClientExtensions {
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxDelay       = TimeSpan.FromSeconds(4);

    const string UnreachableHint =
        "Kurrent Capacitor API cannot be reached, is it running? "               +
        "Make sure the URL is correctly configured and the service is running. " +
        "Check https://github.com/kurrent-io/claude-remember#setup for instructions.";

    extension(HttpClient client) {
        public Task<HttpResponseMessage> PostWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            )
            => SendWithRetryAsync(() => client.PostAsync(url, content, ct), timeout ?? DefaultTimeout, ct);

        public Task<HttpResponseMessage> GetWithRetryAsync(
                string            url,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            )
            => SendWithRetryAsync(() => client.GetAsync(url, ct), timeout ?? DefaultTimeout, ct);
    }

    /// <summary>
    /// Writes a structured JSON error to stderr for when the API is unreachable after all retries.
    /// </summary>
    public static void WriteUnreachableError(string baseUrl, HttpRequestException ex) {
        Console.Error.WriteLine($"{baseUrl} {ex.Message}");
        // var error = new ApiError("connection_failed", ex.Message, UnreachableHint, baseUrl);
        // Console.Error.WriteLine(JsonSerializer.Serialize(error, KapacitorJsonContext.Default.ApiError));
    }

    static async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<Task<HttpResponseMessage>> send,
            TimeSpan                        timeout,
            CancellationToken               ct
        ) {
        var sw      = Stopwatch.StartNew();
        var delayMs = 250;

        while (true) {
            try {
                return await send();
            } catch (HttpRequestException) when (!ct.IsCancellationRequested && sw.Elapsed < timeout) {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, (int)MaxDelay.TotalMilliseconds);
            }
        }
    }
}
