using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`start --verify` mirrors `install --verify`'s launchd-only gate (see
/// DaemonCommandsServiceInstallTests): the engine's readiness/ownership poll needs a manager whose
/// Query/WriteAndBootstrap actually implement the verify algorithm, so non-launchd managers get a
/// clear, coded-nowhere rejection rather than falling into the transaction engine.</summary>
public class DaemonCommandsServiceStartTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Verify_flag_is_rejected_on_a_non_launchd_manager() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(), "test-id").Start(["--verify"]);
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Verify_flag_is_rejected_on_the_windows_manager_too() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new WindowsScheduledTaskServiceManager(Config.Root), "test-id").Start(["--verify"]);
        await Assert.That(exit).IsEqualTo(1);
    }
}
