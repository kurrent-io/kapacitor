using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace kapacitor.Auth;

public sealed record ProxyConfigResponse {
    [JsonPropertyName("github_client_id")] public string GitHubClientId { get; init; } = "";
}

public sealed record DiscoveredTenant {
    [JsonPropertyName("org_id")]    public long   OrgId    { get; init; }
    [JsonPropertyName("org_login")] public string OrgLogin { get; init; } = "";
    [JsonPropertyName("origin")]    public string Origin   { get; init; } = "";
}

public enum DiscoveryError {
    None,
    ProxyUnreachable,
    TokenRejected,
    GitHubError
}

public sealed record DiscoveryResult(DiscoveredTenant[] Tenants, DiscoveryError Error);

public class AuthProxyClient(HttpClient http) {
    public async Task<string?> GetGitHubClientIdAsync(string proxyUrl) {
        try {
            var response = await http.GetAsync($"{proxyUrl}/config");
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.ProxyConfigResponse);
            return body?.GitHubClientId;
        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException) {
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
                _                                                       => new([], DiscoveryError.GitHubError)
            };
        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException) {
            return new([], DiscoveryError.ProxyUnreachable);
        }
    }

    static async Task<DiscoveredTenant[]> ReadTenants(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.DiscoveredTenantArray) ?? [];
}
