using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

/// App-owned UX state only — nothing lifecycle-safety-bearing lives here (spec §3.5). Persisted
/// to `~/.config/kcap/app-state.json` via PathHelpers.ConfigPath; the CLI's own fixed-namespace
/// marker is the source of truth for anything safety-bearing.
public sealed record AppState(
    bool ShimOffered = false,
    bool ShimDenied = false,
    IReadOnlyList<string>? DeclinedTakeoverPairs = null,
    bool ConsentQuarantineAcked = false);

public interface IAppStateStore {
    Task<AppState> LoadAsync();

    /// Serialized read-modify-write; atomic temp+rename; false (logged) on write failure —
    /// caller keeps the claim in memory for the run.
    Task<bool> UpdateAsync(Func<AppState, AppState> mutate);
}

public sealed partial class AppStateStore(string path) : IAppStateStore {
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppState> LoadAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            return Read();
        } finally {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            var next = mutate(Read());
            if (Write(next)) return true;

            Console.Error.WriteLine($"kcap: failed to persist app state to {path}");
            return false;
        } finally {
            _gate.Release();
        }
    }

    // Missing/corrupt → defaults; must never throw out of LoadAsync/UpdateAsync.
    AppState Read() {
        try {
            if (!File.Exists(path)) return new AppState();
            return JsonSerializer.Deserialize(File.ReadAllText(path), AppStateJsonContext.Default.AppState) ?? new AppState();
        } catch {
            return new AppState();
        }
    }

    // Temp write + rename: readers never observe a partial file. No fsync — this is UX state,
    // not durability-critical.
    bool Write(AppState state) {
        try {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, AppStateJsonContext.Default.AppState));
            File.Move(tmp, path, overwrite: true);
            return true;
        } catch {
            return false;
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(AppState))]
    partial class AppStateJsonContext : JsonSerializerContext;
}
