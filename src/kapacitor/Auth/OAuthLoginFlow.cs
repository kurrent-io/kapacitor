using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
// ReSharper disable MethodHasAsyncOverload

namespace kapacitor.Auth;

public static class OAuthLoginFlow {
    public static async Task<int> LoginWithDiscoveryAsync(string serverUrl) {
        // ReSharper disable once ShortLivedHttpClient
        using var http = new HttpClient();

        HttpResponseMessage configResponse;

        try {
            configResponse = await http.GetAsync($"{serverUrl}/auth/config");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(serverUrl, ex);

            return 1;
        }

        if (!configResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error: Failed to fetch auth config from {serverUrl}/auth/config");

            return 1;
        }

        var config = (await configResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.AuthDiscoveryResponse))!;

        return config.Provider switch {
            "None"      => HandleNoneLogin(),
            "GitHubApp" => await HandleGitHubLogin(serverUrl, config),
            "Auth0"     => await HandleAuth0Login(config),
            _           => HandleUnknownProvider(config.Provider)
        };
    }

    static int HandleNoneLogin() {
        Console.Out.WriteLine("Server has no authentication configured — login not required.");

        return 0;
    }

    static int HandleUnknownProvider(string provider) {
        Console.Error.WriteLine($"Error: Unknown auth provider '{provider}'. Update your kapacitor CLI.");

        return 1;
    }

    static async Task<int> HandleGitHubLogin(string serverUrl, AuthDiscoveryResponse config) {
        var clientId = config.GithubClientId!;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new("application/json"));

        var deviceResponse = await http.PostAsync(
            "https://github.com/login/device/code",
            new FormUrlEncodedContent(
                new Dictionary<string, string> {
                    ["client_id"] = clientId,
                    ["scope"]     = "read:user read:org"
                }
            )
        );

        if (!deviceResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error requesting device code: {await deviceResponse.Content.ReadAsStringAsync()}");

            return 1;
        }

        var device   = (await deviceResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.GitHubDeviceCodeResponse))!;
        var interval = device.Interval;

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"  Enter code: {device.UserCode}");
        await Console.Out.WriteLineAsync($"  at: {device.VerificationUri}");
        await Console.Out.WriteLineAsync();

        try { Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true }); } catch {
            /* Browser open is best-effort */
        }

        Console.Write("Waiting for authorization...");

        string? accessToken = null;

        while (accessToken is null) {
            await Task.Delay(TimeSpan.FromSeconds(interval));

            var tokenResponse = await http.PostAsync(
                "https://github.com/login/oauth/access_token",
                new FormUrlEncodedContent(
                    new Dictionary<string, string> {
                        ["client_id"]   = clientId,
                        ["device_code"] = device.DeviceCode,
                        ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code"
                    }
                )
            );

            var tokenResult = (await tokenResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.GitHubTokenResponse))!;

            if (tokenResult.AccessToken is not null) {
                accessToken = tokenResult.AccessToken;
            } else if (tokenResult.Error is not null) {
                switch (tokenResult.Error) {
                    case "authorization_pending":
                        Console.Write(".");

                        continue;
                    case "slow_down":
                        interval += 5;

                        continue;
                    default:
                        Console.Error.WriteLine($"\nError: {tokenResult.Error}");

                        return 1;
                }
            }
        }

        await Console.Out.WriteLineAsync(" done!");

        var exchangeResponse = await http.PostAsJsonAsync(
            $"{serverUrl}/auth/token",
            new() { GithubAccessToken = accessToken },
            KapacitorJsonContext.Default.TokenExchangeRequest
        );

        if (!exchangeResponse.IsSuccessStatusCode) {
            var errorBody = await exchangeResponse.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"Error exchanging token: {errorBody}");

            return 1;
        }

        var exchange = (await exchangeResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.TokenExchangeResponse))!;

        await TokenStore.SaveAsync(
            new() {
                AccessToken    = exchange.AccessToken,
                ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(exchange.ExpiresIn),
                GitHubUsername = exchange.Username,
                Provider       = config.Provider
            }
        );

        await Console.Out.WriteLineAsync($"Logged in as {exchange.Username}");

        return 0;
    }

    static async Task<int> HandleAuth0Login(AuthDiscoveryResponse config) {
        return await LoginAsync(config.Auth0Domain!, config.ClientId!, config.Audience ?? "");
    }

    /// <summary>
    /// Auth0 PKCE login flow (preserved for Auth0 strategy).
    /// </summary>
    static async Task<int> LoginAsync(string auth0Domain, string clientId, string audience) {
        var verifier    = GenerateCodeVerifier();
        var challenge   = GenerateCodeChallenge(verifier);
        var port        = GetAvailablePort();
        var redirectUri = $"http://localhost:{port}/callback";

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var authUrl = $"https://{auth0Domain}/authorize?"                           +
            $"response_type=code&client_id={Uri.EscapeDataString(clientId)}"        +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"                    +
            $"&scope={Uri.EscapeDataString("openid profile email offline_access")}" +
            $"&audience={Uri.EscapeDataString(audience)}"                           +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        await Console.Out.WriteLineAsync("Opening browser for authentication...");
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var context = await listener.GetContextAsync();
        var code    = context.Request.QueryString["code"];

        const string html = "<html><body><h2>Authentication successful!</h2><p>You can close this window.</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType     = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
        listener.Stop();

        if (string.IsNullOrEmpty(code)) {
            Console.Error.WriteLine("Error: No authorization code received.");

            return 1;
        }

        using var http = new HttpClient();

        var tokenResponse = await http.PostAsync(
            $"https://{auth0Domain}/oauth/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string> {
                    ["grant_type"]    = "authorization_code",
                    ["client_id"]     = clientId,
                    ["code"]          = code,
                    ["redirect_uri"]  = redirectUri,
                    ["code_verifier"] = verifier
                }
            )
        );

        if (!tokenResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error: {await tokenResponse.Content.ReadAsStringAsync()}");

            return 1;
        }

        var json = (await tokenResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.Auth0TokenResponse))!;

        var username = "unknown";

        if (json.IdToken is not null) {
            var payload = json.IdToken.Split('.')[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claims = JsonSerializer.Deserialize(Convert.FromBase64String(payload), KapacitorJsonContext.Default.Auth0IdTokenClaims);
            username = claims?.Nickname ?? "unknown";
        }

        await TokenStore.SaveAsync(
            new() {
                AccessToken    = json.AccessToken,
                RefreshToken   = json.RefreshToken,
                ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(json.ExpiresIn),
                GitHubUsername = username,
                Provider       = "Auth0",
                Auth0Domain    = auth0Domain,
                ClientId       = clientId
            }
        );

        await Console.Out.WriteLineAsync($"Logged in as {username}");

        return 0;
    }

    static string GenerateCodeVerifier() {
        var bytes = RandomNumberGenerator.GetBytes(32);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static string GenerateCodeChallenge(string verifier) {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));

        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static int GetAvailablePort() {
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        return port;
    }
}
