namespace kapacitor.Auth;

public static class AuthProxyEndpoint {
    public const string DefaultUrl = "https://auth.kapacitor.ai";

    // KAPACITOR_AUTH_PROXY_URL is an internal dev/test override; not documented for end users.
    public static string Url =>
        (Environment.GetEnvironmentVariable("KAPACITOR_AUTH_PROXY_URL") ?? DefaultUrl).TrimEnd('/');
}
