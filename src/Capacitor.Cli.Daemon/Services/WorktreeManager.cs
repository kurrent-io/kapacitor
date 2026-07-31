using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <paramref name="FetchedRef"/> is the per-worktree ref the daemon fetched
/// the requested <c>baseRef</c> into (e.g. <c>refs/kcap/review/{name}</c>).
/// Tracked so <see cref="WorktreeManager.RemoveAsync"/> can delete it on
/// cleanup. Null for non-review launches.
/// </summary>
public record WorktreeInfo(
        string Path, string Branch, string SourceRepo, bool IsStandalone = false,
        string? FetchedRef = null, string? SnapshotRoot = null) {
    /// <summary>A borrowed cwd (local in-place launch) the daemon does NOT own. Cleanup
    /// never removes it — the <see cref="AgentInstance.Work"/> guard enforces that.</summary>
    public static WorktreeInfo Borrowed(string cwd) => new(cwd, "", cwd, IsStandalone: false);
}

public partial class WorktreeManager(DaemonConfig config, ILogger<WorktreeManager> logger) {
    static readonly string[] SnapshotExcludedPaths = [
        ".capacitor", ".attached", ".mcp.json", ".cursor/mcp.json"
    ];
    const int MaxSnapshotFiles = 50_000;
    const long MaxSnapshotBytes = 2L * 1024 * 1024 * 1024;
    static StringComparison FileSystemPathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Per-repository gate around the git commands that mutate <c>.git/worktrees</c>.
    /// <para><c>git worktree add</c> enumerates the existing entries while creating its own, so two
    /// concurrent adds on one repo can have the later one read a sibling whose metadata directory
    /// exists but whose <c>commondir</c> has not been written yet — git aborts with
    /// <c>fatal: failed to read .git/worktrees/&lt;other&gt;/commondir</c>. <c>worktree remove</c>
    /// mutates the same tree. The daemon really does run these concurrently (a flow launching
    /// several reviewers against one source repo), which is the same concurrency the per-worktree
    /// fetch ref below was introduced for.</para>
    /// <para>Static because <see cref="RemoveAsync"/> is static, so the gate must be shared across
    /// instances. Keyed by full path under the platform's path-case rules. In-process only: two
    /// separate daemon processes on one repo would still race, which needs a cross-process file
    /// lock if it ever bites.</para></summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> WorktreeMetadataGates =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    /// <summary>Runs <paramref name="mutate"/> holding <paramref name="repoPath"/>'s metadata gate.
    /// Only the metadata-mutating git call belongs inside — a network fetch must stay outside, or
    /// concurrent launches serialise behind each other's downloads for no benefit.
    /// <para>Internal rather than private so the serialisation itself is testable: the concurrent
    /// <c>worktree add</c> race it prevents is too narrow to reproduce on demand, so the guard is
    /// pinned directly instead of through a flaky end-to-end repro.</para></summary>
    internal static async Task WithWorktreeMetadataGate(string repoPath, Func<Task> mutate) {
        var key = repoPath;

        try {
            key = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        } catch {
            // Unnormalizable path (too long / invalid chars): fall back to the raw string. Worst
            // case two spellings of one repo get separate gates — still better than none.
        }

        var gate = WorktreeMetadataGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try {
            await mutate();
        } finally {
            gate.Release();
        }
    }

