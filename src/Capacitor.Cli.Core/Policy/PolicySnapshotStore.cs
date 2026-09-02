namespace Capacitor.Cli.Core.Policy;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Per-session persisted <see cref="PolicySnapshot"/>. <see cref="LoadOrBuild"/> makes
/// mid-session policy edits inert: once a session has a saved snapshot, that snapshot governs it
/// until the session's marker is gone, never the live files. A corrupt or unreadable persisted
/// snapshot is treated as absent and silently rebuilt — never thrown.</summary>
public sealed class PolicySnapshotStore(ConfigRoot config) {
    string PathFor(string sessionKey) => config.Path("policy", "sessions", $"{Sanitize(sessionKey)}.json");

    public PolicySnapshot? TryLoad(string sessionKey) {
        try {
            var path = PathFor(sessionKey);
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), PolicyJsonContext.Default.PolicySnapshotFileV1);
            if (file is null) return null;
            var documents = new List<PolicyScopeDocument>();
            foreach (var d in file.Documents) {
                var scope = Enum.Parse<PolicyScope>(d.Scope);
                documents.Add(new PolicyScopeDocument(scope, d.SourcePath, d.Content,
                    PolicyDocumentBinder.Bind(d.Content, scope)));
            }
            return new PolicySnapshot(file.Id, documents, file.Degraded, file.Degradations);
        }
        catch { return null; }
    }

    public void Save(string sessionKey, PolicySnapshot snapshot) {
        try {
            var path = PathFor(sessionKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new PolicySnapshotFileV1(snapshot.Id, snapshot.Degraded, [.. snapshot.Degradations],
                [.. snapshot.Documents.Select(d => new PolicySnapshotFileDocV1(d.Scope.ToString(), d.SourcePath, d.Content))]);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, PolicyJsonContext.Default.PolicySnapshotFileV1));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public PolicySnapshot LoadOrBuild(string sessionKey, string? repoRoot) {
        if (TryLoad(sessionKey) is { } cached) return cached;
        var built = PolicySnapshotBuilder.Build(repoRoot, config);
        Save(sessionKey, built);
        return built;
    }

    static string Sanitize(string sessionKey) =>
        sessionKey.Length is > 0 and <= 64 && sessionKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? sessionKey
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey)))[..32];
}
