using System.Diagnostics;
using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The OS read boundary around a borrowed reviewer.
///
/// <para>Split deliberately into two kinds of test. The profile-shape tests are pure and run
/// everywhere; they pin what is granted and — much more importantly — what is not. The enforcement
/// test actually runs <c>sandbox-exec</c> and reads two sentinels, because a profile that parses and
/// grants the right-looking paths but does not in fact stop an outside read would satisfy every
/// string assertion while shipping the hole.</para>
/// </summary>
public class BorrowedReviewSandboxTests {
    static string Home => "/Users/someone";

    [Test]
    public async Task The_profile_denies_by_default_and_grants_the_snapshot() {
        var profile = BorrowedReviewSandbox.BuildProfile("/snapshots/borrowed-abc", Home);

        await Assert.That(profile).Contains("(deny default)");
        await Assert.That(profile).Contains("(subpath \"/snapshots/borrowed-abc\")");
    }

    /// <summary>The one grant that would silently undo the whole thing. A broad home-directory read
    /// makes the reviewer's reachable surface the user's entire account — credentials, other repos,
    /// browser state — which is the exfiltration this profile exists to stop. Home appears only as a
    /// literal (the runtime stats it), never as a subpath.</summary>
    [Test]
    public async Task The_profile_never_grants_the_home_directory_as_a_subpath() {
        var profile = BorrowedReviewSandbox.BuildProfile("/snapshots/borrowed-abc", Home);

        await Assert.That(profile).DoesNotContain($"(subpath \"{Home}\")");
        await Assert.That(profile).Contains($"(literal \"{Home}\")");
    }

    /// <summary>Vendor state is granted at the directory that holds it, not at its parent. Granting
    /// <c>~/Library</c> or <c>~/.config</c> wholesale would readmit most of what the previous test
    /// rules out, by a different route.</summary>
    [Test]
    [Arguments("/Users/someone/Library")]
    [Arguments("/Users/someone/.config")]
    [Arguments("/Users/someone/Documents")]
    [Arguments("/Users/someone/.ssh")]
    [Arguments("/Users/someone/.aws")]
    public async Task The_profile_never_grants_a_broad_user_directory(string forbidden) {
        var profile = BorrowedReviewSandbox.BuildProfile("/snapshots/borrowed-abc", Home);

        await Assert.That(profile).DoesNotContain($"(subpath \"{forbidden}\")");
    }

    /// <summary>A quote or backslash in the snapshot path must not be able to close its own
    /// <c>(subpath "...")</c> form and append grants of its own. Daemon-owned paths are tame today;
    /// this is what keeps that from being load-bearing.</summary>
    [Test]
    public async Task A_snapshot_path_containing_profile_syntax_is_escaped_not_interpolated() {
        var profile = BorrowedReviewSandbox.BuildProfile("""/snap/a"))(allow file-read* (subpath "/""", Home);

        await Assert.That(profile).DoesNotContain("\"))(allow file-read* (subpath \"/\")");
        await Assert.That(profile).Contains("\\\"");
    }

    [Test]
    public async Task WrapArgv_runs_the_real_binary_under_the_profile() {
        var argv = BorrowedReviewSandbox.WrapArgv("(version 1)", "/opt/bin/copilot", ["--acp", "--stdio"]);

        await Assert.That(argv.ToArray()).IsEquivalentTo(
            ["-p", "(version 1)", "/opt/bin/copilot", "--acp", "--stdio"]);
    }

    /// <summary>The claim itself, executed. Everything above asserts what the profile SAYS; this
    /// asserts what it DOES — a read inside the snapshot succeeds and a read outside it fails.
    ///
    /// <para>Without this, a profile that parsed cleanly and listed all the right paths but got a
    /// containment rule subtly wrong would pass every other test in this file while shipping exactly
    /// the hole the sandbox was added to close.</para></summary>
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

        var profile = BorrowedReviewSandbox.BuildProfile(
            inside, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        await Assert.That(await CatUnderSandboxAsync(profile, insideFile)).IsEqualTo("INSIDE-OK");
        await Assert.That(await CatUnderSandboxAsync(profile, outsideFile)).IsNull();

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Returns the file's contents, or null if the sandbox refused the read.</summary>
    static async Task<string?> CatUnderSandboxAsync(string profile, string path) {
        var psi = new ProcessStartInfo(
            BorrowedReviewSandbox.SandboxExecPath,
            BorrowedReviewSandbox.WrapArgv(profile, "/bin/cat", [path])) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0 ? stdout.Trim() : null;
    }
}
