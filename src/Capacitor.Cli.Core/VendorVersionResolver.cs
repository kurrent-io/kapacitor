using System.Diagnostics;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core;

/// <summary>
/// A vendor binary's own reported version, or <see langword="null"/> when it cannot be determined —
/// which every caller treats as unknown, and therefore denies.
/// </summary>
public sealed class VendorVersionResolver(BinaryProbe binaries) {
    public string? Resolve(string binaryPath) {
        try {
            var resolved = binaries.Resolve(binaryPath);
            if (resolved is null) return null;

            using var proc = Process.Start(new ProcessStartInfo(resolved, ["--version"]) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return null;

            // Both streams are drained CONCURRENTLY with the wait: a vendor that never closes stdout
            // would block a read-then-wait shape before the timeout could apply, and a redirected but
            // undrained stderr wedges the child once its buffer fills. A bounded wait is only bounded
            // if nothing ahead of it can block indefinitely.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(TimeSpan.FromSeconds(10))) {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }

                return null;   // a timeout is an UNKNOWN version, which the capability denies
            }

            // The child has exited, so both reads are complete or completing; bounded again so a detached
            // grandchild holding a pipe cannot keep us here.
            if (!Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5))) return null;

            // A version TOKEN from either stream, rather than requiring the whole trimmed output to be
            // one: vendors already emit banner lines on other paths, and a build that added an "update
            // available" notice would make the gate fail closed and silently disable the reviewer.
            return proc.ExitCode == 0
                ? ExtractVersionToken(stdout.Result) ?? ExtractVersionToken(stderr.Result)
                : null;
        } catch {
            // Any failure to interrogate the binary is "unknown version", which the capability denies. A
            // throw here would surface as a launch error rather than a coded capability refusal.
            return null;
        }
    }

    /// <summary>
    /// The first dotted-numeric token in <paramref name="output"/>, or null. Deliberately narrow: a
    /// certified-version check compares against an exact set, so anything that is not recognisably a version
    /// must read as UNKNOWN (and therefore denied) rather than as some near-miss string.
    /// </summary>
    public static string? ExtractVersionToken(string? output) {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            var tok = raw.Trim().TrimStart('v', 'V');

            if (tok.Length > 0 && tok.All(c => char.IsAsciiDigit(c) || c == '.') && tok.Contains('.'))
                return tok;
        }

        return null;
    }
}
