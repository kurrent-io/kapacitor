using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap daemon reviewer affirm --vendor …</c> for a vendor that HAS an unattended reviewer but no
/// affirmation gate. Antigravity takes a minimum version FLOOR instead, so there is nothing to record
/// — and the two failure modes worth avoiding are succeeding at a no-op (the operator believes a gate
/// was cleared) and reporting it as an unknown vendor (the operator believes they typed it wrong).
/// </summary>
public class DaemonReviewerCommandTests {
    [Test]
    public async Task Antigravity_is_recognised_as_a_reviewer_with_no_affirmation_gate() =>
        await Assert.That(DaemonReviewerCommand.NonAffirmableReviewer.Resolve("antigravity")).IsNotNull();

    [Test]
    [Arguments("ANTIGRAVITY")]
    [Arguments("Antigravity")]
    public async Task TheVendorMatchIsCaseInsensitive(string spelling) =>
        await Assert.That(DaemonReviewerCommand.NonAffirmableReviewer.Resolve(spelling)).IsNotNull();

    /// <summary>No vendor may sit in both tables: one would silently win by ordering, and which one it
    /// is depends on the order of two checks nothing keeps in step.</summary>
    [Test]
    public async Task NoVendorIsBothAffirmableAndNonAffirmable() {
        foreach (var nonAffirmable in DaemonReviewerCommand.NonAffirmableReviewer.All)
            await Assert.That(DaemonReviewerCommand.AffirmableReviewer.Resolve(nonAffirmable.Vendor)).IsNull();
    }

    [Test]
    [Arguments("kiro")]
    [Arguments("gemini")]
    public async Task TheAffirmableReviewersAreUnaffected(string vendor) =>
        await Assert.That(DaemonReviewerCommand.NonAffirmableReviewer.Resolve(vendor)).IsNull();

    /// <summary>The explanation has to redirect the operator, not just refuse: it names the mechanism
    /// that replaced affirmation and both variables that drive it.</summary>
    [Test]
    public async Task TheExplanationNamesTheFloorAndTheVariablesThatDriveIt() {
        var explanation = DaemonReviewerCommand.NonAffirmableReviewer.Resolve("antigravity")!.Explanation;

        await Assert.That(explanation).StartsWith("antigravity_reviewer_not_affirmable");
        await Assert.That(explanation).Contains("MINIMUM VERSION");
        await Assert.That(explanation).Contains("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
        await Assert.That(explanation).Contains("KCAP_ANTIGRAVITY_MIN_CLI_VERSION");
    }

    /// <summary>
    /// Refused, never silently successful — and refused with ITS OWN explanation. The exit code alone
    /// proves nothing here: the unknown-vendor branch below it also returns 1, so a wiring mistake that
    /// dropped this arm entirely would look identical while telling the operator they typed the vendor
    /// wrong. Captures stderr for that reason.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task Affirming_antigravity_refuses_with_its_own_explanation_not_as_an_unknown_vendor() {
        var original = Console.Error;
        var captured = new StringWriter();
        int exitCode;

        Console.SetError(captured);

        try {
            exitCode = await DaemonReviewerCommand.HandleAsync(["affirm", "--vendor", "antigravity"]);
        } finally {
            Console.SetError(original);
        }

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(captured.ToString()).Contains("antigravity_reviewer_not_affirmable");
        await Assert.That(captured.ToString()).DoesNotContain("Unknown reviewer vendor");
    }
}
