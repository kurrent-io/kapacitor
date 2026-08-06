using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The version half of every gated reviewer's decision. Written once so Kiro and Gemini cannot drift
/// apart on what "the operator accepted this build" means — these cases are the contract both inherit.
/// </summary>
public class ReviewerVersionAffirmationsTests {
    [Test]
    public async Task AMatchingBuild_IsAffirmed() {
        await Assert.That(ReviewerVersionAffirmations.Decide("2.16.0", "2.16.0"))
            .IsEqualTo(ReviewerVersionAffirmation.Affirmed);
    }

    /// <summary>Unresolved is checked BEFORE unaffirmed, and the distinction is load-bearing: "we could
    /// not identify the build" and "you have not accepted this build" send an operator to different
    /// fixes.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnidentifiableBuild_IsUnresolved_EvenWithNothingAffirmed(string? installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, null))
            .IsEqualTo(ReviewerVersionAffirmation.Unresolved);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NothingAffirmed_IsUnaffirmed(string? affirmed) {
        await Assert.That(ReviewerVersionAffirmations.Decide("2.16.0", affirmed))
            .IsEqualTo(ReviewerVersionAffirmation.Unaffirmed);
    }

    /// <summary>A CHANGED build is refused whichever way it moved — a downgrade was never affirmed
    /// either, and the gate is "this exact build", not "at least this build".</summary>
    [Test]
    [Arguments("2.17.0")]
    [Arguments("2.15.0")]
    [Arguments("2.16.1")]
    [Arguments("10.0.0")]
    public async Task AChangedBuild_IsUnaffirmedInBothDirections(string installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, "2.16.0"))
            .IsEqualTo(ReviewerVersionAffirmation.Unaffirmed);
    }

    /// <summary>Whitespace is tolerated on both sides — a vendor's own output and a written record both
    /// carry it, and refusing over a trailing newline would refuse a build the operator did affirm.</summary>
    [Test]
    public async Task SurroundingWhitespaceIsTolerated() {
        await Assert.That(ReviewerVersionAffirmations.Decide("  2.16.0\n", "\t2.16.0 "))
            .IsEqualTo(ReviewerVersionAffirmation.Affirmed);
    }

    /// <summary>…but nothing else is. A decorated build string is a different build: the comparison is
    /// Ordinal, so it cannot quietly accept a `v`-prefixed or suffixed variant.</summary>
    [Test]
    [Arguments("v2.16.0")]
    [Arguments("2.16.0-nightly")]
    [Arguments("2.16.0 (build abc)")]
    [Arguments("2.16.O")]
    public async Task ADecoratedBuildStringIsADifferentBuild(string installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, "2.16.0"))
            .IsEqualTo(ReviewerVersionAffirmation.Unaffirmed);
    }

    [Test]
    [Arguments(null, "<none>")]
    [Arguments("", "<none>")]
    [Arguments("   ", "<none>")]
    [Arguments(" 2.16.0 ", "2.16.0")]
    public async Task DescribeRendersAMissingVersionExplicitly(string? version, string expected) {
        await Assert.That(ReviewerVersionAffirmations.Describe(version)).IsEqualTo(expected);
    }
}

/// <summary>
/// The store is vendor-KEYED, which is the property that lets two reviewers share it. Affirming one
/// vendor's build must say nothing about another's — otherwise enabling Kiro would silently clear
/// Gemini's gate, which is the exact failure the gate exists to prevent.
/// </summary>
public class ReviewerVersionStoreVendorKeyingTests {
    static string TempStateDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-reviewer-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task EachVendorHasItsOwnRecord() {
        var dir = TempStateDir();

        new ReviewerVersionStore(dir, "kiro").Affirm("2.16.0");

        await Assert.That(new ReviewerVersionStore(dir, "kiro").Affirmed).IsEqualTo("2.16.0");
        await Assert.That(new ReviewerVersionStore(dir, "gemini").Affirmed).IsNull()
            .Because("affirming one vendor's build must not clear another vendor's gate");
        await Assert.That(ReviewerVersionStore.RecordExists(dir, "gemini")).IsFalse();
    }

    [Test]
    public async Task TwoVendorsCanBeAffirmedIndependently() {
        var dir = TempStateDir();

        new ReviewerVersionStore(dir, "kiro").Affirm("2.16.0");
        new ReviewerVersionStore(dir, "gemini").Affirm("0.54.0");

        await Assert.That(new ReviewerVersionStore(dir, "kiro").Affirmed).IsEqualTo("2.16.0");
        await Assert.That(new ReviewerVersionStore(dir, "gemini").Affirmed).IsEqualTo("0.54.0");
    }

    /// <summary>
    /// Kiro's on-disk filename is pinned to what it was when this type was Kiro-only.
    ///
    /// <para>Not cosmetic: the record is what keeps a shipped reviewer working across an upgrade of kcap
    /// itself. Renaming the file would make every existing affirmation invisible, so operators who had
    /// already accepted their build would find the reviewer silently withheld — a fail-closed direction,
    /// but one nobody asked for and which looks exactly like the bug this work fixes.</para>
    /// </summary>
    [Test]
    public async Task KirosFilenameIsUnchangedSoExistingAffirmationsSurviveAnUpgrade() {
        await Assert.That(ReviewerVersionStore.FileNameFor("kiro"))
            .IsEqualTo("kiro-reviewer-affirmed-version");
    }
}
