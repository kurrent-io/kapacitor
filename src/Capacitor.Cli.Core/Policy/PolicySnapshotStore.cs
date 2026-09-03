namespace Capacitor.Cli.Core.Policy;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Per-session persisted <see cref="PolicySnapshot"/>. <see cref="LoadOrBuild"/> makes
/// mid-session policy edits inert: once a session has a saved snapshot, that snapshot governs it
/// until the session's marker is gone, never the live files. A persisted file that exists but
/// fails to load or re-bind is rebuilt from the live files — but unlike a genuinely absent file,
/// the rebuilt snapshot carries a degradation naming the loss, so a deny that governed the
/// session never vanishes without a trace.</summary>
public sealed class PolicySnapshotStore(ConfigRoot config) {
    enum LoadOutcome { Absent, Corrupt, Loaded }

    string PathFor(string sessionKey) => config.Path("policy", "sessions", $"{Sanitize(sessionKey)}.json");

    public PolicySnapshot? TryLoad(string sessionKey) {
        var (outcome, snapshot) = TryLoadCore(sessionKey);
        return outcome == LoadOutcome.Loaded ? snapshot : null;
    }

    (LoadOutcome Outcome, PolicySnapshot? Snapshot) TryLoadCore(string sessionKey) {
        var path = PathFor(sessionKey);
        bool exists;
        try { exists = File.Exists(path); }
        catch { return (LoadOutcome.Absent, null); } // can't prove it existed; behave as if it didn't
        if (!exists) return (LoadOutcome.Absent, null);

        try {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), PolicyJsonContext.Default.PolicySnapshotFileV1);
            if (file is null) return (LoadOutcome.Corrupt, null);
            var documents = new List<PolicyScopeDocument>();
            foreach (var d in file.Documents ?? []) {
                var scope = Enum.Parse<PolicyScope>(d.Scope);
                documents.Add(new PolicyScopeDocument(scope, d.SourcePath, d.Content,
                    PolicyDocumentBinder.Bind(d.Content, scope)));
            }
            return (LoadOutcome.Loaded, new PolicySnapshot(file.Id, documents, file.Degraded, file.Degradations ?? []));
        }
        catch { return (LoadOutcome.Corrupt, null); }
    }

    /// <summary>False when the snapshot did not reach disk: the caller decides what an unfrozen
    /// session costs, since a later hook rebuilds from whatever the live files say by then.</summary>
    public bool Save(string sessionKey, PolicySnapshot snapshot) {
        try {
            var path = PathFor(sessionKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new PolicySnapshotFileV1(snapshot.Id, PolicyEngine.Version, snapshot.Degraded, [.. snapshot.Degradations],
                [.. snapshot.Documents.Select(d => new PolicySnapshotFileDocV1(d.Scope.ToString(), d.SourcePath, d.Content))]);
            // Unique per write so concurrent hook processes saving the same session never
            // splice each other's temp file before the atomic rename.
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, PolicyJsonContext.Default.PolicySnapshotFileV1));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public PolicySnapshot LoadOrBuild(string sessionKey, string? repoRoot) {
        var (outcome, cached) = TryLoadCore(sessionKey);
        if (outcome == LoadOutcome.Loaded) return cached!;

        var built = PolicySnapshotBuilder.Build(repoRoot, config);
        if (outcome == LoadOutcome.Corrupt) {
            var path = PathFor(sessionKey);
            built = built with {
                Degraded = true,
                Degradations = [.. built.Degradations, $"persisted snapshot at {path} was unloadable; rebuilt from live files"],
            };
        }
        if (Save(sessionKey, built)) return built;

        // Unpersisted means unfrozen: the next hook rebuilds from the live files, so a deny that
        // governed this call can be edited away mid-session. Say so rather than hand back a
        // snapshot that looks clean.
        var directory = Path.GetDirectoryName(PathFor(sessionKey))!;
        return built with {
            Degraded = true,
            Degradations = [.. built.Degradations,
                $"session snapshot could not be persisted under {directory}; policy may not stay frozen for this session"],
        };
    }

    internal static string Sanitize(string sessionKey) =>
        sessionKey.Length is > 0 and <= 64 && sessionKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? sessionKey
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey)))[..32];
}
