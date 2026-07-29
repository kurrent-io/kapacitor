using System.Diagnostics;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// `kcap agent …` must reach <c>AgentCommand</c>. Pinned through the real binary because the
/// dispatch lives in Program.cs's top-level flow, where a guard added ahead of the switch can
/// shadow the whole group without failing a single unit test — which is exactly what happened
/// when the retired-verb tombstone and this command group landed in the same release.
/// </summary>
public class AgentVerbDispatchTests {
    [Test]
    [Arguments("agent")]
    [Arguments("agent ls")]
    [Arguments("agent start")]
    [Arguments("agent stop")]
    [Arguments("agent attach")]
    [Arguments("agent --help")]
    public async Task Agent_verb_is_not_short_circuited_before_dispatch(string argLine) {
        var (stdout, stderr, exitCode) = await RunCli(argLine);
        var output = stdout + stderr;

        // The retired-verb pointer, or any exit 2, means something answered ahead of the switch.
        await Assert.That(output).DoesNotContain("renamed to 'daemon'");
        await Assert.That(exitCode).IsNotEqualTo(2);
    }

    [Test]
    public async Task Agent_help_renders_the_command_group() {
        // --help resolves before the server-config gate, so this is deterministic offline.
        var (stdout, _, exitCode) = await RunCli("agent --help");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("kcap agent");

        foreach (var sub in new[] { "start", "ls", "stop", "attach" }) {
            await Assert.That(stdout).Contains(sub);
        }
    }

    [Test]
    public async Task Retired_daemon_only_subcommand_points_at_the_daemon_group() {
        // `status` only ever meant the daemon; keep that signpost now the tombstone is gone.
        // Needs a server URL: the whole `agent` group resolves config before dispatching, so the
        // signpost is unreachable when none is set — unlike the pre-config tombstone it replaces.
        var (_, stderr, exitCode) = await RunCli("agent status", serverUrl: "http://127.0.0.1:1");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("kcap daemon status");
    }

    static async Task<(string Stdout, string Stderr, int ExitCode)> RunCli(string argLine, string? serverUrl = null) {
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

        if (serverUrl is not null) psi.Environment["KCAP_URL"] = serverUrl;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start kcap process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(AgentVerbDispatchTests).Assembly.Location)!;
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
