namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// Covers <c>KCAP_ACP_DEBUG_FRAMES</c>'s
/// <see cref="DaemonRunner.ParseDebugFramesFlag"/> string parse — mirrors
/// <c>DaemonLogLevelTests.ParseLogLevel_*</c>'s pattern for the same kind of env-var-to-config
/// toggle. The actual <c>DaemonConfig.DebugFrames</c> wiring and the one-time startup Warning at the
/// <c>RunAsync</c> call site are not independently unit-tested (would require a full host boot),
/// matching <c>DaemonRunnerCursorAvailabilityTests</c>' own note about its sibling one-time warning.
/// </summary>
public class DaemonDebugFramesFlagTests {
    [Test]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("TRUE")]
    [Arguments("True")]
    [Arguments(" 1 ")]
    [Arguments("  true  ")]
    public async Task ParseDebugFramesFlag_OnValues_ReturnsTrue(string input) {
        await Assert.That(DaemonRunner.ParseDebugFramesFlag(input)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("yes")]
    [Arguments("nonsense")]
    public async Task ParseDebugFramesFlag_EverythingElse_ReturnsFalse(string? input) {
        await Assert.That(DaemonRunner.ParseDebugFramesFlag(input)).IsFalse();
    }

    // KCAP_ACP_RECONNECT has the OPPOSITE default polarity: ON unless explicitly disabled.
    [Test]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("FALSE")]
    [Arguments(" false ")]
    public async Task ParseAcpReconnectFlag_OffValues_ReturnFalse(string input) {
        await Assert.That(DaemonRunner.ParseAcpReconnectFlag(input)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("banana")]
    public async Task ParseAcpReconnectFlag_EverythingElse_ReturnsTrue(string? input) {
        await Assert.That(DaemonRunner.ParseAcpReconnectFlag(input)).IsTrue();
    }
}
