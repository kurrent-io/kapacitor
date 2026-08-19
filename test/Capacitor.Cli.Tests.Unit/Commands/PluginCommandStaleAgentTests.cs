using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The wiring: `plugin install --pi` names an already-running session, and only on a first install.
/// </summary>
/// <remarks>
/// The detector's own tests are a pure function of injected delegates, so they stay green whether or
/// not anything calls it. These pin the call site, and the re-install case in particular — dating
/// staleness from the installed file's mtime looked right and told every long-installed user their
/// working session was uncaptured, on every re-run and on every npm upgrade.
/// </remarks>
[NotInParallel("HomeEnvVarMutation")]
internal sealed class PluginCommandStaleAgentTests {
    static readonly StaleAgentProcess Running = new("pi", 4821, "/home/dev/gaffer");

    [Test]
    public async Task A_first_install_names_a_session_that_was_already_running() {
        using var path = new KcapOnPath();
        using var home = new FakeUserHome();
        using var pipe = new StringWriter();

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--pi-extension-path", Path.Combine(home.Path, "kcap.ts")],
            Env(home.Path, pipe, found: [Running]));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(pipe.ToString()).Contains("4821");
    }

    [Test]
    public async Task Re_installing_over_an_existing_extension_says_nothing() {
        using var path = new KcapOnPath();
        using var home = new FakeUserHome();
        using var pipe = new StringWriter();

        var extension = Path.Combine(home.Path, "kcap.ts");
        PiExtensionInstaller.Install(extension);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--pi-extension-path", extension],
            Env(home.Path, pipe, found: [Running]));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(pipe.ToString())
                    .DoesNotContain("4821")
                    .Because("the session loaded the extension when it started — it is captured, and "
                           + "saying otherwise on every re-run and every npm upgrade would be noise "
                           + "that is also untrue");
    }

    [Test]
    public async Task A_first_install_with_nothing_running_says_nothing() {
        using var path = new KcapOnPath();
        using var home = new FakeUserHome();
        using var pipe = new StringWriter();

        await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--pi-extension-path", Path.Combine(home.Path, "kcap.ts")],
            Env(home.Path, pipe, found: []));

        await Assert.That(pipe.ToString().Contains("already running", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The fresh install refuses unless `kcap` resolves — the extension it writes invokes it.</summary>
    sealed class KcapOnPath : IDisposable {
        readonly TempDir  _bin = new();
        readonly EnvScope _path;

        public KcapOnPath() {
            var exe = _bin.CreateFile("kcap");

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            _bin.CreateFile("kcap.exe");
            _path = new EnvScope("PATH", _bin.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }

        public void Dispose() {
            _path.Dispose();
            _bin.Dispose();
        }
    }

    static PluginEnvironment Env(string home, TextWriter stdout, StaleAgentProcess[] found) => new(
        HomeDirectory:     home,
        ResolvePluginPath: () => null,
        Stdout:            stdout,
        Stderr:            TextWriter.Null
    ) {
        ResolveMcpBinaryPath = () => "/usr/local/bin/kcap",
        // Never the real process table: what a CI box happens to be running must not decide a result.
        FindStaleAgents      = _ => found,
    };
}
