namespace Capacitor.Cli.Core.Policy;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>Reads the repo and user policy files off disk and binds them into a
/// <see cref="PolicySnapshot"/>. A file that fails to parse is dropped and recorded as a
/// degradation rather than aborting the whole snapshot — the other scope still governs.</summary>
public static class PolicySnapshotBuilder {
    public const string RepoRelativeDir = ".kcap";
    public const string FileName = "approvals.yaml";
    const long MaxFileSizeBytes = 1024 * 1024;

    public static PolicySnapshot Build(string? repoRoot, ConfigRoot config) {
        var documents = new List<PolicyScopeDocument>();
        var degradations = new List<string>();
        if (repoRoot is not null)
            TryLoad(Path.Combine(repoRoot, RepoRelativeDir, FileName), PolicyScope.Repo, documents, degradations);
        TryLoad(config.Path(FileName), PolicyScope.User, documents, degradations);
        var id = ComputeId(documents);
        return new PolicySnapshot(id, documents, degradations.Count > 0, degradations);
    }

    static void TryLoad(string path, PolicyScope scope, List<PolicyScopeDocument> documents, List<string> degradations) {
        string content;
        try {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            if (info.Length > MaxFileSizeBytes) {
                degradations.Add($"{scope.ToString().ToLowerInvariant()} policy at {path} ignored: file exceeds 1 MB");
                return;
            }
            content = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            degradations.Add($"{scope.ToString().ToLowerInvariant()} policy at {path} unreadable: {e.Message}");
            return;
        }
        try {
            documents.Add(new PolicyScopeDocument(scope, path, content, PolicyDocumentBinder.Bind(content, scope)));
        }
        catch (PolicyDocumentException e) {
            degradations.Add($"{scope.ToString().ToLowerInvariant()} policy at {path} ignored: {e.Message}");
        }
    }

    static string ComputeId(List<PolicyScopeDocument> documents) {
        using var ms = new MemoryStream();
        void Write(string s) {
            var bytes = Encoding.UTF8.GetBytes(s);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
            ms.Write(len);
            ms.Write(bytes);
        }
        Write(PolicyEngine.Version);
        foreach (var d in documents) { Write(d.Scope.ToString()); Write(d.Content); }
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }
}
