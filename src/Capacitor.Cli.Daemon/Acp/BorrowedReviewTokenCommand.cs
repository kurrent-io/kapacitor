using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Runs an operator-configured command to obtain a borrowed-review token, so a supervised daemon can
/// authenticate a contained reviewer without a credential being written into its service unit.
///
/// <para><b>Why an indirection rather than capturing the token.</b> A service unit is a file on disk —
/// written world-readable by default on every platform we install one — so capturing
/// <c>GH_TOKEN</c> into it would trade the keychain grant this feature removed for a credential in a
/// readable file. The unit carries the COMMAND instead, which is not a secret; the value is produced
/// at the moment it is needed and never persisted.</para>
///
/// <para>This does not make the daemon a credential-acquiring component. It runs exactly the command
/// the operator configured and nothing else — the same posture as reading a variable they exported,
/// with the secret's lifetime shortened rather than lengthened.</para>
/// </summary>
internal static class BorrowedReviewTokenCommand {
    /// <summary>How long the command may take. Bounded because this runs on the daemon's startup path
    /// (the availability probe) and on a launch path: a command that blocked forever would otherwise
    /// wedge daemon boot or a review.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The token the command printed, or null if it produced nothing usable.
    ///
    /// <para><b>Never throws and never reports the command's output.</b> Callers treat null as "no
    /// token", which is indistinguishable from an unset variable, so a broken command degrades to the
    /// same honest not-advertised state rather than a distinct failure mode. Output is withheld from
    /// diagnostics deliberately: a failing credential command is exactly the thing likeliest to print a
    /// secret to stderr, and this is invoked where the result would be logged.</para></summary>
    internal static string? Run(string commandLine) {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        try {
            // The operator supplies the whole command line, so a shell is the useful contract
            // (`gh auth token`, a keychain lookup, a pipeline). There is no third-party input to
            // inject here — the string comes from the daemon's own environment.
            var (shell, flag) = OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");

            using var process = Process.Start(new ProcessStartInfo(shell, [flag, commandLine]) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            });

            if (process is null) return null;

            // Both streams are drained concurrently: reading one to completion while the other fills
            // its pipe buffer is a deadlock, and stderr must be drained even though it is discarded.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds)) {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }

                return null;
            }

            // The int overload returns once the process exits but does not guarantee the async readers
            // have finished; the parameterless call waits for those too.
            process.WaitForExit();

            if (process.ExitCode != 0) return null;

            return FirstLine(stdout.GetAwaiter().GetResult());
        } catch (Exception) {
            // A missing shell, a permission error, a malformed command: all "no token". This runs
            // inside a static initializer, where an escaping exception would take the daemon down.
            return null;
        }
    }

    /// <summary>The first non-empty line, trimmed — <c>gh auth token</c> and friends emit a trailing
    /// newline, and a token is never multi-line, so anything after the first line is noise rather than
    /// credential.</summary>
    static string? FirstLine(string output) {
        foreach (var line in output.Split('\n')) {
            var trimmed = line.Trim();

            if (trimmed.Length > 0) return trimmed;
        }

        return null;
    }
}
