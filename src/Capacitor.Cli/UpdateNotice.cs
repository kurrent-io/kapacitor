using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>
/// Deterministic exit-time "update available" notice for human-facing invocations.
///
/// <para>Before this, the hint was printed by a fire-and-forget <c>Task.Run</c> launched near the
/// top of <c>Program.cs</c> and never explicitly awaited on most paths — it only reliably resolved
/// inside <see cref="ClaudeHookCommand"/>'s own two await sites, i.e. inside a Claude hook, where
/// nobody reads stderr. Every other command — the ones a human is actually looking at — raced the
/// process exit and usually lost. <see cref="FlushAsync"/> is the fix: <c>Program.cs</c> awaits it
/// from a <c>finally</c> wrapping the whole command dispatch (including the <c>--help</c> and
/// no-server-configured early exits), so it runs deterministically before the process exits.</para>
///
/// <para><see cref="IsHumanFacing"/> is the suppression predicate — nobody is watching stderr on a
/// hook/MCP-server/watcher/foreground-daemon invocation, and printing on <c>update</c>/<c>uninstall</c>
/// or a caller-passed <c>--no-update-check</c> would be actively wrong. Everything else counts.</para>
///
/// <para><see cref="MarkReported"/> plus the shared, lazily-started check task
/// (<see cref="GetSharedCheckAsync"/>) let a second exit-time surface — <c>kcap status</c>'s own
/// inline version line — reuse the same in-flight/completed check instead of triggering a second
/// network round-trip, and suppress this class's own footer once it has already surfaced the
/// information itself.</para>
/// </summary>
internal static class UpdateNotice {
    static readonly object _gate = new();

    static Task<UpdateCommand.UpdateCheckResult?>? _sharedCheck;

    /// <summary>True once the notice has been surfaced (by <see cref="FlushAsync"/> itself, or by
    /// another exit-time surface via <see cref="MarkReported"/>) for this process invocation.</summary>
    static volatile bool _reported;

    /// <summary>
    /// The suppression predicate. False (suppressed) for:
    /// <see cref="CrashReporter.FailOpenCommands"/> (<c>hook</c>, <c>generate-whats-done</c>,
    /// <c>set-title</c>, <c>copilot-finalize</c> — agent-spawned, nobody reads their stderr);
    /// <c>mcp</c> (a stdio JSON-RPC server — stderr is not a terminal) and <c>watch</c> (a
    /// long-lived background process); the entire <c>daemon</c> command family (there is no
    /// separate <c>run</c> subcommand — the foreground shape is plain <c>kcap daemon start</c>
    /// without <c>-d</c>/<c>--detach</c>, which spawns the daemon child and blocks for its whole
    /// lifetime, exactly what <c>Capacitor.AppHost</c> runs on every dev-loop restart; every other
    /// <c>daemon</c> subcommand is infra/diagnostic and the "am I current?" nudge use-case is
    /// already served by <c>kcap status</c>); <c>update</c>/<c>uninstall</c> (nudging "run kcap
    /// update" from inside one of those is noise at best, and uninstall's cache-file write would
    /// race the command's own config-dir deletion); and an explicit <c>--no-update-check</c> flag.
    /// Everything else is human-facing and returns true.
    /// </summary>
    public static bool IsHumanFacing(string command, string[] args) {
        if (CrashReporter.FailOpenCommands.Contains(command)) return false;
        if (command is "mcp" or "watch" or "daemon") return false;
        if (command is "update" or "uninstall") return false;
        if (args.Contains("--no-update-check")) return false;

        return true;
    }

    /// <summary>
    /// Marks the notice as already surfaced by another exit-time surface (e.g. <c>kcap status</c>'s
    /// own inline version line), so a subsequent <see cref="FlushAsync"/> call for the same
    /// invocation prints nothing.
    /// </summary>
    public static void MarkReported() => _reported = true;

    /// <summary>
    /// Lazily starts (or returns the already-started) budgeted check for <paramref name="channel"/>,
    /// so at most one network round-trip happens per process no matter how many call sites
    /// (<see cref="FlushAsync"/>, <c>kcap status</c>) ask for the result.
    /// </summary>
    internal static Task<UpdateCommand.UpdateCheckResult?> GetSharedCheckAsync(string channel) {
        lock (_gate) {
            return _sharedCheck ??= UpdateCommand.CheckForUpdateWithBudgetAsync(channel);
        }
    }

    /// <summary>
    /// The single exit-path helper: awaited from <c>Program.cs</c>'s outer <c>finally</c> for every
    /// invocation. Does nothing (and touches neither disk nor network) unless
    /// <see cref="IsHumanFacing"/> says the command is human-facing, the active profile's
    /// <c>update_check</c> setting hasn't opted out, and nobody has already
    /// <see cref="MarkReported"/> this invocation. Never throws — an update notice must never break
    /// the command it's attached to.
    /// </summary>
    public static async Task FlushAsync(string command, string[] args) {
        try {
            if (_reported || !IsHumanFacing(command, args)) return;

            var profile = await AppConfig.GetActiveProfileAsync();
            if (profile?.UpdateCheck == false) return;

            var channel = UpdateCommand.ResolveChannel(args, profile?.UpdateChannel);
            var result  = await GetSharedCheckAsync(channel);

            // Re-check after the await: `kcap status` may have won the race and already reported
            // while this was in flight (both may share the same in-flight task via GetSharedCheckAsync).
            if (_reported || result is not { Newer: true, Latest: not null, Current: not null }) return;

            _reported = true;

            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync($"Update available: {result.Current} {UpdateCommand.Arrow} {result.Latest}");
            await Console.Error.WriteLineAsync("Run `kcap update` to update");
        } catch {
            // Best effort — an update notice must never break the command it's attached to.
        }
    }
}
