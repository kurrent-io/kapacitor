using System.Text.Json;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>On-disk shape of <c>telemetry.json</c>.</summary>
public readonly record struct TelemetryStateFile(bool? Enabled, bool NoticeShown);

/// <summary>
/// Owns <c>telemetry.json</c> in the CLI config directory: the persisted enable flag and the
/// first-run-notice marker. The anonymous device id lives in its own, lock-free file instead (see
/// <see cref="TelemetryDeviceId"/>) — this file keeps only the fields where a lost update actually
/// matters: a dropped <see cref="SetEnabled"/> silently clobbers a user's opt-out, where a device-id
/// write race just means one of two equally-fine GUIDs wins.
///
/// Deliberately NOT <see cref="MachineId"/>'s <c>machine.json</c> either. That file is an
/// auth-relevant identifier sent to the Capacitor server to prove machine identity; an analytics
/// id is a different purpose with a different lifetime.
///
/// Each mutation (toggling enabled, marking notice shown) acquires a cross-process lock and
/// performs its read-modify-write atomically inside the lock to prevent lost-update races. On
/// lock-acquisition failure, degrades to best-effort unlocked write rather than silently dropping
/// the change — this is critical for opt-out enforcement, where a lost SetEnabled(false) would be
/// a privacy failure.
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
    /// Persists the enable flag. Disabling also deletes the device id file (see
    /// <see cref="TelemetryDeviceId.Delete"/>): the spec's rationale for keeping the analytics id
    /// separate from <c>machine.json</c> is that opt-out can delete it outright, not merely stop
    /// minting new ones. Re-enabling later mints a fresh id on the next
    /// <see cref="TelemetryDeviceId.GetOrCreate"/> call, which is more private than resurrecting the
    /// discarded one.
    /// </summary>
    public static void SetEnabled(bool enabled) {
        Mutate(state => state with { Enabled = enabled });
        if (!enabled) TelemetryDeviceId.Delete();
    }

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
            // Note: without cross-process locking in this fallback path, two concurrent writers can
            // race read-modify-write and last-writer-wins on disk, dropping whichever change lost.
            // This is acceptable as a graceful degradation when the lock mechanism is unavailable,
            // and is documented so future readers don't conclude the re-read was omitted by oversight.
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
    /// <see cref="TelemetrySettings.Resolve(bool?)"/> treats as "no persisted choice → enabled by
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
