using System.Diagnostics;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class PathShimInstallerTests {
    sealed class FakeProcessRunner : IProcessRunner {
        public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
        Func<Task<ProcessResult>> _step = () => Task.FromResult(new ProcessResult(0, "", "", false));

        public void Enqueue(ProcessResult result) => _step = () => Task.FromResult(result);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args, options));
            return _step();
        }
    }

    static string TempDestination(string dirName) =>
        Path.Combine(Directory.CreateTempSubdirectory("kcap-shim-").FullName, dirName);

    // --- Preflight ---

    [Test]
    public async Task Preflight_absent_destination_is_installable() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        var dest = TempDestination("kcap");
        var target = TempDestination("target-cli");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Installable);
    }

    [Test]
    public async Task Preflight_symlink_to_target_is_already_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var target = Path.Combine(dir, "target-cli");
        File.WriteAllText(target, "cli");
        var dest = Path.Combine(dir, "kcap");
        File.CreateSymbolicLink(dest, target);

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.AlreadyInstalled);
    }

    [Test]
    public async Task Preflight_symlink_to_elsewhere_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var target = Path.Combine(dir, "target-cli");
        File.WriteAllText(target, "cli");
        var elsewhere = Path.Combine(dir, "elsewhere-cli");
        File.WriteAllText(elsewhere, "other");
        var dest = Path.Combine(dir, "kcap");
        File.CreateSymbolicLink(dest, elsewhere);

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_regular_file_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var target = Path.Combine(dir, "target-cli");
        var dest = Path.Combine(dir, "kcap");
        File.WriteAllText(dest, "not a symlink");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_directory_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var target = Path.Combine(dir, "target-cli");
        var dest = Path.Combine(dir, "kcap");
        Directory.CreateDirectory(dest);

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_broken_symlink_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var target = Path.Combine(dir, "target-cli");
        var dest = Path.Combine(dir, "kcap");
        File.CreateSymbolicLink(dest, Path.Combine(dir, "does-not-exist"));

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

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var dest = Path.Combine(dir, "kcap");
        var target = Path.Combine(dir, "target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "execution error: User canceled. (-128)", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Cancelled);
    }

    [Test]
    public async Task InstallAsync_other_failure_reports_the_exact_sudo_fallback() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var dest = Path.Combine(dir, "kcap");
        var target = Path.Combine(dir, "target-cli");
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

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var dest = Path.Combine(dir, "kcap");
        var target = Path.Combine(dir, "target-cli");
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

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var dest = Path.Combine(dir, "kcap");
        var target = Path.Combine(dir, "target-cli");
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

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var dest = Path.Combine(dir, "kcap");
        var target = Path.Combine(dir, "target-cli");
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

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var dest = Path.Combine(dir, "kcap");
        var target = Path.Combine(dir, "target-cli");
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

        var dir = Directory.CreateTempSubdirectory("kcap-shim-").FullName;
        var dest = Path.Combine(dir, "kcap");
        var target = Path.Combine(dir, "target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    // Drives InstallAsync against an arbitrary temp `destination` (never the real
    // /usr/local/bin/kcap) via the internal destination-overriding overload.
    static Task<ShimResult> Install(PathShimInstaller installer, string destination, string target, CancellationToken ct) =>
        installer.InstallAsync(target, destination, ct);
}
