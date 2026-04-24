namespace kapacitor.Auth;

public static class AuthProxyEndpoint {
    public const string DefaultUrl = "https://auth.kurrent.example";

    public static string Url =>
        (Environment.GetEnvironmentVariable("KAPACITOR_AUTH_PROXY_URL") ?? DefaultUrl).TrimEnd('/');
}
