using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>Runs an operator-configured command that prints a borrowed-review token, so a service unit
/// can carry the COMMAND where it must not carry the credential. The daemon still acquires nothing on
/// its own: it runs exactly what the operator configured.</summary>
internal static class BorrowedReviewTokenCommand {
    /// <summary>Bounded: this runs on the availability probe and on every borrowed launch, so a command
    /// that blocked forever would wedge daemon startup or a review.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The token the command printed, or null if it produced nothing usable.
    ///
    /// <para>Never throws, and never reports the command's output — a failing credential command is the
    /// likeliest thing to print a secret to stderr, and callers log around this. Null is
    /// indistinguishable from an unset variable, so a broken command degrades to not-advertised.</para></summary>
    internal static string? Run(string commandLine) {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        try {
            // A shell is the useful contract for an operator-supplied line (`gh auth token`, a
            // keychain lookup, a pipeline); the string comes from the daemon's own environment.
            var (shell, flag) = OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");

            using var process = Process.Start(new ProcessStartInfo(shell, [flag, commandLine]) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            });

            if (process is null) return null;

            // Drained concurrently: reading one while the other fills its pipe buffer deadlocks.
            // stderr is discarded but must still be consumed.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds)) {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }

                return null;
            }

            // The int overload does not wait for the async readers; this does.
            process.WaitForExit();

            if (process.ExitCode != 0) return null;

            return FirstLine(stdout.GetAwaiter().GetResult());
        } catch (Exception) {
            return null;   // missing shell, permission error, malformed command: all "no token"
        }
    }

    /// <summary>First non-empty line, trimmed: token printers emit a trailing newline, and a token is
    /// never multi-line.</summary>
    static string? FirstLine(string output) {
        foreach (var line in output.Split('\n')) {
            var trimmed = line.Trim();

            if (trimmed.Length > 0) return trimmed;
        }

        return null;
    }
}
