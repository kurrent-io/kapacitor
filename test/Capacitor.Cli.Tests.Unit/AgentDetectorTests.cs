using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

// Exercises the CLI shim end-to-end against the real process environment. The pure
// PATH-walk algorithm itself now lives in Capacitor.Cli.Core.Setup.AgentDetection and is
// covered by test/Capacitor.Cli.Tests.Unit/Setup/AgentDetectionTests.cs.
public class AgentDetectorTests {
    // PATH_env_mutation serialises tests that blank or replace PATH. Any future test in
    // any class that reads PATH during execution must share this token, otherwise it
    // can observe a transient null/empty PATH while these tests run.
    [Test, NotInParallel("PATH_env_mutation")]
    public async Task Public_returns_false_when_path_env_is_empty() {
        var original = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try {
            await Assert.That(AgentDetector.IsInstalled("anything-at-all")).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Test, NotInParallel("PATH_env_mutation")]
    public async Task Public_returns_false_when_path_env_is_null() {
        var original = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", null);
        try {
            await Assert.That(AgentDetector.IsInstalled("anything-at-all")).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Test, NotInParallel("PATH_env_mutation")]
    public async Task Public_unix_requires_any_execute_bit() {
        if (OperatingSystem.IsWindows()) return; // Unix-only

        using var tmp     = new TempDir();
        var       exec    = Path.Combine(tmp.Path, "agentprobe-exec");
        var       nonExec = Path.Combine(tmp.Path, "agentprobe-nonexec");

        await File.WriteAllTextAsync(exec, "#!/bin/sh\nexit 0\n");
        await File.WriteAllTextAsync(nonExec, "not executable");
        File.SetUnixFileMode(exec,    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);   // 0700
        File.SetUnixFileMode(nonExec, UnixFileMode.UserRead | UnixFileMode.UserWrite);                              // 0600

        var original = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tmp.Path);
        try {
            await Assert.That(AgentDetector.IsInstalled("agentprobe-exec")).IsTrue();
            await Assert.That(AgentDetector.IsInstalled("agentprobe-nonexec")).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }
}

file sealed class TempDir : IDisposable {
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"kcap-test-{Guid.NewGuid().ToString("N")[..8]}"
    );
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}
