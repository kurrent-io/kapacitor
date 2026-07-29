using System.Diagnostics;
using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The OS read boundary around a borrowed reviewer.
///
/// <para>Split deliberately into two kinds of test. The profile-shape tests are pure and run
/// everywhere; they pin what is granted and — much more importantly — what is not. The enforcement
/// tests actually run <c>sandbox-exec</c> and read real files, because a profile that parses and
/// grants the right-looking paths but does not in fact stop an outside read would satisfy every
/// string assertion while shipping the hole.</para>
/// </summary>
public class BorrowedReviewSandboxTests {
    const string Snapshot = "/snapshots/borrowed-abc";
    const string StateRoot = "/snapshots/borrowed-abc.vendor-state";

    static string Profile(string snapshot = Snapshot, string stateRoot = StateRoot,
                          IReadOnlyList<string>? runtimeReads = null) =>
        BorrowedReviewSandbox.BuildProfile(snapshot, stateRoot, runtimeReads ?? []);

    [Test]
    public async Task The_profile_denies_by_default_and_grants_the_snapshot() {
        var profile = Profile();

        await Assert.That(profile).Contains("(deny default)");
        await Assert.That(profile).Contains($"(subpath \"{Snapshot}\")");
    }

    /// <summary>The per-launch state root is the reviewer's whole HOME for this launch, so it must be
    /// writable — the vendor creates its profile and caches there on first run.</summary>
    [Test]
    public async Task The_profile_grants_the_per_launch_state_root_for_read_and_write() {
        var profile = Profile();
        var write   = profile[profile.IndexOf("(allow file-write*", StringComparison.Ordinal)..];

        await Assert.That(profile).Contains($"(subpath \"{StateRoot}\")");
        await Assert.That(write).Contains($"(subpath \"{StateRoot}\")");
    }

    /// <summary>The grants that were the whole reason borrowed review shipped disabled. Each is
    /// data-bearing and each was reachable with NO ACP interaction frame, so the <c>Fail</c> policy
    /// never fired and only the OS boundary stood there.
    ///
    /// <para>Home is not granted in any form now — not even as a literal. An earlier profile pointed
    /// the vendor at the user's real home and had to stat it; the per-launch state root replaced
    /// that.</para></summary>
    [Test]
    [Arguments("/Users/someone")]
    [Arguments("/Users/someone/.copilot")]
    [Arguments("/Users/someone/Library/Keychains")]
    [Arguments("/Users/someone/Library/Caches/copilot")]
    [Arguments("/Library")]
    [Arguments("/opt/homebrew")]
    public async Task The_profile_never_grants_a_previously_granted_data_bearing_tree(string forbidden) {
        var profile = Profile();

        await Assert.That(profile).DoesNotContain($"(subpath \"{forbidden}\")");
        await Assert.That(profile).DoesNotContain($"(literal \"{forbidden}\")");
    }

    /// <summary>A broad user-directory grant makes the reviewer's reachable surface the user's entire
    /// account — credentials, other repositories, browser state. Checked independently of the list
    /// above because these were never granted and must not become so.</summary>
    [Test]
    [Arguments("/Users/someone/Documents")]
    [Arguments("/Users/someone/.ssh")]
    [Arguments("/Users/someone/.aws")]
    [Arguments("/Users/someone/.config")]
    public async Task The_profile_never_grants_a_broad_user_directory(string forbidden) {
        await Assert.That(Profile()).DoesNotContain($"(subpath \"{forbidden}\")");
    }

    /// <summary>Write access is exactly two trees. The previous profile also granted all of
    /// <c>/private/var/folders</c> and <c>/dev</c>; redirecting <c>TMPDIR</c> into the state root
    /// removed the need, and a probe confirmed the vendor still starts without them.</summary>
    [Test]
    public async Task Write_access_is_limited_to_the_snapshot_and_the_state_root() {
        var profile = Profile();
        var write   = profile[profile.IndexOf("(allow file-write*", StringComparison.Ordinal)..];

        await Assert.That(write).DoesNotContain("/private/var/folders");
        await Assert.That(write).DoesNotContain("(subpath \"/dev\")");
        await Assert.That(write).DoesNotContain("(subpath \"/usr\")");
    }

