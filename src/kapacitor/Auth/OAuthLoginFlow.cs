using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace kapacitor.Auth;

public static class OAuthLoginFlow {
    public static async Task<int> LoginWithDiscoveryAsync(string serverUrl) {
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

        var config = await configResponse.Content.ReadFromJsonAsync<JsonElement>();
        var provider = config.GetProperty("provider").GetString()!;

        return provider switch {
            "None" => HandleNoneLogin(),
            "GitHub" => await HandleGitHubLogin(serverUrl, config),
            "Auth0" => await HandleAuth0Login(config),
            _ => HandleUnknownProvider(provider)
        };
    }

    static int HandleNoneLogin() {
        Console.WriteLine("Server has no authentication configured — login not required.");
        return 0;
    }

    static int HandleUnknownProvider(string provider) {
        Console.Error.WriteLine($"Error: Unknown auth provider '{provider}'. Update your kapacitor CLI.");
        return 1;
    }

    static async Task<int> HandleGitHubLogin(string serverUrl, JsonElement config) {
        var clientId = config.GetProperty("github_client_id").GetString()!;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var deviceResponse = await http.PostAsync("https://github.com/login/device/code",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["client_id"] = clientId,
                ["scope"] = "read:user read:org"
            }));

        if (!deviceResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error requesting device code: {await deviceResponse.Content.ReadAsStringAsync()}");
            return 1;
        }

        var device = await deviceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var deviceCode = device.GetProperty("device_code").GetString()!;
        var userCode = device.GetProperty("user_code").GetString()!;
        var verificationUri = device.GetProperty("verification_uri").GetString()!;
        var interval = device.TryGetProperty("interval", out var intervalProp) ? intervalProp.GetInt32() : 5;

        Console.WriteLine();
        Console.WriteLine($"  Enter code: {userCode}");
        Console.WriteLine($"  at: {verificationUri}");
        Console.WriteLine();

        try { Process.Start(new ProcessStartInfo(verificationUri) { UseShellExecute = true }); }
        catch { /* Browser open is best-effort */ }

        Console.Write("Waiting for authorization...");

        string? accessToken = null;

        while (accessToken is null) {
            await Task.Delay(TimeSpan.FromSeconds(interval));

            var tokenResponse = await http.PostAsync("https://github.com/login/oauth/access_token",
                new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["client_id"] = clientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }));

            var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();

            if (tokenJson.TryGetProperty("access_token", out var at)) {
                accessToken = at.GetString();
            } else if (tokenJson.TryGetProperty("error", out var error)) {
                var errorCode = error.GetString();
                if (errorCode == "authorization_pending") {
                    Console.Write(".");
                    continue;
                }
                if (errorCode == "slow_down") {
                    interval += 5;
                    continue;
                }
                Console.Error.WriteLine($"\nError: {errorCode}");
                return 1;
            }
        }

        Console.WriteLine(" done!");

        var exchangeResponse = await http.PostAsJsonAsync($"{serverUrl}/auth/token",
            new { github_access_token = accessToken });

        if (!exchangeResponse.IsSuccessStatusCode) {
            var errorBody = await exchangeResponse.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"Error exchanging token: {errorBody}");
            return 1;
        }

        var exchange = await exchangeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var capacitorToken = exchange.GetProperty("access_token").GetString()!;
        var expiresIn = exchange.GetProperty("expires_in").GetInt32();
        var username = exchange.GetProperty("username").GetString()!;

        TokenStore.Save(new StoredTokens {
            AccessToken = capacitorToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            GitHubUsername = username,
            Provider = "GitHub"
        });

        Console.WriteLine($"Logged in as {username}");
        return 0;
    }

    static async Task<int> HandleAuth0Login(JsonElement config) {
        var domain = config.GetProperty("auth0_domain").GetString()!;
        var clientId = config.GetProperty("client_id").GetString()!;
        var audience = config.TryGetProperty("audience", out var aud) ? aud.GetString()! : "";

        return await LoginAsync(domain, clientId, audience);
    }

    /// <summary>
    /// Auth0 PKCE login flow (preserved for Auth0 strategy).
    /// </summary>
    public static async Task<int> LoginAsync(string auth0Domain, string clientId, string audience) {
        var verifier     = GenerateCodeVerifier();
        var challenge    = GenerateCodeChallenge(verifier);
        var port         = GetAvailablePort();
        var redirectUri  = $"http://localhost:{port}/callback";

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var authUrl = $"https://{auth0Domain}/authorize?" +
            $"response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid profile email offline_access")}" +
            $"&audience={Uri.EscapeDataString(audience)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        Console.WriteLine("Opening browser for authentication...");
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var context = await listener.GetContextAsync();
        var code    = context.Request.QueryString["code"];

        var html   = "<html><body><h2>Authentication successful!</h2><p>You can close this window.</p></body></html>";
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

        using var http          = new HttpClient();
        var       tokenResponse = await http.PostAsync(
            $"https://{auth0Domain}/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = clientId,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = verifier
            })
        );

        if (!tokenResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error: {await tokenResponse.Content.ReadAsStringAsync()}");
            return 1;
        }

        var json         = JsonSerializer.Deserialize<JsonElement>(await tokenResponse.Content.ReadAsStringAsync());
        var accessToken  = json.GetProperty("access_token").GetString()!;
        var refreshToken = json.GetProperty("refresh_token").GetString()!;
        var expiresIn    = json.GetProperty("expires_in").GetInt32();

        var idToken = json.GetProperty("id_token").GetString()!;
        var payload = idToken.Split('.')[1];
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims   = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));
        var username = claims.GetProperty("nickname").GetString() ?? "unknown";

        TokenStore.Save(new StoredTokens {
            AccessToken    = accessToken,
            RefreshToken   = refreshToken,
            ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            GitHubUsername = username,
            Provider       = "Auth0",
            Auth0Domain    = auth0Domain,
            ClientId       = clientId
        });

        Console.WriteLine($"Logged in as {username}");
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
