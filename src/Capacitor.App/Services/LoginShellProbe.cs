namespace Capacitor.App.Services;

/// What users mean by "the terminal" is an *interactive login* shell: `-lic` reads both
/// `.zprofile` (login) and `.zshrc` (interactive — where nvm/npm/agent paths live), unlike the
/// GUI's inherited launchd PATH. Spec §3.6.
public interface ILoginShellProbe {
    /// Terminal PATH, or null when both probe attempts failed — never a fabricated value. Cached
    /// after the first call.
    Task<string?> TerminalPathAsync(CancellationToken ct);

    /// True/false once positively determined via `command -v kcap`; null = unknown. Cached
    /// independently of TerminalPathAsync.
    Task<bool?> KcapOnPathAsync(CancellationToken ct);
}

public sealed class LoginShellProbe(IProcessRunner runner, Func<string, string?> getEnv) : ILoginShellProbe {
    internal const string Sentinel = "<<KCAP-PATH>>";
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    Task<string?>? _terminalPath;
    Task<bool?>? _kcapOnPath;

    public Task<string?> TerminalPathAsync(CancellationToken ct) =>
        _terminalPath ??= RunScript($"printf '{Sentinel}%s{Sentinel}' \"$PATH\"", ct);

    public Task<bool?> KcapOnPathAsync(CancellationToken ct) {
        _kcapOnPath ??= Probe();
        return _kcapOnPath;

        async Task<bool?> Probe() {
            var found = await RunScript(
                    $"command -v kcap >/dev/null 2>&1 && printf '{Sentinel}FOUND{Sentinel}' || printf '{Sentinel}ABSENT{Sentinel}'", ct)
                .ConfigureAwait(false);

            return found switch {
                "FOUND" => true,
                "ABSENT" => false,
                _ => null,
            };
        }
    }

    // One primitive for both questions: -lic (login + interactive) first, -lc (login only) on a
    // nonzero exit or timeout, $SHELL unset/empty → /bin/zsh (macOS default). Both attempts
    // failing is unknown (null), never a guessed shell or a fabricated PATH.
    async Task<string?> RunScript(string script, CancellationToken ct) {
        var shell = getEnv("SHELL");
        if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";

        var result = await Attempt(shell, "-lic", script, ct).ConfigureAwait(false);
        if (Succeeded(result)) return Parse(result.Stdout);

        result = await Attempt(shell, "-lc", script, ct).ConfigureAwait(false);
        return Succeeded(result) ? Parse(result.Stdout) : null;
    }

    // stdin is not connected by our runner (RedirectStandardInput=false → the child reads
    // /dev/null), so no separate seam is needed here.
    Task<ProcessResult> Attempt(string shell, string flag, string script, CancellationToken ct) =>
        runner.RunAsync(shell, [flag, script], new RunOptions(Timeout: ProbeTimeout), ct);

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