    /// <summary>Narrowed from unqualified <c>network*</c>, which also permitted inbound and bind. The
    /// reviewer calls the vendor's API and has no reason to listen.</summary>
    [Test]
    public async Task Network_access_is_outbound_only() {
        var profile = Profile();

        await Assert.That(profile).Contains("(allow network-outbound)");
        await Assert.That(profile).DoesNotContain("(allow network*)");
    }

    /// <summary>Unqualified <c>mach-lookup</c> was only ever needed to reach the keychain; brokered
    /// authentication removed the reason, and a live run completed an authenticated <c>session/new</c>
    /// without it.</summary>
    [Test]
    public async Task The_profile_does_not_grant_unqualified_mach_lookup() {
        await Assert.That(Profile()).DoesNotContain("(allow mach-lookup)");
    }

    /// <summary>Runtime roots are passed in, not assumed, so the profile carries whatever
    /// <see cref="BorrowedReviewRuntimeRoots"/> resolved and nothing else.</summary>
    [Test]
    public async Task Runtime_read_roots_are_granted_read_but_not_write() {
        var profile = Profile(runtimeReads: ["/opt/homebrew/lib", "/opt/homebrew/Cellar"]);
        var read    = profile[profile.IndexOf("(allow file-read*", StringComparison.Ordinal)
                              ..profile.IndexOf("(allow file-write*", StringComparison.Ordinal)];
        var write   = profile[profile.IndexOf("(allow file-write*", StringComparison.Ordinal)..];

        await Assert.That(read).Contains("(subpath \"/opt/homebrew/lib\")");
        await Assert.That(read).Contains("(subpath \"/opt/homebrew/Cellar\")");
        await Assert.That(write).DoesNotContain("/opt/homebrew");
    }

    /// <summary>A quote or backslash in an interpolated path must not be able to close its own
    /// <c>(subpath "...")</c> form and append grants of its own. Daemon-owned paths are tame today;
    /// this is what keeps that from being load-bearing.</summary>
    [Test]
    public async Task A_snapshot_path_containing_profile_syntax_is_escaped_not_interpolated() {
        var profile = Profile(snapshot: """/snap/a"))(allow file-read* (subpath "/""");

        await Assert.That(profile).DoesNotContain("\"))(allow file-read* (subpath \"/\")");
        await Assert.That(profile).Contains("\\\"");
    }

    /// <summary>Same escaping, on the runtime roots — they are derived from a filesystem walk rather
    /// than from a daemon-chosen name, so they are the likelier source of an odd character.</summary>
    [Test]
    public async Task A_runtime_root_containing_profile_syntax_is_escaped_not_interpolated() {
        var profile = Profile(runtimeReads: ["""/opt/x"))(allow file-write* (subpath "/"""]);

        await Assert.That(profile).DoesNotContain("\"))(allow file-write* (subpath \"/\")");
        await Assert.That(profile).Contains("\\\"");
    }

    /// <summary>The profile writer itself refuses to be drawn at a filesystem root.
    ///
    /// <para>Belt and braces behind <see cref="BorrowedReviewRuntimeRoots"/>'s own exclusion, and worth
    /// having at both layers because the failure is total and quiet: <c>(subpath "/")</c> grants the
    /// whole machine while the profile still parses, the vendor still starts, and every named-tree
    /// assertion in this file still passes. The two daemon-chosen paths throw rather than falling back,
    /// because a root there means something upstream is badly wrong.</para></summary>
    [Test]
    [Arguments("/", StateRoot)]
    [Arguments(Snapshot, "/")]
    public async Task The_profile_refuses_to_be_drawn_at_a_filesystem_root(string snapshot, string stateRoot) {
        var ex = Assert.Throws<ArgumentException>(() =>
            BorrowedReviewSandbox.BuildProfile(snapshot, stateRoot, []));

        await Assert.That(ex.Message).Contains("filesystem root");
    }

