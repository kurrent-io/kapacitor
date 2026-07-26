using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests <see cref="ClaudeLauncher.AcceptBypassPermissionsMode(string)"/> — the pre-accept that
/// stops an unattended review-flow reviewer wedging on Claude's Bypass-Permissions consent dialog.
///
/// The consent flag Claude 2.1.x actually reads is <c>skipDangerousModePermissionPrompt</c> in the
/// user settings (verified against the shipped 2.1.x binary: it reads that key from userSettings and
/// writes it there when the user accepts the dialog interactively). Writing the legacy
/// <c>bypassPermissionsModeAccepted</c> into <c>~/.claude.json</c> was NOT honored, hence this.
/// </summary>
public class ClaudeLauncherBypassAcceptanceTests {
    // Kept in step with ClaudeLauncher's constant; asserting the literal here is deliberate — a
    // silent rename of the key would re-introduce the wedge, and this test would catch it.
    const string Key = "skipDangerousModePermissionPrompt";

    static string TempSettings() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-claude-settings-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    static void Cleanup(string settingsPath) {
        try { Directory.Delete(Path.GetDirectoryName(settingsPath)!, true); } catch { /* best-effort */ }
    }

    [Test]
    public async Task Writes_the_acceptance_flag_when_settings_file_is_absent() {
        var path = TempSettings();
        try {
            ClaudeLauncher.AcceptBypassPermissionsMode(path);

            await Assert.That(File.Exists(path)).IsTrue();
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            await Assert.That(root[Key]!.GetValue<bool>()).IsTrue();
        } finally { Cleanup(path); }
    }

    [Test]
    public async Task Preserves_existing_user_settings_when_adding_the_flag() {
        var path = TempSettings();
        try {
            await File.WriteAllTextAsync(path,
                """{"skipWorkflowUsageWarning":true,"env":{"MCP_TOOL_TIMEOUT":"600000"}}""");

            ClaudeLauncher.AcceptBypassPermissionsMode(path);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            await Assert.That(root[Key]!.GetValue<bool>()).IsTrue();
            // Sibling keys must survive untouched.
            await Assert.That(root["skipWorkflowUsageWarning"]!.GetValue<bool>()).IsTrue();
            await Assert.That(root["env"]!["MCP_TOOL_TIMEOUT"]!.GetValue<string>()).IsEqualTo("600000");
        } finally { Cleanup(path); }
    }

    [Test]
    public async Task Is_idempotent_when_flag_already_true() {
        var path = TempSettings();
        try {
            await File.WriteAllTextAsync(path, """{"skipDangerousModePermissionPrompt":true,"keep":"me"}""");

            ClaudeLauncher.AcceptBypassPermissionsMode(path);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            await Assert.That(root[Key]!.GetValue<bool>()).IsTrue();
            await Assert.That(root["keep"]!.GetValue<string>()).IsEqualTo("me");
        } finally { Cleanup(path); }
    }

    [Test]
    public async Task Does_not_clobber_an_unparseable_settings_file() {
        var path = TempSettings();
        try {
            const string garbage = "this is not { valid json";
            await File.WriteAllTextAsync(path, garbage);

            // Must NOT destroy a settings file it can't parse (user-owned, may contain comments the
            // daemon shouldn't touch). Leaves it exactly as-is rather than overwriting.
            ClaudeLauncher.AcceptBypassPermissionsMode(path);

            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(garbage);
        } finally { Cleanup(path); }
    }
}
