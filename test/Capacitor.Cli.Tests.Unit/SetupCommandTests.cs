using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class SetupCommandTests {
    // --- Step 6 import auth-eligibility probe (IsAuthSatisfiedAsync) ---

    [Test]
    public async Task IsAuthSatisfied_ProviderNone_TrueAndNeverProbesToken() {
        var probed = false;

        var ok = await SetupCommand.IsAuthSatisfiedAsync(AuthProvider.None, () => {
            probed = true;

            return Task.FromResult(false);
        });

        await Assert.That(ok).IsTrue();
        await Assert.That(probed).IsFalse(); // provider None short-circuits — no token probe
    }

    [Test]
    public async Task IsAuthSatisfied_AuthedProvider_UsableToken_True() {
        // Models an expired-but-refreshable (or valid) token: the probe resolves to true.
        var ok = await SetupCommand.IsAuthSatisfiedAsync("github", () => Task.FromResult(true));

        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task IsAuthSatisfied_AuthedProvider_NoUsableToken_False() {
        var ok = await SetupCommand.IsAuthSatisfiedAsync("github", () => Task.FromResult(false));

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task IsAuthSatisfied_AuthedProvider_ProbeThrows_FalseNotThrow() {
        // A token I/O / refresh failure (e.g. non-writable token dir) must degrade to an
        // ineligible skip, NOT propagate out of setup.
        var ok = await SetupCommand.IsAuthSatisfiedAsync(
            "github", () => throw new UnauthorizedAccessException("token dir not writable"));

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task InstallPlugin_CreatesNewSettingsFile() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, "settings.json");
        var       marketplace  = "/opt/kcap";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);

        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();
    }

    [Test]
    public async Task InstallPlugin_PreservesExistingSettings() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, "settings.json");
        const string marketplace  = "/opt/kcap";

        // Pre-populate with existing settings
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "permissions": { "allow": ["Bash"] },
              "enabledPlugins": { "other-plugin@foo": true }
            }
            """
        );

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        // Original settings preserved
        await Assert.That(root["permissions"]?["allow"]?[0]?.GetValue<string>())
            .IsEqualTo("Bash");

        await Assert.That(root["enabledPlugins"]?["other-plugin@foo"]?.GetValue<bool>() ?? false)
            .IsTrue();

        // Plugin added
        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);
    }

    [Test]
    public async Task InstallPlugin_UpdatesExistingMarketplacePath() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, "settings.json");
        const string newPath      = "/new/path";

        // Pre-populate with old marketplace path
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "extraKnownMarketplaces": {
                "kurrent": { "source": { "source": "directory", "path": "/old/path" } }
              },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """
        );

        var result = SetupCommand.InstallPlugin(settingsPath, newPath);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(newPath);
    }

    [Test]
    public async Task InstallPlugin_CreatesIntermediateDirectories() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, ".claude", "nested", "settings.json");
        const string marketplace  = "/opt/kcap";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();
        await Assert.That(File.Exists(settingsPath)).IsTrue();
    }

    [Test]
    public async Task InstallPlugin_MalformedJson_StartsFromScratch() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, "settings.json");
        const string marketplace  = "/opt/kcap";

        await File.WriteAllTextAsync(settingsPath, "not json {{{");

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();
    }

    [Test]
    // Touches the process-wide AppConfig.GetConfigPath() (config.json). Share the
    // TokenStoreProfileTests serialization key so it can't run concurrently with tests
    // that reset/read that same shared config (e.g. TokenStoreProfileTests cleanup).
    [NotInParallel(nameof(TokenStoreProfileTests))]
    public async Task Setup_save_profile_config_round_trips_active_profile() {
        // Smoke-check that the discovery-path SetupCommand can save and reload the active
        // profile after MergeProfiles has set it to a non-"default" name. The full discovery
        // flow is end-to-end-tested by the integration suite.
        var cfg = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() {
                ["acme"] = new() { ServerUrl = "https://a.example", DefaultVisibility = "org_public" }
            }
        };
        await ConfigMutator.MutateAsync(_ => cfg);

        var reloaded = await AppConfig.LoadProfileConfig();
        await Assert.That(reloaded.ActiveProfile).IsEqualTo("acme");
        await Assert.That(reloaded.Profiles["acme"].ServerUrl).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task LiveRecordingRestartTip_returns_note_when_any_agent_installed() {
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       true,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false);

        var tip = SetupCommand.LiveRecordingRestartTip(result);

        await Assert.That(tip).IsNotNull();
        await Assert.That(tip!).Contains("new");
        await Assert.That(tip!).Contains("claude --continue");
    }

    [Test]
    public async Task LiveRecordingRestartTip_note_fires_for_non_claude_agents_too() {
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: true);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNotNull();
    }

    [Test]
    public async Task LiveRecordingRestartTip_pi_only_tells_user_to_restart_pi() {
        // A Pi-only install must not print a Claude-specific hint — it should
        // tell the user to restart pi so the kcap extension loads.
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false,
            PiExtensionInstalled:  true);

        var tip = SetupCommand.LiveRecordingRestartTip(result);

        await Assert.That(tip).IsNotNull();
        await Assert.That(tip!).Contains("pi");
        await Assert.That(tip!).DoesNotContain("claude --continue");
    }

    [Test]
    public async Task LiveRecordingRestartTip_is_null_when_no_hooks_installed() {
        // No hooks wired up (e.g. every agent declined or none detected) — don't
        // promise live recording that won't happen.
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNull();
    }

    [Test]
    public async Task GuidedTourCallToAction_is_the_exact_agreed_wording() {
        // Pinned wording — this is the only discovery path for the guided-tour skill, and the
        // prompt it names must appear verbatim in that skill's description (asserted below)
        // or the phrase we tell users to type won't reliably fire it.
        await Assert.That(SetupCommand.GuidedTourCallToAction).IsEqualTo(
            "New to Capacitor? Prompt \"Start kcap guided tour\" in your coding agent to see what "
          + "Capacitor can do for you");
    }

    [Test]
    public async Task ServerSetupCallToAction_is_the_exact_agreed_wording() {
        // Pinned wording: a question the reader self-selects on, then the instruction itself.
        await Assert.That(SetupCommand.ServerSetupQuestion).IsEqualTo(
            "Did you create this Capacitor server?");

        await Assert.That(SetupCommand.ServerSetupAction).IsEqualTo(
            "Complete server setup with instructions here:");

        await Assert.That(SetupCommand.ServerSetupDocsUrl).IsEqualTo(
            "https://capacitor.kurrent.io/docs/getting-started/setup-server/");
    }

    [Test]
    public async Task NextSteps_always_lists_server_setup_and_only_adds_the_tour_when_offered() {
        // Server setup is ungated, so it must survive a run where no agent carried the tour skill.
        var withoutTour = SetupCommand.NextStepItems(offerGuidedTour: false);

        await Assert.That(withoutTour.Count).IsEqualTo(1);
        await Assert.That(withoutTour[0].Question).IsEqualTo(SetupCommand.ServerSetupQuestion);
        await Assert.That(withoutTour[0].Answer)
                    .Contains($"[cyan]{SetupCommand.ServerSetupDocsUrl}[/]");

        var withTour = SetupCommand.NextStepItems(offerGuidedTour: true);

        await Assert.That(withTour.Count).IsEqualTo(2);
        await Assert.That(withTour[0].Question).IsEqualTo(SetupCommand.ServerSetupQuestion);
        await Assert.That(withTour[1].Question).IsEqualTo(SetupCommand.GuidedTourQuestion);
        await Assert.That(withTour[1].Answer)
                    .Contains($"[cyan]{SetupCommand.GuidedTourPromptQuoted}[/]");
    }

    [Test]
    public async Task GuidedTourCallToAction_prompt_is_a_trigger_in_the_skill_description() {
        // Vendors match a user's message against the frontmatter `description:` only — the body
        // is read after the skill fires. So asserting the phrase is somewhere in the file would
        // pass even if it moved below the fences, where it can no longer trigger anything.
        var skill = Path.Combine(RepoRoot(), "kcap", "skills", SetupCommand.GuidedTourSkillName, "SKILL.md");

        await Assert.That(File.Exists(skill)).IsTrue();

        var description = FrontmatterDescription(await File.ReadAllTextAsync(skill));

        await Assert.That(description).Contains(SetupCommand.GuidedTourPrompt);
    }

    /// <summary>
    /// The value of the YAML frontmatter's <c>description:</c> field, flattened to one line.
    /// Throws when there is no frontmatter — an unparseable SKILL.md must fail the test, not
    /// silently return an empty string that a Contains assertion would report as a mismatch.
    /// </summary>
    static string FrontmatterDescription(string content) {
        var lines = content.Replace("\r\n", "\n").Split('\n');

        if (lines.Length < 2 || lines[0].Trim() != "---")
            throw new InvalidOperationException("SKILL.md has no YAML frontmatter block.");

        var value    = new List<string>();
        var inValue  = false;

        for (var i = 1; i < lines.Length; i++) {
            var line = lines[i];

            if (line.Trim() == "---") break;

            if (line.StartsWith("description:", StringComparison.Ordinal)) {
                inValue = true;
                // Covers `description: text` as well as the block form `description: >-`.
                value.Add(line["description:".Length..].Trim().TrimEnd('>', '|', '-').Trim());

                continue;
            }

            if (!inValue) continue;

            // An unindented line is the next top-level key, which ends this value.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break;

            value.Add(line.Trim());
        }

        if (!inValue) throw new InvalidOperationException("SKILL.md frontmatter has no description field.");

        return string.Join(' ', value).Trim();
    }

    /// <summary>Walks up from the test binary to the repo root (the directory holding `kcap/`).</summary>
    static string RepoRoot() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null && !Directory.Exists(Path.Combine(dir, "kcap", "skills")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new DirectoryNotFoundException("Could not locate the repo root from the test binary.");
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_nothing_carries_the_skill() {
        using var tmp = new TempDir();

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, Path.Combine(tmp.Path, ".claude", "settings.json"), GuidedTourPaths(tmp.Path))).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_no_agent_is_detected_even_if_skills_exist() {
        // A machine carrying the skill but running no agent CLI has nothing to type the prompt
        // into. Skill-presence alone is not enough — both halves are required.
        using var tmp   = new TempDir();
        var       paths = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour"));

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            false, Path.Combine(tmp.Path, ".claude", "settings.json"), paths)).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_true_when_the_registered_plugin_ships_the_skill() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, ".claude", "settings.json");
        var       marketplace  = Path.Combine(tmp.Path, "plugin");

        SetupCommand.InstallPlugin(settingsPath, marketplace);
        Directory.CreateDirectory(Path.Combine(marketplace, "skills", "guided-tour"));
        await File.WriteAllTextAsync(Path.Combine(marketplace, "skills", "guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, GuidedTourPaths(tmp.Path))).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_uses_a_legacy_registered_marketplace_path() {
        // IsInstalled recognises pre-rename keys (kurrent/kapacitor); the path reader must use
        // the same key set, or the gate falls back to THIS build's plugin dir — which ships the
        // skill — while Claude actually loads the legacy dir, which does not.
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, ".claude", "settings.json");
        var       legacyDir    = Path.Combine(tmp.Path, "legacy-plugin");

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, $$"""
            {
              "extraKnownMarketplaces": {
                "kapacitor": { "source": { "source": "directory", "path": {{System.Text.Json.JsonSerializer.Serialize(legacyDir)}} } }
              },
              "enabledPlugins": { "kapacitor@kapacitor": true }
            }
            """);
        Directory.CreateDirectory(Path.Combine(legacyDir, "skills", "recap"));

        // Current build's plugin dir DOES ship the skill — the wrong fallback target.
        var modernPlugin = Path.Combine(tmp.Path, "modern-plugin");
        Directory.CreateDirectory(Path.Combine(modernPlugin, "skills", "guided-tour"));
        await File.WriteAllTextAsync(Path.Combine(modernPlugin, "skills", "guided-tour", "SKILL.md"), "skill");

        var paths = GuidedTourPaths(tmp.Path) with { PluginDir = modernPlugin };

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_an_old_plugin_is_registered_and_pluginDir_is_null() {
        // Codex round 4: an older kcap plugin registered in settings, run from a layout where
        // ResolvePluginPath finds nothing. Registration alone must not advertise a skill the
        // registered directory does not ship.
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, ".claude", "settings.json");
        var       oldPlugin    = Path.Combine(tmp.Path, "old-plugin");

        SetupCommand.InstallPlugin(settingsPath, oldPlugin);
        Directory.CreateDirectory(Path.Combine(oldPlugin, "skills", "recap")); // pre-guided-tour layout

        var paths = GuidedTourPaths(tmp.Path); // PluginDir stays null

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();

        // The registered dir gaining the skill flips it — the gate tracks what Claude loads.
        Directory.CreateDirectory(Path.Combine(oldPlugin, "skills", "guided-tour"));
        await File.WriteAllTextAsync(Path.Combine(oldPlugin, "skills", "guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_true_when_shared_agent_skills_are_present() {
        // The case a "was it installed THIS run?" gate gets wrong: skills already on disk mean a
        // wired-up machine, but every installer reports false because there was no work to do.
        using var tmp    = new TempDir();
        var       paths  = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour"));
        await File.WriteAllTextAsync(
            Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, Path.Combine(tmp.Path, ".claude", "settings.json"), paths)).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_the_skill_folder_is_empty() {
        // A failed copy creates the folder before writing SKILL.md, so an empty folder means a
        // broken install, not a usable skill.
        using var tmp   = new TempDir();
        var       paths = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour"));

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, Path.Combine(tmp.Path, ".claude", "settings.json"), paths)).IsFalse();
    }

    [Test]
    public async Task HasSkill_empty_targetDir_is_false_not_a_cwd_probe() {
        // Paths defaults some skill dirs to "" — Path.Combine("", ...) would otherwise probe a
        // relative kcap-guided-tour against whatever directory setup was run from.
        await Assert.That(AgentsSkillsInstaller.HasSkill("", "guided-tour")).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_true_from_the_kiro_and_antigravity_skill_dirs() {
        using var kiro = new TempDir();
        using var anti = new TempDir();

        Directory.CreateDirectory(Path.Combine(kiro.Path, "kiro-skills", "kcap-guided-tour"));
        await File.WriteAllTextAsync(
            Path.Combine(kiro.Path, "kiro-skills", "kcap-guided-tour", "SKILL.md"), "skill");
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, Path.Combine(kiro.Path, "none.json"), GuidedTourPaths(kiro.Path))).IsTrue();

        Directory.CreateDirectory(Path.Combine(anti.Path, "antigravity-skills", "kcap-guided-tour"));
        await File.WriteAllTextAsync(
            Path.Combine(anti.Path, "antigravity-skills", "kcap-guided-tour", "SKILL.md"), "skill");
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, Path.Combine(anti.Path, "none.json"), GuidedTourPaths(anti.Path))).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_only_older_skills_are_installed() {
        // An upgrade from a kcap that predates guided-tour leaves kcap-recap and friends on disk.
        // "Has this installer ever run here" is true there; "can the user run the tour" is not.
        using var tmp   = new TempDir();
        var       paths = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-recap"));
        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-errors"));

        await Assert.That(AgentsSkillsInstaller.IsInstalled(paths.AgentsSkillsDir)).IsTrue();
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, Path.Combine(tmp.Path, ".claude", "settings.json"), paths)).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_the_claude_plugin_dir_lacks_the_skill() {
        // Registration alone isn't enough when the resolved plugin dir is a stale install.
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, ".claude", "settings.json");
        var       pluginDir    = Path.Combine(tmp.Path, "stale-plugin");

        SetupCommand.InstallPlugin(settingsPath, pluginDir);
        Directory.CreateDirectory(Path.Combine(pluginDir, "skills", "recap"));

        var paths = GuidedTourPaths(tmp.Path) with { PluginDir = pluginDir };

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();

        Directory.CreateDirectory(Path.Combine(pluginDir, "skills", "guided-tour"));

        // Folder alone is still not enough — the file is the skill.
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();

        await File.WriteAllTextAsync(
            Path.Combine(pluginDir, "skills", "guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsTrue();
    }

    /// <summary>Paths record carrying only the four fields GuidedTourReachable reads.</summary>
    static CodingAgentsStep.Paths GuidedTourPaths(string root) =>
        new(ClaudeSettingsPath:   Path.Combine(root, ".claude", "settings.json"),
            ClaudeScopeLabel:     "user",
            PluginDir:            null,
            CodexHooksPath:       Path.Combine(root, "codex-hooks.json"),
            CursorHooksPath:      Path.Combine(root, "cursor-hooks.json"),
            CopilotHooksPath:     Path.Combine(root, "copilot-hooks.json"),
            GeminiSettingsPath:   Path.Combine(root, "gemini-settings.json"),
            AgentsSkillsDir:      Path.Combine(root, "agents-skills"),
            LegacyCodexSkillsDir: Path.Combine(root, "legacy-codex-skills"),
            KiroHooksPath:        Path.Combine(root, "kiro-hooks.json"),
            PiExtensionPath:      Path.Combine(root, "pi-ext.ts"),
            OpenCodeExtensionPath: Path.Combine(root, "oc-ext.ts"),
            AntigravityHooksPath: Path.Combine(root, "antigravity-hooks.json"),
            CodexConfigTomlPath:  Path.Combine(root, "config.toml"),
            CursorMcpPath:        Path.Combine(root, "cursor-mcp.json"),
            CopilotMcpPath:       Path.Combine(root, "copilot-mcp.json"),
            CopilotInstructionsPath: Path.Combine(root, "copilot-instructions.md"),
            GeminiInstructionsPath: Path.Combine(root, "GEMINI.md"),
            AntigravityMcpPath:   Path.Combine(root, "antigravity-mcp.json"),
            AntigravityInstructionsPath: Path.Combine(root, "antigravity-instructions.md"),
            AntigravitySkillsDir: Path.Combine(root, "antigravity-skills"),
            OpenCodeMcpPath:      Path.Combine(root, "oc-mcp.json"),
            OpenCodeInstructionsPath: Path.Combine(root, "AGENTS.md"),
            KiroMcpPath:          Path.Combine(root, "kiro-mcp.json"),
            KiroSkillsDir:        Path.Combine(root, "kiro-skills"),
            PiMcpExtensionPath:   Path.Combine(root, "pi-mcp.ts"),
            PiAgentsMdPath:       Path.Combine(root, "pi-AGENTS.md"));

    [Test]
    public async Task ResolveTenantArg_expands_bare_label_to_kcap_subdomain() {
        await Assert.That(SetupCommand.ResolveTenantArg("eventuous")).IsEqualTo("https://eventuous.kcap.ai");
    }

    // --- Step 6 (RunImportStepAsync) wiring ---
    //
    // SetupCommand.ImportRunnerOverride is process-global static state (mutated by
    // RunImportStepAsync's caller — HandleAsync — only via this seam), so every test that sets it
    // must run serialized against the others and reset it to null in a finally block, mirroring
    // the AppConfigResolvedStateTests.ResolvedStateMutation pattern.
    const string ImportRunnerOverrideMutation = nameof(ImportRunnerOverrideMutation);

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunDecision_InvokesRunnerWithPinnedArgs() {
        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt under --no-prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.BaseUrl).IsEqualTo("https://example.test");
        await Assert.That(captured.Repo).IsEqualTo(("acme", "widgets"));
        await Assert.That(captured.DefaultVisibility).IsEqualTo("org_public");
        await Assert.That(captured.AutoSkipExclusions).IsTrue();
        await Assert.That(captured.ForcePrivate).IsFalse();
        await Assert.That(captured.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_InteractiveAccept_InvokesRunner() {
        var invoked = false;
        SetupCommand.ImportRunnerOverride = _ => {
            invoked = true;
            return Task.FromResult(0);
        };

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          false,
                promptYesNo:       () => true,
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }

        await Assert.That(invoked).IsTrue();
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunnerReturnsNonZero_DoesNotThrowAndCompletes() {
        SetupCommand.ImportRunnerOverride = _ => Task.FromResult(1);

        try {
            // Completing without an unhandled exception is the assertion: a non-zero exit
            // code must be swallowed (warned about, not propagated) so setup still finishes.
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunnerThrows_DoesNotPropagateAndCompletes() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("boom");

        try {
            // Completing without the InvalidOperationException escaping is the assertion —
            // import is best-effort and must never fail setup.
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_NoCurrentRepo_SkipsWithoutInvokingRunnerOrPrompting() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       null,
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          false,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_SkipImportFlag_SkipsWithoutInvokingRunner() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        true,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    // The zero-discovery "I already have a workspace" prompt invites a paste, and what people paste
    // is the page they are looking at. Everything downstream appends a fixed root path
    // (/auth/config), so a path on the input silently probes the wrong endpoint and reports the
    // server unreachable. Reduce to the origin first.
    [Test]
    public async Task ToServerOrigin_reduces_a_pasted_page_url_to_its_origin() {
        await Assert.That(SetupCommand.ToServerOrigin("https://acme.kcap.ai/sessions?tab=all#x"))
            .IsEqualTo("https://acme.kcap.ai");
        await Assert.That(SetupCommand.ToServerOrigin("acme.kcap.ai/sessions")).IsEqualTo("acme.kcap.ai");
        await Assert.That(SetupCommand.ToServerOrigin("http://localhost:5108/repo/abc")).IsEqualTo("http://localhost:5108");
        await Assert.That(SetupCommand.ToServerOrigin("localhost:5108/repo/abc")).IsEqualTo("localhost:5108");
    }

    [Test]
    public async Task ToServerOrigin_leaves_a_bare_slug_origin_or_ipv6_host_alone() {
        await Assert.That(SetupCommand.ToServerOrigin("acme")).IsEqualTo("acme");
        await Assert.That(SetupCommand.ToServerOrigin("https://acme.kcap.ai")).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(SetupCommand.ToServerOrigin("https://acme.kcap.ai/")).IsEqualTo("https://acme.kcap.ai");
        // Bracketed IPv6 keeps its colons and port — the ':' and '/' scan must not cut inside it.
        await Assert.That(SetupCommand.ToServerOrigin("[::1]:5108")).IsEqualTo("[::1]:5108");
        await Assert.That(SetupCommand.ToServerOrigin("[::1]:5108/x")).IsEqualTo("[::1]:5108");
        await Assert.That(SetupCommand.ToServerOrigin("http://[::1]:5108/x")).IsEqualTo("http://[::1]:5108");
    }

    [Test]
    public async Task ResolveTenantArg_leaves_urls_fqdns_and_hosts_untouched() {
        await Assert.That(SetupCommand.ResolveTenantArg("https://x.example")).IsEqualTo("https://x.example");
        await Assert.That(SetupCommand.ResolveTenantArg("self.hosted.example")).IsEqualTo("self.hosted.example");
        await Assert.That(SetupCommand.ResolveTenantArg("localhost:5108")).IsEqualTo("localhost:5108");
        await Assert.That(SetupCommand.ResolveTenantArg("localhost")).IsEqualTo("localhost"); // bare loopback, not a slug
    }

    // =====================================================================
    // HandleAsync-level acceptance coverage for the Step 6 import wiring
    // (review finding — see .superpowers/sdd/review-fix-report.md).
    //
    // These drive the FULL wizard (flag parsing → server normalization/probe →
    // auth discovery → profile save → authSatisfied computation → Step 6) against
    // a real WireMock server, intercepting only the final import call via
    // ImportRunnerOverride. Every test here:
    //   • runs from a throwaway git repo (real `git init` + `remote add origin`)
    //     so RepositoryDetection.DetectRepositoryAsync resolves an owner/repo —
    //     HandleAsync reads Environment.CurrentDirectory directly (Step 6), so
    //     there is no way to inject this without actually changing the process cwd.
    //   • redirects HOME to a throwaway directory — every coding-agent path
    //     (ClaudePaths/CodexPaths/CursorPaths/...) resolves from
    //     PathHelpers.HomeDirectory, read live (not cached), so this contains any
    //     install that isn't fully gated by a --skip-*-hooks flag.
    //   • passes every --skip-*-hooks/-mcp/-instructions/-skills flag so Step 4
    //     never attempts a real coding-agent install, belt-and-suspenders with
    //     the HOME redirect above.
    //   • uses auth provider "None" (a WireMock /auth/config stub) so Step 2 never
    //     drives a real OAuth/device-code login flow — HandleAsync's --server-url
    //     path has no way to no-prompt past that login when the provider isn't
    //     None (Decision 9 / authSatisfied is a separate concern from Step 2).
    //   • resets HttpClientExtensions' in-process auth-provider cache first — that
    //     cache is keyed by nothing but process lifetime (first caller wins for
    //     every baseUrl afterward), so a prior call elsewhere in the process could
    //     otherwise make this test's own WireMock stub a no-op. See
    //     HttpClientExtensions.ResetProviderCacheForTesting's doc.
    //
    // All four mutate Environment.CurrentDirectory, HOME, AppConfig's resolved
    // state (SetResolvedState always runs near the end of HandleAsync), and the
    // shared KCAP_CONFIG_DIR config/tokens store — so all four join every
    // NotInParallel group any of those resources already uses elsewhere.
    const string HandleAsyncNotInParallelGroups_HomeEnvVarMutation = "HomeEnvVarMutation"; // shared w/ UninstallCommandTests
    const string HandleAsyncNotInParallelGroups_CwdMutation        = "CwdMutation";        // shared w/ UninstallCommandTests
    const string HandleAsyncNotInParallelGroups_ResolvedState      = "ResolvedStateMutation"; // shared w/ AppConfigResolvedStateTests / ImportVisibilityTests

    static string[] SkipAllAgentInstallFlags => [
        "--skip-claude-hooks", "--skip-codex-hooks", "--skip-codex-network-access",
        "--skip-cursor-hooks", "--skip-cursor-mcp",
        "--skip-copilot-hooks", "--skip-copilot-mcp", "--skip-copilot-instructions",
        "--skip-gemini-hooks", "--skip-gemini-mcp", "--skip-gemini-instructions",
        "--skip-kiro-hooks", "--skip-kiro-mcp", "--skip-kiro-skills",
        "--skip-pi-hooks", "--skip-pi-mcp", "--skip-pi-instructions",
        "--skip-opencode-hooks", "--skip-opencode-mcp", "--skip-opencode-instructions",
        "--skip-antigravity-hooks", "--skip-antigravity-mcp", "--skip-antigravity-instructions", "--skip-antigravity-skills",
    ];

    static string[] BuildArgs(params string[] extra) => ["setup", .. extra, .. SkipAllAgentInstallFlags];

    static void StubAuthProviderNone(WireMockServer server) =>
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"None"}"""));

    [Test]
    [NotInParallel([
        HandleAsyncNotInParallelGroups_HomeEnvVarMutation, HandleAsyncNotInParallelGroups_CwdMutation,
        HandleAsyncNotInParallelGroups_ResolvedState, nameof(TokenStoreProfileTests), ImportRunnerOverrideMutation
    ])]
    public async Task HandleAsync_NoPromptWithServerUrl_AutoImportsWithPinnedInvocation_UnderAuthProviderNoneAndNoToken() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-auto-import", "widgets");

        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            var args = BuildArgs("--server-url", server.Url!, "--no-prompt", "--default-visibility", "org_public");

            var exit = await SetupCommand.HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.Repo).IsEqualTo(("acme-auto-import", "widgets"));
            await Assert.That(captured.AutoSkipExclusions).IsTrue();
            await Assert.That(captured.ForcePrivate).IsFalse();
            await Assert.That(captured.DefaultVisibility).IsEqualTo("org_public");
            await Assert.That(captured.BaseUrl).IsEqualTo(server.Url!.TrimEnd('/'));

            // Auth provider None makes Step 6 eligible WITHOUT any token: Step 2 short-circuits
            // to "no login required" (no OAuth flow ran), so nothing was ever stored — yet
            // import still ran (asserted above). Confirm no token exists for the profile the
            // import actually saw.
            await Assert.That(await TokenStore.LoadAsync(captured.ActiveProfile)).IsNull();
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel([
        HandleAsyncNotInParallelGroups_HomeEnvVarMutation, HandleAsyncNotInParallelGroups_CwdMutation,
        HandleAsyncNotInParallelGroups_ResolvedState, nameof(TokenStoreProfileTests), ImportRunnerOverrideMutation
    ])]
    public async Task HandleAsync_SkipImportFlag_SuppressesAutoImport() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-skip-import", "widgets");

        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            var args = BuildArgs("--server-url", server.Url!, "--no-prompt", "--skip-import");

            // Completing with exit 0 without the override's exception escaping is the
            // assertion — --skip-import must suppress the Step 6 call entirely.
            var exit = await SetupCommand.HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel([
        HandleAsyncNotInParallelGroups_HomeEnvVarMutation, HandleAsyncNotInParallelGroups_CwdMutation,
        HandleAsyncNotInParallelGroups_ResolvedState, nameof(TokenStoreProfileTests), ImportRunnerOverrideMutation
    ])]
    public async Task HandleAsync_SchemeLessServerUrl_ReachesImportRunnerNormalizedWithHttpScheme() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        var port                = new Uri(server.Url!).Port;
        var schemeLessServerUrl = $"localhost:{port}";

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-schemeless", "widgets");

        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            var args = BuildArgs("--server-url", schemeLessServerUrl, "--no-prompt");

            var exit = await SetupCommand.HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured).IsNotNull();
            // AppConfig.SetResolvedState + the Step-1 normalization: the scheme-less
            // --server-url must reach the import runner already normalized (http://
            // for a loopback host), not the raw scheme-less string.
            await Assert.That(captured!.BaseUrl).IsEqualTo($"http://localhost:{port}");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel([
        HandleAsyncNotInParallelGroups_HomeEnvVarMutation, HandleAsyncNotInParallelGroups_CwdMutation,
        HandleAsyncNotInParallelGroups_ResolvedState, nameof(TokenStoreProfileTests), ImportRunnerOverrideMutation
    ])]
    public async Task HandleAsync_ConflictingKcapUrlAndProfileEnvVars_DoesNotHijackSavedServerOrProfile() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-envconflict", "widgets");

        var savedKcapUrl     = Environment.GetEnvironmentVariable("KCAP_URL");
        var savedKcapProfile = Environment.GetEnvironmentVariable("KCAP_PROFILE");
        // Deliberately conflicting: neither matches the --server-url this run actually saves.
        Environment.SetEnvironmentVariable("KCAP_URL", "http://conflicting-env.invalid");
        Environment.SetEnvironmentVariable("KCAP_PROFILE", "conflicting-profile");

        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            var args = BuildArgs("--server-url", server.Url!, "--no-prompt");

            var exit = await SetupCommand.HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.BaseUrl).IsEqualTo(server.Url!.TrimEnd('/'));
            await Assert.That(captured.ActiveProfile).IsEqualTo("default");

            // AppConfig.SetResolvedState assigns directly rather than re-resolving
            // CLI/env/repo precedence — so the just-saved server survives even though a
            // conflicting KCAP_URL/KCAP_PROFILE sat in the environment for the whole call.
            await Assert.That(AppConfig.ResolvedServerUrl).IsEqualTo(server.Url!.TrimEnd('/'));
        } finally {
            SetupCommand.ImportRunnerOverride = null;
            Environment.SetEnvironmentVariable("KCAP_URL", savedKcapUrl);
            Environment.SetEnvironmentVariable("KCAP_PROFILE", savedKcapProfile);
        }
    }

    /// <summary>
    /// Isolation fixture for the HandleAsync acceptance tests above. See the comment block
    /// preceding them for what each piece of isolation guards against.
    /// </summary>
    sealed class HandleAsyncE2EFixture : IAsyncDisposable {
        public string RepoDir { get; }
        public string Home    { get; }

        readonly string  _originalCwd;
        readonly string? _originalHome;

        HandleAsyncE2EFixture(string repoDir, string home, string originalCwd, string? originalHome) {
            RepoDir       = repoDir;
            Home          = home;
            _originalCwd  = originalCwd;
            _originalHome = originalHome;
        }

        public static async Task<HandleAsyncE2EFixture> CreateAsync(string owner, string repo) {
            var repoDir = Directory.CreateTempSubdirectory("kcap-setup-e2e-repo-").FullName;
            await RunGitAsync("init", repoDir);
            await RunGitAsync($"remote add origin https://github.com/{owner}/{repo}.git", repoDir);

            var home = Directory.CreateTempSubdirectory("kcap-setup-e2e-home-").FullName;

            var originalCwd  = Environment.CurrentDirectory;
            var originalHome = Environment.GetEnvironmentVariable("HOME");

            // Reset shared process/config state to a known baseline before this run —
            // mirrors TokenStoreProfileTests.Cleanup / the round-trip test above.
            HttpClientExtensions.ResetProviderCacheForTesting();

            var configPath = AppConfig.GetConfigPath();
            if (File.Exists(configPath)) File.Delete(configPath);

            var tokensDir = PathHelpers.ConfigPath("tokens");
            if (Directory.Exists(tokensDir)) Directory.Delete(tokensDir, recursive: true);

            var legacyTokens = PathHelpers.ConfigPath("tokens.json");
            if (File.Exists(legacyTokens)) File.Delete(legacyTokens);

            Environment.CurrentDirectory = repoDir;
            Environment.SetEnvironmentVariable("HOME", home);

            return new HandleAsyncE2EFixture(repoDir, home, originalCwd, originalHome);
        }

        public ValueTask DisposeAsync() {
            Environment.CurrentDirectory = _originalCwd;
            Environment.SetEnvironmentVariable("HOME", _originalHome);
            HttpClientExtensions.ResetProviderCacheForTesting();

            try { Directory.Delete(RepoDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(Home, recursive: true); } catch { /* best effort */ }

            return ValueTask.CompletedTask;
        }

        static async Task RunGitAsync(string arguments, string workingDir) {
            var psi = new ProcessStartInfo("git", arguments) {
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) {
                var err = await process.StandardError.ReadToEndAsync();

                throw new InvalidOperationException($"git {arguments} failed: {err}");
            }
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-test-{Guid.NewGuid().ToString("N")[..8]}"
        );

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, true); } catch {
                /* best effort */
            }
        }
    }
}
