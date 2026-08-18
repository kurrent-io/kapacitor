using System.Runtime.Versioning;
using Capacitor.Cli.Daemon.Harness.Kiro;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Kiro;

/// <summary>
/// The reviewer home is transcript-bearing — Kiro writes the review context into
/// <c>{KIRO_HOME}/sessions/cli</c> — so creation mode and disposal are security properties, and the
/// multi-daemon sweep case is the one a previous design got backwards.
/// </summary>
public class KiroReviewerHomeTests {
    [Test]
    public async Task Create_MakesAnEmptyOwnerOnlyDirectory() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The reviewer home is POSIX-only: CreateOwnerOnly refuses a platform where the transcript directory cannot be made owner-only.");

        using var tmp = new TempDir();
        var home = KiroReviewerHome.Create(tmp.Path, "epochA", "launch1");

        await Assert.That(Directory.Exists(home)).IsTrue();

        // Empty is not tidiness — it is the mechanism. A seeded settings/mcp.json would reintroduce
        // exactly the global servers this home exists to suppress.
        await Assert.That(Directory.GetFileSystemEntries(home).Length).IsEqualTo(0);

        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(home)).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task Sweep_DeletesAHomeFromAPreviousEpoch() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The reviewer home is POSIX-only: CreateOwnerOnly refuses a platform where the transcript directory cannot be made owner-only.");

        using var tmp = new TempDir();
        var stale    = KiroReviewerHome.Create(tmp.Path, "epochA", "launch1");

        KiroReviewerHome.SweepStale(tmp.Path, "epochB", NullLogger.Instance);

        await Assert.That(Directory.Exists(stale)).IsFalse();
    }

    /// <summary>
    /// The control for the sweep. Without it, an implementation that deleted the whole root would
    /// pass the test above.
    /// </summary>
    [Test]
    public async Task Sweep_KeepsAHomeFromTheCurrentEpoch() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The reviewer home is POSIX-only: CreateOwnerOnly refuses a platform where the transcript directory cannot be made owner-only.");

        using var tmp = new TempDir();
        var live     = KiroReviewerHome.Create(tmp.Path, "epochB", "launch2");

        KiroReviewerHome.SweepStale(tmp.Path, "epochB", NullLogger.Instance);

        await Assert.That(Directory.Exists(live)).IsTrue();
    }

    /// <summary>
    /// The multi-daemon case. A peer daemon's home lives under its OWN state dir with a DIFFERENT
    /// epoch — precisely what a shared-root "delete every epoch that is not mine" rule would have
    /// deleted while the peer was mid-review. Asserting a different epoch is what makes this test
    /// distinguish per-daemon roots from the broken rule; a same-epoch peer would pass either way.
    /// </summary>
    [Test]
    public async Task Sweep_NeverReachesAPeerDaemonsRoot() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The reviewer home is POSIX-only: CreateOwnerOnly refuses a platform where the transcript directory cannot be made owner-only.");

        using var tmp = new TempDir();
        var mine     = tmp.CreateDir("mine");
        var peer     = tmp.CreateDir("peer");
        var peerLive = KiroReviewerHome.Create(peer, "peerEpoch", "launch9");

        KiroReviewerHome.SweepStale(mine, "myEpoch", NullLogger.Instance);

        await Assert.That(Directory.Exists(peerLive)).IsTrue();
    }

    [Test]
    public async Task Delete_RefusesAPathOutsideTheRoot() {
        // Runs everywhere: it never calls Create, uses no symlink and reads no file mode. The
        // containment check it asserts is platform-independent, so skipping it on Windows would drop
        // real coverage for no reason.

        using var tmp = new TempDir();
        var stateDir = tmp.CreateDir("state");
        var outside  = tmp.CreateDir("outside");
        var victim   = outside.PathTo("not-ours");
        Directory.CreateDirectory(victim);

        KiroReviewerHome.Delete(victim, stateDir, NullLogger.Instance);

        await Assert.That(Directory.Exists(victim)).IsTrue();
    }

    /// <summary>
    /// Deleting must remove the home's own content but never follow a link out of it. The nested real
    /// directory is the positive control: without it, an implementation that deleted nothing at all
    /// would satisfy "the canary survived".
    /// </summary>
    [Test]
    public async Task Delete_RemovesRealContentButDoesNotFollowALinkOut() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX symlink and file-mode semantics.");

        using var tmp = new TempDir();
        var stateDir = tmp.CreateDir("state");
        var outside  = tmp.CreateDir("outside");
        var canary   = outside.PathTo("canary.txt");
        await File.WriteAllTextAsync(canary, "keep me");

        var home = KiroReviewerHome.Create(stateDir, "epochA", "launch1");

        var nested = Path.Combine(home, "sessions", "cli");
        Directory.CreateDirectory(nested);
        var transcript = Path.Combine(nested, "session.jsonl");
        await File.WriteAllTextAsync(transcript, "review context");

        Directory.CreateSymbolicLink(Path.Combine(home, "escape"), outside);

        KiroReviewerHome.Delete(home, stateDir, NullLogger.Instance);

        await Assert.That(Directory.Exists(home)).IsFalse();      // real content gone
        await Assert.That(File.Exists(transcript)).IsFalse();
        await Assert.That(File.Exists(canary)).IsTrue();          // link target untouched
    }

    /// <summary>
    /// A repeated launch under the same epoch and agent id must not inherit the previous transcript.
    /// CreateDirectory silently succeeds on an existing directory, so "empty" has to be established
    /// rather than assumed.
    /// </summary>
    [Test]
    public async Task Create_DoesNotInheritAPreviousHomesContents() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX file-mode semantics.");

        using var tmp = new TempDir();
        var first    = KiroReviewerHome.Create(tmp.Path, "epochA", "launch1");
        await File.WriteAllTextAsync(Path.Combine(first, "leftover.jsonl"), "previous review context");

        var second = KiroReviewerHome.Create(tmp.Path, "epochA", "launch1");

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(Directory.GetFileSystemEntries(second).Length).IsEqualTo(0);
    }

    /// <summary>
    /// A link planted AT the home path resolves inside the root, so the lexical containment check
    /// admits it — and enumerating it would follow it into the target. The nested-link test does not
    /// cover this: it links something BENEATH a real home.
    /// </summary>
    [Test]
    public async Task Delete_DoesNotFollowALinkAtTheHomePathItself() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX symlink semantics.");

        using var tmp = new TempDir();
        var stateDir = tmp.CreateDir("state");
        var outside  = tmp.CreateDir("outside");
        var canary   = outside.PathTo("canary.txt");
        await File.WriteAllTextAsync(canary, "keep me");

        var root = KiroReviewerHome.RootFor(stateDir);
        Directory.CreateDirectory(root);

        var impostor = Path.Combine(root, "kcap-kiro-reviewer-epochA-launch1");
        Directory.CreateSymbolicLink(impostor, outside);

        KiroReviewerHome.Delete(impostor, stateDir, NullLogger.Instance);

        await Assert.That(Path.Exists(impostor)).IsFalse();   // the routing entry is gone
        await Assert.That(File.Exists(canary)).IsTrue();      // its target is untouched
    }

    /// <summary>
    /// The mode is the protection, so a host that cannot deliver it must fail the launch rather than
    /// run a reviewer whose transcript directory others can read.
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Create_ThrowsWhenAnExistingDirectoryIsNotOwnerOnly() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX file-mode semantics.");

        using var tmp = new TempDir();
        var root     = KiroReviewerHome.RootFor(tmp.Path);
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                   UnixFileMode.UserExecute | UnixFileMode.OtherRead |
                                   UnixFileMode.OtherExecute);

        await Assert.That(() => KiroReviewerHome.Create(tmp.Path, "epochA", "launch1"))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("kiro_reviewer_home_not_owner_only");
    }
}
