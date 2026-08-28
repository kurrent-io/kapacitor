using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The wiring: `plugin install --kiro` names an already-running session, and only on a first install.
/// </summary>
/// <remarks>
/// The detector's own tests are a pure function of injected delegates, so they stay green whether or
/// not anything calls it. These pin the call site, and the re-install case in particular — dating
/// staleness from the installed file's mtime looked right and told every long-installed user their
/// working session was uncaptured, on every re-run and on every npm upgrade.
/// <para>
/// Every passing case seeds the agent JSON, because a genuine clone shells out to kiro-cli. The
/// marker is what separates the two shapes: a file without one is kcap's first install, a file with
/// one is a re-install.
/// </para>
/// </remarks>
// PATH: the fresh-install precheck resolves `kcap` through it, and so does every peer that
// probes for a vendor CLI.
[NotInParallel("VendorEnvOverrides")]
public sealed class PluginCommandStaleAgentTests {
    static readonly StaleAgentProcess Running = new("kiro", 4821, "/home/dev/gaffer");

    [Test]
    public async Task A_first_install_names_a_session_that_was_already_running() {
        using var onPath   = new KcapOnPath();
        using var home     = new TempHome();
        using var pipe     = new StringWriter();
        var env = Env(home.Path, pipe, found: [Running]);
        SeedAgent(env, installed: false);

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(pipe.ToString()).Contains("4821");
    }

    [Test]
    public async Task Re_installing_over_an_existing_agent_says_nothing() {
        using var onPath   = new KcapOnPath();
        using var home     = new TempHome();
        using var pipe     = new StringWriter();
        var env = Env(home.Path, pipe, found: [Running]);
        SeedAgent(env, installed: true);

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(pipe.ToString())
                    .DoesNotContain("4821")
                    .Because("the session loaded the agent when it started — it is captured, and "
                           + "saying otherwise on every re-run and every npm upgrade would be noise "
                           + "that is also untrue");
    }

    [Test]
    public async Task A_first_install_with_nothing_running_says_nothing() {
        using var onPath   = new KcapOnPath();
        using var home     = new TempHome();
        using var pipe     = new StringWriter();
        var env = Env(home.Path, pipe, found: []);
        SeedAgent(env, installed: false);

        await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro"]);

        await Assert.That(pipe.ToString()).DoesNotContain("already running");
    }

    [Test]
    public async Task An_install_that_failed_claims_nothing_about_future_sessions() {
        using var onPath   = new KcapOnPath();
        using var home     = new TempHome();
        using var pipe     = new StringWriter();
        var env = Env(home.Path, pipe, found: [Running]);

        // A directory where the agent JSON belongs: the clone can't write it, the command exits
        // non-zero, and live capture was never installed — so naming a session that "isn't being
        // captured" would be true but useless, and blaming this install for it would be a lie.
        Directory.CreateDirectory(env.Paths.Kiro.KcapAgentJson);

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(pipe.ToString()).DoesNotContain("4821");
    }

    // A real first install clones the user's default agent with kiro-cli, which isn't on a test box.
    // Seeding the file skips the clone (InstallKiroHooks only shells out when it's absent) and leaves
    // the rest of the install — hook injection, default flip, marker — running for real.
    static void SeedAgent(PluginEnvironment env, bool installed) {
        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.Kiro.KcapAgentJson)!);
        File.WriteAllText(env.Paths.Kiro.KcapAgentJson, """{"name":"kcap","hooks":{}}""");

        if (installed) KiroHooksInstaller.WriteMarker(env.Paths.Kiro.KcapAgentJson, "kiro_default");
    }

    /// <summary>The fresh install refuses unless `kcap` resolves — the agent it writes invokes it.</summary>
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
        Home:     new(home),
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => null,
        Stdout:            stdout,
        Stderr:            TextWriter.Null
    ) {
        Paths = TestHarnessPaths.NoOverrides(new(home)),
        ResolveMcpBinaryPath = () => "/usr/local/bin/kcap",
        // Never the real process table: what a CI box happens to be running must not decide a result.
        FindStaleAgents      = _ => found,
    };
}
