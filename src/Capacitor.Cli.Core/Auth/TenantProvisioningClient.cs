using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Auth;

// Result of a provision POST: HTTP status + parsed body (202 provisioning /
// 200 active / 400 / 409). StatusCode 0 signals a transport/parse failure
// (network error, timeout, or unreadable body); the caller maps it to a failed
// offer. Not serialized — no JSON context entry.
public sealed record ProvisionOutcome(int StatusCode, ProvisionResponse? Body);

// Result of a status GET: HTTP status + parsed body. Surfacing the status (rather than
// collapsing everything to a nullable body) lets the poll distinguish "still provisioning"
// (200) from auth/ownership failures (401/403/404) and transport blips (0), instead of
// treating them all as "keep waiting" and spinning silently. StatusCode 0 = transport/parse
// failure. Not serialized — no JSON context entry.
public sealed record StatusOutcome(int StatusCode, StatusResponse? Body);

// Talks to kcap-web /api/signup/* with a WorkOS bearer access token. All calls
// are best-effort: transport / timeout / parse failures degrade to null
// (availability/status) or StatusCode 0 (provision) rather than throwing, so a
// transient blip during `kcap setup` can't crash the interactive create-tenant
// flow. Mirrors AuthProxyClient's swallow-and-degrade convention.
public sealed class TenantProvisioningClient(HttpClient http) {
    public async Task<AvailabilityResponse?> CheckAvailabilityAsync(
            string baseUrl, string token, string slug, CancellationToken ct) {
        try {
            using var req = Get($"{baseUrl}/api/signup/availability?slug={Uri.EscapeDataString(slug)}", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AvailabilityResponse, ct);
        } catch (Exception e) when (IsTransient(e)) {
            return null;
        }
    }

    public async Task<ProvisionOutcome> ProvisionAsync(
            string baseUrl, string token, string orgName, string slug, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            // Read off the process-wide static rather than threaded down through the provisioner:
            // it is per-run state, the same shape CliTelemetry already has everywhere, and null by
            // construction whenever telemetry is off.
            new ProvisionRequest { OrgName = orgName, Slug = slug, Tier = "free", JoinId = SetupJoin.Current },
            CapacitorJsonContext.Default.ProvisionRequest);

        try {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/signup/provision") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new("Bearer", token);

            using var resp = await http.SendAsync(req, ct);
            ProvisionResponse? body = null;
            try { body = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ProvisionResponse, ct); }
            catch (Exception e) when (IsTransient(e)) { /* empty/non-JSON body — leave null */ }
            return new((int)resp.StatusCode, body);
        } catch (Exception e) when (IsTransient(e)) {
            return new(0, null); // transport failure — caller maps to a failed offer
        }
    }

    public async Task<StatusOutcome> GetStatusAsync(
            string baseUrl, string token, string slug, CancellationToken ct) {
        try {
            using var req = Get($"{baseUrl}/api/signup/status?slug={Uri.EscapeDataString(slug)}", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new((int)resp.StatusCode, null);

            StatusResponse? body = null;
            try { body = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.StatusResponse, ct); }
            catch (Exception e) when (IsTransient(e)) { /* 2xx with empty/non-JSON body — leave null */ }
            return new((int)resp.StatusCode, body);
        } catch (Exception e) when (IsTransient(e)) {
            return new(0, null); // transport failure — caller treats as transient, keeps waiting
        }
    }

    // Network / timeout / unreadable-body failures we degrade on instead of throwing.
    // (ct is CancellationToken.None in the setup flow, so OperationCanceledException here
    // is an HttpClient timeout, not a user cancel.)
    static bool IsTransient(Exception e) =>
        e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException;

    static HttpRequestMessage Get(string url, string token) {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        return req;
    }
}
