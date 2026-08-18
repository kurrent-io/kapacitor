using System.Net;
using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>Every HTTP endpoint the onboarding façade can touch, served from one scripted handler.</summary>
public sealed class AuthHttpScript(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
    public List<string> Seen { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Seen.Add($"{request.Method} {request.RequestUri}");

        return Task.FromResult(respond(request));
    }
}

public static class AuthHttp {
    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public const string DeviceCode = """{"device_code":"dc","user_code":"UC","verification_uri":"","interval":0}""";

    /// <param name="tokenExchange">POST {tenant}/auth/token — defaults to a JWT for "alice".</param>
    public static AuthHttpScript Script(
            string?                                        authConfig    = null,
            string?                                        proxyConfig   = null,
            string?                                        tenants       = null,
            string?                                        workosTenants = null,
            string?                                        orgSwitch     = null,
            Func<HttpRequestMessage, HttpResponseMessage>? tokenExchange = null,
            Func<HttpResponseMessage>?                     devicePoll    = null) =>
        new(request => {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/auth/config", StringComparison.Ordinal)) {
                return authConfig is null ? Status(HttpStatusCode.NotFound) : Json(authConfig);
            }

            if (path == "/config") {
                return proxyConfig is null ? Status(HttpStatusCode.NotFound) : Json(proxyConfig);
            }

            if (path == "/discover-tenants") {
                return tenants is null ? Status(HttpStatusCode.InternalServerError) : Json(tenants);
            }

            if (path == "/discover-tenants-workos") {
                return workosTenants is null ? Status(HttpStatusCode.InternalServerError) : Json(workosTenants);
            }

            if (path == "/user_management/authenticate") {
                return orgSwitch is null ? Status(HttpStatusCode.Unauthorized) : Json(orgSwitch);
            }

            if (path == "/login/device/code") return Json(DeviceCode);

            if (path == "/login/oauth/access_token") {
                return devicePoll?.Invoke() ?? Json("""{"access_token":"gh-token"}""");
            }

            if (path.EndsWith("/auth/token", StringComparison.Ordinal)) {
                return tokenExchange?.Invoke(request)
                    ?? Json("""{"access_token":"capacitor-jwt","expires_in":3600,"username":"alice"}""");
            }

            return Status(HttpStatusCode.NotFound);
        });
}
