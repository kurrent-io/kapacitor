using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// The `--version` output of every gated reviewer's binary, as MEASURED on 2026-08-08, run through the
/// extractor that seeds its version floor.
///
/// <para><b>Why this exists.</b> A floor is affirmed once, on the first startup that finds the binary,
/// and never re-probed — so a wrong token is pinned permanently and gates silently in both directions.
/// <see cref="VendorVersionResolver.ExtractVersionToken"/> takes the FIRST dotted-numeric token, which
/// closes the garbage class (banners, <c>unknown</c>, localised errors) but not the wrong-token class:
/// an update nag, a runtime line like <c>Node.js v22.1.0</c>, or a date stamp like <c>2026.08.08</c> all
/// qualify and can precede the real version.</para>
///
/// <para><b>What it does and does not prove.</b> These are recorded observations of four builds on one
/// host, so this cannot detect the day a vendor ADDS a nag line — nothing local can. What it does is
/// stop the extractor from being changed in a way that breaks a shape we know ships, and give the next
/// person the actual strings rather than a description of them. The negative cases below are the ones
/// review raised, pinned so the limitation stays a KNOWN one rather than being rediscovered as a
/// surprise.</para>
/// </summary>
public class ObservedVendorVersionShapeTests {
    [Test]
    [Arguments("kiro-cli", "kiro-cli 2.16.0", "2.16.0")]
    [Arguments("gemini",   "0.54.0",          "0.54.0")]
    [Arguments("opencode", "1.18.9",          "1.18.9")]
    [Arguments("agy",      "1.1.11",          "1.1.11")]
    public async Task Measured_vendor_output_extracts_the_installed_version(
            string binary, string output, string expected) {
        await Assert.That(VendorVersionResolver.ExtractVersionToken(output)).IsEqualTo(expected)
            .Because($"this is what `{binary} --version` actually printed when it was measured; a floor "
                   + "seeded from a different token would gate the wrong builds forever");
    }

    /// <summary>
    /// The wrong-token class, pinned as ACCEPTED behaviour rather than as a bug.
    ///
    /// <para>Each of these extracts a version-shaped token that is not the installed build. None is
    /// currently produced by any of the four vendors (the test above is the evidence), which is why the
    /// design records and logs the affirmed token instead of anchoring extraction per vendor. If a
    /// vendor ever starts printing one of these shapes, the seeded-floor log line is what surfaces it —
    /// and this test is where the decision to accept that risk is written down.</para>
    /// </summary>
    [Test]
    [Arguments("Update available 0.11.14 -> 0.12.0", "0.11.14")]
    [Arguments("Node.js v22.1.0\nopencode 1.18.9",   "22.1.0")]
    [Arguments("built 2026.08.08 (v1.2.3)",          "2026.08.08")]
    public async Task Version_shaped_noise_before_the_version_still_wins(string output, string extracted) {
        await Assert.That(VendorVersionResolver.ExtractVersionToken(output)).IsEqualTo(extracted)
            .Because("first-qualifying-token extraction cannot tell these from a version — a known, "
                   + "documented limitation, not a defect this test is asking anyone to fix");
    }

    /// <summary>The garbage class, which IS closed: none of these can affirm a floor at all.</summary>
    [Test]
    [Arguments("unknown")]
    [Arguments("")]
    [Arguments("error: command not found")]
    [Arguments("versión no disponible")]
    [Arguments("2")]
    public async Task Unversioned_output_affirms_nothing(string output) {
        await Assert.That(VendorVersionResolver.ExtractVersionToken(output)).IsNull()
            .Because("a build we cannot identify must read as unknown — and therefore denied — rather "
                   + "than as a near-miss string that seeds a nonsense floor");
    }
}
