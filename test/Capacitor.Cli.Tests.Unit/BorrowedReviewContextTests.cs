using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

public class BorrowedReviewContextTests {
    [Test]
    [Arguments("--skip-worktree")]
    [Arguments("--assume-unchanged")]
    public async Task Snapshot_discloses_index_blob_not_private_worktree_bytes(string indexFlag) {
        var repo = NewGitRepo();
        var root = Path.Combine(Path.GetTempPath(), "kcap-review-context-root-" + Guid.NewGuid().ToString("N")[..8]);
        var indexBytes = "{\"mcpServers\":{\"branch\":{}}}"u8.ToArray();
        var privateBytes = "{\"mcpServers\":{\"private-secret\":{}}}"u8.ToArray();

        try {
            await File.WriteAllBytesAsync(Path.Combine(repo, ".mcp.json"), indexBytes);
            Git(repo, "add", ".mcp.json");
            Git(repo, "commit", "-q", "-m", "add branch config");
            Git(repo, "update-index", indexFlag, ".mcp.json");
            await File.WriteAllBytesAsync(Path.Combine(repo, ".mcp.json"), privateBytes);

            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            var snapshot = await manager.CreateBorrowedSnapshotAsync(repo, "review", CancellationToken.None);

            try {
                await Assert.That(File.Exists(Path.Combine(snapshot.SnapshotRoot!, ".mcp.json"))).IsFalse();
                await Assert.That(snapshot.ReviewContextRoot).IsNotNull();
                await Assert.That(Directory.Exists(snapshot.ReviewContextRoot!)).IsTrue();
                await Assert.That(snapshot.ReviewContextGeneration).IsNotNull();
                if (!OperatingSystem.IsWindows()) {
                    var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                        UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                        UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                    await Assert.That(File.GetUnixFileMode(snapshot.ReviewContextRoot!) & forbidden)
                        .IsEqualTo((UnixFileMode)0);
                    await Assert.That(File.GetUnixFileMode(Path.Combine(
                        snapshot.ReviewContextGeneration!.StoragePath, "manifest.json")) & forbidden)
                        .IsEqualTo((UnixFileMode)0);
                }

                var manifest = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
                await Assert.That(manifest["provenance"]!.GetValue<string>()).IsEqualTo("git-index-stage-0");
                await Assert.That(manifest["workingTreeBytes"]!.GetValue<bool>()).IsFalse();
                await Assert.That(manifest["unstagedAndUntrackedOmitted"]!.GetValue<bool>()).IsTrue();

                var entries = manifest["entries"]!.AsArray();
                await Assert.That(entries.Count).IsEqualTo(1);
                await Assert.That(entries[0]!["path"]!.GetValue<string>()).IsEqualTo(".mcp.json");
                await Assert.That(entries[0]!["base64"]!.GetValue<string>())
                    .IsEqualTo(Convert.ToBase64String(indexBytes));
                await Assert.That(entries[0]!["base64"]!.GetValue<string>())
                    .IsNotEqualTo(Convert.ToBase64String(privateBytes));

                var sidecarRoot = snapshot.ReviewContextRoot!;
                await WorktreeManager.RemoveAsync(snapshot);
                await Assert.That(Directory.Exists(sidecarRoot)).IsFalse();
            } finally {
                if (Directory.Exists(snapshot.SnapshotRoot!)) await WorktreeManager.RemoveAsync(snapshot);
            }
        } finally {
            TryDelete(repo);
            TryDelete(root);
        }
    }

