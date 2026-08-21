using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DaemonBridgeUrlTests {
    [Test]
    public async Task TryParseLoopback_accepts_http_127_0_0_1() {
        var ok = DaemonBridgeUrl.TryParseLoopback("http://127.0.0.1:54321/abc123", out var baseUrl);
        await Assert.That(ok).IsTrue();
        await Assert.That(baseUrl).IsEqualTo("http://127.0.0.1:54321/abc123");
    }

    [Test]
    public async Task TryParseLoopback_strips_trailing_slash() {
        var ok = DaemonBridgeUrl.TryParseLoopback("http://127.0.0.1:54321/abc123/", out var baseUrl);
        await Assert.That(ok).IsTrue();
        await Assert.That(baseUrl).IsEqualTo("http://127.0.0.1:54321/abc123");
    }

    [Test]
    public async Task TryParseLoopback_rejects_https() {
        var ok = DaemonBridgeUrl.TryParseLoopback("https://127.0.0.1:54321/abc123", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_non_loopback_host() {
        var ok = DaemonBridgeUrl.TryParseLoopback("http://example.com:54321/abc123", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_null() {
        var ok = DaemonBridgeUrl.TryParseLoopback(null, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_empty() {
        var ok = DaemonBridgeUrl.TryParseLoopback("", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_malformed_uri() {
        var ok = DaemonBridgeUrl.TryParseLoopback("not-a-url", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_ipv6_localhost() {
        // Codex hook payloads should not leave loopback via IPv6 either.
        // We pin to literal "127.0.0.1" — IPv6 :: localhost (::1) is not accepted.
        var ok = DaemonBridgeUrl.TryParseLoopback("http://[::1]:54321/abc123", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_localhost_dns_name() {
        // We require literal IP — "localhost" could resolve to non-loopback in a misconfigured env.
        var ok = DaemonBridgeUrl.TryParseLoopback("http://localhost:54321/abc123", out _);
        await Assert.That(ok).IsFalse();
    }
}
