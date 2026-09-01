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
    [Arguments("app-server", "0.146.0-rc.1", false)] // a prerelease is BELOW the verified release -> pty
    [Arguments("app-server", "0.147.0-rc.1", false)] // even above the floor, a prerelease is unverified -> pty
    [Arguments("app-server", "0.146.0+meta", false)] // build metadata makes the token non-clean -> pty
    [Arguments("app-server", "0.146.x", false)]      // non-numeric patch -> pty
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

    // ── Environment binding: the variable names and the empty guard are the operator's contract ──

    static DaemonConfig BoundWith(params (string Key, string? Value)[] env) {
        var config = new DaemonConfig();
        var map    = env.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        CodexTransportDecision.BindFromEnvironment(config, k => map.TryGetValue(k, out var v) ? v : null);

        return config;
    }

    /// <summary>The exact variable names bind. These are read from the environment and nowhere else, so a
    /// renamed key would silently leave a daemon on defaults while an operator believed they had selected
    /// app-server — which the helper-only tests could not catch.</summary>
    [Test]
    public async Task BindFromEnvironment_binds_both_codex_transport_keys() {
        var config = BoundWith(("KCAP_CODEX_TRANSPORT", "app-server"), ("KCAP_CODEX_APPSERVER_INTERACTIVE", "1"));

        await Assert.That(config.CodexTransport).IsEqualTo("app-server");
        await Assert.That(config.CodexAppServerInteractive).IsTrue();
    }

    /// <summary>An unset or present-but-empty variable leaves the existing setting alone rather than
    /// resetting it — the same contract every other env binding in daemon startup follows.</summary>
    [Test]
    public async Task BindFromEnvironment_leaves_settings_alone_when_unset_or_empty() {
        var untouched = BoundWith();
        await Assert.That(untouched.CodexTransport).IsEqualTo("pty");            // the shipped default
        await Assert.That(untouched.CodexAppServerInteractive).IsFalse();

        var empty = BoundWith(("KCAP_CODEX_TRANSPORT", ""), ("KCAP_CODEX_APPSERVER_INTERACTIVE", ""));
        await Assert.That(empty.CodexTransport).IsEqualTo("pty");
        await Assert.That(empty.CodexAppServerInteractive).IsFalse();
    }

    /// <summary>A value the parser does not recognise leaves the opt-in OFF, and the two keys are
    /// independent: selecting app-server does not by itself opt a daemon's interactive launches in.</summary>
    [Test]
    public async Task BindFromEnvironment_keeps_the_opt_in_off_for_unrecognised_values_and_is_independent() {
        var typo = BoundWith(("KCAP_CODEX_APPSERVER_INTERACTIVE", "ture"));
        await Assert.That(typo.CodexAppServerInteractive).IsFalse();

        var transportOnly = BoundWith(("KCAP_CODEX_TRANSPORT", "app-server"));
        await Assert.That(transportOnly.CodexTransport).IsEqualTo("app-server");
        await Assert.That(transportOnly.CodexAppServerInteractive).IsFalse();
    }
}
