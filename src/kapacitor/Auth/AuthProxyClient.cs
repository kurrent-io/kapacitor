using System.Net;
using System.Net.Http.Json;

namespace kapacitor.Auth;

public interface IAuthProxyClient {
    Task<string?>         GetGitHubClientIdAsync(string proxyUrl);
    Task<DiscoveryResult> DiscoverTenantsAsync(string proxyUrl, string githubAccessToken);
}

public class AuthProxyClient(HttpClient http) : IAuthProxyClient {
    public async Task<string?> GetGitHubClientIdAsync(string proxyUrl) {
        try {
            var response = await http.GetAsync($"{proxyUrl}/config");
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.ProxyConfigResponse);
            return body?.GitHubClientId;
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return null;
        }
    }

    public async Task<DiscoveryResult> DiscoverTenantsAsync(string proxyUrl, string githubAccessToken) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/discover-tenants");
            request.Headers.Authorization = new("Bearer", githubAccessToken);
            var response = await http.SendAsync(request);

            return response.StatusCode switch {
                HttpStatusCode.OK                                       => new(await ReadTenants(response), DiscoveryError.None),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new([], DiscoveryError.TokenRejected),
                _                                                       => new([], DiscoveryError.UpstreamError)
            };
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return new([], DiscoveryError.ProxyUnreachable);
        }
    }

    static async Task<DiscoveredTenant[]> ReadTenants(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.DiscoveredTenantArray) ?? [];
}
