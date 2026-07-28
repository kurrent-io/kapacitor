using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

/// <summary>
/// Outcome of building an authenticated client. Lets hook callers decide whether a POST is even
/// worth attempting and whether to surface a re-auth prompt, instead of blindly POSTing a request
/// the server will reject with 401.
/// </summary>
public enum AuthStatus {
    /// <summary>A valid Bearer token is attached to the client.</summary>
    Ok,

    /// <summary>The server requires no auth ("None" provider) — the client is usable as-is.</summary>
    NoAuthRequired,

    /// <summary>A token is stored but expired and could not be refreshed — re-login required.</summary>
    Expired,

    /// <summary>No token is stored at all — login required.</summary>
    NotAuthenticated,

    /// <summary>
    /// A token is stored, but it was minted by a different server than the one being targeted.
    /// No refresh can heal this (the server validates the token's own signature) — only a login
    /// against the target server, or switching to the profile that owns it.
    /// </summary>
    WrongServer,
}

public static class HttpClientExtensions {
    /// <summary>
    /// Builds an <see cref="HttpClient"/> and reports the auth outcome. Attaches a Bearer token when
    /// one is valid (refreshing transparently if needed); otherwise leaves the client unauthenticated
    /// and returns the reason. Does NOT write to stderr — the caller chooses how, and whether, to
    /// surface it. Hook callers should prefer this over <see cref="CreateAuthenticatedClientAsync"/>
    /// so they can stay quiet on high-frequency events and exit cleanly instead of erroring per-turn.
    /// </summary>
    public static async Task<(HttpClient Client, AuthStatus Status)> CreateClientWithAuthStatusAsync(
        string? baseUrl = null, CancellationToken ct = default, bool allowAutoRedirect = true,
        string? rejectedAccessToken = null) {
        var (client, status, _) = await CreateClientCoreAsync(baseUrl, ct, allowAutoRedirect,
            rejectedAccessToken, autoRetryUnauthorized: false);

        return (client, status);
    }

    /// <summary>
    /// Shared client construction. Returns the resolution alongside the client so callers that
    /// report a mismatch quote the issuing server from the same snapshot the decision used.
    /// </summary>
    static async Task<(HttpClient Client, AuthStatus Status, TokenResolution? Resolution)> CreateClientCoreAsync(
        string? baseUrl, CancellationToken ct, bool allowAutoRedirect,
        string? rejectedAccessToken, bool autoRetryUnauthorized) {
        baseUrl ??= AppConfig.ResolvedServerUrl ?? Environment.GetEnvironmentVariable("KCAP_URL") ?? "http://localhost:5108";

        HttpClient NewClient(DelegatingHandler? retry = null) {
            var primary = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };

            if (retry is null) return new(primary);

            retry.InnerHandler = primary;

            return new(retry);
        }

        var provider = await DiscoverProviderAsync(baseUrl, ct);

        if (provider == "None") {
            return (NewClient(), AuthStatus.NoAuthRequired, null); // No auth needed
        }

        // A forced refresh is only meaningful for a token we already hold and that the server
        // just rejected; the binding check below still gates whether it may be used at all.
        if (rejectedAccessToken is not null) {
            await TokenStore.ForceRefreshAsync(rejectedAccessToken, baseUrl, ct);
        }

        var resolution = await TokenStore.GetValidTokensForServerAsync(baseUrl, ct);

        if (resolution is { Status: AuthStatus.Ok, Tokens: not null }) {
            var client = NewClient(autoRetryUnauthorized ? new UnauthorizedRetryHandler(resolution.Tokens, baseUrl) : null);
            client.DefaultRequestHeaders.Authorization = new("Bearer", resolution.Tokens.AccessToken);

            return (client, AuthStatus.Ok, resolution);
        }

