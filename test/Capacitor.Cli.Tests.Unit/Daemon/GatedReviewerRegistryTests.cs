using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// The registry is the SINGLE list three surfaces derive from — the daemon's opt-out apply loop, the
/// service-unit environment allowlist, and <c>daemon reviewer affirm</c>'s vendor resolution. These pin
/// the properties that make deriving safer than listing, rather than merely tidier.
///
/// <para>The hazard is specific to these switches being opt-OUTs. A row that reaches one surface and
/// not another used to mean a reviewer that could not be turned ON — safe. It now means one that cannot
/// be turned OFF, which on a service-installed daemon leaves the operator no lever at all.</para>
/// </summary>
public class GatedReviewerRegistryTests {
    /// <summary>
    /// Every row has a <see cref="DaemonConfig"/> flag behind it.
    ///
    /// <para>This is the direction the service-unit test structurally cannot see: that one ranges over
    /// the registry and proves each row survives capture, so a row the DAEMON cannot apply is still
    /// green there. Adding a reviewer to the registry publishes its variable into every service unit
    /// and into the affirm verb's usage text; without an accessor the operator sets a documented switch
    /// that does nothing. <c>ConsentApplier</c> throws for that case, and this is what makes CI hit the
    /// throw instead of a customer's first boot.</para>
    /// </summary>
    [Test]
    public async Task Consent_applier_covers_every_gated_reviewer() {
        await Assert.That(GatedReviewers.All).IsNotEmpty()
            .Because("an empty registry would make every assertion here vacuously true");

        foreach (var reviewer in GatedReviewers.All) {
            var config = new DaemonConfig();

            // Resolving is half the assertion: an unmapped vendor throws NotSupportedException.
            DaemonRunner.ConsentApplier(config, reviewer.Vendor)(false);

            // The other half is that it cleared THIS vendor's flag. Naming the expected property is
            // what makes that real: an earlier version counted how many flags went false, which a
            // copy-paste aliasing two vendors onto one property passes — a fresh config per iteration
            // means exactly one flag clears either way. Verified by mutation; the count-based version
            // survived pointing opencode at Kiro's property.
            var expected = ExpectedFlag(reviewer.Vendor);

            await Assert.That(expected(config)).IsFalse()
                .Because($"{reviewer.Vendor}'s opt-out must clear {reviewer.Vendor}'s own flag, not "
                       + "another vendor's — aliasing would silently make one switch disable the wrong "
                       + "reviewer while leaving the named one running");

            foreach (var (vendor, flag) in AllFlags().Where(f => f.Vendor != reviewer.Vendor))
                await Assert.That(flag(config)).IsTrue()
                    .Because($"{reviewer.Vendor}'s opt-out must not touch {vendor}");
        }
    }

    /// <summary>The flag each vendor's opt-out is expected to clear. Deliberately a SECOND, independent
    /// statement of the mapping — a test that re-derived it from the code under test could not detect a
    /// mis-wiring.</summary>
    static Func<DaemonConfig, bool> ExpectedFlag(string vendor) =>
        AllFlags().Single(f => f.Vendor == vendor).Flag;

    static (string Vendor, Func<DaemonConfig, bool> Flag)[] AllFlags() => [
        ("gemini",      c => c.GeminiUnattendedReviewerEnabled),
        ("kiro",        c => c.KiroUnattendedReviewerEnabled),
        ("opencode",    c => c.OpenCodeUnattendedReviewerEnabled),
        ("antigravity", c => c.AntigravityUnattendedReviewerEnabled)
    ];

    /// <summary>An unmapped vendor is refused loudly, not silently no-op'd.</summary>
    [Test]
    public async Task Consent_applier_refuses_a_reviewer_it_cannot_apply() {
        await Assert.That(() => DaemonRunner.ConsentApplier(new DaemonConfig(), "notavendor"))
            .Throws<NotSupportedException>();
    }

    /// <summary>
    /// Every row carries a usable opt-out variable. A null or blank one would land in the service-unit
    /// allowlist as an empty key and quietly drop that reviewer's only lever.
    /// </summary>
    [Test]
    public async Task Every_gated_reviewer_names_an_opt_out_variable() {
        foreach (var reviewer in GatedReviewers.All) {
            await Assert.That(string.IsNullOrWhiteSpace(reviewer.EnableEnvVar)).IsFalse()
                .Because($"{reviewer.Vendor} would otherwise be undisableable");
            await Assert.That(string.IsNullOrWhiteSpace(reviewer.Vendor)).IsFalse();
        }

        await Assert.That(GatedReviewers.All.Select(r => r.EnableEnvVar).Distinct().Count())
            .IsEqualTo(GatedReviewers.All.Length)
            .Because("two reviewers sharing a variable makes one of them undisableable on its own");
    }

}
