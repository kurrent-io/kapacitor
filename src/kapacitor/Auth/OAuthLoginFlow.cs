using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace kapacitor.Auth;

public static class OAuthLoginFlow {
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
