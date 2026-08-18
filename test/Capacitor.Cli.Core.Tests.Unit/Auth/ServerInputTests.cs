using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class ServerInputTests {
    [Test]
    public async Task ResolveTenantArg_expands_bare_label_to_kcap_subdomain() {
        await Assert.That(ServerInput.ResolveTenantArg("eventuous")).IsEqualTo("https://eventuous.kcap.ai");
    }

    // The zero-discovery "I already have a workspace" prompt invites a paste, and what people paste
    // is the page they are looking at. Everything downstream appends a fixed root path
    // (/auth/config), so a path on the input silently probes the wrong endpoint and reports the
    // server unreachable. Reduce to the origin first.
    [Test]
    public async Task ToServerOrigin_reduces_a_pasted_page_url_to_its_origin() {
        await Assert.That(ServerInput.ToServerOrigin("https://acme.kcap.ai/sessions?tab=all#x"))
            .IsEqualTo("https://acme.kcap.ai");
        await Assert.That(ServerInput.ToServerOrigin("acme.kcap.ai/sessions")).IsEqualTo("acme.kcap.ai");
        await Assert.That(ServerInput.ToServerOrigin("http://localhost:5108/repo/abc")).IsEqualTo("http://localhost:5108");
        await Assert.That(ServerInput.ToServerOrigin("localhost:5108/repo/abc")).IsEqualTo("localhost:5108");
    }

    [Test]
    public async Task ToServerOrigin_leaves_a_bare_slug_origin_or_ipv6_host_alone() {
        await Assert.That(ServerInput.ToServerOrigin("acme")).IsEqualTo("acme");
        await Assert.That(ServerInput.ToServerOrigin("https://acme.kcap.ai")).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(ServerInput.ToServerOrigin("https://acme.kcap.ai/")).IsEqualTo("https://acme.kcap.ai");
        // Bracketed IPv6 keeps its colons and port — the ':' and '/' scan must not cut inside it.
        await Assert.That(ServerInput.ToServerOrigin("[::1]:5108")).IsEqualTo("[::1]:5108");
        await Assert.That(ServerInput.ToServerOrigin("[::1]:5108/x")).IsEqualTo("[::1]:5108");
        await Assert.That(ServerInput.ToServerOrigin("http://[::1]:5108/x")).IsEqualTo("http://[::1]:5108");
    }

    [Test]
    public async Task ResolveTenantArg_leaves_urls_fqdns_and_hosts_untouched() {
        await Assert.That(ServerInput.ResolveTenantArg("https://x.example")).IsEqualTo("https://x.example");
        await Assert.That(ServerInput.ResolveTenantArg("self.hosted.example")).IsEqualTo("self.hosted.example");
        await Assert.That(ServerInput.ResolveTenantArg("localhost:5108")).IsEqualTo("localhost:5108");
        await Assert.That(ServerInput.ResolveTenantArg("localhost")).IsEqualTo("localhost"); // bare loopback, not a slug
    }

    // Spectre's --default-visibility prompt offers these as an ordered choice list; changing the
    // order changes which one a bare Enter picks.
    [Test]
    public async Task ValidVisibilities_is_the_pinned_set_and_order() {
        await Assert.That(AppConfig.ValidVisibilities)
            .IsEquivalentTo(["private", "project", "org_public", "public"], CollectionOrdering.Matching);
    }
}
