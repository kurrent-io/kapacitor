using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap daemon reviewer affirm --vendor …</c> is a TABLE, not a per-vendor branch: every gated
/// reviewer records its minimum through the same store, and adding one is a row. These pin that
/// Antigravity is genuinely in that table — reachable, and reachable everywhere the table is read,
/// since a row the usage text and the unknown-vendor message do not derive from would leave the verb
/// working while telling operators it does not.
/// </summary>
public class DaemonReviewerCommandTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    [Arguments("kiro")]
    [Arguments("gemini")]
    [Arguments("antigravity")]
    public async Task EveryGatedReviewerIsAffirmable(string vendor) =>
        await Assert.That(DaemonReviewerCommand.AffirmableReviewer.Resolve(vendor)).IsNotNull();

    [Test]
    [Arguments("ANTIGRAVITY")]
    [Arguments("Antigravity")]
    public async Task TheVendorMatchIsCaseInsensitive(string spelling) =>
        await Assert.That(DaemonReviewerCommand.AffirmableReviewer.Resolve(spelling)).IsNotNull();

    /// <summary>The row carries the binary and the two variables the DAEMON itself reads — the verb
    /// affirms the build the daemon would launch, not whatever is first on PATH, so a wrong path
    /// variable here records a version nothing will ever be compared against.</summary>
    [Test]
    public async Task TheAntigravityRowNamesTheDaemonsOwnBinaryAndVariables() {
        var reviewer = DaemonReviewerCommand.AffirmableReviewer.Resolve("antigravity")!;

        await Assert.That(reviewer.DefaultBinary).IsEqualTo("agy");
        await Assert.That(reviewer.PathEnvVar).IsEqualTo("KCAP_ANTIGRAVITY_PATH");
        await Assert.That(reviewer.EnableEnvVar).IsEqualTo("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
    }

    /// <summary>Both operator-facing lists derive from the table rather than restating it. Asserted on
    /// the vendor the table gained last, because a hand-maintained copy is what would drop it.</summary>
    [Test]
    public async Task TheVendorListOfferedToOperatorsCoversTheWholeTable() {
        foreach (var reviewer in DaemonReviewerCommand.AffirmableReviewer.All)
            await Assert.That(DaemonReviewerCommand.AffirmableReviewer.VendorList).Contains(reviewer.Vendor);
    }

    /// <summary>
    /// An unrecognised vendor is refused as a typo and NAMES the alternatives, so an operator who
    /// misspells one is not left guessing. Captures stderr because the exit code alone proves nothing
    /// — every failure arm of this verb returns 1.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AnUnknownVendorIsRefusedAndOffersTheAffirmableOnes() {
        using var capture = ConsoleOutput.StartErrorCapture();
        var exitCode = await DaemonReviewerCommand.HandleAsync(Daemons.Store, ["affirm", "--vendor", "antigravitee"]);

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError()).Contains("Unknown reviewer vendor");
        await Assert.That(capture.GetCapturedError()).Contains("antigravity");
    }
}
