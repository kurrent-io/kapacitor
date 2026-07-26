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
        await AppConfig.SaveProfileConfig(cfg);

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
            "Prompt \"Start kcap guided tour\" in your agent for a guided tour of Capacitor");
    }

    [Test]
    public async Task GuidedTourCallToAction_prompt_is_a_trigger_in_the_skill_description() {
        // The CTA sends users to a natural-language prompt, not a slash command. If that exact
        // phrase isn't among the skill's advertised triggers, the only discovery path we ship
        // depends on the agent guessing. Keep the two pinned together.
        var skill = Path.Combine(RepoRoot(), "kcap", "skills", "guided-tour", "SKILL.md");

        await Assert.That(File.Exists(skill)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(skill)).Contains("Start kcap guided tour");
    }

    /// <summary>Walks up from the test binary to the repo root (the directory holding `kcap/`).</summary>
    static string RepoRoot() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null && !Directory.Exists(Path.Combine(dir, "kcap", "skills")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new DirectoryNotFoundException("Could not locate the repo root from the test binary.");
    }

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
