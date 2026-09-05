using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// HTTP adapter behind <see cref="ITitleServerPort"/>: reads a session's title from the
/// summary route and pushes a locally resolved one through the same <c>/hooks/set-title</c>
/// path the import sources use. <see cref="GetTitleAsync"/> throws when the server cannot be
/// asked (auth lapse, transport failure, non-404 error) — the resolve loop must distinguish
/// "the server has no title" from "the server couldn't answer", or an outage would trigger a
/// paid generation for a session the watcher already titled.
/// </summary>
internal sealed class TitleServerPort(ConfigRoot configRoot, ProfileContext profiles, string baseUrl) : ITitleServerPort {
    readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<string?> GetTitleAsync(string sessionId, CancellationToken ct) {
        // An id the summary route cannot address (an ACP handshake id, say) has no server-side
        // session to converge with — that is genuine silence, not a failed read.
        if (WorkContextIds.CanonicalSessionId(sessionId) is null) return null;

        var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(configRoot, profiles, _baseUrl, ct);

        using (client) {
            if (status is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) {
                throw new InvalidOperationException($"server auth unavailable: {status}");
            }

            var outcome = await new WorkContextClient(client, _baseUrl).GetSessionSummaryAsync(sessionId, ct);

            return outcome.StatusCode switch {
                >= 200 and < 300 => outcome.Body?.Title,
                404              => null, // not registered server-side (yet): silence, not failure
                _                => throw new HttpRequestException($"session summary read failed: {outcome.StatusCode}"),
            };
        }
    }

    public async Task<bool> PushTitleAsync(string sessionId, string title, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is null) return true; // nothing to converge with

        var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(configRoot, profiles, _baseUrl, ct);

        using (client) {
            if (status is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) return false;

            var payload = new JsonObject {
                ["session_id"] = sessionId,
                ["title"]      = title,
            };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostAsync(new Uri($"{_baseUrl}/hooks/set-title"), content, ct);

            return resp.IsSuccessStatusCode;
        }
    }
}
