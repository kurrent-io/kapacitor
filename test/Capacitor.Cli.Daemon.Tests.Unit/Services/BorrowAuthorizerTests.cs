using System.Diagnostics;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Behaviour tests for <see cref="BorrowAuthorizer"/> — the daemon-side gate deciding whether a
/// cwd may be borrowed (a read-only reviewer run in it). Uses real temp dirs, real symlinks, and
/// real <c>git init</c> repos so the canonicalization + git-root + allowlist policy is exercised
/// against the actual filesystem rather than mocks.
/// </summary>
public class BorrowAuthorizerTests {
    [Test]
    public async Task Absent_path_is_not_allowed_with_path_absent_reason() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-borrow-missing-" + Guid.NewGuid().ToString("N")[..8]);

        var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(missing);

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Reason).IsEqualTo("path_absent");
        await Assert.That(result.CanonicalCwd).IsNull();
        await Assert.That(result.CanonicalGitRoot).IsNull();
    }

    [Test]
    public async Task GitRooted_cwd_with_empty_allowlist_is_allowed() {
        using var repo = MakeTempRepo();
        var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(repo.Path);

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.CanonicalGitRoot).IsEqualTo(BorrowAuthorizer.Canonicalize(repo.Path));
    }

    [Test]
    public async Task NonRepo_cwd_with_empty_allowlist_is_not_allowed() {
        var tmp = Directory.CreateTempSubdirectory("kcap-borrow-norepo-");
        try {
            var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(tmp.FullName);

            await Assert.That(result.Allowed).IsFalse();
            await Assert.That(result.Reason).IsEqualTo("not_allowed");
            await Assert.That(result.CanonicalGitRoot).IsNull();
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task NonRepo_cwd_matching_explicit_allowlist_is_allowed() {
        var tmp = Directory.CreateTempSubdirectory("kcap-borrow-norepo-allow-");
        try {
            var canonical  = BorrowAuthorizer.Canonicalize(tmp.FullName);
            var authorizer = new BorrowAuthorizer(new DaemonConfig { AllowedRepoPaths = [canonical] });

            var result = await authorizer.AuthorizeBorrowAsync(tmp.FullName);

            await Assert.That(result.Allowed).IsTrue();
            await Assert.That(result.Reason).IsNull();
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Symlinked_cwd_resolving_into_allowed_git_root_is_allowed() {
        using var repo = MakeTempRepo();
        var linkParent = Directory.CreateTempSubdirectory("kcap-borrow-link-");
        var link       = Path.Combine(linkParent.FullName, "link-to-repo");
        try {
            Directory.CreateSymbolicLink(link, repo.Path);

            var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(link);

            await Assert.That(result.Allowed).IsTrue();
            await Assert.That(result.CanonicalCwd).IsEqualTo(BorrowAuthorizer.Canonicalize(repo.Path));
        } finally {
            linkParent.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Symlinked_cwd_escaping_nonempty_allowlist_is_not_allowed() {
        var allowedRoot = Directory.CreateTempSubdirectory("kcap-borrow-allowed-");
        var outsideRoot = Directory.CreateTempSubdirectory("kcap-borrow-outside-");
        var link        = Path.Combine(allowedRoot.FullName, "escape-link");
        try {
            Directory.CreateSymbolicLink(link, outsideRoot.FullName);

            var authorizer = new BorrowAuthorizer(
                new DaemonConfig { AllowedRepoPaths = [BorrowAuthorizer.Canonicalize(allowedRoot.FullName)] }
            );

            var result = await authorizer.AuthorizeBorrowAsync(link);

            await Assert.That(result.Allowed).IsFalse();
            await Assert.That(result.Reason).IsEqualTo("not_allowed");
        } finally {
            // Remove the symlink itself first so recursive delete never has to reason about
            // whether it would otherwise be followed into outsideRoot. Directory.Delete removes the
            // reparse point cross-platform (File.Delete throws UnauthorizedAccessException on a
            // Windows directory symlink).
            Directory.Delete(link);
            allowedRoot.Delete(recursive: true);
            outsideRoot.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Ancestor_symlink_escaping_nonempty_allowlist_is_not_allowed() {
        // Allowlisted tree: allowedRoot/*. Inside it, an ANCESTOR dir is a symlink pointing OUT of
        // the tree; the leaf (cwd) is a real, non-symlink dir under that ancestor. A leaf-only
        // canonicalization would leave the path textually under allowedRoot and wrongly allow it.
        var allowedRoot  = Directory.CreateTempSubdirectory("kcap-borrow-anc-allowed-");
        var outsideRoot  = Directory.CreateTempSubdirectory("kcap-borrow-anc-outside-");
        var realLeaf     = Path.Combine(outsideRoot.FullName, "x");
        var linkAncestor = Path.Combine(allowedRoot.FullName, "linkdir");
        try {
            Directory.CreateDirectory(realLeaf);
            Directory.CreateSymbolicLink(linkAncestor, outsideRoot.FullName);

            var cwd = Path.Combine(linkAncestor, "x"); // allowedRoot/linkdir/x → outsideRoot/x

            var authorizer = new BorrowAuthorizer(
                new DaemonConfig { AllowedRepoPaths = [BorrowAuthorizer.Canonicalize(allowedRoot.FullName) + "/*"] }
            );

            var result = await authorizer.AuthorizeBorrowAsync(cwd);

            await Assert.That(result.Allowed).IsFalse();
            await Assert.That(result.Reason).IsEqualTo("not_allowed");
            await Assert.That(result.CanonicalCwd).IsEqualTo(BorrowAuthorizer.Canonicalize(realLeaf));
        } finally {
            Directory.Delete(linkAncestor); // remove symlink before recursing into the roots
            allowedRoot.Delete(recursive: true);
            outsideRoot.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Ancestor_symlink_resolving_into_allowed_root_is_allowed() {
        // The reverse: an ancestor symlink that resolves INTO the allowlisted tree is allowed, and
        // CanonicalCwd is the resolved real path (not the link path).
        var allowedRoot  = Directory.CreateTempSubdirectory("kcap-borrow-anc-in-allowed-");
        var linkParent   = Directory.CreateTempSubdirectory("kcap-borrow-anc-in-link-");
        var realLeaf     = Path.Combine(allowedRoot.FullName, "proj", "x");
        var linkAncestor = Path.Combine(linkParent.FullName, "to-allowed");
        try {
            Directory.CreateDirectory(realLeaf);
            Directory.CreateSymbolicLink(linkAncestor, allowedRoot.FullName);

            var cwd = Path.Combine(linkAncestor, "proj", "x"); // linkParent/to-allowed/proj/x → allowedRoot/proj/x

            var authorizer = new BorrowAuthorizer(
                new DaemonConfig { AllowedRepoPaths = [BorrowAuthorizer.Canonicalize(allowedRoot.FullName) + "/*"] }
            );

            var result = await authorizer.AuthorizeBorrowAsync(cwd);

            await Assert.That(result.Allowed).IsTrue();
            await Assert.That(result.CanonicalCwd).IsEqualTo(BorrowAuthorizer.Canonicalize(realLeaf));
        } finally {
            Directory.Delete(linkAncestor); // remove symlink before recursing into the roots
            linkParent.Delete(recursive: true);
            allowedRoot.Delete(recursive: true);
        }
    }

    static TempDir MakeTempRepo() {
        var repo = new TempDir();
        Run(repo.Path, "init", "-q");

        return repo;
    }

    static void Run(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }
}
