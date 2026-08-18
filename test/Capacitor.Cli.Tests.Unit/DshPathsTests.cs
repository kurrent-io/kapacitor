using Capacitor.Cli.Core.Dsh;

namespace Capacitor.Cli.Tests.Unit;

public class DshPathsTests {
    // Parallel-safe: asserts invariant layout relationships that hold regardless of how
    // ConfigRoot resolves (KCAP_DSH_HOME / home), so no env mutation is needed.

    [Test]
    public async Task SessionJsonl_is_session_jsonl_under_a_per_session_dir() {
        var jsonl = DshPaths.SessionJsonl("abc123", home: "/fake/home");

        // Layout is <root>/sessions/<id>/session.jsonl. Assert the segment names rather than
        // full-path equality (Path.Combine vs GetDirectoryName differ only on separators on Windows).
        await Assert.That(Path.GetFileName(jsonl)).IsEqualTo("session.jsonl");
        var idDir = Path.GetDirectoryName(jsonl)!;
        await Assert.That(Path.GetFileName(idDir)).IsEqualTo("abc123");
        await Assert.That(Path.GetFileName(Path.GetDirectoryName(idDir)!)).IsEqualTo("sessions");
    }

    [Test]
    public async Task PluginsDir_and_SessionsDir_share_the_config_root() {
        var plugins  = DshPaths.PluginsDir(home: "/fake/home");
        var sessions = DshPaths.SessionsDir(home: "/fake/home");

        await Assert.That(Path.GetFileName(plugins)).IsEqualTo("plugins");
        await Assert.That(Path.GetFileName(sessions)).IsEqualTo("sessions");
        await Assert.That(Path.GetDirectoryName(plugins)).IsEqualTo(Path.GetDirectoryName(sessions));
    }
}
