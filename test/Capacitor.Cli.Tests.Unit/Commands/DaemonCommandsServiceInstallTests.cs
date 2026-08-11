using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`install --verify` is a launchd-only slice (Task 10): the engine needs a manager whose
/// WriteAndBootstrap actually classifies/mutates per the verify algorithm, and the on-disk recheck
/// needs GenerateFiles to return exactly one file. Non-launchd managers get a clear, coded-nowhere
/// rejection rather than a deep failure inside the transaction (e.g. GenerateFiles().Single()
/// throwing on Windows's two files).</summary>
public class DaemonCommandsServiceInstallTests {
    [Test]
    public async Task Verify_flag_is_rejected_on_a_non_launchd_manager() {
        var exit = await DaemonCommands.ServiceInstall(new SystemdServiceManager(), ["--verify"], "test-id", true);
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Verify_flag_is_rejected_on_the_windows_manager_too() {
        var exit = await DaemonCommands.ServiceInstall(new WindowsScheduledTaskServiceManager(), ["--verify"], "test-id", true);
        await Assert.That(exit).IsEqualTo(1);
    }

    /// <summary>--replace only has meaning inside the verify transaction engine (it selects
    /// ServiceVerify.InstallVerifiedAsync's ownership matrix) — a plain install has no transaction
    /// to hand it to, so the combination is rejected before even reaching the launchd-only gate
    /// (asserted here on a non-launchd manager, which would otherwise reject for a different
    /// reason).</summary>
    [Test]
    public async Task Replace_without_verify_is_rejected() {
        var exit = await DaemonCommands.ServiceInstall(new SystemdServiceManager(), ["--replace"], "test-id", true);
        await Assert.That(exit).IsEqualTo(1);
    }
}
