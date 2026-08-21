using System.Net;
using System.Net.Http.Json;

namespace Capacitor.Cli.Core.Auth;

public interface IAuthProxyClient {
    Task<ProxyConfigResponse?> GetConfigAsync(string proxyUrl, CancellationToken ct = default);
    Task<DiscoveryResult>      DiscoverTenantsAsync(string proxyUrl, string githubAccessToken, CancellationToken ct = default);
    Task<DiscoveryResult>      DiscoverWorkOSTenantsAsync(string proxyUrl, string workosAccessToken, CancellationToken ct = default);
}

public class AuthProxyClient(HttpClient http) : IAuthProxyClient {
    public async Task<ProxyConfigResponse?> GetConfigAsync(string proxyUrl, CancellationToken ct = default) {
        try {
            using var response = await http.GetAsync($"{proxyUrl}/config", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ProxyConfigResponse, ct);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return null;
        }
    }

    public async Task<DiscoveryResult> DiscoverTenantsAsync(string proxyUrl, string githubAccessToken, CancellationToken ct = default) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/discover-tenants");
            request.Headers.Authorization = new("Bearer", githubAccessToken);
            using var response = await http.SendAsync(request, ct);

            return response.StatusCode switch {
                HttpStatusCode.OK                                       => new(await ReadTenants(response, ct), DiscoveryError.None),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new([], DiscoveryError.TokenRejected),
                _                                                       => new([], DiscoveryError.UpstreamError)
            };
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return new([], DiscoveryError.ProxyUnreachable);
        }
    }

    public async Task<DiscoveryResult> DiscoverWorkOSTenantsAsync(string proxyUrl, string workosAccessToken, CancellationToken ct = default) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/discover-tenants-workos");
            request.Headers.Authorization = new("Bearer", workosAccessToken);
            using var response = await http.SendAsync(request, ct);

            return response.StatusCode switch {
                HttpStatusCode.OK                                       => new(await ReadTenants(response, ct), DiscoveryError.None),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new([], DiscoveryError.TokenRejected),
                _                                                       => new([], DiscoveryError.UpstreamError)
            };
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return new([], DiscoveryError.ProxyUnreachable);
        }
    }

    static async Task<DiscoveredTenant[]> ReadTenants(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.DiscoveredTenantArray, ct) ?? [];
}
