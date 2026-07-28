using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Server identity decides whether a stored token may be sent to a given server, so both
/// directions matter: too strict and a legitimate token is refused, too loose and a token
/// crosses to a server that never issued it.
/// </summary>
public class ServerIdentityTests {
    [Test]
    [Arguments("https://kcap.example.com", "https://kcap.example.com:443")]
    [Arguments("http://kcap.example.com", "http://kcap.example.com:80")]
    [Arguments("https://KCAP.Example.COM", "https://kcap.example.com")]
    [Arguments("HTTPS://kcap.example.com", "https://kcap.example.com")]
    [Arguments("https://kcap.example.com/", "https://kcap.example.com")]
    [Arguments("https://kcap.example.com/base/", "https://kcap.example.com/base")]
    public async Task Equivalent_urls_name_the_same_server(string left, string right)
        => await Assert.That(ServerIdentity.SameServer(left, right)).IsTrue();

    [Test]
    [Arguments("https://kcap.example.com", "https://other.example.com")]
    [Arguments("https://kcap.example.com", "http://kcap.example.com")]
    [Arguments("https://kcap.example.com:8443", "https://kcap.example.com")]
    // Paths are case-sensitive in HTTP, so two path-routed tenants must stay distinct.
    [Arguments("https://kcap.example.com/Tenant", "https://kcap.example.com/tenant")]
    [Arguments("https://kcap.example.com/a", "https://kcap.example.com/b")]
    public async Task Different_servers_do_not_match(string left, string right)
        => await Assert.That(ServerIdentity.SameServer(left, right)).IsFalse();

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("kcap.example.com")]            // relative — no scheme
    [Arguments("ftp://kcap.example.com")]      // not http(s)
    [Arguments("https://user:pw@kcap.example.com")] // userinfo
    [Arguments("https://kcap.example.com?tenant=a")] // query
    [Arguments("https://kcap.example.com#frag")]     // fragment
    public async Task Inadmissible_inputs_canonicalize_to_null(string? url)
        => await Assert.That(ServerIdentity.Canonicalize(url)).IsNull();

    [Test]
    public async Task Query_bearing_urls_never_match_even_each_other() {
        // Dropping the query instead of rejecting it would make these compare equal.
        await Assert.That(ServerIdentity.SameServer(
            "https://kcap.example.com/base?tenant=a", "https://kcap.example.com/base?tenant=b")).IsFalse();
        await Assert.That(ServerIdentity.SameServer(
            "https://kcap.example.com/base?tenant=a", "https://kcap.example.com/base?tenant=a")).IsFalse();
    }

    [Test]
    // The canonical form always carries the EFFECTIVE port, which is what makes an implicit and an
    // explicit default port compare equal.
    [Arguments("https://kcap.example.com/", "https://kcap.example.com:443")]
    [Arguments("https://KCAP.Example.COM:443", "https://kcap.example.com:443")]
    [Arguments("http://localhost:5108", "http://localhost:5108")]
    public async Task Stamping_yields_the_canonical_form(string input, string expected) {
        var ok = ServerIdentity.TryCanonicalizeForStamping(input, out var canonical, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(canonical).IsEqualTo(expected);
        await Assert.That(error).IsEmpty();
    }

    [Test]
    [Arguments("https://kcap.example.com?tenant=a")]
    [Arguments("https://user:pw@kcap.example.com")]
    [Arguments("ftp://kcap.example.com")]
    [Arguments("kcap.example.com")]
    public async Task Stamping_refuses_a_url_it_cannot_bind(string url) {
        // A null ServerUrl means "pre-upgrade token, unenforced". Minting a NEW token with one
        // would silently downgrade it to a credential any server is allowed to receive, so login
        // must fail loudly instead.
        var ok = ServerIdentity.TryCanonicalizeForStamping(url, out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains(url);
    }

    [Test]
    public async Task Unparseable_side_never_matches_a_valid_side() {
        // Fail closed: "we can't tell" must not read as "same server".
        await Assert.That(ServerIdentity.SameServer("not a url", "https://kcap.example.com")).IsFalse();
        await Assert.That(ServerIdentity.SameServer(null, null)).IsFalse();
    }
}
