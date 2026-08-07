using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The version half of every gated reviewer's decision. Written once so Kiro and Gemini cannot drift
/// apart on what "this build clears the bar" means — these cases are the contract both inherit.
///
/// <para>The recorded value is a MINIMUM: a vendor upgrade needs no operator action, a downgrade below
/// the record is refused.</para>
/// </summary>
public class ReviewerVersionAffirmationsTests {
    [Test]
    public async Task AnExactlyEqualBuild_MeetsTheMinimum() {
        await Assert.That(ReviewerVersionAffirmations.Decide("2.16.0", "2.16.0"))
            .IsEqualTo(ReviewerVersionAffirmation.MeetsMinimum);
    }

    /// <summary>The whole point of the change: a vendor patch release must not take the reviewer
    /// offline. If this ever reverts to an equality compare, this is the test that catches it.</summary>
    [Test]
    [Arguments("2.16.1")]
    [Arguments("2.17.0")]
    [Arguments("3.0.0")]
    [Arguments("10.0.0")]
    public async Task ANewerBuild_MeetsTheMinimum_WithNoOperatorAction(string installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, "2.16.0"))
            .IsEqualTo(ReviewerVersionAffirmation.MeetsMinimum);
    }

    /// <summary>"Minimum" is load-bearing, not decorative — an older build than the one recorded is
    /// still refused.</summary>
    [Test]
    [Arguments("2.15.0")]
    [Arguments("2.16")]
    [Arguments("1.99.99")]
    public async Task AnOlderBuild_IsBelowTheMinimum(string installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, "2.16.0"))
            .IsEqualTo(ReviewerVersionAffirmation.BelowMinimum);
    }

    /// <summary>Unresolved is checked FIRST, and it means only "the installed string is blank" — it
    /// deliberately does NOT mean "unparseable", which would refuse pairs allowed today (see the
    /// monotonicity tests below).</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnidentifiableBuild_IsUnresolved_EvenWithNoMinimumRecorded(string? installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, null))
            .IsEqualTo(ReviewerVersionAffirmation.Unresolved);
    }

    /// <summary>An unparseable but NON-BLANK installed string must not reach Unresolved: blank and
    /// unorderable are different problems with different remedies.</summary>
    [Test]
    [Arguments("daily-20240806")]
    [Arguments("1.2.3.4.5")]
    public async Task AnUnparseableButNonBlankBuild_IsNotUnresolved(string installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, "2.16.0"))
            .IsNotEqualTo(ReviewerVersionAffirmation.Unresolved);
    }

    /// <summary>Its own arm, not folded into BelowMinimum: the remedy differs — record a minimum,
    /// rather than change the installed build.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NoMinimumRecorded_IsItsOwnOutcome(string? minimum) {
        await Assert.That(ReviewerVersionAffirmations.Decide("2.16.0", minimum))
            .IsEqualTo(ReviewerVersionAffirmation.NoMinimumRecorded);
    }

    /// <summary>Whitespace is tolerated on both sides — a vendor's own output and a written record both
    /// carry it.</summary>
    [Test]
    public async Task SurroundingWhitespaceIsTolerated() {
        await Assert.That(ReviewerVersionAffirmations.Decide("  2.16.0\n", "\t2.16.0 "))
            .IsEqualTo(ReviewerVersionAffirmation.MeetsMinimum);
    }

    /// <summary>A `v` prefix and a pre-release/build suffix are decoration, not a different build: the
    /// shared parse strips them, so a decorated newer build still clears the floor. Under the old
    /// ordinal-equality rule every one of these was refused.</summary>
    [Test]
    [Arguments("v2.17.0")]
    [Arguments("2.17.0-nightly")]
    [Arguments("2.17.0+build.9")]
    public async Task ADecoratedNewerBuildStillClearsTheFloor(string installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, "2.16.0"))
            .IsEqualTo(ReviewerVersionAffirmation.MeetsMinimum);
    }

    // ── Incomparable ─────────────────────────────────────────────────────────
    //
    // Exactly one side orders. Ordinal-comparing across domains would refuse a genuine upgrade while
    // LABELLING it "below minimum" — the bug an earlier revision of this design shipped in review.

    [Test]
    public async Task AnUnorderableMinimum_WithAnOrderableBuild_IsIncomparable() {
        await Assert.That(ReviewerVersionAffirmations.Decide("2.0.0", "1.2.3.4.5"))
            .IsEqualTo(ReviewerVersionAffirmation.Incomparable);
    }

    [Test]
    public async Task AnUnorderableBuild_WithAnOrderableMinimum_IsIncomparable() {
        await Assert.That(ReviewerVersionAffirmations.Decide("1.2.3.4.5", "2.0.0"))
            .IsEqualTo(ReviewerVersionAffirmation.Incomparable);
    }

    /// <summary>Never mislabelled as BelowMinimum — the denial text sends an operator somewhere
    /// different, and "below minimum" would be a claim we did not compute.</summary>
    [Test]
    [Arguments("2.0.0", "1.2.3.4.5")]
    [Arguments("1.2.3.4.5", "2.0.0")]
    public async Task Incomparable_IsNeverReportedAsBelowMinimum(string installed, string minimum) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, minimum))
            .IsNotEqualTo(ReviewerVersionAffirmation.BelowMinimum);
    }

    /// <summary>
    /// What makes refusing an Incomparable pair acceptable: recording the installed build as the
    /// minimum — what `kcap daemon reviewer affirm` does — clears it in BOTH directions, so the arm is
    /// not a dead end.
    /// </summary>
    [Test]
    [Arguments("2.0.0")]      // orderable installed, previously-unorderable floor
    [Arguments("1.2.3.4.5")]  // unorderable installed: both sides become that same string
    public async Task AffirmingTheInstalledBuild_ClearsIncomparable(string installed) {
        await Assert.That(ReviewerVersionAffirmations.Decide(installed, installed))
            .IsEqualTo(ReviewerVersionAffirmation.MeetsMinimum);
    }

    // ── Monotonicity ─────────────────────────────────────────────────────────

    /// <summary>
    /// THE property that makes this change safe: the new rule must never refuse a pair the old
    /// ordinal-equality rule allowed. The old rule allowed exactly the ordinal-equal non-blank pairs,
    /// so every such pair must still be admitted — including values no version parser accepts, which
    /// an earlier revision of this design regressed.
    /// </summary>
    [Test]
    [Arguments("2.16.0")]          // orders
    [Arguments("daily-20240806")]  // does not order at all
    [Arguments("1.2.3.4.5")]       // five components — passes the vendor token filter, fails TryParse
    [Arguments("1.")]
    [Arguments(".5")]
    public async Task EveryPairTheOldRuleAllowed_IsStillAllowed(string value) {
        await Assert.That(ReviewerVersionAffirmations.Decide(value, value))
            .IsEqualTo(ReviewerVersionAffirmation.MeetsMinimum)
            .Because("ordinal-equal pairs were allowed before this change and must stay allowed");
    }

    /// <summary>Two unorderable but DIFFERENT strings were refused before and still are — the fallback
    /// preserves the old behaviour in both directions, not just the permissive one.</summary>
    [Test]
    public async Task TwoDifferentUnorderableStrings_AreStillRefused() {
        await Assert.That(ReviewerVersionAffirmations.Decide("daily-20240806", "daily-20240101"))
            .IsEqualTo(ReviewerVersionAffirmation.BelowMinimum);
    }

    // ── The shared parse ─────────────────────────────────────────────────────

    /// <summary>Shared with <c>DaemonRunner.CliVersionAllowed</c> so the two gates cannot disagree
    /// about what counts as an orderable version.</summary>
    [Test]
    [Arguments("2.16.0", true)]
    [Arguments("v2.16.0", true)]
    [Arguments("2.16.0-rc1", true)]
    [Arguments("2.16.0+build", true)]
    [Arguments("  2.16.0 ", true)]
    [Arguments("1.2.3.4", true)]
    [Arguments("1.2.3.4.5", false)]
    [Arguments("daily-20240806", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task TryParseVersion_ClassifiesOrderability(string? raw, bool orders) {
        await Assert.That(ReviewerVersionAffirmations.TryParseVersion(raw) is not null).IsEqualTo(orders);
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
