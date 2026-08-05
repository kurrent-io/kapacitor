using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// A vendor binary's own reported version, or <see langword="null"/> when it cannot be determined —
/// which every caller treats as unknown, and therefore denies.
///
/// <para><b>Moved here from the Gemini reviewer's resolver rather than copied for Kiro.</b> Two traps
/// its history records, and a second copy would reintroduce: reading a stream to completion BEFORE
/// the bounded wait deadlocks on a vendor that never closes stdout (and an undrained stderr wedges
/// the child once its buffer fills), and requiring the whole trimmed output to equal a version makes
/// the gate fail closed the day a vendor adds an "update available" banner.</para>
/// </summary>
internal static class VendorVersionResolver {
    internal static string? Resolve(string binaryPath) {
        try {
            var resolved = CliResolver.ResolveExecutable(binaryPath);
            if (resolved is null) return null;

            using var proc = Process.Start(new ProcessStartInfo(resolved, ["--version"]) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return null;

            // Both streams are drained CONCURRENTLY with the wait, and the wait is what bounds this.
            //
            // Review caught a deadlock: the previous shape called ReadToEnd() before WaitForExit(10s), so a
            // vendor that never closed stdout blocked before the timeout could apply — and stderr was
            // redirected but never drained, so filling its buffer wedged the child too. A bounded wait is
            // only bounded if nothing ahead of it can block indefinitely.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(TimeSpan.FromSeconds(10))) {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }

                return null;   // a timeout is an UNKNOWN version, which the capability denies
            }

            // The child has exited, so both reads are complete or completing; bounded again so a detached
            // grandchild holding a pipe cannot keep us here.
            if (!Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5))) return null;

            // Extract a version TOKEN from either stream rather than requiring the whole trimmed output to be
            // one. Measured: gemini 0.53.0 prints the version to stdout AND stderr — but requiring exact
            // equality is brittle either way, since the vendor already emits banner lines (skill-conflict
            // warnings) on other paths, and a build that added an "update available" notice would make the
            // gate fail closed and silently disable the reviewer. Review's point, and it applies even though
            // today's format happens to work.
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
    internal static string? ExtractVersionToken(string? output) {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            var tok = raw.Trim().TrimStart('v', 'V');

            if (tok.Length > 0 && tok.All(c => char.IsAsciiDigit(c) || c == '.') && tok.Contains('.'))
                return tok;
        }

        return null;
    }
}
