using System.Net;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Reports who you are AND whether the server agrees.
///
/// Printing local token metadata alone is misleading: "expires tomorrow" says only that a clock
/// hasn't passed, not that any server accepts the token. That gap once turned a real 401 into a
/// multi-day misdiagnosis, because whoami cheerfully reported a valid token for a server that was
/// rejecting every request. So this asks the server directly.
/// </summary>
public static class WhoamiCommand {
    /// <summary>Cheap authenticated GET used purely to ask "do you accept this token?".</summary>
    internal const string ProbePath = "/api/me/notification-prefs";

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The server's verdict on the token, and the exit code it implies.</summary>
    internal readonly record struct ProbeVerdict(string Line, int ExitCode);

    /// <summary>
    /// Maps a probe response to what we tell the user. ONLY 401/403 are verdicts about the token:
    /// everything else means we failed to ask, and reporting that as "rejected" would send people
    /// to re-run `kcap login` for an outage or an older server that lacks the endpoint.
    /// </summary>
    internal static ProbeVerdict Interpret(HttpStatusCode? status) => status switch {
        null                                    => new("could not verify (server unreachable)", 0),
        HttpStatusCode.Unauthorized             => new("REJECTS this token (run 'kcap login')", 1),
        HttpStatusCode.Forbidden                => new("REJECTS this token (run 'kcap login')", 1),
        HttpStatusCode.NotFound                 => new("could not verify (endpoint not available on this server)", 0),
        >= HttpStatusCode.OK and < (HttpStatusCode)300              => new("accepts this token", 0),
        >= (HttpStatusCode)300 and < (HttpStatusCode)400            => new("could not verify (unexpected redirect)", 0),
        { } other                               => new($"could not verify (server error {(int)other})", 0)
    };

    public static async Task<int> HandleAsync(string baseUrl) {
        var provider = await HttpClientExtensions.DiscoverProviderAsync(baseUrl);

        if (provider == "None") {
            await Console.Out.WriteLineAsync("Provider: None (no authentication)");
            await Console.Out.WriteLineAsync($"Server:   {baseUrl}");

            return 0;
        }

        // ONE raw snapshot for everything below — deliberately NOT the refresh-aware accessor.
        // Diagnosing your auth must not mutate it: routing this through a refresh could rotate a
        // WorkOS credential (single-use refresh token) as a side effect of merely running whoami,
        // and would let the expiry printed here describe a different token than the one probed.
        var profile  = await TokenStore.ResolveProfileNameAsync();
        var snapshot = await TokenStore.LoadForProfileAsync(profile);

        if (snapshot is null) {
            Console.Error.WriteLine("Not authenticated. Run `kcap login`.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Username: {snapshot.GitHubUsername}");
        await Console.Out.WriteLineAsync($"Provider: {snapshot.Provider}");
        await Console.Out.WriteLineAsync($"Profile:  {profile}");
        await Console.Out.WriteLineAsync($"Expires:  {snapshot.ExpiresAt:u}");
        await Console.Out.WriteLineAsync($"Server:   {baseUrl}");
        await Console.Out.WriteLineAsync($"Expired:  {(snapshot.IsExpired ? "yes" : "no")}");

        // A token minted elsewhere can never be accepted here, and no refresh can change that —
        // say so instead of spending a request to be told 401.
        if (snapshot.ServerUrl is not null && !ServerIdentity.SameServer(snapshot.ServerUrl, baseUrl)) {
            await Console.Out.WriteLineAsync(
                $"Server:   token was issued by {snapshot.ServerUrl} — run 'kcap login'");

            return 1;
        }

        var verdict = Interpret(await ProbeAsync(baseUrl, snapshot.AccessToken));
        await Console.Out.WriteLineAsync($"Server:   {verdict.Line}");

        return verdict.ExitCode;
    }

    // Null means "we never got an answer" (transport failure or timeout), which is deliberately
    // distinct from any status code the server did return.
    static async Task<HttpStatusCode?> ProbeAsync(string baseUrl, string accessToken) {
        try {
            // No redirect following: a login redirect would otherwise masquerade as some other
            // status. No retry handler either — this client must send exactly the token we printed.
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

            using var response = await http.GetOnceAsync(
                $"{AppConfig.NormalizeUrl(baseUrl)}{ProbePath}", ProbeTimeout);

            return response.StatusCode;
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) {
            return null;
        }
    }
}
