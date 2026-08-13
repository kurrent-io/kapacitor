using System.Diagnostics;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// `kcap feedback` must reach <see cref="Capacitor.Cli.Commands.FeedbackCommand"/>. Pinned through
/// the real binary for the same reason <c>AgentVerbDispatchTests</c> is: the dispatch lives in
/// Program.cs's top-level flow, where a guard added ahead of the switch can shadow a whole verb
/// without failing a single unit test that only calls <c>FeedbackCommand</c> directly.
///
/// Asserts a string only <c>FeedbackCommand</c> emits (its usage line naming both category flags)
/// rather than merely "not Unknown command" — a bare "not the tombstone" check would pass for any
/// other pre-dispatch guard that exits non-zero with unrelated text.
/// </summary>
public class FeedbackVerbDispatchTests {
    [Test]
    public async Task Bare_feedback_reaches_the_handlers_usage_error() {
        var (stdout, stderr, exitCode) = await RunCli("feedback");
        var output = stdout + stderr;

        await Assert.That(output).Contains("kcap feedback (--bug | --feedback)");
        await Assert.That(output).Contains("Pass exactly one of --bug or --feedback.");
        await Assert.That(output).DoesNotContain("Unknown command");
        await Assert.That(exitCode).IsNotEqualTo(0);
    }

    static async Task<(string Stdout, string Stderr, int ExitCode)> RunCli(
            string argLine, bool clearServerUrl = false) {
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

        // Isolate from the developer's own profile so this assertion doesn't depend on whether
        // this machine happens to have a server configured. Unreachable-but-present is enough:
        // FeedbackCommand's usage error fires before any server call is attempted.
        psi.Environment["KCAP_URL"] = clearServerUrl ? "" : "http://127.0.0.1:1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start kcap process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(FeedbackVerbDispatchTests).Assembly.Location)!;
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
