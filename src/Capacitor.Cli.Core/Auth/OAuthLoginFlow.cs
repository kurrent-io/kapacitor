using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;

// ReSharper disable MethodHasAsyncOverload

namespace Capacitor.Cli.Core.Auth;

public static class AuthProvider {
    public const string GitHubApp = "GitHubApp";
    public const string WorkOS    = "workos";
    public const string None      = "None";
}

public enum GitHubFlow { Browser, Device }

public static class OAuthLoginFlow {
    /// <summary>GET <c>{serverUrl}/auth/config</c>, or <c>null</c> with the failure already reported.</summary>
    internal static async Task<AuthDiscoveryResponse?> FetchAuthConfigAsync(
            HttpClient http, string serverUrl, CancellationToken ct, IAuthProgress progress) {
        HttpResponseMessage configResponse;

        try {
            configResponse = await http.GetAsync($"{serverUrl}/auth/config", ct);
        } catch (HttpRequestException ex) {
            progress.Error(HttpClientExtensions.UnreachableErrorText(serverUrl, ex));

            return null;
        }

        var config = configResponse.IsSuccessStatusCode
            ? await configResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AuthDiscoveryResponse, ct)
            : null;

        if (config is null) progress.Error($"Error: Failed to fetch auth config from {serverUrl}/auth/config");

