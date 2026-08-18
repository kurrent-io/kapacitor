namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The shared opt-out parser both the daemon (to build config) and the CLI's <c>daemon service
/// install</c> notice (to describe a captured value) read. The install-output bug qodo found was
/// exactly a second reader disagreeing with this one: the notice said a captured value "enables" the
/// reviewer regardless of what it was, so a captured <c>0</c> was reported as keeping the reviewer on
/// while the daemon disabled it. These pin the classification that both surfaces now derive.
/// </summary>
public class ReviewerConsentTests {
    [Test]
    [Arguments(null)]        // unset — the default
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("TRUE")]
    [Arguments("yes")]
    [Arguments("on")]
    public async Task Enabling_and_unset_values_are_enabled(string? value) {
        await Assert.That(ReviewerConsent.IsEnabled(value)).IsTrue();
        await Assert.That(ReviewerConsent.DescribeUnparseable("VAR", value)).IsNull();
    }

    [Test]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("No")]
    [Arguments("off")]
    [Arguments("\"0\"")]     // quoting artifact — one level of matched quotes stripped
    [Arguments("'off'")]
    public async Task Disabling_values_are_disabled_and_do_not_warn(string value) {
        await Assert.That(ReviewerConsent.IsEnabled(value)).IsFalse();
        await Assert.That(ReviewerConsent.DescribeUnparseable("VAR", value)).IsNull()
            .Because("a recognised disable is not a mistake to warn about");
    }

    [Test]
    [Arguments("flase")]     // the mistyped disable this whole direction exists to catch
    [Arguments("disabled")]
    [Arguments("y")]
    [Arguments("2")]
    [Arguments("0\"")]       // an unmatched trailing quote is NOT stripped, so it stays unrecognised
    public async Task Unrecognised_values_fail_closed_and_warn(string value) {
        await Assert.That(ReviewerConsent.IsEnabled(value)).IsFalse()
            .Because("unset already enables, so a value we cannot read is a failed attempt to disable");

        var warning = ReviewerConsent.DescribeUnparseable("KCAP_KIRO_UNATTENDED_REVIEWER", value);
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains("DISABLED")
            .Because("the warning must state the outcome the parse actually produced");
    }

    /// <summary>
    /// The property the install notice depends on: IsEnabled and DescribeUnparseable are BOTH derived
    /// from Recognise, so they cannot disagree. A value that warns must also be disabled; a value that
    /// does not warn is whatever Recognise said.
    /// </summary>
    [Test]
    [Arguments("1")]
    [Arguments("0")]
    [Arguments("flase")]
    [Arguments(null)]
    public async Task IsEnabled_and_the_warning_agree(string? value) {
        var recognised = ReviewerConsent.Recognise(value);
        var warned     = ReviewerConsent.DescribeUnparseable("V", value) is not null;

        await Assert.That(warned).IsEqualTo(recognised is null);
        if (recognised is { } known)
            await Assert.That(ReviewerConsent.IsEnabled(value)).IsEqualTo(known);
        else
            await Assert.That(ReviewerConsent.IsEnabled(value)).IsFalse();
    }
}
