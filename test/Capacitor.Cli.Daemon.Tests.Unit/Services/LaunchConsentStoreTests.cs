using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LaunchConsentStoreTests {
    [Test]
    public async Task Missing_file_yields_upgrade_safe_policy() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance);
        await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Allow);
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(45);
        await Assert.That(store.Current.Rules.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Corrupt_file_yields_upgrade_safe_policy() {
        using var dir = new TempDir();
        dir.CreateFile("consent.json", "{not json");
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance);
        await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Allow);
    }

    [Test]
    public async Task Replace_persists_and_reloads() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance);
        var next = new LaunchConsentPolicy(LaunchConsentDefault.Prompt, 60,
            [new LaunchConsentRule("deny", "user_x", "review-flow", null, "codex")]);
        var ok = store.TryReplace(next, out var error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();

        var reloaded = new LaunchConsentStore(dir.Path, NullLogger.Instance);
        await Assert.That(reloaded.Current.Default).IsEqualTo(LaunchConsentDefault.Prompt);
        await Assert.That(reloaded.Current.PromptTimeoutSeconds).IsEqualTo(60);
        await Assert.That(reloaded.Current.Rules[0].Requester).IsEqualTo("user_x");
    }

    [Test]
    public async Task Replace_rejects_invalid_action_and_kind() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance);
        var badAction = new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45,
            [new LaunchConsentRule("maybe", null, null, null, null)]);
        await Assert.That(store.TryReplace(badAction, out var e1)).IsFalse();
        await Assert.That(e1).Contains("action");

        var badKind = new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45,
            [new LaunchConsentRule("allow", null, "flows", null, null)]);
        await Assert.That(store.TryReplace(badKind, out var e2)).IsFalse();
        await Assert.That(e2).Contains("kind");
    }

    [Test]
    public async Task Replace_clamps_prompt_timeout() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance);
        await Assert.That(store.TryReplace(
            new LaunchConsentPolicy(LaunchConsentDefault.Allow, 1, []), out _)).IsTrue();
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(5);
        await Assert.That(store.TryReplace(
            new LaunchConsentPolicy(LaunchConsentDefault.Allow, 9999, []), out _)).IsTrue();
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(300);
    }

    [Test]
    public async Task Replace_writes_an_owner_only_consent_file_in_an_owner_only_directory() {
        // consent.json carries requester ids and repo paths, so it must not be world/group
        // readable. Unix-only: file modes are a no-op on Windows.
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance);
        var ok = store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45, []), out _);
        await Assert.That(ok).IsTrue();

        const UnixFileMode ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode ownerOnlyDir  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        var consentFile = dir.PathTo("consent.json");
        await Assert.That(File.GetUnixFileMode(consentFile)).IsEqualTo(ownerOnlyFile);
        await Assert.That(File.GetUnixFileMode(dir.Path)).IsEqualTo(ownerOnlyDir);
    }
}
