using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Tests.Unit.Harness.Gemini;

/// <summary>
/// kcap merges its command hooks into Gemini's SHARED
/// <c>~/.gemini/settings.json</c> (nested <c>{hooks:[{command}]}</c> entries),
/// so the merge must preserve user-authored hooks AND every other settings key.
/// </summary>
public class GeminiHooksTests {
    const string KcapCommand = "kcap hook --gemini";

    static bool HasKcap(JsonArray entries) =>
        entries.Any(e => e?["hooks"] is JsonArray inner
                      && inner.Any(h => h?["command"]?.GetValue<string>() == KcapCommand));

    static bool HasCommand(JsonArray entries, string command) =>
        entries.Any(e => e?["hooks"] is JsonArray inner
                      && inner.Any(h => h?["command"]?.GetValue<string>() == command));

    [Test]
    public async Task fresh_install_merges_lifecycle_events() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        var ok = PluginCommand.InstallGeminiHooks(settingsPath);
        await Assert.That(ok).IsTrue();

        var hooks = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject()["hooks"]!.AsObject();

        foreach (var evt in new[] { "SessionStart", "SessionEnd", "Notification" }) {
            await Assert.That(HasKcap(hooks[evt]!.AsArray())).IsTrue();
        }
    }

    [Test]
    public async Task install_preserves_user_hooks_and_other_settings() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"security":{"auth":{"selectedType":"oauth-personal"}},"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo hi"}]}]}}
            """);

        await Assert.That(PluginCommand.InstallGeminiHooks(settingsPath)).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        // Unrelated settings keys survive the merge.
        await Assert.That(root["security"]!["auth"]!["selectedType"]!.GetValue<string>()).IsEqualTo("oauth-personal");

        var start = root["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(HasCommand(start, "echo hi")).IsTrue();   // user hook preserved
        await Assert.That(HasKcap(start)).IsTrue();                 // kcap hook added
    }

    [Test]
    public async Task reinstall_does_not_duplicate_the_kcap_entry() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        PluginCommand.InstallGeminiHooks(settingsPath);
        PluginCommand.InstallGeminiHooks(settingsPath);

        var start = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!
            .AsObject()["hooks"]!["SessionStart"]!.AsArray();

        var kcapCount = start.Count(e => e?["hooks"] is JsonArray inner
                                      && inner.Any(h => h?["command"]?.GetValue<string>() == KcapCommand));
        await Assert.That(kcapCount).IsEqualTo(1);
    }

    [Test]
    public async Task remove_deletes_only_kcap_hooks() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"security":{"auth":{"selectedType":"oauth-personal"}},"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo hi"}]}]}}
            """);

        PluginCommand.InstallGeminiHooks(settingsPath);
        await Assert.That(PluginCommand.RemoveGeminiHooks(settingsPath)).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        var start = root["hooks"]!["SessionStart"]!.AsArray();

        await Assert.That(HasKcap(start)).IsFalse();                                          // kcap gone
        await Assert.That(HasCommand(start, "echo hi")).IsTrue();                             // user hook kept
        await Assert.That(root["security"]!["auth"]!["selectedType"]!.GetValue<string>()).IsEqualTo("oauth-personal");
    }

    [Test]
    public async Task install_leaves_malformed_settings_untouched() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        // A shared settings.json that is half-written / not valid JSON, but carries
        // user content we must never destroy by "starting fresh".
        const string original = "{ \"theme\": \"dark\", \"hooks\": { \"SessionStart\": [ {{ broken";
        await File.WriteAllTextAsync(settingsPath, original);

        var ok = PluginCommand.InstallGeminiHooks(settingsPath);

        await Assert.That(ok).IsFalse();                                            // fails closed
        await Assert.That(await File.ReadAllTextAsync(settingsPath)).IsEqualTo(original); // untouched
    }

    [Test]
    public async Task install_leaves_non_object_settings_untouched() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        // Valid JSON, but not an object — still not something we can safely merge into.
        const string original = "[1, 2, 3]";
        await File.WriteAllTextAsync(settingsPath, original);

        await Assert.That(PluginCommand.InstallGeminiHooks(settingsPath)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(settingsPath)).IsEqualTo(original);
    }

    // status parity (PR #169): `kcap status` renders the Gemini ✓/✗ marker via
    // GeminiHooksInstaller.IsInstalled, so both detection branches need direct
    // coverage — the marker file, and the settings-hooks fallback when no marker
    // is present. Mirrors the Cursor/Pi installer-detection tests.
    [Test]
    public async Task IsInstalled_true_via_marker_file() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        GeminiHooksInstaller.WriteMarker(settingsPath);   // marker only — no settings.json written

        await Assert.That(GeminiHooksInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_via_settings_hooks_when_marker_absent() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        PluginCommand.InstallGeminiHooks(settingsPath);    // merges kcap hooks into settings.json
        GeminiHooksInstaller.DeleteMarker(settingsPath);   // force the settings-scan fallback

        await Assert.That(GeminiHooksInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_no_marker_and_no_kcap_hooks() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        // Real settings.json with a user hook but no kcap entry, and no marker.
        await File.WriteAllTextAsync(settingsPath, """
            {"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo hi"}]}]}}
            """);

        await Assert.That(GeminiHooksInstaller.IsInstalled(settingsPath)).IsFalse();
    }
}
