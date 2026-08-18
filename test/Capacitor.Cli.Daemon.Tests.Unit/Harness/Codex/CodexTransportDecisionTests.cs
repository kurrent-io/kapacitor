using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>The single rule that decides PTY vs app-server: operator selection AND the spike-pinned
/// version floor, failing toward PTY on anything unparseable.</summary>
public class CodexTransportDecisionTests {
    [Test]
    [Arguments("pty", "0.146.0", false)]           // not selected
    [Arguments(null, "0.146.0", false)]            // unset -> pty
    [Arguments("app-server", "0.146.0", true)]     // selected + at floor
    [Arguments("app-server", "0.147.0", true)]     // above floor
    [Arguments("app-server", "1.2.0", true)]       // well above
    [Arguments("app-server", "0.145.0", false)]    // below floor
    [Arguments("app-server", "0.99.0", false)]     // below floor (minor)
    [Arguments("app-server", null, false)]         // unknown version fails toward pty
    [Arguments("app-server", "", false)]           // empty version fails toward pty
    [Arguments("app-server", "not-a-version", false)]
    [Arguments("App-Server", "0.146.0", true)]     // selection is case-insensitive
    [Arguments("  app-server  ", "0.146.0", true)] // trimmed
    [Arguments("app-server", "codex-cli 0.146.0", true)] // tolerant of surrounding text
    [Arguments("app-server", "v0.146.0", true)]    // v-prefixed
    public async Task UsesAppServer_truth_table(string? transport, string? version, bool expected) {
        await Assert.That(CodexTransportDecision.UsesAppServer(transport, version)).IsEqualTo(expected);
    }

    [Test]
    public async Task MeetsFloor_is_exactly_the_pinned_floor() {
        await Assert.That(CodexTransportDecision.MeetsFloor(CodexTransportDecision.VersionFloor)).IsTrue();
        await Assert.That(CodexTransportDecision.MeetsFloor("0.146.1")).IsTrue();
        await Assert.That(CodexTransportDecision.MeetsFloor("0.146")).IsTrue(); // patch defaults to 0
        await Assert.That(CodexTransportDecision.MeetsFloor("0.145.99")).IsFalse();
    }
}
