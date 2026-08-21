using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Disk-backed marker store recording sessions that the user disabled via
/// <c>kcap disable</c>. Hook handlers check <see cref="IsDisabled"/>
/// before doing any server-bound work so a disabled session never re-sends
/// transcript data after its server-side record was deleted.
/// </summary>
/// <remarks>
/// Both the Claude hook path in <c>Program.cs</c> and the Codex hook path in
/// <see cref="CodexHookCommand"/> share this helper —
/// without it, the next Codex <c>Stop</c> hook would restart the watcher and
/// re-enliven a session the user just deleted.
/// </remarks>
internal static class DisabledSessions {
    static string GetDisabledDir() => PathHelpers.ConfigPath("disabled");

    internal static bool IsDisabled(string sessionId) =>
        File.Exists(Path.Combine(GetDisabledDir(), sessionId));

    internal static void Mark(string sessionId) {
        var dir = GetDisabledDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, sessionId), "");
    }

    internal static void RemoveMarker(string sessionId) {
        var path = Path.Combine(GetDisabledDir(), sessionId);

        try { File.Delete(path); } catch {
            /* ignore */
        }
    }
}