    /// <summary>A runtime root of <c>/</c> is DROPPED rather than throwing — those are derived from a
    /// filesystem walk, and the launch failing loudly at exec is the safe outcome. Dropping must not
    /// take the legitimate grants with it.</summary>
    [Test]
    public async Task A_filesystem_root_among_the_runtime_reads_is_dropped() {
        var profile = Profile(runtimeReads: ["/", "/opt/hb/lib"]);
        var read    = profile[profile.IndexOf("(allow file-read*", StringComparison.Ordinal)
                              ..profile.IndexOf("(allow file-write*", StringComparison.Ordinal)];

        await Assert.That(read).DoesNotContain("(subpath \"/\")");
        await Assert.That(read).Contains("(subpath \"/opt/hb/lib\")");
    }

    [Test]
    public async Task WrapArgv_runs_the_real_binary_under_the_profile() {
        var argv = BorrowedReviewSandbox.WrapArgv("(version 1)", "/opt/bin/copilot", ["--acp", "--stdio"]);

        await Assert.That(argv.ToArray()).IsEquivalentTo(
            ["-p", "(version 1)", "/opt/bin/copilot", "--acp", "--stdio"]);
    }

    /// <summary>The state root is a SIBLING of the snapshot, never inside it. Inside, a per-round
    /// refresh would destroy the running vendor's state and hand the reviewer its own profile as
    /// content under review.</summary>
    [Test]
    public async Task The_state_directories_sit_outside_the_snapshot() {
        var stateRoot = Capacitor.Cli.Daemon.Services.WorktreeManager.VendorStateRootFor(Snapshot);
        var home      = BorrowedReviewSandbox.HomeDirectoryIn(stateRoot);
        var temp      = BorrowedReviewSandbox.TempDirectoryIn(stateRoot);

        await Assert.That(home.StartsWith(Snapshot + Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsFalse();
        await Assert.That(temp.StartsWith(Snapshot + Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsFalse();
        await Assert.That(home).IsNotEqualTo(temp);
    }

    // ── enforcement: what the profile DOES, not what it says ────────────────────────────────────

    /// <summary>The claim itself, executed. Everything above asserts what the profile SAYS; this
    /// asserts what it DOES — a read inside the snapshot succeeds and a read outside it fails.</summary>
    [Test]
    public async Task Enforcement_a_read_inside_the_snapshot_succeeds_and_outside_fails() {
        Skip.Unless(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && BorrowedReviewSandbox.Available,
                    "needs macOS with sandbox-exec");

        var root    = Directory.CreateTempSubdirectory("kcap-sandbox-test").FullName;
        var inside  = Path.Combine(root, "snapshot");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(outside);

        var insideFile  = Path.Combine(inside,  "in.txt");
        var outsideFile = Path.Combine(outside, "out.txt");
        await File.WriteAllTextAsync(insideFile,  "INSIDE-OK");
        await File.WriteAllTextAsync(outsideFile, "OUTSIDE-LEAKED");

        var profile = RealisticProfileFor(inside);

        await Assert.That(await CatUnderSandboxAsync(profile, insideFile)).IsEqualTo("INSIDE-OK");
        await Assert.That(await CatUnderSandboxAsync(profile, outsideFile)).IsNull();

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// One enforcement sentinel per tree the previous profile granted, asserting each is unreadable
    /// <b>now that the profile is what a real launch would use</b>.
    ///
    /// <para><b>Why <c>/bin/cat</c> rather than a reviewer.</b> The earlier evidence for these trees
    /// was a live Copilot run in which the agent declined to read the keychain on its own initiative.
    /// That is a model-layer refusal and it is not containment evidence — it holds only while the build
    /// keeps choosing to refuse, which is precisely the vendor-dependence this sandbox exists to
    /// remove. <c>cat</c> never asks and never refuses, so it models the drifted build that silently
    /// accepts an out-of-bounds path. A live run additionally confirmed the equivalent reads fail with
    /// the permission request explicitly GRANTED.</para>
    ///
    /// <para>Each case is skipped when the path is absent on the host, so the assertion is never
    /// vacuously satisfied by a missing file.</para>
    /// </summary>
    [Test]
    [Arguments("keychain",           "Library/Keychains/login.keychain-db")]
    [Arguments("vendor state",       ".copilot/config.json")]
    [Arguments("vendor history",     ".copilot/command-history-state.json")]
    [Arguments("ssh private key",    ".ssh/id_ed25519")]
    [Arguments("aws credentials",    ".aws/credentials")]
    [Arguments("gh credentials",     ".config/gh/hosts.yml")]
    public async Task Enforcement_a_previously_granted_home_tree_is_unreadable(string what, string relative) {
        Skip.Unless(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && BorrowedReviewSandbox.Available,
                    "needs macOS with sandbox-exec");

        var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = Path.Combine(home, relative);
        Skip.Unless(File.Exists(target), $"{what} not present on this host: {target}");

        var snapshot = Directory.CreateTempSubdirectory("kcap-sandbox-sentinel").FullName;
        try {
            await Assert.That(await CatUnderSandboxAsync(RealisticProfileFor(snapshot), target))
                .IsNull().Because($"a borrowed reviewer must not be able to read the {what}");
        } finally {
            Directory.Delete(snapshot, recursive: true);
        }
    }

    /// <summary>The runtime prefix's config and data trees. These are the reason the whole-prefix
    /// grant was replaced by software subdirectories: on an ordinary developer machine
    /// <c>var</c> holds service databases and logs and <c>etc</c> holds service configuration.</summary>
    [Test]
    [Arguments("prefix config", "/opt/homebrew/etc")]
    [Arguments("prefix data",   "/opt/homebrew/var")]
    [Arguments("system library","/Library/Preferences")]
    public async Task Enforcement_a_previously_granted_system_tree_is_unlistable(string what, string directory) {
        Skip.Unless(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && BorrowedReviewSandbox.Available,
                    "needs macOS with sandbox-exec");
        Skip.Unless(Directory.Exists(directory), $"{what} not present on this host: {directory}");

        var snapshot = Directory.CreateTempSubdirectory("kcap-sandbox-sentinel").FullName;
        try {
            // `ls` on a granted directory exits 0; on a denied one it cannot open it and exits non-zero.
            await Assert.That(await RunUnderSandboxAsync(RealisticProfileFor(snapshot), "/bin/ls", directory))
                .IsNull().Because($"a borrowed reviewer must not be able to enumerate {what}");
        } finally {
            Directory.Delete(snapshot, recursive: true);
        }
    }

    /// <summary>A profile shaped like a real launch's: the runtime roots a real spawn would resolve
    /// for the configured vendor binary, so an enforcement result cannot be an artifact of a
    /// deliberately thin test profile. Asserting containment against a profile that grants less than
    /// production is how a sentinel passes while the shipped boundary leaks.</summary>
    static string RealisticProfileFor(string snapshot) =>
        BorrowedReviewSandbox.BuildProfile(
            snapshot,
            Capacitor.Cli.Daemon.Services.WorktreeManager.VendorStateRootFor(snapshot),
            BorrowedReviewRuntimeRoots.Resolve(ResolveVendorBinary()));

    /// <summary>The real Copilot binary when present, else any plausible interpreter, so the resolved
    /// runtime roots are those of an actual installation.</summary>
    static string ResolveVendorBinary() {
        foreach (var candidate in new[] { "/opt/homebrew/bin/copilot", "/usr/local/bin/copilot",
                                          "/opt/homebrew/bin/node", "/usr/bin/env" })
            if (File.Exists(candidate)) return candidate;

        return "/usr/bin/env";
    }

    /// <summary>Returns the file's contents, or null if the sandbox refused the read.</summary>
    static Task<string?> CatUnderSandboxAsync(string profile, string path) =>
        RunUnderSandboxAsync(profile, "/bin/cat", path);

    static async Task<string?> RunUnderSandboxAsync(string profile, string program, string path) {
        var psi = new ProcessStartInfo(
            BorrowedReviewSandbox.SandboxExecPath,
            BorrowedReviewSandbox.WrapArgv(profile, program, [path])) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0 ? stdout.Trim() : null;
    }
}
