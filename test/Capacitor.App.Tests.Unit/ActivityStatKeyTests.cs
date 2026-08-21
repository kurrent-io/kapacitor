using AppUnderTest = Capacitor.App.App;

namespace Capacitor.App.Tests.Unit;

/// Regression coverage for a Critical bug found in review: the original
/// ActivityStatKey wrapped BOTH log files in one try/catch, so a missing `.1` rotation file
/// (every fresh install, until the first 1MB rotation) collapsed the WHOLE key to the "absent"
/// constant — appends to the live file never changed it, and the Activity tab went stale until
/// the tab was reselected. Drives the two-path testable overload directly against a temp
/// directory; never touches the real daemon-dir resolution under ~/.config/kcap/daemons.
public class ActivityStatKeyTests {
    [Test]
    public async Task Live_file_append_changes_the_key_even_when_the_rotation_file_is_absent() {
        using var tmp = new TempDir();
        var p1 = tmp.PathTo("consent-decisions.jsonl.1");
        var live = tmp.PathTo("consent-decisions.jsonl");
        File.WriteAllText(live, "one\n");

        var before = AppUnderTest.ActivityStatKey(p1, live);
        File.AppendAllText(live, "two\n");
        var after = AppUnderTest.ActivityStatKey(p1, live);

        await Assert.That(after).IsNotEqualTo(before);
    }

    [Test]
    public async Task Key_is_stable_when_neither_file_exists() {
        using var tmp = new TempDir();
        var p1 = tmp.PathTo("consent-decisions.jsonl.1");
        var live = tmp.PathTo("consent-decisions.jsonl");

        var first = AppUnderTest.ActivityStatKey(p1, live);
        var second = AppUnderTest.ActivityStatKey(p1, live);

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task Key_changes_when_the_rotation_file_appears() {
        using var tmp = new TempDir();
        var p1 = tmp.PathTo("consent-decisions.jsonl.1");
        var live = tmp.PathTo("consent-decisions.jsonl");
        File.WriteAllText(live, "one\n");

        var before = AppUnderTest.ActivityStatKey(p1, live);
        File.WriteAllText(p1, "rotated\n");
        var after = AppUnderTest.ActivityStatKey(p1, live);

        await Assert.That(after).IsNotEqualTo(before);
    }
}
