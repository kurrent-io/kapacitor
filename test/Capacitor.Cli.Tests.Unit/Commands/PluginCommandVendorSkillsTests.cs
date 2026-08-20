using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Every vendor that reads the agent-agnostic <c>~/.agents/skills</c> tree gets it from its own
/// <c>plugin install</c>, not only from a full <c>kcap setup</c> or an explicit <c>--skills</c>.
/// </summary>
/// <remarks>
/// Without it a Cursor-only user has hooks and MCP and no skills at all, and the tour handoff names a
/// slash command their agent has never heard of. The target flags are mutually exclusive, so
/// `--cursor --skills` cannot be one invocation — only the install itself can close the gap.
///
/// The refresh (`--if-installed`) counterpart is the opposite property and is tested here too: it may
/// top a tree up, never create one. The npm postinstall runs it for every vendor on each
/// `npm install -g`, so creating there would undo a deliberate `plugin remove --skills`.
/// </remarks>
[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandVendorSkillsTests {
    [Test]
    [MethodDataSource(nameof(Vendors))]
    public async Task fresh_install_writes_the_shared_agent_skills(Vendor vendor) {
        using var scope = new VendorScope(vendor);

        var exit = await PluginCommand.HandleAsync(vendor.InstallArgs(scope.Home), scope.Env);

        await Assert.That(exit).IsEqualTo(0);

        foreach (var name in AgentsSkillsInstaller.SourceNames)
            await Assert.That(AgentsSkillsInstaller.HasSkill(scope.Env.AgentsSkillsDir, name))
                        .IsTrue()
                        .Because($"`plugin install --{vendor.Flag}` should have written kcap-{name}");
    }

    [Test]
    [MethodDataSource(nameof(Vendors))]
    public async Task refresh_does_not_create_skills_for_a_vendor_never_installed(Vendor vendor) {
        using var scope = new VendorScope(vendor);

        var exit = await PluginCommand.HandleAsync(
            [.. vendor.InstallArgs(scope.Home), "--if-installed"], scope.Env);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(scope.Env.AgentsSkillsDir))
                    .IsFalse()
                    .Because("the npm postinstall runs this on every upgrade; it must not install "
                           + "skills for someone who never opted into anything");
    }

    [Test]
    [MethodDataSource(nameof(Vendors))]
    public async Task refresh_does_not_resurrect_skills_the_user_removed(Vendor vendor) {
        using var scope = new VendorScope(vendor);

        // Install for real, then remove the skills the way a user would.
        await PluginCommand.HandleAsync(vendor.InstallArgs(scope.Home), scope.Env);
        await PluginCommand.HandleAsync(["plugin", "remove", "--skills"], scope.Env);
        await Assert.That(AgentsSkillsInstaller.IsInstalled(scope.Env.AgentsSkillsDir)).IsFalse();

        var exit = await PluginCommand.HandleAsync(
            [.. vendor.InstallArgs(scope.Home), "--if-installed"], scope.Env);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(AgentsSkillsInstaller.IsInstalled(scope.Env.AgentsSkillsDir))
                    .IsFalse()
                    .Because("`remove --skills` drops the marker so an upgrade cannot undo it — a "
                           + "vendor refresh must respect that too, or the removal comes back on a "
                           + "command the user never ran");
    }

    [Test]
    public async Task fresh_install_kiro_writes_its_own_skills_tree_not_the_shared_one() {
        using var scope = new VendorScope(Vendor.Kiro);

        await PluginCommand.HandleAsync(["plugin", "install", "--kiro"], scope.Env);

        // Kiro reads ~/.kiro/skills; writing the shared tree instead would be silently useless to it.
        await Assert.That(AgentsSkillsInstaller.IsInstalled(scope.Env.KiroSkillsDir)).IsTrue();
        await Assert.That(Directory.Exists(scope.Env.AgentsSkillsDir)).IsFalse();
    }

    [Test]
    public async Task fresh_install_antigravity_writes_its_own_skills_tree_not_the_shared_one() {
        using var scope = new VendorScope(Vendor.Antigravity);

        await PluginCommand.HandleAsync(["plugin", "install", "--antigravity"], scope.Env);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(scope.Env.AntigravitySkillsDir)).IsTrue();
        await Assert.That(Directory.Exists(scope.Env.AgentsSkillsDir)).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(Vendors))]
    public async Task the_skip_flag_declines_the_shared_skills(Vendor vendor) {
        using var scope = new VendorScope(vendor);

        var exit = await PluginCommand.HandleAsync(
            [.. vendor.InstallArgs(scope.Home), $"--skip-{vendor.Flag}-skills"], scope.Env);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(scope.Env.AgentsSkillsDir))
                    .IsFalse()
                    .Because("every other artifact these installs write has a skip flag, so the shared "
                           + "tree needs one too — nothing else lets you take hooks without it");
    }

    [Test]
    public async Task install_sweeps_legacy_codex_skills_even_when_the_tree_is_already_current() {
        using var scope = new VendorScope(Vendor.Cursor);

        await PluginCommand.HandleAsync(Vendor.Cursor.InstallArgs(scope.Home), scope.Env);
        await Assert.That(AgentsSkillsInstaller.IsCurrent(scope.Env.AgentsSkillsDir)).IsTrue();

        // A pre-migration machine still carrying the old Codex-only copy.
        var legacy = Path.Combine(scope.Env.LegacyCodexSkills, "kcap-recap");
        Directory.CreateDirectory(legacy);

        await PluginCommand.HandleAsync(Vendor.Cursor.InstallArgs(scope.Home), scope.Env);

        await Assert.That(Directory.Exists(legacy))
                    .IsFalse()
                    .Because("the tree being current is exactly when the sweep gets skipped, so gating "
                           + "it on the copy leaves the stale dir behind for good");
    }

    /// <summary>The five vendors that read the shared tree. Kiro and Antigravity read their own and
    /// are covered separately.</summary>
    public static IEnumerable<Func<Vendor>> Vendors() {
        yield return () => Vendor.Cursor;
        yield return () => Vendor.Copilot;
        yield return () => Vendor.Gemini;
        yield return () => Vendor.Pi;
        yield return () => Vendor.OpenCode;
    }

    public sealed record Vendor(string Flag, string[] ClearedEnvVars) {
        public static readonly Vendor Cursor      = new("cursor", []);
        public static readonly Vendor Copilot     = new("copilot", ["COPILOT_HOME"]);
        public static readonly Vendor Gemini      = new("gemini", ["GEMINI_CLI_HOME"]);
        public static readonly Vendor Pi          = new("pi", ["PI_CODING_AGENT_DIR"]);
        public static readonly Vendor Kiro        = new("kiro", ["KIRO_HOME"]);
        public static readonly Vendor Antigravity = new("antigravity", []);

        // OpenCode resolves its config dir from OPENCODE_CONFIG_DIR then XDG_CONFIG_HOME before the
        // home it is handed, so both have to go or the install lands in the real user config.
        public static readonly Vendor OpenCode =
            new("opencode", ["OPENCODE_CONFIG_DIR", "XDG_CONFIG_HOME"]);

        public string[] InstallArgs(string home) => Flag switch {
            "cursor"   => ["plugin", "install", "--cursor",
                           "--cursor-hooks-path", Path.Combine(home, ".cursor", "hooks.json")],
            "opencode" => ["plugin", "install", "--opencode",
                           "--opencode-plugin-path",
                           Path.Combine(home, ".config", "opencode", "plugins", "kcap.ts")],
            _          => ["plugin", "install", "--" + Flag],
        };

        public override string ToString() => Flag;
    }

    /// <summary>
    /// A fake home with the vendor's own env vars cleared, a resolvable `kcap` on PATH, and a
    /// <see cref="PluginEnvironment"/> pointed at the shipped skills tree.
    /// </summary>
    sealed class VendorScope : IDisposable {
        readonly FakeUserHome    _home;
        readonly TempDir         _binDir;
        readonly List<EnvScope>  _envScopes = [];

        public VendorScope(Vendor vendor) {
            _home   = new FakeUserHome();
            _binDir = new TempDir();

            foreach (var key in vendor.ClearedEnvVars)
                _envScopes.Add(new EnvScope(key, null));

            // The fresh path refuses to install unless `kcap` resolves — it is what the hooks it
            // writes will invoke. Both names, because the Windows leg matches on PATHEXT.
            foreach (var name in new[] { "kcap", "kcap.exe" }) {
                var path = _binDir.CreateFile(name);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            _envScopes.Add(new EnvScope(
                "PATH", _binDir.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")));

            Env = new PluginEnvironment(
                HomeDirectory:     _home.Path,
                // The siblings pass `() => null`, which short-circuits the skills copy to a warning and
                // would make every assertion here vacuous.
                ResolvePluginPath: RepoTree.KcapDir,
                Stdout:            TextWriter.Null,
                Stderr:            TextWriter.Null
            ) { ResolveMcpBinaryPath = () => Path.Combine(_binDir.Path, "kcap") };
        }

        public string            Home => _home.Path;
        public PluginEnvironment Env  { get; }

        public void Dispose() {
            foreach (var scope in _envScopes) scope.Dispose();
            _binDir.Dispose();
            _home.Dispose();
        }
    }
}
