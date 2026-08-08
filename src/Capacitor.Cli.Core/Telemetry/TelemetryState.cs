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
    /// Returns the stable device id, creating one on first call. Returns null — and writes
    /// nothing — when telemetry is disabled, so an opted-out user never has an analytics
    /// identifier minted for them.
    /// </summary>
    public static string? GetOrCreateDeviceId() {
        string? result = null;
        Mutate(state => {
            if (state.Enabled is false) {
                result = null;
                return null;   // signal: no write needed
            }
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

    public static void SetEnabled(bool enabled) =>
        Mutate(state => state with { Enabled = enabled });

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

    static void WriteLocked(string path, TelemetryStateFile state) {
        try {
            var json = JsonSerializer.Serialize(state, CapacitorJsonContext.Default.TelemetryStateFile);
            File.WriteAllText(path, json);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort.
        }
    }
}
