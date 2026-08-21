using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class UnattendedLaunchPolicyTests {
    sealed class FakeLauncher(string vendor, bool supportsUnattended) : IHostedAgentLauncher {
        public string Vendor  => vendor;
        public string CliPath => vendor;
        public bool   SupportsUnattended => supportsUnattended;
        public bool   IsAvailable() => true;
        public void   Prepare(LauncherContext ctx) { }
        public LaunchArgs BuildArgs(LauncherContext ctx) => new([], null);
        public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) => new([], null);
        public void   Cleanup(AgentInstance agent) { }
    }

    [Test]
    public async Task Unattended_launch_on_unsupported_vendor_is_rejected() {
        var reason = UnattendedLaunchPolicy.RejectionReason(new FakeLauncher("gemini", supportsUnattended: false), isReviewFlow: true);

        await Assert.That(reason).IsNotNull();
        await Assert.That(reason!).Contains("gemini");
    }

    [Test]
    public async Task Unattended_launch_on_supported_vendor_is_allowed() {
        var reason = UnattendedLaunchPolicy.RejectionReason(new FakeLauncher("claude", supportsUnattended: true), isReviewFlow: true);

        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Non_unattended_launch_is_always_allowed_even_if_vendor_lacks_support() {
        var reason = UnattendedLaunchPolicy.RejectionReason(new FakeLauncher("gemini", supportsUnattended: false), isReviewFlow: false);

        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Cursor_descriptor_unattended_launch_is_allowed_with_zero_prompt_policy() {
        var reason = UnattendedLaunchPolicy.RejectionReason(
            "cursor", supportsUnattended: AcpVendorDescriptors.Cursor.SupportsUnattended, isReviewFlow: true);

        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Copilot_descriptor_unattended_launch_is_allowed() {
        var reason = UnattendedLaunchPolicy.RejectionReason(
            "copilot", supportsUnattended: AcpVendorDescriptors.Copilot.SupportsUnattended, isReviewFlow: true);

        await Assert.That(reason).IsNull();
    }
}
