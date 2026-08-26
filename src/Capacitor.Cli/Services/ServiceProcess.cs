using System.Diagnostics;

namespace Capacitor.Cli.Services;

/// <summary>
/// Minimal synchronous shell-out for service registration tools
/// (launchctl/systemctl/schtasks). Not used in tests — managers' side-effecting
/// methods are the one part not exercised in CI.
/// </summary>
static class ServiceProcess {
    public static (int ExitCode, string StdOut, string StdErr) Run(string file, params string[] args) {
        var psi = new ProcessStartInfo {
            FileName               = file,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>Run and throw with captured stderr on non-zero exit.</summary>
    public static void Check(string file, params string[] args) {
        var (code, _, err) = Run(file, args);
        if (code != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed (exit {code}): {err.Trim()}");
    }

    /// <summary>
    /// Like <see cref="Run"/> but bounded: on expiry, kills the whole process tree and awaits
    /// its exit rather than returning with a still-running child. Stdout/stderr are drained on
    /// background tasks (not <c>ReadToEnd</c>) so a chatty child can't deadlock on a full pipe
    /// while <c>WaitForExit</c> blocks — mirrors <see cref="Core.ProcessRunner"/>.
    /// </summary>
    public static (int ExitCode, string StdOut, string StdErr, bool TimedOut) RunBounded(
            string file, string[] args, TimeSpan timeout) {
        var psi = new ProcessStartInfo {
            FileName               = file,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var exited = p.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited) {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result, !exited);
    }
}
