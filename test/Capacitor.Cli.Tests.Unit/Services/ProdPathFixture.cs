using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>HOME/lock-dir isolation and a stubbed launchd manager (every <c>launchctl print</c>
/// reports the unit not found) shared by the real-<see cref="LaunchdServiceManager"/> production-path
/// suites — each test arranges only its own on-disk plist evidence before touching
/// <see cref="Manager"/> or <see cref="PlistPath"/>.</summary>
sealed class ProdPathFixture : IDisposable {
    readonly string _id;
    readonly string? _originalHome;
    readonly string _home;
    readonly string _lockDir;

    public string PlistPath => LaunchdUnit.PlistPath(_id);
    public string DaemonPath { get; }

    public LaunchdServiceManager Manager { get; }

    public ProdPathFixture(string id) {
        _id = id;
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _home = Directory.CreateTempSubdirectory($"kcap-{id}-home-").FullName;
        Environment.SetEnvironmentVariable("HOME", _home);

        _lockDir = Directory.CreateTempSubdirectory($"kcap-{id}-lock-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(_lockDir);

        DaemonPath = Path.Combine(_lockDir, "kcap-daemon");
        File.WriteAllText(DaemonPath, "");

        Manager = new(
            runProcess: (_, args) => PrintNotFound(args),
            runBounded: (_, args, _) => {
                var (code, stdout, stderr) = PrintNotFound(args);
                return (code, stdout, stderr, false);
            });
    }

    (int ExitCode, string StdOut, string StdErr) PrintNotFound(string[] args) =>
        args[0] == "print"
            ? (113, "", $"Could not find service \"{LaunchdUnit.Label(_id)}\" in domain for user gui: 501")
            : (0, "", "");

    public void Dispose() {
        DaemonLockPaths.OverrideDirectoryForTesting(null);
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }
}
