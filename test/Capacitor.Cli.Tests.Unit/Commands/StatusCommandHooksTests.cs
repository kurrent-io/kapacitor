using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StatusCommandHooksTests {
    [Test]
    public async Task DetectsClaudePlugin_when_enabled() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": true } }
            """);

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsClaudePlugin_disabled_when_false() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": false } }
            """);

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsClaudePlugin_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_when_kcap_command_present() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_no_kcap_command() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    // Fix #2: non-string command field should not throw — treated as not-installed.
    [Test]
    public async Task DetectsCodexHooks_returns_false_for_numeric_command_field() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": 42, "timeout": 5 }] }
                ]
              }
            }
            """);

        // Must not throw; numeric command is not a kcap entry.
        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    [Test]
    public async Task HooksStatusLine_includes_all_eight_agents() {
        // status must surface every supported agent so an install of any one can be
        // verified from `kcap status`. Gemini and Kiro were each previously absent
        // from the line entirely (added in PR #169); OpenCode was likewise missing
        // until the status surface was wired up.
        var line = StatusCommand.BuildHooksStatusLine(
            claude: true, codex: false, cursor: false, copilot: false, gemini: true, kiro: true, pi: true, opencode: true);

        await Assert.That(line).Contains("Claude ✓");
        await Assert.That(line).Contains("Codex ✗");
        await Assert.That(line).Contains("Cursor ✗");
        await Assert.That(line).Contains("Copilot ✗");
        await Assert.That(line).Contains("Gemini ✓");
        await Assert.That(line).Contains("Kiro ✓");
        await Assert.That(line).Contains("Pi ✓");
        await Assert.That(line).Contains("OpenCode ✓");
    }

    [Test]
    public async Task HooksStatusLine_marks_gemini_kiro_pi_and_opencode_not_installed() {
        var line = StatusCommand.BuildHooksStatusLine(
            claude: false, codex: false, cursor: false, copilot: false, gemini: false, kiro: false, pi: false, opencode: false);

        await Assert.That(line).Contains("Gemini ✗");
        await Assert.That(line).Contains("Kiro ✗");
        await Assert.That(line).Contains("Pi ✗");
        await Assert.That(line).Contains("OpenCode ✗");
    }
}
