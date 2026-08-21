using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.OpenCode;

/// <summary>
/// Per-machine record of OpenCode sessions fully imported to a given server, used
/// to skip already-loaded sessions on re-run (the server exposes no completeness
/// signal). Shape: { "&lt;serverUrl&gt;": { "&lt;sessionId&gt;": "&lt;contentFingerprint&gt;" } }.
/// The value is a content fingerprint over the session's reconstructed transcript
/// AND its child subagents, so a same-line-count mutation (a tool part completing,
/// an in-place text edit, a changed/added child) invalidates the skip and re-imports.
/// Keyed by server URL so it never claims a session is loaded on the wrong server.
/// </summary>
internal sealed class OpenCodeImportLedger {
    readonly string _path;
    readonly Dictionary<string, Dictionary<string, string>> _byServer;

    OpenCodeImportLedger(string path, Dictionary<string, Dictionary<string, string>> data) {
        _path = path; _byServer = data;
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".cache", "kcap", "opencode-imported.json");

    public static OpenCodeImportLedger Load(string path) {
        try {
            if (File.Exists(path)) {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize(json, OpenCodeLedgerJsonContext.Default.DictionaryStringDictionaryStringString);
                if (data is not null) return new(path, data);
            }
        } catch { /* missing/corrupt → empty ledger */ }
        return new(path, new(StringComparer.Ordinal));
    }

    public bool IsComplete(string serverUrl, string sessionId, string fingerprint) =>
        _byServer.TryGetValue(serverUrl, out var sessions)
        && sessions.TryGetValue(sessionId, out var recorded)
        && recorded == fingerprint;

    public void MarkComplete(string serverUrl, string sessionId, string fingerprint) {
        if (!_byServer.TryGetValue(serverUrl, out var sessions)) {
            sessions = new(StringComparer.Ordinal);
            _byServer[serverUrl] = sessions;
        }
        sessions[sessionId] = fingerprint;
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_byServer, OpenCodeLedgerJsonContext.Default.DictionaryStringDictionaryStringString);
            File.WriteAllText(_path, json);
        } catch { /* best effort — a ledger write failure must not fail the import */ }
    }
}

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal partial class OpenCodeLedgerJsonContext : JsonSerializerContext { }
