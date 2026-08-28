using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StatusCommandHooksTests {
    [Test]
    public async Task DetectsClaudePlugin_when_enabled() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": true } }
            """);

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsClaudePlugin_disabled_when_false() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": false } }
            """);

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsClaudePlugin_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(path)).IsFalse();
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

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsTrue();
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

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsFalse();
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
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsFalse();
    }

    /// Every harness the build knows appears on the line — a tenth would too, since the line is
    /// built from the registry rather than a parameter per vendor.
    [Test]
    public async Task HooksStatusLine_reports_every_harness() {
        var wired = new[] { HarnessId.Claude, HarnessId.Gemini, HarnessId.Kiro, HarnessId.Pi };
        var line  = StatusCommand.BuildHooksStatusLine(
            HarnessRegistry.Identities.Select(i => (i.Id, Wired: wired.Contains(i.Id))));

        await Assert.That(line).Contains("Claude ✓");
        await Assert.That(line).Contains("Codex ✗");
        await Assert.That(line).Contains("Cursor ✗");
        await Assert.That(line).Contains("Copilot ✗");
        await Assert.That(line).Contains("Gemini ✓");
        await Assert.That(line).Contains("Kiro ✓");
        await Assert.That(line).Contains("Pi ✓");
        await Assert.That(line).Contains("OpenCode ✗");
        await Assert.That(line).Contains("Antigravity ✗");
    }

    /// Claude's label carries a product suffix everywhere else; the line has room for one word.
    [Test]
    public async Task HooksStatusLine_shortens_the_one_suffixed_label() {
        var line = StatusCommand.BuildHooksStatusLine([(HarnessId.Claude, true)]);

        await Assert.That(line).IsEqualTo("Claude ✓");
    }
}
