using System.Diagnostics;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The Helpers project stamps the CLI's output directory in at compile time. Nothing else would
/// notice that stamp going stale until the integration suite ran, and that one skips on Windows.
/// </summary>
public class KcapProcessTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task The_stamped_binary_path_resolves_and_runs() {
        var psi = KcapProcess.StartInfo(Daemons.Store, "--version");

        using var process = Process.Start(psi)!;
        var version = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(process.ExitCode).IsEqualTo(0);
        await Assert.That(version).StartsWith("kcap ");
    }
}
