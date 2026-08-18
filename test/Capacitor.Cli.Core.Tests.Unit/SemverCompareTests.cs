namespace Capacitor.Cli.Core.Tests.Unit;

public class SemverCompareTests {
    [Test] public async Task Returns_true_when_latest_is_strictly_newer_patch()  => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.3")).IsTrue();
    [Test] public async Task Returns_true_when_latest_is_strictly_newer_minor()  => await Assert.That(SemverCompare.IsNewer("0.7.0", "0.6.5")).IsTrue();
    [Test] public async Task Returns_true_when_latest_is_strictly_newer_major()  => await Assert.That(SemverCompare.IsNewer("1.0.0", "0.9.9")).IsTrue();

    [Test] public async Task Returns_false_when_equal()                          => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_strictly_newer()      => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.7.0")).IsFalse();

    [Test] public async Task Returns_false_when_current_has_prerelease_suffix()  => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.5-alpha.1")).IsFalse();
    [Test] public async Task Returns_false_when_latest_has_prerelease_suffix()   => await Assert.That(SemverCompare.IsNewer("0.6.5-rc.1", "0.6.5")).IsFalse();

    [Test] public async Task Returns_false_when_current_has_build_metadata()     => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.5+abcdef")).IsFalse();
    [Test] public async Task Returns_false_when_latest_has_build_metadata()      => await Assert.That(SemverCompare.IsNewer("0.6.5+abcdef", "0.6.5")).IsFalse();

    [Test] public async Task Returns_false_when_latest_is_null()                 => await Assert.That(SemverCompare.IsNewer(null, "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_null()                => await Assert.That(SemverCompare.IsNewer("0.6.5", null)).IsFalse();
    [Test] public async Task Returns_false_when_latest_is_unknown_literal()      => await Assert.That(SemverCompare.IsNewer("unknown", "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_unknown_literal()     => await Assert.That(SemverCompare.IsNewer("0.6.5", "unknown")).IsFalse();
    [Test] public async Task Returns_false_when_latest_is_unparseable_garbage()  => await Assert.That(SemverCompare.IsNewer("not.a.version", "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_unparseable_garbage() => await Assert.That(SemverCompare.IsNewer("0.6.5", "not.a.version")).IsFalse();
    [Test] public async Task Returns_false_when_both_empty()                     => await Assert.That(SemverCompare.IsNewer("", "")).IsFalse();
    [Test] public async Task Returns_false_when_both_whitespace()                => await Assert.That(SemverCompare.IsNewer("  ", "  ")).IsFalse();
}
