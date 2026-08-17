using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>
/// Tests <see cref="Cli.Daemon.Harness.Claude.ClaudeLauncher.AcceptBypassPermissionsMode(string)"/> — the pre-accept that
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

    [Test]
    public async Task Writes_the_acceptance_flag_when_settings_file_is_absent() {
        using var tmp = TempDir.WithPathTo("settings.json", out var path);
        ClaudeLauncher.AcceptBypassPermissionsMode(path);

        await Assert.That(File.Exists(path)).IsTrue();
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        await Assert.That(root[Key]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Preserves_existing_user_settings_when_adding_the_flag() {
        using var tmp = TempDir.WithPathTo("settings.json", out var path);
        await File.WriteAllTextAsync(path,
            """{"skipWorkflowUsageWarning":true,"env":{"MCP_TOOL_TIMEOUT":"600000"}}""");

        ClaudeLauncher.AcceptBypassPermissionsMode(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        await Assert.That(root[Key]!.GetValue<bool>()).IsTrue();
        // Sibling keys must survive untouched.
        await Assert.That(root["skipWorkflowUsageWarning"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["env"]!["MCP_TOOL_TIMEOUT"]!.GetValue<string>()).IsEqualTo("600000");
    }

    [Test]
    public async Task Is_idempotent_when_flag_already_true() {
        using var tmp = TempDir.WithPathTo("settings.json", out var path);
        await File.WriteAllTextAsync(path, """{"skipDangerousModePermissionPrompt":true,"keep":"me"}""");

        ClaudeLauncher.AcceptBypassPermissionsMode(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        await Assert.That(root[Key]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["keep"]!.GetValue<string>()).IsEqualTo("me");
    }

    [Test]
    public async Task Does_not_clobber_an_unparseable_settings_file() {
        using var tmp = TempDir.WithPathTo("settings.json", out var path);
        const string garbage = "this is not { valid json";
        await File.WriteAllTextAsync(path, garbage);

        // Must NOT destroy a settings file it can't parse (user-owned, may contain comments the
        // daemon shouldn't touch). Leaves it exactly as-is rather than overwriting.
        ClaudeLauncher.AcceptBypassPermissionsMode(path);

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(garbage);
    }
}
