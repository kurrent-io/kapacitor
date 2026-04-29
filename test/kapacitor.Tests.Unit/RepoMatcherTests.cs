using System.Diagnostics;
using kapacitor.Daemon;
using kapacitor.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace kapacitor.Tests.Unit;

/// <summary>
/// Behaviour tests for <see cref="RepoMatcher"/>. Each test creates a real
/// git repo with a controlled <c>origin</c> remote and asserts that
/// <see cref="RepoMatcher.FindAsync"/> returns the expected confirmed roots.
/// </summary>
public class RepoMatcherTests {
    static string MakeTempRepo(string originUrl, string? subdir = null) {
        var root = Path.Combine(Path.GetTempPath(), "kapacitor-repo-matcher-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        Run(root, "init", "-q");
        Run(root, "remote", "add", "origin", originUrl);

        if (subdir is not null) {
            var sub = Path.Combine(root, subdir);
            Directory.CreateDirectory(sub);

            return sub;
        }

        return root;
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

    static RepoMatcher NewMatcher() => new(new DaemonConfig(), NullLogger<RepoMatcher>.Instance);

    [Test]
    public async Task FindAsync_MatchingHttpsOrigin_ReturnsRoot() {
        var repo = MakeTempRepo("https://github.com/contoso/widgets.git");
        try {
            var result = await NewMatcher().FindAsync("contoso", "widgets", [repo], CancellationToken.None);

            await Assert.That(result).Count().IsEqualTo(1);
            await Assert.That(result[0]).IsEqualTo(Path.GetFullPath(repo));
        } finally { Directory.Delete(repo, true); }
    }

    [Test]
    public async Task FindAsync_MatchingSshOrigin_ReturnsRoot() {
        var repo = MakeTempRepo("git@github.com:contoso/widgets.git");
        try {
            var result = await NewMatcher().FindAsync("contoso", "widgets", [repo], CancellationToken.None);

            await Assert.That(result).Count().IsEqualTo(1);
        } finally { Directory.Delete(repo, true); }
    }

    [Test]
    public async Task FindAsync_DifferentOwner_ReturnsEmpty() {
        var repo = MakeTempRepo("https://github.com/contoso/widgets.git");
        try {
            var result = await NewMatcher().FindAsync("other-org", "widgets", [repo], CancellationToken.None);

            await Assert.That(result).IsEmpty();
        } finally { Directory.Delete(repo, true); }
    }

    [Test]
    public async Task FindAsync_CandidateInsideRepo_WalksUpToRoot() {
        var sub = MakeTempRepo("https://github.com/contoso/widgets.git", subdir: "src/Foo");
        var root = Path.GetFullPath(Path.Combine(sub, "..", ".."));
        try {
            var result = await NewMatcher().FindAsync("contoso", "widgets", [sub], CancellationToken.None);

            await Assert.That(result).Count().IsEqualTo(1);
            await Assert.That(result[0]).IsEqualTo(root);
        } finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FindAsync_MissingDirectory_Skipped() {
        var ghost = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N")[..8]);

        var result = await NewMatcher().FindAsync("contoso", "widgets", [ghost], CancellationToken.None);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task FindAsync_NonGitDirectory_Skipped() {
        var dir = Path.Combine(Path.GetTempPath(), "kapacitor-not-a-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try {
            var result = await NewMatcher().FindAsync("contoso", "widgets", [dir], CancellationToken.None);

            await Assert.That(result).IsEmpty();
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task FindAsync_DuplicateCandidatesPointingAtSameRoot_DedupedToOne() {
        var sub = MakeTempRepo("https://github.com/contoso/widgets.git", subdir: "src");
        var root = Path.GetFullPath(Path.Combine(sub, ".."));
        try {
            var result = await NewMatcher().FindAsync("contoso", "widgets", [sub, root, sub], CancellationToken.None);

            await Assert.That(result).Count().IsEqualTo(1);
        } finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FindAsync_MultipleDistinctCheckouts_ReturnsAll() {
        var repoA = MakeTempRepo("https://github.com/contoso/widgets.git");
        var repoB = MakeTempRepo("git@github.com:contoso/widgets.git");
        try {
            var result = await NewMatcher().FindAsync("contoso", "widgets", [repoA, repoB], CancellationToken.None);

            await Assert.That(result).Count().IsEqualTo(2);
            await Assert.That(result).Contains(Path.GetFullPath(repoA));
            await Assert.That(result).Contains(Path.GetFullPath(repoB));
        } finally {
            Directory.Delete(repoA, true);
            Directory.Delete(repoB, true);
        }
    }

    [Test]
    public async Task FindAsync_OwnerCaseInsensitive() {
        var repo = MakeTempRepo("https://github.com/Contoso/Widgets.git");
        try {
            var result = await NewMatcher().FindAsync("contoso", "WIDGETS", [repo], CancellationToken.None);

            await Assert.That(result).Count().IsEqualTo(1);
        } finally { Directory.Delete(repo, true); }
    }

    [Test]
    public async Task FindAsync_AllowedRepoPathsContributesCandidates() {
        var repo = MakeTempRepo("https://github.com/contoso/widgets.git");
        try {
            var config = new DaemonConfig { AllowedRepoPaths = [repo] };
            var matcher = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);

            // Pass empty server candidates — repo should still surface from AllowedRepoPaths.
            var result = await matcher.FindAsync("contoso", "widgets", [], CancellationToken.None);

            await Assert.That(result).Count().IsEqualTo(1);
        } finally { Directory.Delete(repo, true); }
    }
}
