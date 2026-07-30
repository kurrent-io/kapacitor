using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>Runs an operator-configured command that prints a borrowed-review token, so a service unit
/// can carry the COMMAND where it must not carry the credential. The daemon still acquires nothing on
/// its own: it runs exactly what the operator configured, and only for an actual borrowed launch.</summary>
internal static class BorrowedReviewTokenCommand {
    /// <summary>Bounded: a command that blocked forever would wedge a review.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Longest token accepted. A real token is well under this; anything longer is a command
    /// printing something else, and is rejected rather than truncated so a mangled prefix is never sent
    /// to a vendor as a credential.</summary>
    internal const int MaxTokenLength = 4096;

    /// <summary>The token the command printed, or null if it produced nothing usable.
    ///
    /// <para>Never throws, and never reports the command's output — a failing credential command is the
    /// likeliest thing to print a secret to stderr, and callers log around this. Null is
    /// indistinguishable from an unset variable, so a broken command degrades to not-advertised.</para></summary>
    internal static string? Run(string commandLine) {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        try {
            return RunAsync(commandLine).GetAwaiter().GetResult();
        } catch (Exception) {
            return null;   // missing shell, permission error, malformed command: all "no token"
        }
    }

    static async Task<string?> RunAsync(string commandLine) {
        using var cts = new CancellationTokenSource(Timeout);

        // A shell is the useful contract for an operator-supplied line (`gh auth token`, a keychain
        // lookup, a pipeline); the string comes from the daemon's own environment.
        var (shell, flag) = OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");

        using var process = Process.Start(new ProcessStartInfo(shell, [flag, commandLine]) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        });

        if (process is null) return null;

        // stderr is drained but NEVER accumulated: it is the stream most likely to carry the secret on a
        // failure, and buffering it would both put the credential in a managed string (reachable in a
        // core dump) and let a chatty command exhaust memory. stdout is read only up to one bounded line.
        var drainErr = DiscardAsync(process.StandardError, cts.Token);
        var readOut  = ReadTokenLineAsync(process.StandardOutput, cts.Token);

        try {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            Kill(process);
            await SettleAsync(drainErr, readOut).ConfigureAwait(false);

            return null;
        }

        var token = await SettleAsync(drainErr, readOut).ConfigureAwait(false);

        return process.ExitCode == 0 ? token : null;
    }

    /// <summary>Observes both readers so neither is left unawaited after the process ends or is killed —
    /// a descendant holding the pipe open would otherwise keep them, and their buffers, alive past
    /// return. Failures are swallowed: the caller's contract is "a token or null".</summary>
    static async Task<string?> SettleAsync(Task drainErr, Task<string?> readOut) {
        string? token = null;

        try { token = await readOut.ConfigureAwait(false); } catch (Exception) { /* no token */ }
        try { await drainErr.ConfigureAwait(false); }        catch (Exception) { /* discarded anyway */ }

        return token;
    }

    static void Kill(Process process) {
        try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
    }

    /// <summary>Reads to EOF, keeping nothing.</summary>
    static async Task DiscardAsync(StreamReader reader, CancellationToken ct) {
        var sink = new char[4096];

        while (await reader.ReadAsync(sink, ct).ConfigureAwait(false) > 0) { }
    }

    /// <summary>The first non-empty line, trimmed, up to <see cref="MaxTokenLength"/>. Token printers emit
    /// a trailing newline and a token is never multi-line, so reading one line is sufficient — and the cap
    /// is what keeps a command that prints megabytes from being a memory exhaustion vector.</summary>
    static async Task<string?> ReadTokenLineAsync(StreamReader reader, CancellationToken ct) {
        var buffer = new char[512];
        var line   = new System.Text.StringBuilder();

        while (true) {
            var read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);

            if (read == 0) break;

            for (var i = 0; i < read; i++) {
                if (buffer[i] == '\n') {
                    if (line.ToString().Trim().Length > 0) return Finish(line);

                    line.Clear();   // skip a leading blank line
                    continue;
                }

                if (line.Length >= MaxTokenLength) return null;   // reject, never truncate

                line.Append(buffer[i]);
            }
        }

        return Finish(line);
    }

    static string? Finish(System.Text.StringBuilder line) {
        var trimmed = line.ToString().Trim();

        return trimmed.Length == 0 || trimmed.Length > MaxTokenLength ? null : trimmed;
    }
}