        return config;
    }

    internal static GitHubFlow ChooseGitHubFlow(bool forceDevice, bool isHeadless, bool hasExchangeUrl)
        => forceDevice || isHeadless || !hasExchangeUrl ? GitHubFlow.Device : GitHubFlow.Browser;

    /// <summary>
    /// Picks the discovery provider before any auth runs: <c>--github</c> selects the GitHub App
    /// path; otherwise discovery uses the org SSO path (WorkOS). Returns <c>null</c> when no
    /// provider can serve a non-interactive session — the caller reports
    /// <see cref="HeadlessDiscoveryUnsupportedMessage"/> and stops.
    ///
    /// <para>A no-flag headless caller used to fall back to the GitHub App, whose device flow needs
    /// no local browser (WorkOS authenticates through a 127.0.0.1 loopback callback that a remote
    /// box's browser can never reach). That fallback was the only remaining source of NEW GitHub App
    /// sign-ins, and it dead-ended: the GitHub App branch has no provisioning path, so a headless
    /// user with no workspace authenticated and was then told to ask an admin to install a GitHub
    /// App. Failing before auth is both honest and one round trip cheaper. Closing this properly
    /// means giving WorkOS a headless path — see the DoD in issue #535.</para>
    /// </summary>
    internal static string? ChooseDiscoveryProvider(string[] args, bool isInteractive) {
        if (args.Contains("--github")) return AuthProvider.GitHubApp;
        if (isInteractive) return AuthProvider.WorkOS;

        // Headless: only an explicit device-flow request can still get through, and only GitHub has
        // one. `--device` is the documented way to sign in from SSH or a container, so it stays an
        // escape hatch alongside --github — it is a deliberate request, not the implicit fallback
        // removed above. Tested on the headless branch only: on a machine with a browser `--device`
        // has always meant "use the device flow if the login step needs GitHub", never "take
        // discovery off org SSO".
        return args.Contains("--device") ? AuthProvider.GitHubApp : null;
    }

    /// <summary>
    /// What a non-interactive session is told instead of being routed onto legacy auth. Names the
    /// two routes that actually work — create a workspace in a browser, or point at one that
    /// already exists — and deliberately mentions no GitHub App.
    /// </summary>
    internal static string HeadlessDiscoveryUnsupportedMessage() =>
        "Setting up a new workspace needs an interactive terminal, and this session is non-interactive.\n"
      + $"  • Create a workspace at {ProvisioningEndpoint.Url}/signup, then run: kcap setup <slug>\n"
      + "  • Or point at an existing workspace: kcap setup --server-url <url>";

    /// <summary>
    /// `kcap login` runs tenant discovery when there's no configured server (nothing to log into yet)
    /// or the user explicitly asked with <c>--discover</c>; otherwise it logs into the configured server.
    /// </summary>
    internal static bool ShouldDiscoverLogin(string? baseUrl, string[] args)
        => args.Contains("--discover") || baseUrl is null;

    /// <summary>
    /// Runs GitHub Device Flow interactively. Reports the user code and verification URL, opens the
    /// system browser to the verification URL, and polls GitHub for the access token, through
    /// <paramref name="progress"/>. Intended for CLI use — not suitable for headless callers.
    /// </summary>
    /// <returns>The GitHub access token on success, or <c>null</c> on failure.</returns>
    public static async Task<string?> RunDeviceFlowAsync(string clientId, CancellationToken ct = default, IAuthProgress? progress = null) {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new("application/json"));

        return await RunDeviceFlowAsync(http, clientId, ct, progress);
    }

    // HttpClient-injectable core — the test seam for pinning the device-code/poll endpoints
    // to a fake handler (RunDeviceFlowAsync(clientId) hardcodes github.com and can't be redirected).
    internal static async Task<string?> RunDeviceFlowAsync(
            HttpClient http, string clientId, CancellationToken ct = default, IAuthProgress? progress = null,
            TimeProvider? time = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var deviceResponse = await PostFormForJsonAsync(
            http,
            "https://github.com/login/device/code",
            new() {
                ["client_id"] = clientId,
                ["scope"]     = "read:user read:org"
            },
            ct
        );

        if (!deviceResponse.IsSuccessStatusCode) {
            progress.Error($"Error requesting device code: {await deviceResponse.Content.ReadAsStringAsync(ct)}");

            return null;
        }

        var device   = (await deviceResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.DeviceCodeResponse, ct))!;
        var interval = device.IntervalOrDefault;

        var copied = Clipboard.TryCopy(device.UserCode);

        var browserOpened = TryOpenBrowser(device.BrowserUri);

        progress.Notice("");
        progress.Notice("To finish signing in to GitHub:");
        progress.Notice("");
        progress.Notice(
            browserOpened
                ? $"  1. Your browser should have opened {device.VerificationUri}"
                : $"  1. Open {device.VerificationUri} in a browser"
        );

        if (browserOpened) progress.Notice("     (if it didn't open, go to that URL yourself)");

        // Clipboard-copy suffix folds into the code: DeviceCode's contract carries no separate flag for it.
        progress.DeviceCode(device.UserCode + (copied ? "  (copied to clipboard)" : ""), device.VerificationUri, "GitHub");

        return await PollDeviceGrantAsync(
            http, "https://github.com/login/oauth/access_token",
            new() { ["client_id"] = clientId, ["device_code"] = device.DeviceCode },
            CapacitorJsonContext.Default.GitHubTokenResponse,
            r => (r.AccessToken, r.Error),
            device, interval, ct, progress, time);
    }

    /// <summary>
    /// RFC 8628 §3.4-3.5 polling, shared by every provider. Parameterised on the token URL, the extra
    /// form fields, and how to read a token and an error code out of the response.
    /// </summary>
    /// <param name="device">
    /// Carries the <c>expires_in</c> that bounds the loop. Without it the poll ran forever against a
    /// code the server had already discarded.
    /// </param>
    internal static async Task<T?> PollDeviceGrantAsync<T, TResponse>(
            HttpClient http, string tokenUrl, Dictionary<string, string> form,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> typeInfo,
            Func<TResponse, (T? Value, string? Error)> read,
            DeviceCodeResponse device, int interval,
            CancellationToken ct, IAuthProgress progress, TimeProvider? time = null)
        where T : class where TResponse : class {
        time ??= TimeProvider.System;
        form["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code";

        var deadline = time.GetUtcNow().AddSeconds(device.ExpiresInOrDefault);

        while (true) {
            // The sleep stays on the real clock deliberately: `time` exists to bound the deadline, and
            // a fake one here would leave the timer waiting for an advance nobody makes.
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            ct.ThrowIfCancellationRequested();

            if (time.GetUtcNow() >= deadline) {
                progress.Error($"\nThe code expired before it was approved. Re-run to get a new one.");

                return null;
            }

            var response = await PostFormForJsonAsync(http, tokenUrl, form, ct);

            // Parse the body whatever the status: RFC 8628 §3.5 carries authorization_pending in a 400,
            // and GitHub returns it in a 200. Only an UNREADABLE body is a transport problem, and it is
            // retried rather than force-unwrapped — a 429 or a 5xx HTML page used to be an NRE here.
            TResponse? parsed = null;

            try {
                parsed = await response.Content.ReadFromJsonAsync(typeInfo, ct);
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException) {
                // fall through to the transient arm below
            }

            if (parsed is null) {
                if (response.IsSuccessStatusCode) {
                    progress.Error("\nThe sign-in service returned an unreadable response.");

                    return null;
                }

                // Transient by elimination: a non-2xx we could not parse. Back off and keep polling
                // until the deadline rather than failing a sign-in the user may still be completing.
                interval += 5;
                progress.PollTick();

                continue;
            }

            var (value, error) = read(parsed);

            if (value is not null) {
                progress.Notice(" done!");

                return value;
            }

            switch (error) {
                case "authorization_pending":
                    progress.PollTick();

                    continue;
                case "slow_down":
                    interval += 5;

                    continue;
                case "expired_token":
                    progress.Error("\nThe code expired before it was approved. Re-run to get a new one.");

                    return null;
                case "access_denied":
                    progress.Error("\nThe request was denied.");

                    return null;
                default:
                    progress.Error($"\nError: {error ?? "unknown error"}");

                    return null;
            }
        }
    }

    // Accept rides on the request, not the client: an injected HttpClient (the façade's) would
    // otherwise get GitHub's form-encoded default and fail to parse.
    static async Task<HttpResponseMessage> PostFormForJsonAsync(
            HttpClient http, string url, Dictionary<string, string> form, CancellationToken ct) {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new("application/json"));

        return await http.SendAsync(request, ct);
    }

    /// <summary>
    /// GitHub authorization-code-with-PKCE via OidcClient's front-channel (authorize URL + PKCE +
    /// state) over a 127.0.0.1 loopback, then the proxy-mediated JSON code-exchange to the Capacitor
    /// server (GitHub Apps need client_secret on the token POST, which the server adds). Returns the
    /// GitHub access token, or <c>null</c> on cancel/timeout/state-mismatch/error — a null is a hard
    /// failure (the caller does NOT fall back to device flow on null, only on a loopback bind exception
    /// thrown out of <see cref="LoopbackBrowser"/>). <paramref name="browser"/> is the test seam.
    /// </summary>
    public static async Task<string?> RunGitHubBrowserFlowAsync(
            string clientId, string codeExchangeUrl, IBrowser? browser = null, TimeSpan? timeout = null,
            CancellationToken ct = default, IAuthProgress? progress = null) {
        progress ??= ConsoleAuthProgress.Instance;
        browser  ??= new LoopbackBrowser(progress: progress);
        var redirectUri = $"http://127.0.0.1:{GetAvailablePort()}/callback";

        var options = new OidcClientOptions {
            Authority   = "https://github.com",
            ClientId    = clientId,
            Scope       = "read:user read:org",
            RedirectUri = redirectUri,
            LoadProfile = false,
            DisablePushedAuthorization = true,
            Browser     = browser,
            ProviderInformation = new ProviderInformation {
                IssuerName        = "https://github.com",
                AuthorizeEndpoint = "https://github.com/login/oauth/authorize",
                TokenEndpoint     = "https://github.com/login/oauth/access_token", // required non-empty; never called
            },
        };
        options.Policy.Discovery.RequireKeySet = false;

        var oidc  = new OidcClient(options);
        var state = await oidc.PrepareLoginAsync(cancellationToken: ct);

        var result = await browser.InvokeAsync(
            new BrowserOptions(state.StartUrl, redirectUri) { Timeout = timeout ?? TimeSpan.FromMinutes(5) }, ct);

        if (result.ResultType != BrowserResultType.Success) {
            ct.ThrowIfCancellationRequested(); // a caller cancel is neither a timeout nor a denial
            progress.Error(result.ResultType == BrowserResultType.Timeout
                ? "Timed out waiting for authorization. Re-run `kcap login` to try again."
                : $"Authorization failed: {result.Error ?? result.ResultType.ToString()}");

            return null;
        }

        var resp = new AuthorizeResponse(result.Response);
        if (resp.IsError) {
            progress.Error($"Authorization failed: {resp.Error}");

            return null;
        }
        if (!string.Equals(resp.State, state.State, StringComparison.Ordinal)) {
            progress.Error("Error: state mismatch — possible CSRF. Aborting.");

            return null;
        }
        if (string.IsNullOrEmpty(resp.Code)) {
            progress.Error("Authorization failed: no authorization code received.");

            return null;
        }

        // Bound the proxy exchange to the login timeout — a stalled endpoint must not hang the CLI —
        // while still observing the caller's own cancellation.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new("application/json"));

        HttpResponseMessage tokenResponse;

        try {
            tokenResponse = await http.PostAsJsonAsync(
                codeExchangeUrl,
                new GitHubCodeExchangeRequest { Code = resp.Code, CodeVerifier = state.CodeVerifier, RedirectUri = redirectUri },
                CapacitorJsonContext.Default.GitHubCodeExchangeRequest,
                cancellationToken: cts.Token
            );
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException) {
            ct.ThrowIfCancellationRequested(); // the caller's cancel aborted the exchange — not a connectivity failure
            progress.Error($"Could not reach the code-exchange endpoint at {codeExchangeUrl}: {ex.Message}");

            return null;
        }

        if (!tokenResponse.IsSuccessStatusCode) {
            progress.Error($"Error exchanging code: {await tokenResponse.Content.ReadAsStringAsync(cts.Token)}");

            return null;
        }

        GitHubTokenResponse? tokenResult;

        try {
            tokenResult = await tokenResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.GitHubTokenResponse, cancellationToken: cts.Token);
        } catch (JsonException ex) {
            var raw = await tokenResponse.Content.ReadAsStringAsync(cts.Token);
            progress.Error($"Code-exchange response was not valid JSON ({ex.Message}): {raw}");

            return null;
        }

        if (tokenResult?.AccessToken is null) {
            progress.Error($"Error: {tokenResult?.Error ?? "no access_token in response"}");

            return null;
        }

        progress.Notice("Authorization complete.");

        return tokenResult.AccessToken;
    }

    public static async Task<int> ExchangeAndSaveAsync(
            string serverUrl, string githubAccessToken, string provider, CancellationToken ct = default, IAuthProgress? progress = null) {
        progress ??= ConsoleAuthProgress.Instance;

        using var http = new HttpClient();

        var exchanged = await ExchangeAsync(http, serverUrl, githubAccessToken, provider, profile: null, progress, ct);

        if (exchanged is null) return 1;

        await TokenStore.SaveAsync(exchanged.Value.Tokens);

        progress.Notice($"Logged in as {exchanged.Value.Username}");

        return 0;
    }

    /// <summary>
    /// Exchanges a GitHub access token for a Capacitor JWT and returns the tokens WITHOUT saving —
    /// persistence belongs to the caller's commit boundary. <c>null</c> means the exchange failed and
    /// the reason has already been reported through <paramref name="progress"/>. <paramref name="profile"/>
    /// only names the profile in that error text.
    /// </summary>
    public static async Task<(StoredTokens Tokens, string? Username)?> ExchangeAsync(
            HttpClient        http,
            string            serverUrl,
            string            githubAccessToken,
            string            provider,
            string?           profile,
            IAuthProgress     progress,
            CancellationToken ct = default) {
        if (provider is not AuthProvider.GitHubApp) {
            progress.Error($"Error: unknown auth provider '{provider}'");

            return null;
        }

        var exchangeResponse = await http.PostAsJsonAsync(
            $"{serverUrl}/auth/token",
            new TokenExchangeRequest { GithubAccessToken = githubAccessToken },
            CapacitorJsonContext.Default.TokenExchangeRequest,
            cancellationToken: ct
        );

        if (!exchangeResponse.IsSuccessStatusCode) {
            WriteExchangeError(await exchangeResponse.Content.ReadAsStringAsync(ct), profile, progress);

            return null;
        }

        var exchange = (await exchangeResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.TokenExchangeResponse, ct))!;

        if (!ServerIdentity.TryCanonicalizeForStamping(serverUrl, out var canonical, out var identityError)) {
            progress.Error($"Error: {identityError}");

            return null;
        }

        return (new StoredTokens {
            AccessToken    = exchange.AccessToken,
            ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(exchange.ExpiresIn),
            GitHubUsername = exchange.Username,
            Provider       = provider,
            ServerUrl      = canonical
        }, exchange.Username);
    }

    /// <summary>
    /// Exchanges a GitHub access token for a Capacitor JWT and saves it to the named profile.
    /// Unlike the single-argument overload, this does NOT print "Logged in as …" — the caller
    /// is responsible for user-facing output. Returns 0 on success, 1 on failure.
    /// </summary>
    public static async Task<int> ExchangeAndSaveAsync(
            string serverUrl, string githubAccessToken, string provider, string profile,
            CancellationToken ct = default, IAuthProgress? progress = null) {
        using var http = new HttpClient();

        return await ExchangeAndSaveAsync(http, serverUrl, githubAccessToken, provider, profile, ct, progress);
    }

    public static async Task<int> ExchangeAndSaveAsync(
            HttpClient        http,
            string            serverUrl,
            string            githubAccessToken,
            string            provider,
            string            profile,
            CancellationToken ct = default,
            IAuthProgress?    progress = null
        ) {
        progress ??= ConsoleAuthProgress.Instance;

        var exchanged = await ExchangeAsync(http, serverUrl, githubAccessToken, provider, profile, progress, ct);

        if (exchanged is null) return 1;

        await TokenStore.SaveAsync(profile, exchanged.Value.Tokens, ct);

        return 0;
    }

    /// <summary>
    /// Reports the server's <c>/auth/token</c> error. When the server reports that the Capacitor
    /// GitHub App isn't installed on the user's org, appends a troubleshooting checklist — the most
    /// common cause is the device-flow consent being completed under a different GitHub account than
    /// the one with org membership.
    /// </summary>
    static void WriteExchangeError(string body, string? profile, IAuthProgress progress) {
        var prefix = profile is null
            ? "Error exchanging token"
            : $"Error exchanging token for profile '{profile}'";

        var serverMessage = TryParseInstallationMessage(body);

        if (serverMessage is null) {
            progress.Error($"{prefix}: {body}");

            return;
        }

        progress.Error($"{prefix}: {serverMessage}");
        progress.Error("");
        progress.Error("This usually means the Capacitor GitHub App isn't visible to your GitHub user.");
        progress.Error("Common fixes:");
        progress.Error("  1. Authorize as the right GitHub account. The device-flow page authorizes");
        progress.Error("     whoever is signed in to your browser — sign in to https://github.com as");
        progress.Error("     your org user, then re-run `kcap setup ...`.");
        progress.Error("  2. If your org enforces SAML SSO, authorize SSO for the App at");
        progress.Error("     https://github.com/settings/apps/authorizations.");
        progress.Error("  3. Revoke a stale prior authorization at the same URL and retry.");
        progress.Error("");
        progress.Error("If the App was never installed on your org, an org admin must install it.");
    }

    internal static string? TryParseInstallationMessage(string body) {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try {
            var parsed = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.AuthErrorResponse);
            var msg    = parsed?.Error;

            return msg is not null && msg.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                ? msg
                : null;
        } catch (JsonException) {
            return null;
        }
    }

    internal static async Task<string?> AcquireGitHubTokenAsync(
            string clientId, string? codeExchangeUrl, bool forceDevice, CancellationToken ct = default, IAuthProgress? progress = null) {
        using var http = new HttpClient();

        return await AcquireGitHubTokenAsync(http, clientId, codeExchangeUrl, forceDevice, ct, progress);
    }

    // HttpClient-injectable core: the device-flow leg runs on the caller's client so the façade's
    // one client (and a test's scripted handler) covers it.
    internal static async Task<string?> AcquireGitHubTokenAsync(
            HttpClient http, string clientId, string? codeExchangeUrl, bool forceDevice,
            CancellationToken ct = default, IAuthProgress? progress = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var headless = HeadlessEnvironment.IsHeadless();
        var choice   = ChooseGitHubFlow(forceDevice, headless, hasExchangeUrl: IsValidExchangeUrl(codeExchangeUrl));

        if (choice == GitHubFlow.Browser) {
            try {
                var token = await RunGitHubBrowserFlowAsync(clientId, codeExchangeUrl!, ct: ct, progress: progress);

                return token ??
                    // Browser flow ran but user cancelled / state mismatch — don't silently fall back.
                    null;
            } catch (HttpListenerException ex) {
                progress.Error($"Could not bind loopback listener ({ex.Message}); falling back to device flow.");
            } catch (PlatformNotSupportedException ex) {
                progress.Error($"Loopback listener not supported on this platform ({ex.Message}); falling back to device flow.");
            }
        }

        return await RunDeviceFlowAsync(http, clientId, ct, progress);
    }

    internal const string WorkOSApiBase = "https://api.workos.com";

    /// <summary>
    /// Builds OidcClient options for the WorkOS AuthKit authorization-code-with-PKCE flow.
    /// Authorize + token both on the API domain (never the AuthKit UI domain). WorkOS is a
    /// public client (no secret) with non-standard endpoints, no discovery, and no id_token, so
    /// discovery/keyset/userinfo are disabled and the response is mapped by hand.
    /// </summary>
    internal static OidcClientOptions BuildWorkOSOptions(string clientId, string apiBase, string redirectUri) {
        var options = new OidcClientOptions {
            Authority   = apiBase,            // anonymous-principal issuer; discovery stays off (ProviderInformation set)
            ClientId    = clientId,
            Scope       = "",                 // preserve current no-scope behavior
            RedirectUri = redirectUri,
            LoadProfile = false,              // WorkOS has no userinfo endpoint
            DisablePushedAuthorization = true,
            ProviderInformation = new ProviderInformation {
                IssuerName        = apiBase,
                AuthorizeEndpoint = $"{apiBase}/user_management/authorize",     // always the API domain
                TokenEndpoint     = $"{apiBase}/user_management/authenticate",
            },
        };
        options.Policy.Discovery.RequireKeySet = false;

        return options;
    }

    /// <summary>WorkOS front-channel extras: <c>provider=authkit</c> (+ <c>organization_id</c> when org-scoped).</summary>
    internal static Parameters WorkOSFrontChannel(string? organizationId) {
        var p = new Parameters { { "provider", "authkit" } };
        if (!string.IsNullOrEmpty(organizationId)) p.Add("organization_id", organizationId);

        return p;
    }

    /// <summary>
    /// WorkOS AuthKit authorization-code-with-PKCE login via OidcClient. Org-scoped when
    /// <paramref name="organizationId"/> is set. Maps the raw token response (which carries WorkOS's
    /// non-standard organization_id/user and no id_token) into <see cref="WorkOSAuthResponse"/> via the
    /// source-gen context — omitted/nullable fields don't throw. <paramref name="apiBase"/> is the test seam.
    /// </summary>
    public static async Task<WorkOSAuthResponse?> AuthenticateWorkOSAsync(
            string clientId, string? organizationId, IBrowser browser, string apiBase = WorkOSApiBase,
            CancellationToken ct = default, IAuthProgress? progress = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var redirectUri = $"http://127.0.0.1:{GetAvailablePort()}/callback";
        var options     = BuildWorkOSOptions(clientId, apiBase, redirectUri);
        options.Browser = browser;

        var oidc   = new OidcClient(options);
        var result = await oidc.LoginAsync(new LoginRequest { FrontChannelExtraParameters = WorkOSFrontChannel(organizationId) }, ct);

        // Surface the actual reason (timeout / state mismatch / token-endpoint / upstream OIDC error)
        // rather than collapsing every failure to a single opaque "sign-in failed".
        if (result.IsError) {
            ct.ThrowIfCancellationRequested(); // OidcClient renders a caller cancel as an error result
            progress.Error(WorkOSSignInError(result.Error, result.ErrorDescription));

            return null;
        }

        if (result.TokenResponse?.Json is not { } json) {
            progress.Error("WorkOS sign-in failed: empty token response.");

            return null;
        }

        return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.WorkOSAuthResponse);
    }

    /// <summary>Best-effort — the environments this flow serves often have no browser at all.</summary>
    static bool TryOpenBrowser(string url) {
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// RFC 8628 against AuthKit (<c>api.workos.com/user_management/*</c>), NOT Connect's
    /// <c>{authkit-domain}/oauth2/*</c> — that one returns no organization and requires a client secret.
    /// Public client: no secret anywhere in this flow, so the proxy is not involved.
    /// </summary>
    internal static async Task<WorkOSAuthResponse?> RunWorkOSDeviceFlowAsync(
            HttpClient http, string clientId, CancellationToken ct = default,
            IAuthProgress? progress = null, string apiBase = WorkOSApiBase, TimeProvider? time = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var authorize = await PostFormForJsonAsync(
            http, $"{apiBase}/user_management/authorize/device", new() { ["client_id"] = clientId }, ct);

        if (!authorize.IsSuccessStatusCode) {
            // Step 1 failing is the rollout hazard, not a user error: if CLI Auth is not enabled on this
            // AuthKit client every headless sign-in dies here, and a generic message would send people
            // hunting through the rest of setup.
            progress.Error("Could not start device sign-in with WorkOS.");
            progress.Error($"  {(int)authorize.StatusCode} from {apiBase}/user_management/authorize/device");
            progress.Error("  If this persists, device authorization may not be enabled for this");
            progress.Error("  workspace's AuthKit application — an administrator has to turn it on.");

            return null;
        }

        DeviceCodeResponse? device = null;

        try {
            device = await authorize.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.DeviceCodeResponse, ct);
        } catch (System.Text.Json.JsonException) {
            // fall through
        }

        if (device is null || string.IsNullOrEmpty(device.DeviceCode)) {
            progress.Error("WorkOS returned no device code.");

            return null;
        }

        // Best-effort: the population this flow exists for has no browser here at all.
        var browserOpened = TryOpenBrowser(device.BrowserUri);

        progress.Notice("");
        progress.Notice("To finish signing in:");
        progress.Notice("");
        progress.Notice(
            browserOpened
                ? $"  1. Your browser should have opened {device.VerificationUri}"
                : $"  1. Open {device.VerificationUri} in a browser");

        // No clipboard copy, unlike the GitHub flow: the code has to be READ ALOUD OR RETYPED on
        // another device for this flow to mean anything, and a silent copy invites pasting it into
        // whatever page is already open.
        progress.DeviceCode(device.UserCode, device.VerificationUri, "WorkOS");

        return await PollDeviceGrantAsync(
            http, $"{apiBase}/user_management/authenticate",
            new() { ["client_id"] = clientId, ["device_code"] = device.DeviceCode },
            CapacitorJsonContext.Default.WorkOSAuthResponse,
            r => (string.IsNullOrEmpty(r.AccessToken) ? null : r, r.Error),
            device, device.IntervalOrDefault, ct, progress, time);
    }

    /// <summary>Maps an OidcClient WorkOS failure to a user-facing message, preserving the actionable detail.</summary>
    internal static string WorkOSSignInError(string? error, string? description) => error switch {
        "Timeout"    => "Timed out waiting for authorization. Re-run `kcap login` to try again.",
        "UserCancel" => "WorkOS sign-in was cancelled.",
        _            => $"WorkOS sign-in failed: {error ?? "unknown error"}"
                      + (string.IsNullOrEmpty(description) ? "" : $" — {description}")
    };

    /// <summary>
    /// WorkOS sign-in against a KNOWN server: authenticate, enforce the org gate, and build the
    /// server-bound tokens WITHOUT saving them. <c>null</c> means the reason is already reported.
    /// </summary>
    internal static async Task<(StoredTokens Tokens, string Username)?> WorkOSTokensForServerAsync(
            string serverUrl, string clientId, string? organizationId, IBrowser browser,
            CancellationToken ct, IAuthProgress progress, string apiBase = WorkOSApiBase) {
        // AuthenticateWorkOSAsync already reported the specific failure reason.
        var json = await AuthenticateWorkOSAsync(clientId, organizationId, browser, apiBase, ct, progress);
        if (json is null) return null;

        // Org gate: a multi-org user must not be "logged in" to the wrong org — every API
        // call would then fail the server's org check. Reject before saving tokens.
        if (!string.IsNullOrEmpty(organizationId) && !string.Equals(json.OrganizationId, organizationId, StringComparison.Ordinal)) {
            progress.Error($"Error: signed in to the wrong WorkOS organization (expected {organizationId}). Re-run `kcap login` and pick the correct organization.");

            return null;
        }

        if (!ServerIdentity.TryCanonicalizeForStamping(serverUrl, out var canonical, out var identityError)) {
            progress.Error($"Error: {identityError}");

            return null;
        }

        var username = WorkOSDisplayName(json.User);

        return (new StoredTokens {
            AccessToken    = json.AccessToken,
            RefreshToken   = json.RefreshToken,
            ExpiresAt      = TokenStore.JwtExpiry(json.AccessToken),
            GitHubUsername = username,
            Provider       = AuthProvider.WorkOS,
            ClientId       = clientId,
            // The kcap server we authenticated FOR — not api.workos.com, which issued the
            // token but says nothing about which Capacitor server will accept it.
            ServerUrl      = canonical
        }, username);
    }

    /// <summary>Human display name from a WorkOS user (first+last, else email, else "unknown").</summary>
    internal static string WorkOSDisplayName(WorkOSUserInfo? user) {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(user?.FirstName)) parts.Add(user!.FirstName!);
        if (!string.IsNullOrEmpty(user?.LastName))  parts.Add(user!.LastName!);

        return parts.Count > 0 ? string.Join(' ', parts) : user?.Email ?? "unknown";
    }

    /// <summary>
    /// Public-client WorkOS org-switch: exchanges a refresh token for an org-scoped token. The spike
    /// confirmed the resulting refresh token stays bound to the org, so subsequent refreshes need no
    /// organization_id. No client secret.
    /// </summary>
    public static async Task<WorkOSAuthResponse?> SwitchWorkOSOrgAsync(
            HttpClient http, string apiBase, string clientId, string refreshToken, string organizationId, CancellationToken ct = default) {
        var resp = await http.PostAsync(
            $"{apiBase.TrimEnd('/')}/user_management/authenticate",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"]      = "refresh_token",
                ["client_id"]       = clientId,
                ["refresh_token"]   = refreshToken,
                ["organization_id"] = organizationId
            }), ct);

        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.WorkOSAuthResponse, ct);
    }

    /// <summary>
    /// Public-client WorkOS token refresh (org-less): exchanges a refresh token for a fresh access
    /// token without switching organization. Keeps the org-less token alive across the create-tenant
    /// provisioning poll, which can outlive WorkOS's ~5-minute access-token TTL. No client secret.
    /// </summary>
    public static async Task<WorkOSAuthResponse?> RefreshWorkOSTokenAsync(
            HttpClient http, string apiBase, string clientId, string refreshToken, CancellationToken ct = default) {
        // Degrade transport/timeout/parse failures to null (mirrors TenantProvisioningClient): this
        // fires automatically and repeatedly during the provisioning poll, so a blip must not throw
        // and abort the flow — the token source keeps the current token and retries next tick.
        try {
            var resp = await http.PostAsync(
                $"{apiBase.TrimEnd('/')}/user_management/authenticate",
                new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["grant_type"]    = "refresh_token",
                    ["client_id"]     = clientId,
                    ["refresh_token"] = refreshToken
                }), ct);

            if (!resp.IsSuccessStatusCode) return null;

            return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.WorkOSAuthResponse, ct);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException) {
            return null;
        }
    }

    // The server-supplied code-exchange URL must be a fully-qualified http(s) URI before
    // we trust it. An empty string, whitespace, relative path, or javascript:/file: URL
    // is treated as "no browser flow available" and the dispatcher falls back to device flow.
    internal static bool IsValidExchangeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
     && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    internal static int GetAvailablePort() {
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        return port;
    }
}
