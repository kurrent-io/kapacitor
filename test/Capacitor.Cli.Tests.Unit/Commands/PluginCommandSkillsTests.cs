using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginCommandSkillsTests {
    [Test]
    public async Task Install_with_both_codex_and_skills_flags_returns_error() {
        using var tmp = new TempDir();
        var capturedErr = new StringWriter();
        var exit = await new PluginCommand(TestEnv(fakeHome: tmp.Path, stderr: capturedErr)).HandleAsync(
            ["plugin", "install", "--codex", "--skills"]);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capturedErr.ToString()).Contains("mutually exclusive");
    }

    [Test]
    public async Task Remove_with_both_codex_and_skills_flags_returns_error() {
        using var tmp = new TempDir();
        var capturedErr = new StringWriter();
        var exit = await new PluginCommand(TestEnv(fakeHome: tmp.Path, stderr: capturedErr)).HandleAsync(
            ["plugin", "remove", "--codex", "--skills"]);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capturedErr.ToString()).Contains("mutually exclusive");
    }

    [Test]
    public async Task Install_skills_writes_to_agents_dir_and_cleans_legacy() {
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();

        var skillsSrc = pluginRoot.CreateDir("skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            skillsSrc.CreateDir(name);
            skillsSrc.CreateFile([name, "SKILL.md"],
                $"---\nname: {name}\n---\nbody");
        }

        var legacyDir = fakeHome.CreateDir(".codex", "skills");
        legacyDir.CreateDir("kcap-recap");

        var exit = await new PluginCommand(TestEnv(fakeHome.Path, pluginRoot.Path)).HandleAsync(
            ["plugin", "install", "--skills"]);
        await Assert.That(exit).IsEqualTo(0);

        var target = fakeHome.PathTo(".agents", "skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            await Assert.That(Directory.Exists(Path.Combine(target, $"kcap-{name}"))).IsTrue();
        }
        await Assert.That(Directory.Exists(legacyDir.PathTo("kcap-recap"))).IsFalse();
    }

    [Test]
    public async Task Install_skills_with_if_installed_is_noop_when_marker_absent() {
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();

        // Seed a valid plugin source — proves the gate short-circuits
        // BEFORE attempting any work, not because the source is invalid.
        var skillsSrc = pluginRoot.CreateDir("skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            skillsSrc.CreateDir(name);
            skillsSrc.CreateFile([name, "SKILL.md"],
                $"---\nname: {name}\n---\nbody");
        }

        var exit = await new PluginCommand(TestEnv(fakeHome.Path, pluginRoot.Path)).HandleAsync(
            ["plugin", "install", "--skills", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        // No marker existed → installer must not have run.
        var target = fakeHome.PathTo(".agents", "skills");
        await Assert.That(Directory.Exists(target)).IsFalse();
    }

    [Test]
    public async Task Install_skills_with_if_installed_refreshes_when_marker_present() {
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();

        var skillsSrc = pluginRoot.CreateDir("skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            skillsSrc.CreateDir(name);
            skillsSrc.CreateFile([name, "SKILL.md"],
                $"---\nname: {name}\ndescription: fresh\n---\nfresh body");
        }

        // Pre-seed marker (simulating a prior install).
        var target = fakeHome.CreateDir(".agents", "skills");
        target.CreateFile(AgentsSkillsInstaller.MarkerFileName,
            "old-version");

        var exit = await new PluginCommand(TestEnv(fakeHome.Path, pluginRoot.Path)).HandleAsync(
            ["plugin", "install", "--skills", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        // Skills must be present after refresh.
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            await Assert.That(Directory.Exists(target.PathTo($"kcap-{name}"))).IsTrue();
        }

        // Marker must have been overwritten with the current assembly version.
        var currentMarker = await File.ReadAllTextAsync(target.PathTo(AgentsSkillsInstaller.MarkerFileName));
        await Assert.That(currentMarker.Trim()).IsNotEqualTo("old-version");
    }

    [Test]
    public async Task Install_skills_with_if_installed_refreshes_pre_marker_install() {
        // Regression: an existing install from a pre-marker build has owned
        // kcap-* folders but no marker file. The first upgrade-time
        // postinstall must still refresh it (and stamp the marker so future
        // upgrades take the marker-fast-path).
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();

        var skillsSrc = pluginRoot.CreateDir("skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            skillsSrc.CreateDir(name);
            skillsSrc.CreateFile([name, "SKILL.md"],
                $"---\nname: {name}\ndescription: fresh\n---\nfresh body");
        }

        // Pre-marker install: kcap-* folder exists, no marker file.
        var target = fakeHome.PathTo(".agents", "skills");
        var staleSkill = Path.Combine(target, "kcap-recap");
        Directory.CreateDirectory(staleSkill);
        await File.WriteAllTextAsync(
            Path.Combine(staleSkill, "SKILL.md"),
            "---\nname: kcap-recap\n---\nstale body");
        await Assert.That(File.Exists(Path.Combine(target, AgentsSkillsInstaller.MarkerFileName))).IsFalse();

        var exit = await new PluginCommand(TestEnv(fakeHome.Path, pluginRoot.Path)).HandleAsync(
            ["plugin", "install", "--skills", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        // All skills present + freshly written.
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            await Assert.That(Directory.Exists(Path.Combine(target, $"kcap-{name}"))).IsTrue();
        }
        var refreshed = await File.ReadAllTextAsync(Path.Combine(target, "kcap-recap", "SKILL.md"));
        await Assert.That(refreshed).Contains("fresh body");
        await Assert.That(refreshed).DoesNotContain("stale body");

        // Marker now stamped so the next upgrade takes the fast path.
        await Assert.That(File.Exists(Path.Combine(target, AgentsSkillsInstaller.MarkerFileName))).IsTrue();
    }

    [Test]
    public async Task Install_skills_with_if_installed_is_noop_when_marker_matches_current_version() {
        // Fast path: same-version reinstalls (e.g. `npm install -g @kurrent/kcap`
        // when the same version is already installed) must not re-copy every skill.
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();

        var skillsSrc = pluginRoot.CreateDir("skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            skillsSrc.CreateDir(name);
            skillsSrc.CreateFile([name, "SKILL.md"],
                $"---\nname: {name}\n---\nfresh body");
        }

        // Pre-seed: marker holds the *current* CLI version.
        var target = fakeHome.CreateDir(".agents", "skills");
        target.CreateFile(AgentsSkillsInstaller.MarkerFileName,
            AgentsSkillsInstaller.CurrentVersion());

        // Pre-seed one skill folder with a sentinel that the installer
        // would otherwise overwrite. If the short-circuit fires, this
        // file should survive untouched.
        target.CreateDir("kcap-recap");
        target.CreateFile(["kcap-recap", "SKILL.md"],
            "stale body — must NOT be overwritten");

        var exit = await new PluginCommand(TestEnv(fakeHome.Path, pluginRoot.Path)).HandleAsync(
            ["plugin", "install", "--skills", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        // Sentinel still intact → installer did not run.
        var preserved = await File.ReadAllTextAsync(target.PathTo("kcap-recap", "SKILL.md"));
        await Assert.That(preserved).IsEqualTo("stale body — must NOT be overwritten");
    }

    [Test]
    public async Task Install_skills_with_if_installed_swallows_plugin_resolution_failure() {
        using var fakeHome = new TempDir();
        var capturedErr    = new StringWriter();

        // Pre-seed marker so the gate proceeds…
        var target = fakeHome.CreateDir(".agents", "skills");
        target.CreateFile(AgentsSkillsInstaller.MarkerFileName,
            "some-version");

        // …but plugin path is null (resolution failed).
        var env = TestEnv(fakeHome.Path, pluginPath: null, stderr: capturedErr);

        var exit = await new PluginCommand(env).HandleAsync(
            ["plugin", "install", "--skills", "--if-installed"]);

        // Refresh path must never fail npm install — exit 0, nothing on stderr.
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capturedErr.ToString()).IsEmpty();
    }

    [Test]
    public async Task Remove_skills_clears_agents_and_legacy() {
        using var fakeHome = new TempDir();

        var agentsDir = fakeHome.PathTo(".agents", "skills");
        var legacyDir = fakeHome.PathTo(".codex", "skills");
        Directory.CreateDirectory(agentsDir);
        Directory.CreateDirectory(legacyDir);
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            Directory.CreateDirectory(Path.Combine(agentsDir, $"kcap-{name}"));
        }
        foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
            Directory.CreateDirectory(Path.Combine(legacyDir, name));
        }

        var exit = await new PluginCommand(TestEnv(fakeHome.Path)).HandleAsync(
            ["plugin", "remove", "--skills"]);
        await Assert.That(exit).IsEqualTo(0);

        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            await Assert.That(Directory.Exists(Path.Combine(agentsDir, $"kcap-{name}"))).IsFalse();
        }
        await Assert.That(Directory.Exists(legacyDir)).IsFalse();
    }

    static PluginEnvironment TestEnv(
        string      fakeHome,
        string?     pluginPath = null,
        TextWriter? stdout     = null,
        TextWriter? stderr     = null
    ) => new(
        Home:     new(fakeHome),
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => pluginPath,
        Stdout:            stdout ?? TextWriter.Null,
        Stderr:            stderr ?? TextWriter.Null
    ) {
        Harnesses = TestHarnesses.Under(new(fakeHome)),
    };
}
