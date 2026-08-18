using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// Verifies that <see cref="DaemonConfig"/> is correctly populated from
/// <see cref="DaemonSettings"/> profile values — mirrors the logic in
/// DaemonRunner.RunAsync so that changes to either stay in sync.
/// </summary>
public class DaemonConfigProfileTests {
    // Helper: simulate the DaemonRunner profile-wiring block in isolation.
    static DaemonConfig ApplyProfileSettings(DaemonConfig config, DaemonSettings? profileDaemon) {
        if (string.IsNullOrEmpty(config.Name) && !string.IsNullOrEmpty(profileDaemon?.Name))
            config.Name = profileDaemon.Name;

        if (config.MaxConcurrentAgents == 5 && profileDaemon is { MaxAgents: var mx and not 5 })
            config.MaxConcurrentAgents = mx;

        if (!string.IsNullOrEmpty(profileDaemon?.ClaudePath))
            config.ClaudePath = profileDaemon.ClaudePath;

        if (!string.IsNullOrEmpty(profileDaemon?.CodexPath))
            config.CodexPath = profileDaemon.CodexPath;

        return config;
    }

    // ── claude_path from profile ─────────────────────────────────────────────

    [Test]
    public async Task ClaudePath_FromProfile_OverridesDefault() {
        var daemon  = new DaemonSettings { ClaudePath = "/opt/claude/bin/claude" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("/opt/claude/bin/claude");
    }

    [Test]
    public async Task ClaudePath_NullInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { ClaudePath = null };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("claude");
    }

    [Test]
    public async Task ClaudePath_EmptyInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { ClaudePath = "" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("claude");
    }

    [Test]
    public async Task ClaudePath_NullProfile_KeepsDefault() {
        var config = ApplyProfileSettings(new DaemonConfig(), profileDaemon: null);

        await Assert.That(config.ClaudePath).IsEqualTo("claude");
    }

    // ── codex_path from profile ──────────────────────────────────────────────

    [Test]
    public async Task CodexPath_FromProfile_OverridesDefault() {
        var daemon  = new DaemonSettings { CodexPath = "/opt/codex/bin/codex" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.CodexPath).IsEqualTo("/opt/codex/bin/codex");
    }

    [Test]
    public async Task CodexPath_NullInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { CodexPath = null };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.CodexPath).IsEqualTo("codex");
    }

    [Test]
    public async Task CodexPath_EmptyInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { CodexPath = "" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.CodexPath).IsEqualTo("codex");
    }

    [Test]
    public async Task CodexPath_NullProfile_KeepsDefault() {
        var config = ApplyProfileSettings(new DaemonConfig(), profileDaemon: null);

        await Assert.That(config.CodexPath).IsEqualTo("codex");
    }

    // ── both paths set simultaneously ────────────────────────────────────────

    [Test]
    public async Task BothPaths_FromProfile_OverrideBothDefaults() {
        var daemon  = new DaemonSettings { ClaudePath = "/a/claude", CodexPath = "/b/codex" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("/a/claude");
        await Assert.That(config.CodexPath).IsEqualTo("/b/codex");
    }
}
