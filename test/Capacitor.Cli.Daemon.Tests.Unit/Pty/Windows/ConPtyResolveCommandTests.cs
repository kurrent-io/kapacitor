using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Windows;

/// <summary>
/// Regression for the hosted-launch <c>CreateProcessW failed: 193</c> failure. The ConPty
/// command resolver must prefer <c>codex.cmd</c> over npm's extensionless Git-Bash
/// <c>#!/bin/sh</c> twin, and flag it as a <c>.cmd</c> so <c>Spawn</c> wraps it in
/// <c>cmd.exe /c</c>. A bare extensionless script handed straight to <c>CreateProcessW</c>
/// fails with 193 (not a valid Win32 application) — which parked hosted Codex launches on
/// Windows at "Waiting for session to start…".
/// </summary>
public class ConPtyResolveCommandTests {
    [Test, NotInParallel]
    public async Task ResolveCommand_prefers_cmd_over_extensionless_twin_on_path() {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var name = $"kcap-conptyprobe-{Guid.NewGuid():N}";
        tmp.CreateFile(name, "#!/bin/sh\nexit 0\n"); // shim twin
        var cmd = tmp.PathTo(name + ".cmd");
        await File.WriteAllTextAsync(cmd, "@echo off\r\n");

        var       savedPath = Environment.GetEnvironmentVariable("PATH");
        using var pathEnv   = EnvScope.Exclusive("PATH", $"{tmp.Path}{Path.PathSeparator}{savedPath}");

        var (resolved, isCmd) = ConPtyProcess.ResolveCommand(name);

        await Assert.That(resolved).IsEqualTo(cmd, StringComparison.OrdinalIgnoreCase);
        await Assert.That(isCmd).IsTrue(); // drives the cmd.exe /c wrapper in Spawn
    }

    /// <summary>A plain <c>.exe</c> still resolves and is NOT flagged as a <c>.cmd</c>, so it
    /// runs directly without a needless <c>cmd.exe</c> wrapper.</summary>
    [Test, NotInParallel]
    public async Task ResolveCommand_finds_exe_and_marks_not_cmd() {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var name = $"kcap-conptyexe-{Guid.NewGuid():N}";
        var exe  = tmp.PathTo(name + ".exe");
        await File.WriteAllTextAsync(exe, "");

        var       savedPath = Environment.GetEnvironmentVariable("PATH");
        using var pathEnv   = EnvScope.Exclusive("PATH", $"{tmp.Path}{Path.PathSeparator}{savedPath}");

        var (resolved, isCmd) = ConPtyProcess.ResolveCommand(name);

        await Assert.That(resolved).IsEqualTo(exe, StringComparison.OrdinalIgnoreCase);
        await Assert.That(isCmd).IsFalse();
    }
}
