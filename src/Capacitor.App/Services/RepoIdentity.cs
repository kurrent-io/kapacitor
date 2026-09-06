using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// A rail group's identity: same Key = same repository wherever it is checked out. The key is
/// NOT a filesystem path — never hand it to path-formatting helpers.
public sealed record RepoIdentity(string Key, string Label);

public sealed class RepoIdentityResolver(Func<string, string?>? readOriginUrl = null) {
    readonly Func<string, string?> _readOriginUrl = readOriginUrl ?? GitRemoteReader.ReadOriginUrl;
    readonly Dictionary<string, RepoIdentity> _byRoot = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    public RepoIdentity ForLocalRoot(string normalizedRoot) {
        lock (_lock) {
            if (_byRoot.TryGetValue(normalizedRoot, out var cached)) return cached;
            var identity = Resolve(normalizedRoot);
            _byRoot[normalizedRoot] = identity;
            return identity;
        }
    }

    RepoIdentity Resolve(string root) {
        var url = root.Length > 0 ? _readOriginUrl(root) : null;
        var ownerRepo = url is null ? null : OwnerRepoOf(url);
        return ownerRepo is null
            ? new RepoIdentity($"path:{root}", root.Length > 0 ? RepoLabel.Leaf(root) : "No repository")
            : new RepoIdentity($"repo:{ownerRepo.ToLowerInvariant()}", ownerRepo);
    }

    public static RepoIdentity ForRemote(string? repoOwner, string? repoName, string? repoPath, string daemonKey) {
        if (!string.IsNullOrEmpty(repoOwner) && !string.IsNullOrEmpty(repoName))
            return new($"repo:{repoOwner.ToLowerInvariant()}/{repoName.ToLowerInvariant()}", $"{repoOwner}/{repoName}");
        var path = repoPath ?? "";
        return new($"daemon:{daemonKey}:{path}", path.Length > 0 ? RepoLabel.Leaf(path) : "No repository");
    }

    static string? OwnerRepoOf(string url) {
        var normalized = RemoteMatcher.NormalizeRemoteUrl(url);
        return normalized is null ? null : RemoteMatcher.PathAfterHost(normalized);
    }
}