    public async Task<WorktreeInfo> CreateAsync(string repoPath, string? name = null, string? baseRef = null) {
        name ??= $"agent-{Guid.NewGuid():N}"[..20];

        // Place worktrees under the repo's own .capacitor/ directory so they inherit
        // the repo's workspace trust in Claude Code (trust cascades up parent dirs).
        var worktreeRoot = Path.Combine(repoPath, ".capacitor", "worktrees");
        var worktreePath = Path.Combine(worktreeRoot, name);
        var branch       = $"capacitor/{name}";

        Directory.CreateDirectory(worktreeRoot);

        if (await IsGitRepoWithCommits(repoPath)) {
            if (!string.IsNullOrEmpty(baseRef)) {
                // Fetch into a per-worktree ref instead of the shared FETCH_HEAD
                // so concurrent review launches in the same source repo can't
                // race on each other's fetches. The unique ref carries the
                // worktree name so it's traceable and easy to clean up.
                var fetchedRef = $"refs/kcap/review/{name}";
                // Fetch OUTSIDE the metadata gate: it's already collision-free (per-worktree ref)
                // and it's the slow, network-bound half.
                await RunGit(repoPath, FetchTimeout, "fetch", "origin", $"{baseRef}:{fetchedRef}");
                await WithWorktreeMetadataGate(repoPath, () =>
                    RunGit(repoPath, GitTimeout, "worktree", "add", "-B", branch, worktreePath, fetchedRef));

                return new WorktreeInfo(worktreePath, branch, repoPath, FetchedRef: fetchedRef);
            }

            await WithWorktreeMetadataGate(repoPath, () =>
                RunGit(repoPath, GitTimeout, "worktree", "add", worktreePath, "-b", branch));

            return new WorktreeInfo(worktreePath, branch, repoPath);
        }

        // Standalone: copy files + git init
        Directory.CreateDirectory(worktreePath);
        CopyDirectory(repoPath, worktreePath);
        await RunGit(worktreePath, GitTimeout, "init");
        await RunGit(worktreePath, GitTimeout, "add", "-A");
        await RunGit(worktreePath, GitTimeout, "commit", "-m", "Initial snapshot");

        return new WorktreeInfo(worktreePath, "", repoPath, IsStandalone: true);
    }

    /// <summary>Suffix marking a borrowed launch's per-launch vendor state directory, which sits
    /// BESIDE the snapshot it belongs to. Outside the snapshot deliberately: a per-round refresh
    /// replaces the snapshot's contents, which would both destroy the running vendor's state and
    /// present that state to the reviewer as content under review.</summary>
    public const string VendorStateSuffix = ".vendor-state";

    /// <summary>The per-launch vendor state root for a borrowed snapshot. One definition, shared by
    /// the launch path that fills it and the cleanup paths that remove it.</summary>
    public static string VendorStateRootFor(string snapshotRoot) =>
        snapshotRoot.TrimEnd(Path.DirectorySeparatorChar) + VendorStateSuffix;

    public static async Task RemoveAsync(WorktreeInfo worktree, bool deleteBranch = true) {
        if (worktree.IsStandalone) {
            var root = worktree.SnapshotRoot ?? worktree.Path;

            // The vendor state directory is the daemon's, holds the reviewer's whole HOME for this
            // launch, and must not outlive it.
            if (worktree.SnapshotRoot is not null)
                DeleteTreeNoFollow(VendorStateRootFor(worktree.SnapshotRoot));

            DeleteTreeNoFollow(root);

            return;
        }

        // Same metadata tree as `worktree add` — a removal concurrent with an add can hand the add a
        // half-torn-down sibling entry.
        await WithWorktreeMetadataGate(worktree.SourceRepo, () =>
            RunGit(worktree.SourceRepo, GitTimeout, "worktree", "remove", worktree.Path, "--force"));

        if (deleteBranch && !string.IsNullOrEmpty(worktree.Branch)) {
            await RunGitBestEffort(worktree.SourceRepo, "branch", "-D", worktree.Branch);
        }

        if (!string.IsNullOrEmpty(worktree.FetchedRef)) {
            await RunGitBestEffort(worktree.SourceRepo, "update-ref", "-d", worktree.FetchedRef);
        }
    }

    /// <summary>Builds an independent, bundle-derived repository snapshot outside the source
    /// checkout. Unlike a linked worktree it shares no gitdir, refs, reflogs, object alternates, or
    /// worktree registration with the requester's repository.</summary>
    public async Task<WorktreeInfo> CreateBorrowedSnapshotAsync(
            string sourceRepoRoot, string? name, CancellationToken ct) {
        return await CreateBorrowedSnapshotAsync(sourceRepoRoot, sourceRepoRoot, name, ct);
    }

