using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The Agents screen is built entirely out of this. Nothing the browser can discover for itself is in
// it, so a mistake here does not degrade the screen — it empties it.
public class FirstRunMachineReportTests {
    static FirstRunMachineReport Evaluate(
            HarnessRegistry?    harnesses = null,
            HarnessOfferLedger? ledger    = null,
            bool?               shell     = null,
            string?             platform  = null) =>
        FirstRunMachineReport.Evaluate(
            "nostromo", "machine-1", harnesses ?? TestHarnesses.All(),
            ledger ?? new HarnessOfferLedger(), shell, platform);

    // Key absence is the only way this shape says "we did not look", so every harness this build
    // knows has to appear — an omitted one reads as unknown and vanishes from BOTH the found list and
    // the not-found list, which is a probe silently unreported rather than a harness honestly absent.
    [Test]
    public async Task Reports_every_harness_including_the_ones_it_did_not_find() {
        var report = Evaluate();

        await Assert.That(report.Harnesses.Count).IsEqualTo(HarnessRegistry.Identities.Count);

        foreach (var harness in HarnessRegistry.Identities)
            await Assert.That(report.Harnesses.ContainsKey(harness.VendorId)).IsTrue();
    }

    // The one thing HarnessInventory cannot carry: it ORs these into a single Detected flag, which is
    // enough to raise a nudge and lossy for a screen that names the signal it saw.
    [Test]
    public async Task Keeps_the_two_detection_signals_apart() {
        // Claude answers only from PATH and Cursor only from its own state, so a report that folded
        // the two would show the same shape for both.
        using var bin      = new TempDir();
        var       report   = Evaluate(TestHarnesses.Over(
            TestBinaries.Searching(bin, "claude"),
            TestHarnesses.Probing(HarnessId.Claude, "claude"),
            TestHarnesses.Of(HarnessId.Cursor, detected: true)));

        await Assert.That(report.Harnesses["claude"].BinaryOnPath).IsTrue();
        await Assert.That(report.Harnesses["claude"].ConfigFound).IsFalse();
        await Assert.That(report.Harnesses["cursor"].BinaryOnPath).IsFalse();
        await Assert.That(report.Harnesses["cursor"].ConfigFound).IsTrue();
    }

    [Test]
    public async Task Reports_the_wired_probe_per_vendor() {
        var report = Evaluate(TestHarnesses.All(wired: [HarnessId.Claude]));

        await Assert.That(report.Harnesses["claude"].AlreadyWired).IsTrue();
        await Assert.That(report.Harnesses["codex"].AlreadyWired).IsFalse();
    }

    // A dismissal made in the terminal must reach the screen, or it defaults that row back on and
    // re-offers what the user already turned down.
    [Test]
    public async Task Carries_a_local_dismissal_so_the_screen_does_not_reverse_it() {
        var ledger = new HarnessOfferLedger {
            Vendors = new() {
                ["cursor"] = new HarnessOfferEntry { Declined = true },
                ["codex"]  = new HarnessOfferEntry { Declined = false }
            }
        };

        var report = Evaluate(ledger: ledger);

        await Assert.That(report.Declined).Contains("cursor");
        await Assert.That(report.Declined).DoesNotContain("codex");
    }

    // Null is unreported; an empty string would correlate every unreported flow to the same
    // non-machine.
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Reports_a_blank_machine_id_as_unreported(string machineId) {
        var report = FirstRunMachineReport.Evaluate(
            "nostromo", machineId, TestHarnesses.All(), new HarnessOfferLedger(), null);

        await Assert.That(report.MachineId).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Passes_the_login_shell_answer_through_unchanged(bool? shell) {
        await Assert.That(Evaluate(shell: shell).LoginShellFindsCli).IsEqualTo(shell);
    }

    [Test]
    [Arguments(null)]
    [Arguments(FirstRunPlatforms.MacOs)]
    [Arguments(FirstRunPlatforms.Linux)]
    public async Task Passes_the_platform_through_unchanged(string? platform) {
        await Assert.That(Evaluate(platform: platform).Platform).IsEqualTo(platform);
    }

    // Every one of the three is a value the browser maps; null is what an unrecognised host reports,
    // and it draws no fix affordance rather than guessing at one.
    [Test]
    public async Task Names_this_host_as_one_of_the_platforms_the_browser_maps() {
        var current = FirstRunPlatforms.Current();

        await Assert.That(current).IsNotNull();
        await Assert.That(new[] { FirstRunPlatforms.MacOs, FirstRunPlatforms.Linux, FirstRunPlatforms.Windows })
                    .Contains(current!);
    }
}
