namespace Capacitor.App.Services;

/// Seam over process spawning so StartDaemonAsync is testable without touching a real CLI
/// binary. The production implementation wraps System.Diagnostics.Process.
public interface IProcessRunner {
    Task<(int ExitCode, string Stderr)> RunAsync(string fileName, string[] args, CancellationToken ct);
}
