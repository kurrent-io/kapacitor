using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Surface 2: the interactive, exit-time stderr notice pointing at a harness that is installed but
/// not yet set up for recording. Mirrors <see cref="UpdateNotice"/> — best-effort, one line,
/// never blocks, human-facing commands only — and shares surface 1's 6-hour evaluation throttle
/// (via <see cref="HarnessOfferStore"/>) so the two nudge channels together fire at most once per
/// window.
/// </summary>
internal static class HarnessSetupNotice {
    /// <summary>
    /// Suppressed for the agent/tooling-spawned commands whose stderr nobody reads
    /// (<see cref="CrashReporter.FailOpenCommands"/>), for the non-interactive/long-lived families
    /// (<c>mcp</c>, <c>watch</c>, <c>daemon</c>, <c>completion</c>), for <c>update</c>/<c>uninstall</c>
    /// (noise mid-command), for the <c>harness</c> group itself (dismissing must never print a fresh
    /// nudge), and for <c>status</c> (which already shows an inline "not configured" line).
    /// </summary>
    static bool ShouldNotify(string command) {
        if (CrashReporter.FailOpenCommands.Contains(command)) return false;
        return command is not ("mcp" or "watch" or "daemon" or "completion"
            or "update" or "uninstall" or "harness" or "status");
    }

    public static async Task FlushAsync(
            string command, ConfigRoot config, ProfileContext profiles, HarnessRegistry harnesses) {
        try {
            if (!ShouldNotify(command)) return;
            if (Console.IsErrorRedirected) return; // scripts/pipelines never see it

            var profile = profiles.Effective;
            var notice = HarnessNudgeEmitter.ResolveNotice(
                harnesses, new HarnessOfferStore(config),
                profile?.DisableHarnessNudge is true, DateTimeOffset.UtcNow);
            if (notice is null) return;

            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(notice);
        } catch {
            // Best effort — a setup notice must never break the command it's attached to.
        }
    }
}