    public async Task<WorktreeInfo> CreateBorrowedSnapshotAsync(
            string sourceRepoRoot, string requestedCwd, string? name, CancellationToken ct) {
        var source = Path.GetFullPath(sourceRepoRoot);
        var cwd = Path.GetFullPath(requestedCwd);
        var relativeCwd = Path.GetRelativePath(source, cwd).Replace(Path.DirectorySeparatorChar, '/');
        if (relativeCwd == ".." || relativeCwd.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidOperationException("borrowed_snapshot_cwd_outside_source");
        var root = Path.GetFullPath(Path.Combine(config.WorktreeRoot, "borrowed-snapshots"));
        EnsureSeparateRoots(source, root);
        Directory.CreateDirectory(root);

        name ??= $"borrowed-{Guid.NewGuid():N}"[..25];
        var final = Path.Combine(root, name);
        var staging = final + ".preparing-" + Guid.NewGuid().ToString("N")[..8];
        var promoted = false;
        try {
            await BuildIndependentSnapshotAsync(source, staging, SnapshotExcludedPaths, ct);
            Directory.Move(staging, final);
            promoted = true;
            var executionPath = relativeCwd == "."
                ? final
                : ContainedPath(final, relativeCwd);
            if (!Directory.Exists(executionPath))
                throw new InvalidOperationException("borrowed_snapshot_cwd_missing");
            return new WorktreeInfo(final == executionPath ? final : executionPath, "", source,
                IsStandalone: true, SnapshotRoot: final);
        } catch {
            DeleteTreeNoFollow(staging);
            if (promoted) DeleteTreeNoFollow(final);
            throw;
        }
    }

    /// <summary>Rebuilds a borrowed snapshot from a pristine independent generation, then replaces
    /// the live snapshot contents. The source repository is never used as the reviewer's cwd and
    /// reviewer-created git metadata cannot survive into the next round.</summary>
    public async Task SyncFromSourceAsync(
            string sourceRepoRoot, string targetWorktreePath,
            string[] excludePaths, CancellationToken ct) {
        await SyncFromSourceAsync(
            sourceRepoRoot, targetWorktreePath, targetWorktreePath, excludePaths, ct);
    }

    public async Task SyncFromSourceAsync(
            string sourceRepoRoot, string targetWorktreePath, string executionPath,
            string[] excludePaths, CancellationToken ct) {
        if (string.IsNullOrEmpty(sourceRepoRoot))
            throw new ArgumentException("Source repo root must not be empty.", nameof(sourceRepoRoot));
        if (string.IsNullOrEmpty(targetWorktreePath))
            throw new ArgumentException("Target worktree path must not be empty.", nameof(targetWorktreePath));

        var source = Path.GetFullPath(sourceRepoRoot);
        var target = Path.GetFullPath(targetWorktreePath);
        var execution = Path.GetFullPath(executionPath);
        if (string.Equals(source, target, StringComparison.Ordinal))
            throw new InvalidOperationException($"Source and target paths are the same: {source}");
        if (!Directory.Exists(source))
            throw new InvalidOperationException($"Source repo root does not exist: {source}");
        if (!string.Equals(execution, target, FileSystemPathComparison) &&
            !execution.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                FileSystemPathComparison))
            throw new InvalidOperationException("borrowed_snapshot_execution_path_outside_target");
        if (!File.Exists(Path.Combine(source, ".git")) && !Directory.Exists(Path.Combine(source, ".git")))
            throw new InvalidOperationException($"Source path does not appear to be a git repo (no .git entry): {source}");

        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidOperationException("Snapshot target has no parent directory.");
        var staging = Path.Combine(parent, Path.GetFileName(target) + ".refresh-" + Guid.NewGuid().ToString("N")[..8]);
        try {
            var exclusions = SnapshotExcludedPaths.Concat(excludePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await BuildIndependentSnapshotAsync(source, staging, exclusions, ct);
            ReplaceTreeContentsNoFollow(target, staging, execution);
        } finally {
            DeleteTreeNoFollow(staging);
        }
    }

    async Task BuildIndependentSnapshotAsync(
            string source, string destination, string[] exclusions, CancellationToken ct) {
        for (var attempt = 0; attempt < 2; attempt++) {
            try {
                await BuildIndependentSnapshotOnceAsync(source, destination, exclusions, ct);
                return;
            } catch (SourceChangedException) when (attempt == 0) {
                DeleteTreeNoFollow(destination);
            } catch (SourceChangedException) {
                DeleteTreeNoFollow(destination);
                throw new InvalidOperationException("borrowed_snapshot_source_changed");
            }
        }
        throw new InvalidOperationException("borrowed_snapshot_source_changed");
    }

    async Task BuildIndependentSnapshotOnceAsync(
            string source, string destination, string[] exclusions, CancellationToken ct) {
        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidOperationException("Snapshot destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var bundle = Path.Combine(parent, ".bundle-" + Guid.NewGuid().ToString("N") + ".git");
        try {
            var sourceHead = (await RunGitCapture(source, GitTimeout, true, "rev-parse", "HEAD")).Trim();
            await RunGit(source, GitTimeout, sourceReadOnly: true, "bundle", "create", bundle, "HEAD");
            if (await GitConfigBoolAsync(source, "core.sparseCheckout"))
                throw new InvalidOperationException("borrowed_snapshot_sparse_checkout_unsupported");

            await RunGit(parent, GitTimeout, "clone", "--no-hardlinks", "--no-checkout", "--", bundle, destination);
            await RunGit(destination, GitTimeout, "checkout", "--detach", "HEAD");
            var clonedHead = (await RunGitCapture(destination, GitTimeout, false, "rev-parse", "HEAD")).Trim();
            if (!string.Equals(sourceHead, clonedHead, StringComparison.Ordinal)) throw new SourceChangedException();
            await RunGitBestEffort(destination, "remote", "remove", "origin");
            await RunGitBestEffort(destination, "reflog", "expire", "--expire=now", "--all");
            var fetchHead = Path.Combine(destination, ".git", "FETCH_HEAD");
            if (File.Exists(fetchHead)) File.Delete(fetchHead);

            var staged = await RunGitCapture(source, GitTimeout, true, "ls-files", "--stage", "-z");
            if (staged.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
                throw new InvalidOperationException("borrowed_snapshot_submodules_unsupported");

            var manifest = await ReadSourceManifestAsync(source, exclusions, ct);
            await ApplyReservedIndexPolicyAsync(destination);
            await CopyManifestAsync(source, destination, manifest, ct);
            RemoveFilesOutsideManifest(destination, manifest.Keys, ct);
            VerifyIndependentGit(destination, source);
            await VerifyDestinationManifestAsync(destination, manifest, ct);

            var finalHead = (await RunGitCapture(source, GitTimeout, true, "rev-parse", "HEAD")).Trim();
            var finalManifest = await ReadSourceManifestAsync(source, exclusions, ct);
            if (!string.Equals(sourceHead, finalHead, StringComparison.Ordinal) ||
                !ManifestsEqual(manifest, finalManifest))
                throw new SourceChangedException();
            LogSyncCompleted(source, destination, manifest.Count);
        } finally {
            try { if (File.Exists(bundle)) File.Delete(bundle); } catch { /* startup sweep handles leftovers */ }
        }
    }

    static async Task<Dictionary<string, SnapshotFile>> ReadSourceManifestAsync(
            string source, string[] exclusions, CancellationToken ct) {
        var stdout = await RunGitCapture(source, GitTimeout, true, "ls-files", "-co", "--exclude-standard", "-z");
        var result = new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var raw in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            ct.ThrowIfCancellationRequested();
            var rel = NormalizeRelativePath(raw);
            if (IsUnderExcluded(rel, exclusions)) {
                if (rel.Equals(".attached", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(".attached/", StringComparison.OrdinalIgnoreCase) ||
                    rel.Equals(".capacitor", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(".capacitor/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"borrowed_snapshot_reserved_path: {rel}");
                continue;
            }
            var path = ContainedPath(source, rel);
            if (!File.Exists(path)) continue; // tracked deletion
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"borrowed_snapshot_symlink_unsupported: {rel}");
            if (result.Count >= MaxSnapshotFiles)
                throw new InvalidOperationException("borrowed_snapshot_capacity_exceeded");
            await using var input = OpenSequentialRead(path);
            var streamLength = input.Length;
            total = checked(total + streamLength);
            if (total > MaxSnapshotBytes)
                throw new InvalidOperationException("borrowed_snapshot_capacity_exceeded");
            var prefix = new byte["version https://git-lfs.github.com/spec/v1\n"u8.Length];
            var prefixLength = await input.ReadAsync(prefix, ct);
            if (prefix.AsSpan(0, prefixLength).StartsWith("version https://git-lfs.github.com/spec/v1\n"u8))
                throw new InvalidOperationException($"borrowed_snapshot_lfs_pointer_unsupported: {rel}");
            input.Position = 0;
            var hash = await SHA256.HashDataAsync(input, ct);
            if (input.Length != streamLength) throw new SourceChangedException();
            UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
            if (!result.TryAdd(rel, new SnapshotFile(streamLength, hash, mode)))
                throw new InvalidOperationException($"borrowed_snapshot_path_collision: {rel}");
        }
        return result;
    }

    static async Task CopyManifestAsync(
            string source, string destination, Dictionary<string, SnapshotFile> manifest,
            CancellationToken ct) {
        foreach (var (rel, file) in manifest) {
            ct.ThrowIfCancellationRequested();
            var sourcePath = ContainedPath(source, rel);
            var path = ContainedPath(destination, rel);
            EnsureParentDirectories(destination, path);
            if (Directory.Exists(path)) DeleteTreeNoFollow(path);
            await using (var input = OpenSequentialRead(sourcePath))
            await using (var output = new FileStream(path, new FileStreamOptions {
                Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
                await input.CopyToAsync(output, ct);
            if (file.Mode is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
        }
    }

    static void RemoveFilesOutsideManifest(string destination, IEnumerable<string> accepted, CancellationToken ct) {
        var keep = accepted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(destination)) {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(entry).Equals(".git", StringComparison.Ordinal)) continue;
            RemoveUnaccepted(entry, destination, keep, ct);
        }
    }

    static bool RemoveUnaccepted(string path, string root, HashSet<string> keep, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReparsePoint)) { File.Delete(path); return true; }
        if (!attrs.HasFlag(FileAttributes.Directory)) {
            if (!keep.Contains(rel)) File.Delete(path);
            return !File.Exists(path);
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) RemoveUnaccepted(child, root, keep, ct);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        return !Directory.Exists(path);
    }

    static void ReplaceTreeContentsNoFollow(string target, string staging, string executionPath) {
        var relative = Path.GetRelativePath(target, executionPath);
        var protectedSegments = relative == "."
            ? []
            : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        ReplaceDirectoryContentsNoFollow(target, staging, protectedSegments, 0);
    }

    static void ReplaceDirectoryContentsNoFollow(
            string target, string? staging, string[] protectedSegments, int protectedIndex) {
        var protectedName = protectedIndex < protectedSegments.Length
            ? protectedSegments[protectedIndex]
            : null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
            if (protectedName is null ||
                !Path.GetFileName(entry).Equals(protectedName, FileSystemPathComparison))
                DeleteTreeNoFollow(entry);
        if (staging is not null) foreach (var entry in Directory.EnumerateFileSystemEntries(staging)) {
            if (protectedName is not null &&
                Path.GetFileName(entry).Equals(protectedName, FileSystemPathComparison))
                continue;
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
        if (protectedName is null) return;

        var protectedTarget = Path.Combine(target, protectedName);
        if (File.Exists(protectedTarget)) File.Delete(protectedTarget);
        Directory.CreateDirectory(protectedTarget);
        var protectedStaging = staging is null ? null : Path.Combine(staging, protectedName);
        if (protectedStaging is not null && !Directory.Exists(protectedStaging)) protectedStaging = null;
        ReplaceDirectoryContentsNoFollow(
            protectedTarget, protectedStaging, protectedSegments, protectedIndex + 1);
    }

    static void DeleteTreeNoFollow(string path) {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReparsePoint) || !attrs.HasFlag(FileAttributes.Directory)) {
            if (OperatingSystem.IsWindows() && attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteTreeNoFollow(child);
        if (OperatingSystem.IsWindows() && attrs.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        Directory.Delete(path);
    }

    static void EnsureSeparateRoots(string source, string snapshotRoot) {
        var prefix = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (snapshotRoot.Equals(source, FileSystemPathComparison) ||
            snapshotRoot.StartsWith(prefix, FileSystemPathComparison))
            throw new InvalidOperationException("borrowed_snapshot_root_inside_source");
    }

    static string NormalizeRelativePath(string raw) {
        var rel = raw.Replace('\\', '/').TrimStart('/').Normalize(NormalizationForm.FormC);
        if (rel.Length == 0 || rel.Split('/').Any(p => p is "" or "." or "..") ||
            rel.Split('/').Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"borrowed_snapshot_invalid_path: {raw}");
        return rel;
    }

    static string ContainedPath(string root, string rel) {
        var path = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, FileSystemPathComparison))
            throw new InvalidOperationException($"borrowed_snapshot_path_escape: {rel}");
        return path;
    }

    static void EnsureParentDirectories(string root, string path) {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"borrowed_snapshot_parent_missing: {path}");
        var stack = new Stack<string>();
        while (!parent.Equals(normalizedRoot, FileSystemPathComparison)) {
            stack.Push(parent);
            parent = Path.GetDirectoryName(parent)
                ?? throw new InvalidOperationException($"borrowed_snapshot_parent_outside_root: {path}");
        }
        while (stack.TryPop(out var dir)) {
            if (File.Exists(dir)) File.Delete(dir);
            Directory.CreateDirectory(dir);
        }
    }

    static async Task VerifyDestinationManifestAsync(
            string destination, Dictionary<string, SnapshotFile> manifest, CancellationToken ct) {
        foreach (var (rel, expected) in manifest) {
            var path = ContainedPath(destination, rel);
            if (!File.Exists(path) || new FileInfo(path).Length != expected.Length)
                throw new InvalidOperationException($"borrowed_snapshot_destination_mismatch: {rel}");
            await using var input = OpenSequentialRead(path);
            var hash = await SHA256.HashDataAsync(input, ct);
            if (!hash.AsSpan().SequenceEqual(expected.Hash))
                throw new InvalidOperationException($"borrowed_snapshot_destination_mismatch: {rel}");
        }
    }

    static void VerifyIndependentGit(string destination, string source) {
        var gitDir = Path.Combine(destination, ".git");
        if (!Directory.Exists(gitDir) || File.Exists(Path.Combine(gitDir, "objects", "info", "alternates")))
            throw new InvalidOperationException("borrowed_snapshot_git_not_independent");
        foreach (var file in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories)) {
            if (file.Contains(Path.DirectorySeparatorChar + "objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (new FileInfo(file).Length > 1024 * 1024) continue;
            var text = File.ReadAllText(file);
            if (text.Contains(source, StringComparison.Ordinal))
                throw new InvalidOperationException("borrowed_snapshot_source_path_disclosed");
        }
    }

    static bool ManifestsEqual(
            Dictionary<string, SnapshotFile> left, Dictionary<string, SnapshotFile> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var other) && pair.Value.Mode == other.Mode &&
            pair.Value.Length == other.Length && pair.Value.Hash.AsSpan().SequenceEqual(other.Hash));

    static async Task ApplyReservedIndexPolicyAsync(string destination) {
        foreach (var path in new[] { ".mcp.json", ".cursor/mcp.json" })
            try { await RunGit(destination, GitTimeout, "update-index", "--skip-worktree", "--", path); }
            catch { /* absent from index */ }
        Directory.CreateDirectory(Path.Combine(destination, ".git", "info"));
        File.AppendAllText(Path.Combine(destination, ".git", "info", "exclude"), "\n.attached/\n");
    }

    static FileStream OpenSequentialRead(string path) => new(path, new FileStreamOptions {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    sealed record SnapshotFile(long Length, byte[] Hash, UnixFileMode? Mode);
    sealed class SourceChangedException : Exception;

    static bool IsUnderExcluded(string rel, string[] prefixes) {
        rel = rel.Replace('\\', '/');
        foreach (var prefix in prefixes) {
            var normalized = prefix.Replace('\\', '/').TrimEnd('/');
            if (rel.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public Task CleanupOrphanedAsync(IEnumerable<string>? activeWorktreePaths = null) {
        // Legacy global root — clean up any leftover worktrees from before the per-repo change
        var worktreePaths = activeWorktreePaths as string[] ?? [..activeWorktreePaths ?? []];
        CleanupDirectory(config.WorktreeRoot, worktreePaths, "borrowed-snapshots");
        CleanupDirectory(Path.Combine(config.WorktreeRoot, "borrowed-snapshots"), worktreePaths);

        // Per-repo roots — scan each allowed repo for .capacitor/worktrees/
        foreach (var repoPath in config.AllowedRepoPaths) {
            var cleanPath   = repoPath.TrimEnd('/', '*');
            var perRepoRoot = Path.Combine(cleanPath, ".capacitor", "worktrees");
            CleanupDirectory(perRepoRoot, worktreePaths);
        }

        return Task.CompletedTask;
    }

    void CleanupDirectory(
            string root, IEnumerable<string>? activeWorktreePaths,
            string? reservedDirectoryName = null) {
        if (!Directory.Exists(root)) return;

        var activePaths = activeWorktreePaths?.Select(Path.GetFullPath).ToArray() ?? [];

        foreach (var dir in Directory.GetDirectories(root)) {
            if (reservedDirectoryName is not null &&
                Path.GetFileName(dir).Equals(reservedDirectoryName, StringComparison.OrdinalIgnoreCase))
                continue;
            var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);

            // Two ways to be live, and BOTH are checked. A directory is live if it is itself an active
            // worktree (or contains one) — the original rule — or, for a per-launch vendor state
            // directory, if the snapshot it sits beside is: a state directory is never itself an active
            // worktree path, so without the second arm the sweep reaps a running reviewer's HOME.
            //
            // Checking the first arm unconditionally is what keeps the suffix a hint rather than a
            // classification. Snapshot directories are named from the agent id, so one could legitimately
            // end in the suffix; deriving an "owner" and testing only that would then compare an active
            // snapshot against a path that does not exist and delete the live worktree.
            if (IsActive(fullDir, activePaths)) continue;
            if (fullDir.EndsWith(VendorStateSuffix, StringComparison.OrdinalIgnoreCase) &&
                IsActive(fullDir[..^VendorStateSuffix.Length], activePaths))
                continue;

            LogCleaningUp(dir);
            try { DeleteTreeNoFollow(dir); } catch (Exception ex) { LogCleanupFailed(ex, dir); }
        }
    }

    /// <summary>Whether <paramref name="candidate"/> is, or contains, an active worktree.</summary>
    static bool IsActive(string candidate, string[] activePaths) {
        if (candidate.Length == 0) return false;

        var prefix = candidate + Path.DirectorySeparatorChar;

        return activePaths.Any(path =>
            path.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Default timeout for local git operations (worktree add, init, commit, …).</summary>
    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Longer timeout for network git operations (fetch).</summary>
    static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    static async Task<bool> IsGitRepoWithCommits(string path) {
        try {
            var       psi  = NewGitPsi(path, ["rev-parse", "HEAD"]);
            using var proc = Process.Start(psi)!;
            using var cts  = new CancellationTokenSource(GitTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch {
                    /* best-effort */
                }

                return false;
            }

            return proc.ExitCode == 0;
        } catch { return false; }
    }

    static Task RunGit(string cwd, TimeSpan timeout, params string[] args) =>
        RunGit(cwd, timeout, sourceReadOnly: false, args);

    static async Task RunGit(string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) {
        var result = await RunGitCaptureResult(cwd, timeout, sourceReadOnly, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    static async Task<string> RunGitCapture(string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) {
        var result = await RunGitCaptureResult(cwd, timeout, sourceReadOnly, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout;
    }

    static async Task<bool> GitConfigBoolAsync(string cwd, string key) {
        var result = await RunGitCaptureResult(cwd, GitTimeout, true, "config", "--bool", "--get", key);
        if (result.ExitCode == 1) return false; // key is absent
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git config --bool --get {key} failed: {result.Stderr}");
        return string.Equals(result.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCaptureResult(
            string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) {
        var psi = NewGitPsi(cwd, args, sourceReadOnly);
        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try {
            await proc.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { proc.Kill(true); } catch { /* best-effort */ }
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:F0}s");
        }
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    static async Task RunGitBestEffort(string cwd, params string[] args) {
        try { await RunGit(cwd, GitTimeout, args); } catch {
            /* best-effort */
        }
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> for git with prompts disabled
    /// (<c>GIT_TERMINAL_PROMPT=0</c>, <c>GCM_INTERACTIVE=Never</c>) so an
    /// unattended daemon can never block on a credential prompt.
    /// </summary>
    static ProcessStartInfo NewGitPsi(string cwd, string[] args, bool sourceReadOnly = false) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            Environment = {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"]     = "Never"
            }
        };
        if (sourceReadOnly) {
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_CONFIG_COUNT"] = "2";
            psi.Environment["GIT_CONFIG_KEY_0"] = "maintenance.auto";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "false";
            psi.Environment["GIT_CONFIG_KEY_1"] = "core.fsmonitor";
            psi.Environment["GIT_CONFIG_VALUE_1"] = "false";
        }

        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleaning up orphaned worktree: {Path}")]
    partial void LogCleaningUp(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced {FileCount} files from {Source} into worktree {Target}")]
    partial void LogSyncCompleted(string source, string target, int fileCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clean up {Path}")]
    partial void LogCleanupFailed(Exception ex, string path);

    static void CopyDirectory(string source, string dest) {
        foreach (var file in Directory.GetFiles(source)) {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            if (Path.GetFileName(dir) == ".git") {
                continue;
            }

            var destDir = Path.Combine(dest, Path.GetFileName(dir));
            Directory.CreateDirectory(destDir);
            CopyDirectory(dir, destDir);
        }
    }
}
