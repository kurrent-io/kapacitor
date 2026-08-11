namespace Capacitor.App.Services;

/// What users mean by "the terminal" is an *interactive login* shell: `-lic` reads both
/// `.zprofile` (login) and `.zshrc` (interactive — where nvm/npm/agent paths live), unlike the
/// GUI's inherited launchd PATH. Spec §3.6.
public interface ILoginShellProbe {
    /// Terminal PATH, or null when both probe attempts completed but neither produced one — never
    /// a fabricated value. Cached once determined; a process-start failure (not a completed probe)
    /// is retried on the next call instead of being cached.
    Task<string?> TerminalPathAsync(CancellationToken ct);

    /// True/false once positively determined via `command -v kcap`; null = unknown. Cached
    /// independently of TerminalPathAsync, with the same retry-on-process-start-failure rule.
    Task<bool?> KcapOnPathAsync(CancellationToken ct);
}

public sealed class LoginShellProbe(IProcessRunner runner, Func<string, string?> getEnv) : ILoginShellProbe {
    internal const string Sentinel = "<<KCAP-PATH>>";
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    // The bool is "Cacheable": both attempts ran to completion (however unsuccessfully) is a
    // determined outcome; a process-start failure (RunScript below) means the question was never
    // actually asked, so TerminalPathAsync/KcapOnPathAsync must not memoize it.
    Task<(string? Value, bool Cacheable)>? _terminalPath;
    Task<(bool? Value, bool Cacheable)>? _kcapOnPath;

    public async Task<string?> TerminalPathAsync(CancellationToken ct) {
        var task = _terminalPath ??= RunScript($"printf '{Sentinel}%s{Sentinel}' \"$PATH\"");
        var (value, cacheable) = await task.WaitAsync(ct).ConfigureAwait(false);
        if (!cacheable) _terminalPath = null;
        return value;
    }

    public async Task<bool?> KcapOnPathAsync(CancellationToken ct) {
        var task = _kcapOnPath ??= ProbeKcapOnPath();
        var (value, cacheable) = await task.WaitAsync(ct).ConfigureAwait(false);
        if (!cacheable) _kcapOnPath = null;
        return value;
    }

    async Task<(bool? Value, bool Cacheable)> ProbeKcapOnPath() {
        var (found, cacheable) = await RunScript(
                $"command -v kcap >/dev/null 2>&1 && printf '{Sentinel}FOUND{Sentinel}' || printf '{Sentinel}ABSENT{Sentinel}'")
            .ConfigureAwait(false);

        bool? value = found switch {
            "FOUND" => true,
            "ABSENT" => false,
            _ => null,
        };
        return (value, cacheable);
    }

    // One primitive for both questions: -lic (login + interactive) first, -lc (login only) on a
    // nonzero exit, a timeout, or a process-start failure (e.g. a stale $SHELL); $SHELL
    // unset/empty → /bin/zsh (macOS default). Runs on CancellationToken.None — this probe is
    // shared cache-wide (see the two public methods above), so it must not be tied to whichever
    // caller happened to trigger it; a caller's own ct only bounds *their* wait via WaitAsync.
    async Task<(string? Value, bool Cacheable)> RunScript(string script) {
        var shell = getEnv("SHELL");
        if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";

        var first = await Attempt(shell, "-lic", script).ConfigureAwait(false);
        if (first.Ran && Succeeded(first.Result!)) return (Parse(first.Result!.Stdout), true);

        var second = await Attempt(shell, "-lc", script).ConfigureAwait(false);
        if (second.Ran && Succeeded(second.Result!)) return (Parse(second.Result!.Stdout), true);

        return (null, first.Ran && second.Ran);
    }

    // stdin is not connected by our runner (RedirectStandardInput=false → the child reads
    // /dev/null). A process-start failure (bad/non-executable $SHELL) is caught here rather than
    // left to propagate, so the fallback attempt still runs and the caller above still gets a
    // clean null instead of a crash.
    async Task<(bool Ran, ProcessResult? Result)> Attempt(string shell, string flag, string script) {
        try {
            var result = await runner.RunAsync(shell, [flag, script], new RunOptions(Timeout: ProbeTimeout), CancellationToken.None)
                .ConfigureAwait(false);
            return (true, result);
        } catch (Exception) {
            return (false, null);
        }
    }

    static bool Succeeded(ProcessResult result) => result.ExitCode == 0 && !result.TimedOut;

    /// Content between the sentinel PAIR; null if the sentinel is absent, appears only once
    /// (torn output), or the pair is empty of a match — startup chatter ahead of the payload
    /// (motd, nvm banners) never leaks in.
    internal static string? Parse(string stdout) {
        var start = stdout.IndexOf(Sentinel, StringComparison.Ordinal);
        if (start < 0) return null;

        var contentStart = start + Sentinel.Length;
        var end = stdout.IndexOf(Sentinel, contentStart, StringComparison.Ordinal);
        return end < 0 ? null : stdout[contentStart..end];
    }
}
