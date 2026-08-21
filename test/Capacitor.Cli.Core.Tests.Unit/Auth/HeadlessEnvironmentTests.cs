using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class HeadlessEnvironmentTests {
    [Test]
    public async Task Detects_ssh_connection() {
        var env = new Dictionary<string, string?> { ["SSH_CONNECTION"] = "1.2.3.4 5 6.7.8.9 22" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsTrue();
    }

    [Test]
    public async Task Detects_ssh_client() {
        var env = new Dictionary<string, string?> { ["SSH_CLIENT"] = "1.2.3.4 5 22" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsTrue();
    }

    [Test]
    public async Task Detects_ssh_on_macos() {
        var env = new Dictionary<string, string?> { ["SSH_CONNECTION"] = "1.2.3.4 5 6.7.8.9 22" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.MacOS)).IsTrue();
    }

    [Test]
    public async Task Linux_without_display_is_headless() {
        var env = new Dictionary<string, string?>();
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsTrue();
    }

    [Test]
    public async Task Linux_with_display_is_not_headless() {
        var env = new Dictionary<string, string?> { ["DISPLAY"] = ":0" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsFalse();
    }

    [Test]
    public async Task Linux_with_wayland_is_not_headless() {
        var env = new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland-0" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsFalse();
    }

    [Test]
    public async Task MacOS_default_is_not_headless() {
        var env = new Dictionary<string, string?>();
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.MacOS)).IsFalse();
    }

    [Test]
    public async Task Windows_default_is_not_headless() {
        var env = new Dictionary<string, string?>();
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Windows)).IsFalse();
    }
}
