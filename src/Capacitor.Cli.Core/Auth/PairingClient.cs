using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

/// <summary>A mint attempt. <c>StatusCode 0</c> is a transport failure, and 404 specifically means
/// this tenant does not serve the pairing channel at all — see <see cref="PairingClient"/>.</summary>
public sealed record MintOutcome(int StatusCode, MintPairingResponse? Body);

/// <summary>One poll. The body is absent on every non-200, and on a 200 whose body was unreadable —
/// which <see cref="PairingPoll.Classify"/> treats as "keep waiting" rather than as an answer.</summary>
public sealed record PollOutcome(int StatusCode, PairingStatusResponse? Body);

/// <summary>The pairing routes the flow needs, as a seam: the flow's loop, backoff and guards are
/// the part worth testing, and they should not need a socket to exercise.</summary>
public interface IPairingChannel {
    Task<MintOutcome> MintAsync(string serverUrl, string machineId, string machineName, CancellationToken ct);

    Task<PollOutcome> PollAsync(string serverUrl, string pairingId, string secret, CancellationToken ct);

    Task<int> CompleteAsync(string serverUrl, string pairingId, string secret, string? accessToken, CancellationToken ct);
}

/// <summary>
/// The CLI's client for the tenant's pairing routes.
///
/// <para>Degrades rather than throws, on <see cref="TenantProvisioningClient"/>'s convention: a
/// transient blip mid-poll must not crash an interactive <c>kcap setup</c>, and the poll loop is the
/// right place to decide what a blip means.</para>
/// </summary>
public sealed class PairingClient(HttpClient http) : IPairingChannel {
    /// <summary>
    /// Mints a pairing on the tenant.
    ///
    /// <para><b>A 404 is the availability oracle for the whole browser flow.</b> The routes are
    /// mapped only when the tenant has <c>Features:FirstRunSetup</c> on, so their absence is a fact
    /// the CLI can observe rather than a version number it has to guess at — and a tenant that has
    /// not enabled the flow gets today's login path with nothing to configure.</para>
    /// </summary>
    public async Task<MintOutcome> MintAsync(
            string serverUrl, string machineId, string machineName, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            new MintPairingRequest { MachineId = machineId, MachineName = machineName },
            CapacitorJsonContext.Default.MintPairingRequest);

        try {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/pairings") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode) return new((int)resp.StatusCode, null);

            MintPairingResponse? body = null;

            // Guarded like the poll's: an unreadable body must not collapse to StatusCode 0, which
            // the flow reports as "could not reach the server" about a server that just answered.
            try { body = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.MintPairingResponse, ct); }
            catch (Exception e) when (IsTransient(e)) { /* leave null — the flow's guard reports it */ }

            return new((int)resp.StatusCode, body);
        } catch (Exception e) when (IsTransient(e)) {
            return new(0, null);
        }
    }

    public async Task<PollOutcome> PollAsync(
            string serverUrl, string pairingId, string secret, CancellationToken ct) {
        try {
            using var req = Secured(HttpMethod.Get, $"{serverUrl}/api/pairings/{Uri.EscapeDataString(pairingId)}/status", secret);
            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode) return new((int)resp.StatusCode, null);

            PairingStatusResponse? body = null;

            try { body = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.PairingStatusResponse, ct); }
            catch (Exception e) when (IsTransient(e)) { /* 200 with an unreadable body — poll again */ }

            return new((int)resp.StatusCode, body);
        } catch (Exception e) when (IsTransient(e)) {
            return new(0, null);
        }
    }

    /// <summary>
    /// Ends the flow and invalidates the secret.
    ///
    /// <para><paramref name="accessToken"/> is sent so the server can corroborate the continuity
    /// check the CLI has already made for itself: left purely client-side, an old or modified build
    /// would fail it silently. The server answers 403 on a mismatch.</para>
    ///
    /// <para>Returns the status for diagnosis only. <b>Completion is cosmetic to the caller</b> —
    /// invalidating the secret means a retried call whose first attempt succeeded answers 401, and by
    /// then setup has already finished.</para>
    /// </summary>
    public async Task<int> CompleteAsync(
            string serverUrl, string pairingId, string secret, string? accessToken, CancellationToken ct) {
        try {
            using var req = Secured(HttpMethod.Post, $"{serverUrl}/api/pairings/{Uri.EscapeDataString(pairingId)}/complete", secret);

            if (!string.IsNullOrWhiteSpace(accessToken)) req.Headers.Authorization = new("Bearer", accessToken);

            using var resp = await http.SendAsync(req, ct);

            return (int)resp.StatusCode;
        } catch (Exception e) when (IsTransient(e)) {
            return 0;
        }
    }

    static HttpRequestMessage Secured(HttpMethod method, string url, string secret) {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation(HttpClientExtensions.PairingSecretHeader, secret);

        return req;
    }

    // ct is CancellationToken.None on the setup path, so an OperationCanceledException here is an
    // HttpClient timeout rather than a user cancel.
    static bool IsTransient(Exception e) =>
        e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException;
}
