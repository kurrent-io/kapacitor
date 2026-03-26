using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using kapacitor.Auth;

namespace kapacitor;

static class HttpClientExtensions {
    /// <summary>
    /// Creates an HttpClient with a Bearer token from the local token store.
    /// Checks auth discovery first — if the server uses "None" provider, skips auth entirely.
    /// All CLI commands that call the Capacitor server should use this
    /// instead of <c>new HttpClient()</c>.
    /// </summary>
    public static async Task<HttpClient> CreateAuthenticatedClientAsync() {
        var client = new HttpClient();

        var baseUrl = Environment.GetEnvironmentVariable("KAPACITOR_URL") ?? "http://localhost:5108";
        var provider = await DiscoverProviderAsync(baseUrl);

        if (provider == "None")
            return client; // No auth needed

        var tokens = await TokenStore.GetValidTokensAsync();

        if (tokens is not null) {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        }

        return client;
    }

    static string? _cachedProvider;

    public static async Task<string> DiscoverProviderAsync(string baseUrl) {
        if (_cachedProvider is not null) return _cachedProvider;

        using var http = new HttpClient();
        try {
            var response = await http.GetAsync($"{baseUrl}/auth/config");
            if (response.IsSuccessStatusCode) {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                var provider = json.GetProperty("provider").GetString() ?? "None";
                _cachedProvider = provider; // Only cache successful discovery
                return provider;
            }
        } catch {
            // Server unreachable — don't cache, try tokens as fallback
        }

        // Fallback: try existing tokens (don't cache — allow re-discovery next time)
        return TokenStore.Load()?.Provider ?? "None";
    }

    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxDelay       = TimeSpan.FromSeconds(4);

    const string UnreachableHint =
        "Kurrent Capacitor API cannot be reached, is it running? "                    +
        "Make sure the URL is correctly configured and the service is running. "      +
        "Check https://github.com/kurrent-io/claude-remember#setup for instructions." +
        "\rError connecting to: ";

    extension(HttpClient client) {
        public Task<HttpResponseMessage> PostWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            )
            => SendWithRetryAsync(() => client.PostAsync(url, content, ct), timeout ?? DefaultTimeout, ct);

        public Task<HttpResponseMessage> GetWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default)
            => SendWithRetryAsync(() => client.GetAsync(url, ct), timeout ?? DefaultTimeout, ct);
    }

    /// <summary>
    /// Writes a structured JSON error to stderr for when the API is unreachable after all retries.
    /// </summary>
    public static void WriteUnreachableError(string baseUrl, HttpRequestException ex) {
        Console.Error.WriteLine($"{UnreachableHint} {baseUrl} {ex.Message}");
    }

    static async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> send, TimeSpan timeout, CancellationToken ct) {
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
