using System.Diagnostics;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// `agent` was the daemon verb until May 2026. The rename left the dead verb in old
/// transcripts, docs, and habits, and the generic unknown-command error sent people hunting
/// for a daemon that was never down. The CLI answers it with a rename pointer — and
/// deliberately NOT a working alias: the verb must stay dead, so the non-zero exit is pinned
/// as hard as the message.
/// </summary>
public class RetiredAgentVerbTests {
    [Test]
    [Arguments("agent")]
    [Arguments("agent start")]
    [Arguments("agent --help")]
    public async Task Agent_verb_prints_a_rename_pointer_and_exits_2(string argLine) {
        var binary = GetCliBinaryPath();

        if (!File.Exists(binary)) {
            throw new FileNotFoundException(
                $"kcap binary not found at {binary}. Build it first: " +
                "dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj",
                binary
            );
        }

        var psi = new ProcessStartInfo(binary, argLine + " --no-update-check") {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start kcap process");

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(process.ExitCode).IsEqualTo(2);
        await Assert.That(stderr).Contains("renamed to 'daemon'");
        await Assert.That(stderr).Contains("`kcap daemon start|stop|status`");
    }

    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(RetiredAgentVerbTests).Assembly.Location)!;
        var binDir      = Path.GetDirectoryName(asmDir)!;
        var config      = Path.GetFileName(binDir);
        var testBin     = Path.GetDirectoryName(binDir)!;
        var testProjDir = Path.GetDirectoryName(testBin)!;
        var testRoot    = Path.GetDirectoryName(testProjDir)!;
        var repoRoot    = Path.GetDirectoryName(testRoot)!;
        var binaryName  = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";
        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }
}
