using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Truth table for <see cref="UpdateAdvisoryResolver.Resolve(UpdateCommand.UpdateCheckResult?, string, string?)"/>
/// — the pure cap logic. The cap engages ONLY on the stable <c>latest</c> channel with a cached, stable
/// server version present; otherwise the raw npm result passes through unchanged. When capping, the
/// target is <c>min(npm latest, server version)</c> and "newer" is recomputed against it.
/// </summary>
public class UpdateAdvisoryResolverTests {
    static UpdateCommand.UpdateCheckResult Result(string current, string? latest, bool newer) =>
        new(current, latest, newer, FromCache: true);

    // ── passthrough (no cap) ─────────────────────────────────────────────────────

    [Test]
    public async Task NoCachedServer_PassesThroughNpmLatest() {
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.0", "0.12.0", newer: true), "latest", cachedServerVersion: null);

        await Assert.That(advisory.Target).IsEqualTo("0.12.0");
        await Assert.That(advisory.Newer).IsTrue();
        await Assert.That(advisory.ServerCapped).IsFalse();
    }

    [Test]
    public async Task BetaChannel_NotCapped_EvenWithACachedServer() {
        // Beta users deliberately ride ahead of the server; the cap never applies off the latest channel.
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.0", "0.12.0", newer: true), "beta", cachedServerVersion: "0.11.15");

        await Assert.That(advisory.Target).IsEqualTo("0.12.0");
        await Assert.That(advisory.ServerCapped).IsFalse();
    }

    [Test]
    public async Task CachedServerIsPrerelease_NotCapped() {
        // Belt-and-braces: the server only sends stable versions, but a malformed cached value must
        // not become a cap.
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.0", "0.12.0", newer: true), "latest", cachedServerVersion: "0.11.15-beta.1");

        await Assert.That(advisory.Target).IsEqualTo("0.12.0");
        await Assert.That(advisory.ServerCapped).IsFalse();
    }

    [Test]
    public async Task ServerAtOrAheadOfNpm_NotCapped() {
        // min(npm, server) = npm here — a server at/ahead of npm never withholds an installable release.
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.0", "0.11.15", newer: true), "latest", cachedServerVersion: "0.12.0");

        await Assert.That(advisory.Target).IsEqualTo("0.11.15");
        await Assert.That(advisory.ServerCapped).IsFalse();
    }

    // ── capping ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task NpmAheadOfServer_CapsAtServerVersion() {
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.0", "0.12.0", newer: true), "latest", cachedServerVersion: "0.11.15");

        await Assert.That(advisory.Target).IsEqualTo("0.11.15")
            .Because("capped at the server version, never the withheld npm latest");
        await Assert.That(advisory.Newer).IsTrue();
        await Assert.That(advisory.ServerCapped).IsTrue();
    }

    [Test]
    public async Task UserAtServerVersion_ButBehindNpm_NotNewer() {
        // Already as current as the server supports ⇒ no nudge (never a downgrade recommendation).
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.15", "0.12.0", newer: true), "latest", cachedServerVersion: "0.11.15");

        await Assert.That(advisory.Target).IsEqualTo("0.11.15");
        await Assert.That(advisory.Newer).IsFalse();
        await Assert.That(advisory.ServerCapped).IsTrue();
    }

    [Test]
    public async Task UserAheadOfServerTarget_NotNewer() {
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.16", "0.12.0", newer: true), "latest", cachedServerVersion: "0.11.15");

        await Assert.That(advisory.Newer).IsFalse()
            .Because("a user between the server and npm is never told to downgrade");
    }

    // ── degenerate inputs ────────────────────────────────────────────────────────

    [Test]
    public async Task NullResult_NotAvailable() {
        var advisory = UpdateAdvisoryResolver.Resolve(null, "latest", cachedServerVersion: "0.11.15");

        await Assert.That(advisory.Newer).IsFalse();
        await Assert.That(advisory.Target).IsNull();
        await Assert.That(advisory.ServerCapped).IsFalse();
    }

    [Test]
    public async Task NullLatest_PassesThrough_NoCap() {
        var advisory = UpdateAdvisoryResolver.Resolve(Result("0.11.0", latest: null, newer: false), "latest", cachedServerVersion: "0.11.15");

        await Assert.That(advisory.Target).IsNull();
        await Assert.That(advisory.ServerCapped).IsFalse();
    }

    // ── the stable-release gate ──────────────────────────────────────────────────

    [Test]
    [Arguments("0.11.15", true)]
    [Arguments("0.11.15+sha.abc", true)]   // build metadata is ignored
    [Arguments("0.11.15-beta.1", false)]   // prerelease
    [Arguments("0.11", false)]             // fewer than three components
    [Arguments("not-a-version", false)]
    [Arguments("", false)]
    public async Task IsStableRelease_Gate(string version, bool expected) {
        await Assert.That(UpdateAdvisoryResolver.IsStableRelease(version)).IsEqualTo(expected);
    }
}
