using System.Diagnostics;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit;

public class PathShimInstallerTests {
    sealed class FakeProcessRunner : IProcessRunner {
        public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
        Func<Task<ProcessResult>> _step = () => Task.FromResult(new ProcessResult(0, "", "", false));

        public void Enqueue(ProcessResult result) => _step = () => Task.FromResult(result);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args, options));
            return _step();
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotImplementedException();
    }

    // --- Preflight ---

    [Test]
    public async Task Preflight_absent_destination_is_installable() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Installable);
    }

    [Test]
    public async Task Preflight_symlink_to_target_is_already_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        var dest = tmp.PathTo("kcap");
        File.CreateSymbolicLink(dest, target);

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.AlreadyInstalled);
    }

    [Test]
    public async Task Preflight_symlink_to_elsewhere_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        var elsewhere = tmp.PathTo("elsewhere-cli");
        File.WriteAllText(elsewhere, "other");
        var dest = tmp.PathTo("kcap");
        File.CreateSymbolicLink(dest, elsewhere);

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_regular_file_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        var dest = tmp.PathTo("kcap");
        File.WriteAllText(dest, "not a symlink");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_directory_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        var dest = tmp.CreateDir("kcap");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_broken_symlink_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        var dest = tmp.PathTo("kcap");
        File.CreateSymbolicLink(dest, tmp.PathTo("does-not-exist"));

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    // --- OsascriptArgs ---

    [Test]
    public async Task OsascriptArgs_passes_the_target_as_the_last_raw_argv_element() {
        var args = PathShimInstaller.OsascriptArgs("/App Space/kcap");

        await Assert.That(args[^1]).IsEqualTo("/App Space/kcap");
        await Assert.That(args[^2]).IsEqualTo("--");
    }

    [Test]
    public async Task OsascriptArgs_script_never_interpolates_the_target() {
        var args = PathShimInstaller.OsascriptArgs("/App Space/kcap");
        // Everything before the "--" separator is the AppleScript source; the target lives only
        // in the argv element after it (asserted separately above).
        var separator = Array.IndexOf(args, "--");
        var scriptSource = string.Join('\n', args[..separator]);

        await Assert.That(scriptSource).Contains("quoted form of item 1 of argv");
        await Assert.That(scriptSource).Contains("mkdir -p /usr/local/bin");
        await Assert.That(scriptSource).Contains("ln -s ");
        await Assert.That(scriptSource).DoesNotContain("-f");
        await Assert.That(scriptSource).DoesNotContain("/App Space/kcap");
    }

    // --- PosixQuote ---

    [Test]
    public async Task PosixQuote_escapes_single_quotes() {
        await Assert.That(PathShimInstaller.PosixQuote("a'b")).IsEqualTo("'a'\"'\"'b'");
    }

    [Test]
    public async Task PosixQuote_round_trips_through_a_real_shell() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX shell");

        const string input = "a'b \"c\" \\d";
        var quoted = PathShimInstaller.PosixQuote(input);

        var psi = new ProcessStartInfo("/bin/sh", ["-c", $"printf %s {quoted}"]) {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(output).IsEqualTo(input);
    }

    // --- LooksLikeTarget ---

    [Test]
    public async Task LooksLikeTarget_rejects_newline() {
        await Assert.That(PathShimInstaller.LooksLikeTarget("/usr/local/bin/ev\nil")).IsFalse();
    }

    [Test]
    public async Task LooksLikeTarget_rejects_carriage_return() {
        await Assert.That(PathShimInstaller.LooksLikeTarget("/usr/local/bin/ev\ril")).IsFalse();
    }

    [Test]
    public async Task LooksLikeTarget_accepts_a_plain_path() {
        await Assert.That(PathShimInstaller.LooksLikeTarget("/App Space/kcap")).IsTrue();
    }

    // --- InstallAsync ---

    [Test]
    public async Task InstallAsync_cancel_is_detected_via_negative_128_in_stderr() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "execution error: User canceled. (-128)", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Cancelled);
    }

    // Regression: a genuine failure's shell error text can embed the (unescaped, by the shell's
    // OWN error reporting — not our argv passing) target path verbatim. A target path that just
    // happens to contain the bare substring "-128" (e.g. a PR/build-numbered directory) must not
    // be misread as AppleScript's "(-128)" cancellation marker — that would silently discard the
    // real failure's Detail and SudoFallback.
    [Test]
    public async Task InstallAsync_bare_negative_128_substring_without_parens_is_not_treated_as_cancel() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("app-128", "kcap");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", $"ln: {target}: Permission denied", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.SudoFallback).IsEqualTo(
            "sudo mkdir -p /usr/local/bin && sudo ln -s " + PathShimInstaller.PosixQuote(target) + " /usr/local/bin/kcap");
    }

    [Test]
    public async Task InstallAsync_other_failure_reports_the_exact_sudo_fallback() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "administrator privileges denied", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.SudoFallback).IsEqualTo(
            "sudo mkdir -p /usr/local/bin && sudo ln -s " + PathShimInstaller.PosixQuote(target) + " /usr/local/bin/kcap");
    }

    [Test]
    public async Task InstallAsync_success_then_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(true) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
    }

    [Test]
    public async Task InstallAsync_success_then_probe_false_is_installed_but_not_on_path() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(false) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.InstalledButNotOnPath);
    }

    [Test]
    public async Task InstallAsync_success_then_probe_null_is_installed_but_not_on_path() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(null) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.InstalledButNotOnPath);
    }

    [Test]
    public async Task InstallAsync_rejects_a_target_with_a_newline_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await installer.InstallAsync("/usr/local/bin/ev\nil", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task InstallAsync_conflict_preflight_fails_without_calling_the_runner() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(dest, "pre-existing, not a symlink");
        var runner = new FakeProcessRunner();
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task InstallAsync_already_installed_preflight_succeeds_without_calling_the_runner() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(true) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    // Regression: AlreadyInstalled used to short-circuit straight to Installed on the symlink
    // alone (spec §5 forbids exactly that — "never report success on the symlink alone"). The
    // symlink resolving to our target says nothing about whether the login shell's PATH actually
    // includes /usr/local/bin, so AlreadyInstalled must run the SAME post-install probe the
    // freshly-linked branch does.
    [Test]
    public async Task InstallAsync_already_installed_then_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(true) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    // Regression: ShimOfferCoordinator's offer decision already consumed
    // KcapOnPathAsync (caching its "absent" answer) before ever calling InstallAsync — the
    // post-install probe must not just replay that stale cached answer.
    [Test]
    public async Task InstallAsync_success_stale_cached_false_but_fresh_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(false), // the stale, already-cached pre-install answer
            KcapOnPathFreshBehavior = _ => Task.FromResult<bool?>(true), // the real post-install state
        };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(probe.KcapOnPathForceRefreshCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task InstallAsync_already_installed_stale_cached_false_but_fresh_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(false),
            KcapOnPathFreshBehavior = _ => Task.FromResult<bool?>(true),
        };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(runner.Calls).IsEmpty();
        await Assert.That(probe.KcapOnPathForceRefreshCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task InstallAsync_already_installed_then_probe_false_is_installed_but_not_on_path() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(false) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.InstalledButNotOnPath);
        await Assert.That(runner.Calls).IsEmpty();
    }

    // Drives InstallAsync against an arbitrary temp `destination` (never the real
    // /usr/local/bin/kcap) via the internal destination-overriding overload.
    static Task<ShimResult> Install(PathShimInstaller installer, string destination, string target, CancellationToken ct) =>
        installer.InstallAsync(target, destination, ct);
}
