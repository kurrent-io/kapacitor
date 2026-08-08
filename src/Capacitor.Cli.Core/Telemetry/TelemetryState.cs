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
/// cross-process lock to prevent lost-update races where two concurrent writers each read the
/// pre-write state, modify different fields, and then last-writer-wins clobber the other's
/// field. On lock-acquisition failure, degrades to best-effort unlocked write rather than
/// silently dropping the change — this is critical for opt-out enforcement, where a lost
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
        var state = Read();
        if (state.Enabled is false) return null;
        if (!string.IsNullOrWhiteSpace(state.Id)) return state.Id;

        var id = Guid.NewGuid().ToString("N");
        Write(state with { Id = id });

        return Read().Id ?? id;   // a peer may have won the race; adopt whatever landed
    }

    public static void SetEnabled(bool enabled) => Write(Read() with { Enabled = enabled });

    public static void MarkNoticeShown() => Write(Read() with { NoticeShown = true });

    static void Write(TelemetryStateFile state) {
        var path = Path;
        var dir = System.IO.Path.GetDirectoryName(path)!;

        // Acquire a cross-process lock to prevent lost updates when multiple processes modify
        // different fields concurrently. On timeout or lock-acquisition failure, fall back to
        // unlocked write — better than silently dropping the change, especially critical for
        // SetEnabled(false) opt-out enforcement.
        try {
            using (ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(2))) {
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(state, CapacitorJsonContext.Default.TelemetryStateFile);
                File.WriteAllText(path, json);
            }
        } catch (Exception e) when (e is TimeoutException or OperationCanceledException) {
            // Lock timeout; proceed with unlocked write as fallback.
            WriteUnlocked(path, dir, state);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Lock acquire failure or file I/O error; proceed with unlocked write.
            WriteUnlocked(path, dir, state);
        }
    }

    static void WriteUnlocked(string path, string dir, TelemetryStateFile state) {
        try {
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(state, CapacitorJsonContext.Default.TelemetryStateFile);
            File.WriteAllText(path, json);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort. A device id we fail to persist just means a new one next run,
            // which skews counts slightly — never a reason to fail the user's command.
        }
    }
}