    [Test]
    public async Task Staged_blob_is_returned_while_later_unstaged_bytes_are_omitted() {
        var repo = NewGitRepo();
        var root = NewRoot();
        var staged = "{\"mcpServers\":{\"staged\":{}}}"u8.ToArray();
        var unstaged = "{\"mcpServers\":{\"unstaged-private\":{}}}"u8.ToArray();
        try {
            await File.WriteAllBytesAsync(Path.Combine(repo, ".mcp.json"), staged);
            Git(repo, "add", ".mcp.json");
            await File.WriteAllBytesAsync(Path.Combine(repo, ".mcp.json"), unstaged);

            var snapshot = await Manager(root).CreateBorrowedSnapshotAsync(
                repo, "review", CancellationToken.None);
            try {
                var manifest = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
                var encoded = manifest["entries"]![0]!["base64"]!.GetValue<string>();
                await Assert.That(encoded).IsEqualTo(Convert.ToBase64String(staged));
                await Assert.That(encoded).IsNotEqualTo(Convert.ToBase64String(unstaged));
            } finally { await WorktreeManager.RemoveAsync(snapshot); }
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Reserved_path_authored_as_a_directory_fails_closed() {
        var repo = NewGitRepo();
        var root = NewRoot();
        try {
            Directory.CreateDirectory(Path.Combine(repo, ".mcp.json"));
            const string decomposedChild = "cafe\u0301";
            File.WriteAllText(Path.Combine(repo, ".mcp.json", decomposedChild), "branch data");
            Git(repo, "add", ".mcp.json/" + decomposedChild);
            Git(repo, "commit", "-q", "-m", "reserved path directory");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root).CreateBorrowedSnapshotAsync(repo, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_reserved_path_is_directory");
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Reserved_path_authored_as_a_git_symlink_fails_closed() {
        Skip.When(OperatingSystem.IsWindows(), "Git symlink mode is covered on POSIX.");
        var repo = NewGitRepo();
        var root = NewRoot();
        try {
            File.CreateSymbolicLink(Path.Combine(repo, ".mcp.json"), "tracked.txt");
            Git(repo, "add", ".mcp.json");
            Git(repo, "commit", "-q", "-m", "reserved path symlink");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root).CreateBorrowedSnapshotAsync(repo, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_non_regular_mode");
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Aggregate_capacity_accepts_exact_limit_and_rejects_one_extra_byte() {
        var exactRepo = NewGitRepo();
        var exactRoot = NewRoot();
        var overRepo = NewGitRepo();
        var overRoot = NewRoot();
        try {
            await File.WriteAllBytesAsync(Path.Combine(exactRepo, ".mcp.json"), new byte[256 * 1024]);
            Git(exactRepo, "add", ".mcp.json");
            var exact = await Manager(exactRoot).CreateBorrowedSnapshotAsync(
                exactRepo, "review", CancellationToken.None);
            try {
                var manifest = JsonNode.Parse(exact.ReviewContextGeneration!.JsonUtf8)!.AsObject();
                await Assert.That(manifest["entries"]![0]!["byteCount"]!.GetValue<long>())
                    .IsEqualTo(256L * 1024);
            } finally { await WorktreeManager.RemoveAsync(exact); }

            await File.WriteAllBytesAsync(Path.Combine(overRepo, ".mcp.json"), new byte[256 * 1024 + 1]);
            Git(overRepo, "add", ".mcp.json");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(overRoot).CreateBorrowedSnapshotAsync(
                    overRepo, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_capacity_exceeded");
        } finally {
            TryDelete(exactRepo); TryDelete(exactRoot);
            TryDelete(overRepo); TryDelete(overRoot);
        }
    }

    [Test]
    public async Task Matching_unmerged_index_entry_fails_closed() {
        var repo = NewGitRepo();
        var root = NewRoot();
        try {
            var oid = GitCapture(repo, "rev-parse", "HEAD:tracked.txt").Trim();
            GitWithInput(repo,
                Encoding.ASCII.GetBytes(
                    $"100644 {oid} 1\t.mcp.json\n" +
                    $"100644 {oid} 2\t.mcp.json\n" +
                    $"100644 {oid} 3\t.mcp.json\n"),
                "update-index", "--index-info");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root).CreateBorrowedSnapshotAsync(
                    repo, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_unmerged_index");
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Invalid_utf8_under_reserved_path_fails_but_unrelated_invalid_utf8_is_ignored() {
        Skip.When(OperatingSystem.IsWindows(), "Raw non-UTF8 Git paths are covered on POSIX.");
        var badRepo = NewGitRepo();
        var badRoot = NewRoot();
        var unrelatedRepo = NewGitRepo();
        var unrelatedRoot = NewRoot();
        try {
            var badOid = GitCapture(badRepo, "rev-parse", "HEAD:tracked.txt").Trim();
            GitWithInput(badRepo,
                RawIndexRecord(badOid, ".mcp.json/"u8, 0xff),
                "update-index", "-z", "--index-info");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(badRoot).CreateBorrowedSnapshotAsync(
                    badRepo, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_invalid_path_encoding");

            var expected = "{\"mcpServers\":{}}"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(unrelatedRepo, ".mcp.json"), expected);
            Git(unrelatedRepo, "add", ".mcp.json");
            var unrelatedOid = GitCapture(unrelatedRepo, "rev-parse", "HEAD:tracked.txt").Trim();
            GitWithInput(unrelatedRepo,
                RawIndexRecord(unrelatedOid, "unrelated-"u8, 0xff),
                "update-index", "-z", "--index-info");
            var snapshot = await Manager(unrelatedRoot).CreateBorrowedSnapshotAsync(
                unrelatedRepo, "review", CancellationToken.None);
            try {
                var manifest = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
                await Assert.That(manifest["entries"]!.AsArray().Count).IsEqualTo(1);
                await Assert.That(manifest["entries"]![0]!["base64"]!.GetValue<string>())
                    .IsEqualTo(Convert.ToBase64String(expected));
            } finally { await WorktreeManager.RemoveAsync(snapshot); }
        } finally {
            TryDelete(badRepo); TryDelete(badRoot);
            TryDelete(unrelatedRepo); TryDelete(unrelatedRoot);
        }
    }

    [Test]
    public async Task Matching_gitlink_reports_non_regular_context_failure() {
        var repo = NewGitRepo();
        var root = NewRoot();
        try {
            var commit = GitCapture(repo, "rev-parse", "HEAD").Trim();
            GitWithInput(repo,
                Encoding.ASCII.GetBytes($"160000 {commit} 0\t.mcp.json\n"),
                "update-index", "--index-info");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root).CreateBorrowedSnapshotAsync(
                    repo, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_non_regular_mode");
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Regular_mode_entry_whose_object_is_not_a_blob_fails_closed() {
        var repo = NewGitRepo();
        var root = NewRoot();
        try {
            var commit = GitCapture(repo, "rev-parse", "HEAD").Trim();
            GitWithInput(repo,
                Encoding.ASCII.GetBytes($"100644 {commit} 0\t.mcp.json\n"),
                "update-index", "--index-info");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root).CreateBorrowedSnapshotAsync(
                    repo, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_non_blob_object");
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Object_id_validation_rejects_zero_malformed_and_non_hex_values() {
        await Assert.That(WorktreeManager.IsValidObjectId(new string('0', 40))).IsFalse();
        await Assert.That(WorktreeManager.IsValidObjectId("abc")).IsFalse();
        await Assert.That(WorktreeManager.IsValidObjectId(new string('g', 40))).IsFalse();
        await Assert.That(WorktreeManager.IsValidObjectId(new string('a', 40))).IsTrue();
        await Assert.That(WorktreeManager.IsValidObjectId(new string('a', 64))).IsTrue();
    }

    [Test]
    public async Task Preexisting_linked_sidecar_root_fails_without_touching_its_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        var repo = NewGitRepo();
        var root = NewRoot();
        var external = Directory.CreateTempSubdirectory("kcap-review-context-external-").FullName;
        try {
            File.WriteAllText(Path.Combine(external, "sentinel"), "keep-me");
            var snapshots = Path.Combine(root, "borrowed-snapshots");
            Directory.CreateDirectory(snapshots);
            var sidecar = WorktreeManager.ReviewContextRootFor(
                Path.Combine(snapshots, "review"));
            Directory.CreateSymbolicLink(sidecar, external);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root).CreateBorrowedSnapshotAsync(
                    repo, "review", CancellationToken.None));

            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_unsafe_storage_path");
            await Assert.That(File.ReadAllText(Path.Combine(external, "sentinel")))
                .IsEqualTo("keep-me");
        } finally {
            TryDelete(repo); TryDelete(root); TryDelete(external);
        }
    }

    [Test]
    public async Task Linked_borrowed_snapshots_parent_fails_without_writing_outside_worktree_root() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        var repo = NewGitRepo();
        var root = NewRoot();
        var external = Directory.CreateTempSubdirectory("kcap-review-parent-external-").FullName;
        try {
            File.WriteAllText(Path.Combine(external, "sentinel"), "keep-me");
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(
                Path.Combine(root, "borrowed-snapshots"), external);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root).CreateBorrowedSnapshotAsync(
                    repo, "review", CancellationToken.None));

            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_unsafe_storage_path");
            await Assert.That(Directory.GetFileSystemEntries(external).Select(Path.GetFileName))
                .IsEquivalentTo(["sentinel"]);
            await Assert.That(File.ReadAllText(Path.Combine(external, "sentinel")))
                .IsEqualTo("keep-me");
        } finally {
            try { Directory.Delete(Path.Combine(root, "borrowed-snapshots")); } catch { }
            TryDelete(repo); TryDelete(root); TryDelete(external);
        }
    }

    [Test]
    public async Task Case_collisions_follow_the_actual_destination_filesystem_semantics() {
        var repo = NewGitRepo();
        var root = NewRoot();
        try {
            var oid = GitCapture(repo, "rev-parse", "HEAD:tracked.txt").Trim();
            GitWithInput(repo, Encoding.ASCII.GetBytes(
                $"100644 {oid} 0\t.mcp.json\n100644 {oid} 0\t.MCP.JSON\n"),
                "update-index", "--index-info");

            Directory.CreateDirectory(root);
            var caseSensitive = IsCaseSensitive(root);
            if (!caseSensitive) {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await Manager(root).CreateBorrowedSnapshotAsync(
                        repo, "review", CancellationToken.None));
                await Assert.That(ex!.Message)
                    .StartsWith("borrowed_snapshot_review_context_path_collision");
            } else {
                var snapshot = await Manager(root).CreateBorrowedSnapshotAsync(
                    repo, "review", CancellationToken.None);
                try {
                    var manifest = JsonNode.Parse(
                        snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
                    await Assert.That(manifest["entries"]!.AsArray().Count).IsEqualTo(1);
                } finally { await WorktreeManager.RemoveAsync(snapshot); }
            }
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Invalid_blob_utf8_is_preserved_as_base64_without_optional_text() {
        var repo = NewGitRepo();
        var root = NewRoot();
        byte[] bytes = [0xff, 0xfe, 0x00, 0x61];
        try {
            await File.WriteAllBytesAsync(Path.Combine(repo, ".mcp.json"), bytes);
            Git(repo, "add", ".mcp.json");
            var snapshot = await Manager(root).CreateBorrowedSnapshotAsync(
                repo, "review", CancellationToken.None);
            try {
                var entry = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!
                    ["entries"]![0]!;
                await Assert.That(entry["base64"]!.GetValue<string>())
                    .IsEqualTo(Convert.ToBase64String(bytes));
                await Assert.That(entry["text"]).IsNull();
            } finally { await WorktreeManager.RemoveAsync(snapshot); }
        } finally { TryDelete(repo); TryDelete(root); }
    }

    [Test]
    public async Task Untracked_config_is_omitted_with_affirmative_empty_entries() {
        var repo = NewGitRepo();
        var root = NewRoot();
        try {
            File.WriteAllText(Path.Combine(repo, ".mcp.json"), "private untracked bytes");
            var snapshot = await Manager(root).CreateBorrowedSnapshotAsync(
                repo, "review", CancellationToken.None);
            try {
                var manifest = JsonNode.Parse(
                    snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
                await Assert.That(manifest["entries"]!.AsArray()).IsEmpty();
                await Assert.That(File.Exists(Path.Combine(
                    snapshot.SnapshotRoot!, ".mcp.json"))).IsFalse();
            } finally { await WorktreeManager.RemoveAsync(snapshot); }
        } finally { TryDelete(repo); TryDelete(root); }
    }

    static string NewGitRepo() {
        var repo = Path.Combine(Path.GetTempPath(), "kcap-review-context-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "tracked");
        Git(repo, "add", "tracked.txt");
        Git(repo, "commit", "-q", "-m", "initial");
        return repo;
    }

    static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "kcap-review-context-root-" + Guid.NewGuid().ToString("N")[..8]);

    static WorktreeManager Manager(string root) => new(
        new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

    static void Git(string cwd, params string[] args) {
        _ = GitCapture(cwd, args);
    }

    static string GitCapture(string cwd, params string[] args) {
        using var process = Process.Start(new ProcessStartInfo("git", args) {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        return process.StandardOutput.ReadToEnd();
    }

    static void GitWithInput(string cwd, byte[] input, params string[] args) {
        using var process = Process.Start(new ProcessStartInfo("git", args) {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        process.StandardInput.BaseStream.Write(input);
        process.StandardInput.Close();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
    }

    static byte[] RawIndexRecord(string oid, ReadOnlySpan<byte> pathPrefix, byte trailingByte) {
        using var bytes = new MemoryStream();
        bytes.Write(Encoding.ASCII.GetBytes($"100644 {oid} 0\t"));
        bytes.Write(pathPrefix);
        bytes.WriteByte(trailingByte);
        bytes.WriteByte(0);
        return bytes.ToArray();
    }

    static bool IsCaseSensitive(string directory) {
        var stem = "probe-" + Guid.NewGuid().ToString("N");
        var lower = Path.Combine(directory, stem + "a");
        var upper = Path.Combine(directory, stem + "A");
        try {
            File.WriteAllText(lower, "probe");
            return !File.Exists(upper);
        } finally {
            try { File.Delete(lower); } catch { }
            try { File.Delete(upper); } catch { }
        }
    }

    static void TryDelete(string path) {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
