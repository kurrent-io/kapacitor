using System.Diagnostics;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Both branches of the URL failure policy, each in an isolated child process.
///
/// <para>This cannot be an in-process test: the <see cref="UrlFailurePolicy.FailFast"/> branch calls
/// <c>Environment.Exit(2)</c>, which would terminate the test runner. The child host is the same
/// mechanism the PDEATHSIG / FailFast tests use, for the same reason.</para>
///
/// <para>Both cases are driven with an absolute wrong-scheme URL (<c>ftp://host</c>) rather than a
/// scheme-less one. That is the class that discriminates: an implementation validating only
/// <c>UriKind.Absolute</c> accepts <c>ftp://host</c> while still violating the invariant.</para>
/// </summary>
public class ProcessUrlPolicyTests {
    [Test]
    public async Task FailFast_policy_prints_the_hint_and_exits_2() {
        var (exit, stdout, stderr) = await RunHostAsync("url-policy-failfast");

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(stderr).Contains("server_url is missing a scheme");
        // If the policy regressed to non-exiting, the host reaches its own marker instead.
        await Assert.That(stdout).DoesNotContain("NO-EXIT");
    }

    [Test]
    public async Task Throw_policy_raises_UnusableServerUrlException_without_exiting() {
        var (exit, stdout, _) = await RunHostAsync("url-policy-throw");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).IsEqualTo("THREW");
    }

    [Test]
    public async Task Default_policy_is_FailFast() {
        // Guards the default: flipping it would silently change every interactive command.
        await Assert.That(ProcessUrlPolicy.Current).IsEqualTo(UrlFailurePolicy.FailFast);
    }

    static async Task<(int Exit, string Stdout, string Stderr)> RunHostAsync(string mode) {
        var psi = new ProcessStartInfo("dotnet", $"\"{ResolveNativeHostDll()}\" {mode}") {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        using var host = Process.Start(psi) ?? throw new InvalidOperationException("failed to start NativeTestHost");

        var stdout = await host.StandardOutput.ReadToEndAsync();
        var stderr = await host.StandardError.ReadToEndAsync();
        await host.WaitForExitAsync();

        return (host.ExitCode, stdout, stderr);
    }

    // Sibling-project resolution, mirroring UnixSpawnerThreadTests.ResolveNativeHostDll.
    static string ResolveNativeHostDll() {
        var dir      = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm      = Path.GetFileName(dir);
        var config   = Path.GetFileName(Path.GetDirectoryName(dir)!);
        var testRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        var hostDll  = Path.Combine(testRoot, "Capacitor.Cli.Tests.Unit.NativeTestHost", "bin", config, tfm,
            "Capacitor.Cli.Tests.Unit.NativeTestHost.dll");

        if (!File.Exists(hostDll))
            throw new InvalidOperationException($"NativeTestHost not built at {hostDll} — build Capacitor.slnx first");

        return hostDll;
    }
}
