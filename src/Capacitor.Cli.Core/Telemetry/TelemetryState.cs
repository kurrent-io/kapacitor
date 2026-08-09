using System.Text.Json;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>On-disk shape of <c>telemetry.json</c>.</summary>
public readonly record struct TelemetryStateFile(string? Id, bool? Enabled, bool NoticeShown);

/// <summary>
/// Owns <c>telemetry.json</c> in the CLI config directory: the anonymous device id, the
/// persisted enable flag, and the first-run-notice marker.
///
/// Deliberately NOT <see cref="MachineId"/>'s <c>machine.json</c>. That file is an
/// auth-relevant identifier sent to the Capacitor server to prove machine identity;
/// an analytics id is a different purpose with a different lifetime, and keeping it separate
/// means opting out can delete it without touching authentication.
///
/// Each mutation (device id creation, toggling enabled, marking notice shown) acquires a
/// cross-process lock and performs its read-modify-write atomically inside the lock to prevent
/// lost-update races. On lock-acquisition failure, degrades to best-effort unlocked write rather
/// than silently dropping the change — this is critical for opt-out enforcement, where a lost
/// SetEnabled(false) would be a privacy failure.
/// </summary>
public static class TelemetryState {
    /// <summary>Test seam. Null in production, where the path resolves under the config dir.</summary>
    public static string? PathOverride { get; set; }

    static string Path => PathOverride ?? PathHelpers.ConfigPath("telemetry.json");

    public static TelemetryStateFile Read() {
        var path = Path;
        if (!File.Exists(path)) return default;

        try {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.TelemetryStateFile);
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return default;   // corrupt or transiently locked → defaults, never throw
        }
    }

    public static bool? PersistedEnabled() => Read().Enabled;

    /// <summary>
    /// Returns the stable device id, creating one on first call. Mints unconditionally — whether
    /// telemetry is enabled is NOT this method's decision. That precedence is
    /// <see cref="TelemetrySettings.Resolve"/>'s job alone; <see cref="CliTelemetry.Initialize"/>
    /// is the one gate that gets to skip this call entirely when disabled. An earlier revision
    /// re-checked <c>state.Enabled is false</c> here too, which meant an explicit
    /// <c>KCAP_TELEMETRY=1</c> could never override a persisted opt-out: Initialize would resolve
    /// enabled, call this method, and this method would independently veto it and return null,
    /// disabling the facade right back.
    /// </summary>
    public static string? GetOrCreateDeviceId() {
        string? result = null;
        Mutate(state => {
            if (!string.IsNullOrWhiteSpace(state.Id)) {
                result = state.Id;
                return null;   // signal: no write needed
            }
            var id = Guid.NewGuid().ToString("N");
            result = id;
            return state with { Id = id };
        });
        return result;
    }

    /// <summary>
    /// Persists the enable flag. Disabling also clears <see cref="TelemetryStateFile.Id"/>: the
    /// spec's rationale for a device id file separate from <c>machine.json</c> is that opt-out
    /// can delete the analytics id outright, not merely stop minting new ones. Re-enabling later
    /// mints a fresh id (see <see cref="GetOrCreateDeviceId"/>), which is more private than
    /// resurrecting the discarded one.
    /// </summary>
    public static void SetEnabled(bool enabled) =>
        Mutate(state => state with { Enabled = enabled, Id = enabled ? state.Id : null });

    public static void MarkNoticeShown() =>
        Mutate(state => state with { NoticeShown = true });

    /// <summary>
    /// Acquires a cross-process lock, reads current state, applies the mutation, and writes
    /// back atomically if the mutation returns a non-null result. Returning null from the
    /// delegate signals "no change needed"; no write occurs in either the locked or fallback path.
    /// On lock-acquisition failure, falls back to unlocked read-modify-write to ensure changes
    /// like SetEnabled(false) are never silently dropped.
    /// </summary>
    static void Mutate(Func<TelemetryStateFile, TelemetryStateFile?> apply) {
        var path = Path;
        var dir = System.IO.Path.GetDirectoryName(path)!;

        try {
            using (ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(2))) {
                Directory.CreateDirectory(dir);
                var currentState = ReadLocked(path);
                var newState = apply(currentState);
                if (newState.HasValue) {
                    WriteLocked(path, newState.Value);
                }
            }
        } catch (Exception) {
            // Lock acquisition failed (timeout, foreign-owned mutex, path errors, etc.)
            // or an exception escaped from inside the lock (should not happen, but catch broadly
            // per global constraint: telemetry must never throw to NativeAOT runtime).
            // Degrade to unlocked read-modify-write rather than silently dropping the change.
            MutateUnlocked(path, dir, apply);
        }
    }

    static void MutateUnlocked(string path, string dir, Func<TelemetryStateFile, TelemetryStateFile?> apply) {
        try {
            Directory.CreateDirectory(dir);
            var currentState = ReadLocked(path);
            var newState = apply(currentState);
            if (newState.HasValue) {
                WriteLocked(path, newState.Value);
            }
            // Note: without cross-process locking in this fallback path, two concurrent processes
            // can each mint different GUIDs, and last-writer-wins on disk. A process that loses the
            // race will have returned its own id, now orphaned on disk. This is acceptable as a
            // graceful degradation when the lock mechanism is unavailable, and is documented so
            // future readers don't conclude the re-read was omitted by oversight.
        } catch (Exception) {
            // Best effort. Telemetry must never throw to the NativeAOT runtime.
        }
    }

    static TelemetryStateFile ReadLocked(string path) {
        if (!File.Exists(path)) return default;
        try {
            var json = File.ReadAllText(path);
            var deserialized = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.TelemetryStateFile);
            return deserialized;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return default;
        }
    }

    /// <summary>
    /// Writes atomically: serialise to a temp file in the SAME directory, then rename over the
    /// target. <see cref="Read"/> takes no lock (locking every command-startup read would put a
    /// cross-process mutex on the hot path), so it relies on never observing a half-written file.
    /// A plain <c>File.WriteAllText(path, ...)</c> truncates the target before rewriting it — a
    /// concurrent unlocked <see cref="Read"/> landing in that window sees partial JSON, hits its
    /// catch, and returns <c>default</c> (<c>Enabled = null</c>), which
    /// <see cref="TelemetrySettings.Resolve"/> treats as "no persisted choice → enabled by
    /// default" — silently re-enabling telemetry a process just opted out of. The temp file lives
    /// next to the target so the rename stays on one volume (required for it to be atomic), and a
    /// reader then only ever sees the fully-old or fully-new file, never a torn one.
    /// </summary>
    static void WriteLocked(string path, TelemetryStateFile state) {
        var dir      = System.IO.Path.GetDirectoryName(path) ?? "";
        var tempPath = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");

        try {
            var json = JsonSerializer.Serialize(state, CapacitorJsonContext.Default.TelemetryStateFile);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort. Clean up the temp file so a failed write doesn't leave debris behind —
            // itself best-effort, since we're already in the never-throw fallback path.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
