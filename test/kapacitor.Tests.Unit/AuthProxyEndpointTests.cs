using kapacitor.Auth;

namespace kapacitor.Tests.Unit;

public class AuthProxyEndpointTests {
    [Test]
    [NotInParallel(nameof(AuthProxyEndpointTests))]
    public async Task Returns_default_when_env_var_is_unset() {
        Environment.SetEnvironmentVariable("KAPACITOR_AUTH_PROXY_URL", null);
        try {
            await Assert.That(AuthProxyEndpoint.Url).IsEqualTo(AuthProxyEndpoint.DefaultUrl);
        } finally {
            Environment.SetEnvironmentVariable("KAPACITOR_AUTH_PROXY_URL", null);
        }
    }

    [Test]
    [NotInParallel(nameof(AuthProxyEndpointTests))]
    public async Task Uses_env_var_when_set() {
        Environment.SetEnvironmentVariable("KAPACITOR_AUTH_PROXY_URL", "https://local-proxy.test/");
        try {
            await Assert.That(AuthProxyEndpoint.Url).IsEqualTo("https://local-proxy.test");
        } finally {
            Environment.SetEnvironmentVariable("KAPACITOR_AUTH_PROXY_URL", null);
        }
    }
}