        return (NewClient(), resolution.Status, resolution);
    }

    /// <summary>
    /// Creates an HttpClient with a Bearer token from the local token store, printing an actionable
    /// re-auth hint to stderr when no valid token is available. Checks auth discovery first — if the
    /// server uses "None" provider, skips auth entirely. Interactive CLI commands use this; hook
    /// callers should prefer <see cref="CreateClientWithAuthStatusAsync"/> so they control messaging.
    /// </summary>
    /// <param name="autoRetryUnauthorized">
    /// Installs <see cref="UnauthorizedRetryHandler"/> so a 401 is transparently retried once after
    /// a refresh. Pass <c>false</c> from callers that run their own 401-retry loop over the returned
    /// client — the MCP servers do — so a single rejection isn't retried (and refreshed) twice.
    /// </param>
    public static async Task<HttpClient> CreateAuthenticatedClientAsync(string? baseUrl = null, CancellationToken ct = default,
            bool autoRetryUnauthorized = true) {
        var (client, status, resolution) = await CreateClientCoreAsync(baseUrl, ct, allowAutoRedirect: true,
            rejectedAccessToken: null, autoRetryUnauthorized);

        switch (status) {
            case AuthStatus.Expired:
                await Console.Error.WriteLineAsync("Authentication token has expired. Run 'kcap login' to re-authenticate.");

                break;
            case AuthStatus.NotAuthenticated:
                await Console.Error.WriteLineAsync("Not authenticated. Run 'kcap login' to authenticate.");

                break;
            case AuthStatus.WrongServer:
                var target = baseUrl ?? AppConfig.ResolvedServerUrl ?? "the configured server";
                await Console.Error.WriteLineAsync(
                    $"Stored token was issued by {resolution?.IssuedServerUrl} but this command targets {target}. " +
                    $"Run 'kcap login' (or switch profiles with 'kcap use') to authenticate against {target}.");

                break;
        }

        return client;
    }

    static string? cachedProvider;

    /// <summary>
    /// Test seam: clears the in-process discovery cache above. The cache is keyed by
    /// nothing but process lifetime (unlike <see cref="AuthProviderCache"/>'s on-disk,
    /// per-<c>baseUrl</c> store) — the FIRST call to <see cref="DiscoverProviderAsync"/>
    /// in a process wins for every subsequent call regardless of <c>baseUrl</c>. A test
    /// whose SUT calls this against its own WireMock stub must reset it first, or it can
    /// silently observe a value cached by an earlier call against a different server.
    /// </summary>
    internal static void ResetProviderCacheForTesting() => cachedProvider = null;

    public static async Task<string> DiscoverProviderAsync(string baseUrl, CancellationToken ct = default) {
        if (cachedProvider is not null) {
            return cachedProvider;
        }

        // Hooks call this BEFORE any *WithRetryAsync, so a legacy scheme-less
        // server_url would crash here first if we did not guard. Fail fast with
        // the same actionable message the retry guards print.
        EnsureAbsolute(baseUrl);

        // Cross-process cache: each hook invocation is a fresh process, so the in-process static
        // above never helps a hook. Skip the /auth/config round-trip when a recent result is on disk.
        var cached = AuthProviderCache.TryGet(baseUrl);

        if (cached is not null) {
            cachedProvider = cached;

            return cached;
        }

        using var http = new HttpClient();

        try {
            var response = await http.GetAsync($"{baseUrl}/auth/config", ct);

            if (response.IsSuccessStatusCode) {
                var config   = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AuthDiscoveryResponse, ct);
                var provider = config?.Provider ?? "None";
                cachedProvider = provider;          // in-process
                AuthProviderCache.Set(baseUrl, provider); // cross-process; only cache successful discovery

                return provider;
            }
        } catch {
            // Server unreachable — don't cache, try tokens as fallback.
            // Catches both HttpRequestException (connection failures) and
            // OperationCanceledException (caller's CT fired — fall through to
            // local-token fallback rather than bubbling the cancellation).
        }

        // Fallback: try existing tokens (don't cache — allow re-discovery next time)
        return (await TokenStore.LoadAsync())?.Provider ?? "None";
    }

    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxDelay       = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Per-attempt cap on a single HTTP call inside <see cref="SendWithRetryAsync(Func{CancellationToken, Task{HttpResponseMessage}}, TimeSpan, CancellationToken)"/>.
    /// Enforced via a linked <see cref="CancellationTokenSource"/> so the wall-clock cap
    /// is observable on the token we pass to <see cref="HttpClient"/> — not on the
    /// client's own <see cref="HttpClient.Timeout"/> (default 100s), which would
    /// otherwise raise an unhandled <see cref="TaskCanceledException"/> at every call
    /// site whose <c>catch</c> only covers <see cref="HttpRequestException"/>.
    /// </summary>
    internal static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(60);

    const string UnreachableHint =
        "Kurrent Capacitor API cannot be reached, is it running? "                    +
        "Make sure the URL is correctly configured and the service is running. "      +
        "Check https://github.com/kurrent-io/claude-remember#setup for instructions." +
        "\rError connecting to: ";

    internal const string SchemeMissingHint =
        "server_url is missing a scheme. Run: kcap config set server_url https://<host>";

    /// <summary>
    /// Pure test seam for <see cref="EnsureAbsolute"/>. Returns <c>true</c> only
    /// for absolute http/https URLs.
    /// </summary>
    public static bool IsAcceptableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";

    /// <summary>
    /// Fails fast with an actionable message if <paramref name="url"/> is not
    /// an absolute http/https URL. Called by every <c>*WithRetryAsync</c>
    /// extension so a legacy scheme-less config produces a clean exit instead
    /// of an unhandled <see cref="InvalidOperationException"/> from
    /// <see cref="HttpClient.PrepareRequestMessage"/>.
    /// </summary>
    static void EnsureAbsolute(string url) {
        if (IsAcceptableUrl(url)) return;
        Console.Error.WriteLine(SchemeMissingHint);
        Environment.Exit(2);
    }

    extension(HttpClient client) {
        public Task<HttpResponseMessage> PostWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            ) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(token => client.PostAsync(url, content, token), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> GetWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(token => client.GetAsync(url, token), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> PutWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            ) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(token => client.PutAsync(url, content, token), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> DeleteWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(token => client.DeleteAsync(url, token), timeout ?? DefaultTimeout, ct);
        }

        /// <summary>
        /// Single-attempt POST with a hard per-call timeout. No retry, no
        /// backoff. Used by hook-path call sites where retries would burst
        /// the shared dispatcher budget. <paramref name="ct"/> is honoured;
        /// expiry of <paramref name="timeout"/> surfaces as
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        public async Task<HttpResponseMessage> PostOnceAsync(
                string            url,
                HttpContent       content,
                TimeSpan          timeout,
                CancellationToken ct = default
            ) {
            EnsureAbsolute(url);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await client.PostAsync(url, content, linkedCts.Token);
        }

        /// <summary>Single-attempt GET — see <see cref="PostOnceAsync"/>.</summary>
        public async Task<HttpResponseMessage> GetOnceAsync(
                string            url,
                TimeSpan          timeout,
                CancellationToken ct = default
            ) {
            EnsureAbsolute(url);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await client.GetAsync(url, linkedCts.Token);
        }
    }

    /// <summary>
    /// Writes a structured JSON error to stderr for when the API is unreachable after all retries.
    /// </summary>
    public static void WriteUnreachableError(string baseUrl, HttpRequestException ex) {
        Console.Error.WriteLine($"{UnreachableHint} {baseUrl} {ex.Message}");
    }

    /// <summary>
    /// Checks if the response is a 401 and prints the server's error message.
    /// Returns true if the response was a 401 (caller should return early).
    /// </summary>
    public static async Task<bool> HandleUnauthorizedAsync(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.Unauthorized) {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync();

        try {
            using var doc     = JsonDocument.Parse(body);
            var       message = doc.RootElement.Str("message");
            await Console.Error.WriteLineAsync(message ?? "Authentication failed. Run 'kcap login' to re-authenticate.");
        } catch {
            await Console.Error.WriteLineAsync("Authentication failed. Run 'kcap login' to re-authenticate.");
        }

        return true;
    }

    internal static Task<HttpResponseMessage> SendWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> send,
            TimeSpan                                           totalTimeout,
            CancellationToken                                  ct
        ) => SendWithRetryAsync(send, totalTimeout, PerAttemptTimeout, ct);

    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> send,
            TimeSpan                                           totalTimeout,
            TimeSpan                                           perAttemptTimeout,
            CancellationToken                                  ct
        ) {
        var        sw        = Stopwatch.StartNew();
        var        delayMs   = 250;
        Exception? lastError = null;

        while (true) {
            // Hard wall-clock guard: never start a new attempt (or sleep) past totalTimeout,
            // even when perAttemptTimeout would otherwise allow it. Without this, a default
            // call (total=30s, per-attempt=60s) against a hung server still blocks for ~60s.
            var remaining = totalTimeout - sw.Elapsed;

            if (remaining <= TimeSpan.Zero)
                throw BudgetExhausted(totalTimeout, perAttemptTimeout, lastError);

            var attemptCap = remaining < perAttemptTimeout ? remaining : perAttemptTimeout;

            using var attemptCts = new CancellationTokenSource(attemptCap);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptCts.Token);

            try {
                return await send(linkedCts.Token);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                // Caller cancelled — surface as cancellation, never retry.
                throw;
            } catch (HttpRequestException ex) when (sw.Elapsed < totalTimeout) {
                // Transient transport error within retry budget — back off and try again.
                lastError = ex;
            } catch (OperationCanceledException ex) when (sw.Elapsed < totalTimeout) {
                // Per-attempt timeout fired (linked CTS, not caller's ct) and retry budget
                // remains — back off and try again. Without this branch the same condition
                // would surface as an unhandled TaskCanceledException at every call site
                // that only catches HttpRequestException (import probes, transcript POSTs,
                // session-start hooks, ...).
                lastError = ex;
            } catch (HttpRequestException ex) {
                // Budget exhausted on transport error — surface as HttpRequestException so
                // existing `catch (HttpRequestException)` handlers degrade gracefully.
                throw new HttpRequestException(
                    $"Request failed after exhausting the {totalTimeout.TotalSeconds:F0}s retry budget.",
                    ex
                );
            } catch (OperationCanceledException ex) {
                throw BudgetExhausted(totalTimeout, perAttemptTimeout, ex);
            }

            // Cap the backoff sleep to the remaining budget so a retry delay can never push
            // us past totalTimeout. If nothing's left, jump back to the loop top so the
            // hard-guard above throws with lastError preserved as the inner exception.
            var remainingAfter = totalTimeout - sw.Elapsed;

            if (remainingAfter <= TimeSpan.Zero) continue;

            var actualDelayMs = (int)Math.Min(delayMs, remainingAfter.TotalMilliseconds);
            await Task.Delay(actualDelayMs, ct);
            delayMs = Math.Min(delayMs * 2, (int)MaxDelay.TotalMilliseconds);
        }

        static HttpRequestException BudgetExhausted(TimeSpan totalTimeout, TimeSpan perAttemptTimeout, Exception? inner) =>
            new(
                $"Request did not complete within the {totalTimeout.TotalSeconds:F0}s retry budget "      +
                $"(per-attempt timeout {perAttemptTimeout.TotalSeconds:F0}s).",
                inner
            );
    }
}
