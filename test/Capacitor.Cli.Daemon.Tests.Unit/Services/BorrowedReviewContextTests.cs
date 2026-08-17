using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class BorrowedReviewContextTests {
    [Test]
    [Arguments("--skip-worktree")]
    [Arguments("--assume-unchanged")]
    public async Task Snapshot_discloses_index_blob_not_private_worktree_bytes(string indexFlag) {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var indexBytes = "{\"mcpServers\":{\"branch\":{}}}"u8.ToArray();
        var privateBytes = "{\"mcpServers\":{\"private-secret\":{}}}"u8.ToArray();

        await File.WriteAllBytesAsync(repo.PathTo(".mcp.json"), indexBytes);
        Git(repo.Path, "add", ".mcp.json");
        Git(repo.Path, "commit", "-q", "-m", "add branch config");
        Git(repo.Path, "update-index", indexFlag, ".mcp.json");
        await File.WriteAllBytesAsync(repo.PathTo(".mcp.json"), privateBytes);

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root.Path }, NullLogger<WorktreeManager>.Instance);
        var snapshot = await manager.CreateBorrowedSnapshotAsync(repo.Path, "review", CancellationToken.None);

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
    }

    [Test]
    public async Task Staged_blob_is_returned_while_later_unstaged_bytes_are_omitted() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var staged = "{\"mcpServers\":{\"staged\":{}}}"u8.ToArray();
        var unstaged = "{\"mcpServers\":{\"unstaged-private\":{}}}"u8.ToArray();
        await File.WriteAllBytesAsync(repo.PathTo(".mcp.json"), staged);
        Git(repo.Path, "add", ".mcp.json");
        await File.WriteAllBytesAsync(repo.PathTo(".mcp.json"), unstaged);

        var snapshot = await Manager(root.Path).CreateBorrowedSnapshotAsync(
            repo.Path, "review", CancellationToken.None);
        try {
            var manifest = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
            var encoded = manifest["entries"]![0]!["base64"]!.GetValue<string>();
            await Assert.That(encoded).IsEqualTo(Convert.ToBase64String(staged));
            await Assert.That(encoded).IsNotEqualTo(Convert.ToBase64String(unstaged));
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Reserved_path_authored_as_a_directory_fails_closed() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        Directory.CreateDirectory(repo.PathTo(".mcp.json"));
        const string decomposedChild = "cafe\u0301";
        File.WriteAllText(repo.PathTo(".mcp.json", decomposedChild), "branch data");
        Git(repo.Path, "add", ".mcp.json/" + decomposedChild);
        Git(repo.Path, "commit", "-q", "-m", "reserved path directory");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(root.Path).CreateBorrowedSnapshotAsync(repo.Path, "review", CancellationToken.None));
        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_review_context_reserved_path_is_directory");
    }

    [Test]
    public async Task Reserved_path_authored_as_a_git_symlink_fails_closed() {
        Skip.When(OperatingSystem.IsWindows(), "Git symlink mode is covered on POSIX.");
        using var repo = NewGitRepo();
        using var root = new TempDir();
        File.CreateSymbolicLink(repo.PathTo(".mcp.json"), "tracked.txt");
        Git(repo.Path, "add", ".mcp.json");
        Git(repo.Path, "commit", "-q", "-m", "reserved path symlink");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(root.Path).CreateBorrowedSnapshotAsync(repo.Path, "review", CancellationToken.None));
        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_review_context_non_regular_mode");
    }

    [Test]
    public async Task Aggregate_capacity_accepts_exact_limit_and_declares_one_extra_byte_as_omitted() {
        using var exactRepo = NewGitRepo();
        using var overRepo = NewGitRepo();
        using var roots = new TempDir();
        var exactRoot = roots.PathTo("exact");
        var overRoot  = roots.PathTo("over");

        await File.WriteAllBytesAsync(exactRepo.PathTo(".mcp.json"), new byte[256 * 1024]);
        Git(exactRepo.Path, "add", ".mcp.json");
        var exact = await Manager(exactRoot).CreateBorrowedSnapshotAsync(
            exactRepo.Path, "review", CancellationToken.None);
        try {
            var manifest = JsonNode.Parse(exact.ReviewContextGeneration!.JsonUtf8)!.AsObject();
            await Assert.That(manifest["entries"]![0]!["byteCount"]!.GetValue<long>())
                .IsEqualTo(256L * 1024);
            await Assert.That(manifest["omittedForCapacity"]!.AsArray()).IsEmpty();
        } finally { await WorktreeManager.RemoveAsync(exact); }

        // One byte past the cap no longer refuses the launch: the build succeeds, the config
        // stays out of the executable tree, and the manifest declares the omission by path,
        // size and hash so the reviewer can flag what it could not read.
        var overBytes = new byte[256 * 1024 + 1];
        overBytes[0] = (byte)'x';
        await File.WriteAllBytesAsync(overRepo.PathTo(".mcp.json"), overBytes);
        Git(overRepo.Path, "add", ".mcp.json");
        var expectedOid = GitCapture(overRepo.Path, "rev-parse", ":.mcp.json").Trim();
        var over = await Manager(overRoot).CreateBorrowedSnapshotAsync(
            overRepo.Path, "review", CancellationToken.None);
        try {
            await Assert.That(File.Exists(Path.Combine(over.SnapshotRoot!, ".mcp.json"))).IsFalse();
            var manifest = JsonNode.Parse(over.ReviewContextGeneration!.JsonUtf8)!.AsObject();
            await Assert.That(manifest["entries"]!.AsArray()).IsEmpty();
            var omitted = manifest["omittedForCapacity"]!.AsArray();
            await Assert.That(omitted.Count).IsEqualTo(1);
            var record = omitted[0]!.AsObject();
            await Assert.That(record["path"]!.GetValue<string>()).IsEqualTo(".mcp.json");
            await Assert.That(record["indexMode"]!.GetValue<string>()).IsEqualTo("100644");
            await Assert.That(record["blobObjectId"]!.GetValue<string>()).IsEqualTo(expectedOid);
            await Assert.That(record["byteCount"]!.GetValue<long>()).IsEqualTo(256L * 1024 + 1);
            await Assert.That(record["sha256"]!.GetValue<string>())
                .IsEqualTo(Convert.ToHexString(SHA256.HashData(overBytes)).ToLowerInvariant());
            await Assert.That(record["base64"]).IsNull();
            await Assert.That(record["text"]).IsNull();
        } finally { await WorktreeManager.RemoveAsync(over); }
    }

    [Test]
    public async Task Omitted_oversized_config_does_not_consume_capacity_needed_by_later_configs() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        // `.cursor/mcp.json` sorts before `.mcp.json` in the index, so the oversized blob is
        // considered first — a later, small config must still be admitted in full.
        var oversized = new byte[256 * 1024 + 1];
        var small = "{\"mcpServers\":{\"small\":{}}}"u8.ToArray();
        Directory.CreateDirectory(repo.PathTo(".cursor"));
        await File.WriteAllBytesAsync(repo.PathTo(".cursor", "mcp.json"), oversized);
        await File.WriteAllBytesAsync(repo.PathTo(".mcp.json"), small);
        Git(repo.Path, "add", ".cursor/mcp.json", ".mcp.json");

        var snapshot = await Manager(root.Path).CreateBorrowedSnapshotAsync(
            repo.Path, "review", CancellationToken.None);
        try {
            var manifest = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
            var entries = manifest["entries"]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);
            await Assert.That(entries[0]!["path"]!.GetValue<string>()).IsEqualTo(".mcp.json");
            await Assert.That(entries[0]!["base64"]!.GetValue<string>())
                .IsEqualTo(Convert.ToBase64String(small));
            var omitted = manifest["omittedForCapacity"]!.AsArray();
            await Assert.That(omitted.Count).IsEqualTo(1);
            await Assert.That(omitted[0]!["path"]!.GetValue<string>()).IsEqualTo(".cursor/mcp.json");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Refresh_after_config_grows_past_capacity_succeeds_and_declares_omission() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var small = "{\"mcpServers\":{\"initial\":{}}}"u8.ToArray();
        await File.WriteAllBytesAsync(repo.PathTo(".mcp.json"), small);
        Git(repo.Path, "add", ".mcp.json");
        var manager = Manager(root.Path);
        var snapshot = await manager.CreateBorrowedSnapshotAsync(
            repo.Path, "review", CancellationToken.None);
        try {
            // A between-rounds refresh with a config grown past the cap must not fail — a
            // throw here is what used to terminate a live reviewer mid-flow.
            await File.WriteAllBytesAsync(
                repo.PathTo(".mcp.json"), new byte[256 * 1024 + 1]);
            Git(repo.Path, "add", ".mcp.json");

            var generation = await manager.SyncBorrowedSnapshotFromSourceAsync(
                repo.Path, snapshot.SnapshotRoot!, snapshot.GitRelativeCwd!, [],
                snapshot.ReviewContextRoot!, CancellationToken.None);
            await Assert.That(File.Exists(Path.Combine(snapshot.SnapshotRoot!, ".mcp.json")))
                .IsFalse();
            var manifest = JsonNode.Parse(generation.JsonUtf8)!.AsObject();
            await Assert.That(manifest["entries"]!.AsArray()).IsEmpty();
            var omitted = manifest["omittedForCapacity"]!.AsArray();
            await Assert.That(omitted.Count).IsEqualTo(1);
            await Assert.That(omitted[0]!["path"]!.GetValue<string>()).IsEqualTo(".mcp.json");
            await Assert.That(omitted[0]!["byteCount"]!.GetValue<long>())
                .IsEqualTo(256L * 1024 + 1);
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Manifest_validation_rejects_malformed_or_out_of_set_omissions() {
        var validOmission = new BorrowedReviewContextOmission(
            ".mcp.json", "100644", new string('a', 40), 1, new string('b', 64));
        var matched = new HashSet<string>(StringComparer.Ordinal) { ".mcp.json" };

        WorktreeManager.ValidateReviewContextManifest(
            OmissionManifest(validOmission), "g", "h", matched);

        foreach (var (label, omission, paths) in new (string, BorrowedReviewContextOmission, IReadOnlySet<string>)[] {
            ("outside matched set", validOmission, new HashSet<string>(StringComparer.Ordinal) { "other" }),
            ("zero byte count", validOmission with { ByteCount = 0 }, matched),
            ("invalid mode", validOmission with { IndexMode = "120000" }, matched),
            ("invalid object id", validOmission with { BlobObjectId = new string('0', 40) }, matched),
            ("uppercase sha", validOmission with { Sha256 = new string('B', 64) }, matched),
            ("truncated sha", validOmission with { Sha256 = new string('b', 63) }, matched),
        }) {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                WorktreeManager.ValidateReviewContextManifest(
                    OmissionManifest(omission), "g", "h", paths));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_invalid_manifest")
                .Because(label);
        }

        // A path may appear as shipped content or as an omission, never both.
        var entry = new BorrowedReviewContextEntry(
            ".mcp.json", "100644", new string('a', 40), 0,
            Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), "", "");
        var dup = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.ValidateReviewContextManifest(
                OmissionManifest(validOmission) with { Entries = [entry] }, "g", "h", matched));
        await Assert.That(dup!.Message)
            .StartsWith("borrowed_snapshot_review_context_invalid_manifest");

        // Every matched path must be REPRESENTED, not merely every representation matched: a
        // manifest that lost a record between write and read-back would otherwise validate — and
        // an empty one now reads as an affirmative all-clear to the reviewer.
        var incomplete = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.ValidateReviewContextManifest(
                OmissionManifest(validOmission), "g", "h",
                new HashSet<string>(StringComparer.Ordinal) { ".mcp.json", ".cursor/mcp.json" }));
        await Assert.That(incomplete!.Message)
            .StartsWith("borrowed_snapshot_review_context_invalid_manifest");
    }

    static BorrowedReviewContextManifest OmissionManifest(BorrowedReviewContextOmission omission) =>
        new(1, "g", "h", "git-index-stage-0", WorkingTreeBytes: false,
            UnstagedAndUntrackedOmitted: true, "-", [], [omission]);

    [Test]
    public async Task Matching_unmerged_index_entry_fails_closed() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var oid = GitCapture(repo.Path, "rev-parse", "HEAD:tracked.txt").Trim();
        GitWithInput(repo.Path,
            Encoding.ASCII.GetBytes(
                $"100644 {oid} 1\t.mcp.json\n" +
                $"100644 {oid} 2\t.mcp.json\n" +
                $"100644 {oid} 3\t.mcp.json\n"),
            "update-index", "--index-info");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(root.Path).CreateBorrowedSnapshotAsync(
                repo.Path, "review", CancellationToken.None));
        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_review_context_unmerged_index");
    }

    [Test]
    public async Task Invalid_utf8_under_reserved_path_fails_but_unrelated_invalid_utf8_is_ignored() {
        Skip.When(OperatingSystem.IsWindows(), "Raw non-UTF8 Git paths are covered on POSIX.");
        using var badRepo = NewGitRepo();
        using var unrelatedRepo = NewGitRepo();
        using var roots = new TempDir();
        var badRoot       = roots.PathTo("bad");
        var unrelatedRoot = roots.PathTo("unrelated");

        var badOid = GitCapture(badRepo.Path, "rev-parse", "HEAD:tracked.txt").Trim();
        GitWithInput(badRepo.Path,
            RawIndexRecord(badOid, ".mcp.json/"u8, 0xff),
            "update-index", "-z", "--index-info");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(badRoot).CreateBorrowedSnapshotAsync(
                badRepo.Path, "review", CancellationToken.None));
        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_review_context_invalid_path_encoding");

        var expected = "{\"mcpServers\":{}}"u8.ToArray();
        await File.WriteAllBytesAsync(unrelatedRepo.PathTo(".mcp.json"), expected);
        Git(unrelatedRepo.Path, "add", ".mcp.json");
        var unrelatedOid = GitCapture(unrelatedRepo.Path, "rev-parse", "HEAD:tracked.txt").Trim();
        GitWithInput(unrelatedRepo.Path,
            RawIndexRecord(unrelatedOid, "unrelated-"u8, 0xff),
            "update-index", "-z", "--index-info");
        var snapshot = await Manager(unrelatedRoot).CreateBorrowedSnapshotAsync(
            unrelatedRepo.Path, "review", CancellationToken.None);
        try {
            var manifest = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
            await Assert.That(manifest["entries"]!.AsArray().Count).IsEqualTo(1);
            await Assert.That(manifest["entries"]![0]!["base64"]!.GetValue<string>())
                .IsEqualTo(Convert.ToBase64String(expected));
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Matching_gitlink_reports_non_regular_context_failure() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var commit = GitCapture(repo.Path, "rev-parse", "HEAD").Trim();
        GitWithInput(repo.Path,
            Encoding.ASCII.GetBytes($"160000 {commit} 0\t.mcp.json\n"),
            "update-index", "--index-info");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(root.Path).CreateBorrowedSnapshotAsync(
                repo.Path, "review", CancellationToken.None));
        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_review_context_non_regular_mode");
    }

    [Test]
    public async Task Regular_mode_entry_whose_object_is_not_a_blob_fails_closed() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var commit = GitCapture(repo.Path, "rev-parse", "HEAD").Trim();
        GitWithInput(repo.Path,
            Encoding.ASCII.GetBytes($"100644 {commit} 0\t.mcp.json\n"),
            "update-index", "--index-info");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(root.Path).CreateBorrowedSnapshotAsync(
                repo.Path, "review", CancellationToken.None));
        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_review_context_non_blob_object");
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
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var external = Directory.CreateTempSubdirectory("kcap-review-context-external-").FullName;
        try {
            File.WriteAllText(Path.Combine(external, "sentinel"), "keep-me");
            var snapshots = root.PathTo("borrowed-snapshots");
            Directory.CreateDirectory(snapshots);
            var sidecar = WorktreeManager.ReviewContextRootFor(
                Path.Combine(snapshots, "review"));
            Directory.CreateSymbolicLink(sidecar, external);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root.Path).CreateBorrowedSnapshotAsync(
                    repo.Path, "review", CancellationToken.None));

            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_unsafe_storage_path");
            await Assert.That(File.ReadAllText(Path.Combine(external, "sentinel")))
                .IsEqualTo("keep-me");
        } finally {
            TryDelete(external);
        }
    }

    [Test]
    public async Task Linked_borrowed_snapshots_parent_fails_without_writing_outside_worktree_root() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var external = Directory.CreateTempSubdirectory("kcap-review-parent-external-").FullName;
        try {
            File.WriteAllText(Path.Combine(external, "sentinel"), "keep-me");
            Directory.CreateSymbolicLink(
                root.PathTo("borrowed-snapshots"), external);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root.Path).CreateBorrowedSnapshotAsync(
                    repo.Path, "review", CancellationToken.None));

            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_unsafe_storage_path");
            await Assert.That(Directory.GetFileSystemEntries(external).Select(p => Path.GetFileName(p)))
                .IsEquivalentTo(["sentinel"]);
            await Assert.That(File.ReadAllText(Path.Combine(external, "sentinel")))
                .IsEqualTo("keep-me");
        } finally {
            // Remove the symlink before TempDir.Dispose recurses into root, so cleanup never
            // has to reason about whether it would follow the link into external.
            try { Directory.Delete(root.PathTo("borrowed-snapshots")); } catch { }
            TryDelete(external);
        }
    }

    [Test]
    public async Task Case_collisions_follow_the_actual_destination_filesystem_semantics() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        var oid = GitCapture(repo.Path, "rev-parse", "HEAD:tracked.txt").Trim();
        GitWithInput(repo.Path, Encoding.ASCII.GetBytes(
            $"100644 {oid} 0\t.mcp.json\n100644 {oid} 0\t.MCP.JSON\n"),
            "update-index", "--index-info");

        var caseSensitive = IsCaseSensitive(root.Path);
        if (!caseSensitive) {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Manager(root.Path).CreateBorrowedSnapshotAsync(
                    repo.Path, "review", CancellationToken.None));
            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_review_context_path_collision");
        } else {
            var snapshot = await Manager(root.Path).CreateBorrowedSnapshotAsync(
                repo.Path, "review", CancellationToken.None);
            try {
                var manifest = JsonNode.Parse(
                    snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
                await Assert.That(manifest["entries"]!.AsArray().Count).IsEqualTo(1);
            } finally { await WorktreeManager.RemoveAsync(snapshot); }
        }
    }

    [Test]
    public async Task Invalid_blob_utf8_is_preserved_as_base64_without_optional_text() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        byte[] bytes = [0xff, 0xfe, 0x00, 0x61];
        await File.WriteAllBytesAsync(repo.PathTo(".mcp.json"), bytes);
        Git(repo.Path, "add", ".mcp.json");
        var snapshot = await Manager(root.Path).CreateBorrowedSnapshotAsync(
            repo.Path, "review", CancellationToken.None);
        try {
            var entry = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!
                ["entries"]![0]!;
            await Assert.That(entry["base64"]!.GetValue<string>())
                .IsEqualTo(Convert.ToBase64String(bytes));
            await Assert.That(entry["text"]).IsNull();
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Untracked_config_is_omitted_with_affirmative_empty_entries() {
        using var repo = NewGitRepo();
        using var root = new TempDir();
        File.WriteAllText(repo.PathTo(".mcp.json"), "private untracked bytes");
        var snapshot = await Manager(root.Path).CreateBorrowedSnapshotAsync(
            repo.Path, "review", CancellationToken.None);
        try {
            var manifest = JsonNode.Parse(
                snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
            await Assert.That(manifest["entries"]!.AsArray()).IsEmpty();
            await Assert.That(manifest["omittedForCapacity"]!.AsArray()).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(
                snapshot.SnapshotRoot!, ".mcp.json"))).IsFalse();
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    static TempDir NewGitRepo() {
        var repo = new TempDir();
        Git(repo.Path, "init", "-q");
        Git(repo.Path, "config", "user.email", "test@example.com");
        Git(repo.Path, "config", "user.name", "Test");
        File.WriteAllText(repo.PathTo("tracked.txt"), "tracked");
        Git(repo.Path, "add", "tracked.txt");
        Git(repo.Path, "commit", "-q", "-m", "initial");
        return repo;
    }

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
