using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;
using Spectre.Console;
using Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Commands;

public static class SetupCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var serverUrlArg     = GetArg(args, "--server-url");

        // `kcap setup <tenant>`: a leading positional arg (bare slug or full URL) is treated as the
        // server, equivalent to --server-url. A bare single label expands to {slug}.kcap.ai.
        if (serverUrlArg is null && args.Length > 1 && !args[1].StartsWith('-'))
            serverUrlArg = ResolveTenantArg(args[1]);
        var noPrompt         = args.Contains("--no-prompt");
        var forceDevice      = args.Contains("--device");
        var skipClaudeFlag   = args.Contains("--skip-claude-hooks");
        var skipCodexFlag    = args.Contains("--skip-codex-hooks");
        var skipCodexNetworkFlag = args.Contains("--skip-codex-network-access");
        var skipCursorFlag   = args.Contains("--skip-cursor-hooks");
        var skipCursorMcpFlag = args.Contains("--skip-cursor-mcp");
        var skipCopilotFlag  = args.Contains("--skip-copilot-hooks");
        var skipCopilotMcpFlag = args.Contains("--skip-copilot-mcp");
        var skipCopilotInstructionsFlag = args.Contains("--skip-copilot-instructions");
        var skipGeminiFlag   = args.Contains("--skip-gemini-hooks");
        var skipGeminiMcpFlag = args.Contains("--skip-gemini-mcp");
        var skipGeminiInstructionsFlag = args.Contains("--skip-gemini-instructions");
        var skipKiroFlag     = args.Contains("--skip-kiro-hooks");
        var skipKiroMcpFlag  = args.Contains("--skip-kiro-mcp");
        var skipKiroSkillsFlag = args.Contains("--skip-kiro-skills");
        var skipPiFlag       = args.Contains("--skip-pi-hooks");
        var skipPiMcpFlag    = args.Contains("--skip-pi-mcp");
        var skipPiInstructionsFlag = args.Contains("--skip-pi-instructions");
        var skipOpenCodeFlag = args.Contains("--skip-opencode-hooks");
        var skipOpenCodeMcpFlag = args.Contains("--skip-opencode-mcp");
        var skipOpenCodeInstructionsFlag = args.Contains("--skip-opencode-instructions");
        var skipAntigravityFlag = args.Contains("--skip-antigravity-hooks");
        var skipAntigravityMcpFlag = args.Contains("--skip-antigravity-mcp");
        var skipAntigravityInstructionsFlag = args.Contains("--skip-antigravity-instructions");
        var skipAntigravitySkillsFlag = args.Contains("--skip-antigravity-skills");
        var skipImport       = args.Contains("--skip-import");
        var legacyPluginScope = GetArg(args, "--plugin-scope"); // "user" | "project" | "skip" | null
        var skipClaude       = skipClaudeFlag || legacyPluginScope == "skip";
        var legacyProjectScope = legacyPluginScope == "project";

        // Resolve repo root once and reuse for both the project-scope install path and the
        // non-repo tip at the end. --plugin-scope project writes hooks at <repo>/.claude/...,
        // so it requires a working tree; without one the hooks would land in a directory
        // unrelated to any project, or — worse — under a subdirectory of the repo if we
        // used cwd directly, which means two devs running setup from different subdirs
        // install hooks in different places.
        var gitRoot = GitRepository.FindRoot(Environment.CurrentDirectory);

        if (legacyProjectScope && gitRoot is null) {
            await Console.Error.WriteLineAsync(
                $"--plugin-scope project requires a git working tree, but '{Environment.CurrentDirectory}' is not inside one.");
            await Console.Error.WriteLineAsync(
                "Either re-run `kcap setup` from inside your repo, or drop --plugin-scope project to install user-scope hooks.");
            return 1;
        }

        AnsiConsole.Write(new Rule("[bold green]Welcome to Capacitor[/]").Centered());

        // Check if already configured
        var existingProfile = await AppConfig.LoadProfileConfig();
        var activeProfile   = string.IsNullOrWhiteSpace(existingProfile.ActiveProfile) ? "default" : existingProfile.ActiveProfile;
        var existing        = existingProfile.Profiles.GetValueOrDefault(activeProfile);
        var existingTokens  = await TokenStore.LoadAsync();

        if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
            var rerun = AnsiConsole.Prompt(
                new ConfirmationPrompt($"Already configured for [cyan]{Markup.Escape(existing.ServerUrl)}[/] as [cyan]{Markup.Escape(existingTokens.GitHubUsername ?? "?")}[/]. Re-run setup?")
                    { DefaultValue = false });

            if (!rerun) {
                AnsiConsole.MarkupLine("[dim]Setup cancelled.[/]");

                return 0;
            }
        }

        // Step 1: Server
        AnsiConsole.Write(new Rule("[yellow]Step 1/6 — Server[/]").LeftJustified());
        string serverUrl;
        string? preAuthToken = null;
        string  provider;
        bool    loginComplete = false; // WorkOS discovery authenticates inline; skip the Step-2 login.

        if (serverUrlArg is not null) {
            var normalized = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Checking server…",
                async _ => await ServerUrlNormalizer.NormalizeAsync(
                    serverUrlArg, skipProbe: false, CancellationToken.None));

            if (!normalized.Reachable) {
                AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(normalized.Warning ?? serverUrlArg)}");
                AnsiConsole.MarkupLine("  [dim]Check the URL is correct and the server is running.[/]");
                return 1;
            }

            serverUrl = normalized.Url;
            await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");

            // Reachable, but with an informational warning (e.g. https→http downgrade).
            if (normalized.Warning is not null)
                AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(normalized.Warning)}");

            try {
                provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl);
                AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{Markup.Escape(provider)}[/]");
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");
                return 1;
            }
        } else if (noPrompt) {
            await Console.Error.WriteLineAsync("  --server-url is required with --no-prompt");
            return 1;
        } else {
            var discovered = await RunDiscoveryAsync(args, forceDevice);
            if (discovered is null) return 1;
            (serverUrl, preAuthToken, provider, loginComplete) = discovered.Value;
        }

        await Console.Out.WriteLineAsync();

        // Step 2: Login
        AnsiConsole.Write(new Rule("[yellow]Step 2/6 — Login[/]").LeftJustified());

        if (loginComplete) {
            // WorkOS discovery already authenticated + saved the active (picked) profile.
            var cfgAfter = await AppConfig.LoadProfileConfig();
            var tokens   = await TokenStore.LoadAsync(cfgAfter.ActiveProfile);
            AnsiConsole.MarkupLine($"  [green]✓[/] Logged in as [cyan]{Markup.Escape(tokens?.GitHubUsername ?? "?")}[/]");
        } else if (provider == AuthProvider.None) {
            await Console.Out.WriteLineAsync("  Auth provider is None — no login required.");
        } else if (preAuthToken is not null) {
            var exchangeResult = await OAuthLoginFlow.ExchangeAndSaveAsync(serverUrl, preAuthToken, provider);
            if (exchangeResult != 0) {
                await Console.Error.WriteLineAsync("  Token exchange failed.");
                return 1;
            }
            // Keep formatting consistent with the non-discovery branch
            var tokens = await TokenStore.LoadAsync();
            AnsiConsole.MarkupLine($"  [green]✓[/] Logged in as [cyan]{Markup.Escape(tokens?.GitHubUsername ?? "?")}[/]");
        } else {
            var loginResult = await OAuthLoginFlow.LoginWithDiscoveryAsync(serverUrl, forceDevice);

            if (loginResult != 0) {
                await Console.Error.WriteLineAsync("  Login failed.");

                return 1;
            }

            var tokens = await TokenStore.LoadAsync();
            await Console.Out.WriteLineAsync($"  ✓ Logged in as {tokens?.GitHubUsername}");
        }

        await Console.Out.WriteLineAsync();

        // Step 3: Default session visibility
        AnsiConsole.Write(new Rule("[yellow]Step 3/6 — Default session visibility[/]").LeftJustified());

        string defaultVisibility;

        if (noPrompt) {
            defaultVisibility = (GetArg(args, "--default-visibility") ?? "org_public").ToLowerInvariant();

            if (!AppConfig.ValidVisibilities.Contains(defaultVisibility)) {
                await Console.Error.WriteLineAsync($"  Invalid default-visibility: {defaultVisibility}. Must be: {string.Join(", ", AppConfig.ValidVisibilities)}");

                return 1;
            }

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        } else {
            defaultVisibility = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Which of your sessions should be readable by other users in the same Kurrent Capacitor account by default?")
                    .AddChoices(AppConfig.ValidVisibilities)
                    .UseConverter(v => v switch {
                        "private"    => "All private — only you can see your sessions",
                        "project"    => "Project repos public to fellow project members, others private",
                        "org_public" => "Org repos public, others private (default)",
                        "public"     => "All public — others can see all your sessions",
                        _            => v
                    }));

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        }

        await Console.Out.WriteLineAsync();

        // Step 4: Coding agents
        AnsiConsole.Write(new Rule("[yellow]Step 4/6 — Coding agents[/]").LeftJustified());
        await Console.Out.WriteLineAsync("  Capacitor records sessions by installing hooks into your coding agent CLIs.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();
        var detected   = new CodingAgentsStep.DetectedAgents(
            Claude:  AgentDetector.IsInstalled("claude"),
            Codex:   AgentDetector.IsInstalled("codex"),
            Cursor:  CursorPaths.IsInstalled(),
            // Dir presence covers users who launch Copilot through an IDE
            // wrapper; the PATH probe covers fresh installs that haven't run
            // yet (no ~/.copilot until first launch).
            Copilot: CopilotPaths.IsInstalled() || AgentDetector.IsInstalled("copilot"),
            // Dir presence covers IDE-launched Gemini; the PATH probe covers a
            // fresh install that hasn't created ~/.gemini yet.
            Gemini:  GeminiPaths.IsInstalled()  || AgentDetector.IsInstalled("gemini"),
            // Same dual signal for Kiro: the ~/.kiro tree or the conversation DB
            // covers IDE-launched users; the PATH probe (kiro / kiro-cli) covers
            // fresh CLI installs.
            Kiro:    KiroPaths.IsInstalled() || AgentDetector.IsInstalled("kiro") || AgentDetector.IsInstalled("kiro-cli"),
            // Pi keeps state under ~/.pi/agent; the PATH probe covers fresh
            // installs that haven't created it yet.
            Pi:      PiPaths.IsInstalled() || AgentDetector.IsInstalled("pi"),
            // OpenCode keeps config under ~/.config/opencode + data under
            // ~/.local/share/opencode; the PATH probe covers fresh installs.
            OpenCode: OpenCodePaths.IsInstalled() || AgentDetector.IsInstalled("opencode"),
            // Antigravity (GUI IDE) keeps state under ~/.gemini/antigravity; the PATH
            // probe covers a CLI/fresh install that hasn't created it yet.
            Antigravity: AntigravityPaths.IsInstalled() || AgentDetector.IsInstalled("antigravity"));

        bool PromptYesNo(string text) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(text) { DefaultValue = true });

        var detectedSummary = SetupDecisions.DetectedAgentsSummary(detected);

        if (detectedSummary is not null)
            await Console.Out.WriteLineAsync($"  Detected coding agents: {detectedSummary}");

        // The single install-consent decision, replacing the nine per-vendor prompts. Made
        // BEFORE CodingAgentsStep.Options is constructed, so it uses the LOCAL `noPrompt` (there
        // is no `options` object yet). NoPrompt alone would not imply InstallAgents, so this must
        // be set explicitly here or `--no-prompt` would silently stop installing agents.
        var installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt, PromptYesNo);

        // gitRoot is guaranteed non-null here when legacyProjectScope is true (the early
        // guard at the top of HandleAsync returns 1 otherwise).
        var claudeSettingsPath = legacyProjectScope
            ? Path.Combine(gitRoot!, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        var stepOptions = new CodingAgentsStep.Options(
            SkipClaude:  skipClaude,
            SkipCodex:   skipCodexFlag,
            SkipCursor:  skipCursorFlag,
            SkipCopilot: skipCopilotFlag,
            SkipGemini:  skipGeminiFlag,
            SkipKiro:    skipKiroFlag,
            SkipPi:      skipPiFlag,
            SkipOpenCode: skipOpenCodeFlag,
            SkipAntigravity: skipAntigravityFlag,
            NoPrompt:    noPrompt,
            SkipCodexNetworkAccess: skipCodexNetworkFlag,
            SkipCursorMcp: skipCursorMcpFlag,
            SkipCopilotMcp: skipCopilotMcpFlag,
            SkipCopilotInstructions: skipCopilotInstructionsFlag,
            SkipGeminiMcp: skipGeminiMcpFlag,
            SkipGeminiInstructions: skipGeminiInstructionsFlag,
            SkipAntigravityMcp: skipAntigravityMcpFlag,
            SkipAntigravityInstructions: skipAntigravityInstructionsFlag,
            SkipAntigravitySkills: skipAntigravitySkillsFlag,
            SkipOpenCodeMcp: skipOpenCodeMcpFlag,
            SkipOpenCodeInstructions: skipOpenCodeInstructionsFlag,
            SkipKiroMcp: skipKiroMcpFlag,
            SkipKiroSkills: skipKiroSkillsFlag,
            SkipPiMcp: skipPiMcpFlag,
            SkipPiInstructions: skipPiInstructionsFlag,
            InstallAgents: installAgents);

        // allowlist the Capacitor server(s) Codex skills need to reach. A single
        // **.kcap.ai wildcard covers every SaaS tenant (current + future) and the auth
        // proxy; self-hosted servers are added as exact hosts. Derived from the active
        // server URL plus every configured profile so switching profiles still works.
        var profilesForDomains = await AppConfig.LoadProfileConfig();
        var codexAllowDomains  = CodexConfigToml.BuildAllowDomains(
            new[] { serverUrl }.Concat(profilesForDomains.Profiles.Values.Select(p => p.ServerUrl)));

        var stepPaths = new CodingAgentsStep.Paths(
            ClaudeSettingsPath:   claudeSettingsPath,
            ClaudeScopeLabel:     legacyProjectScope ? "project" : "user",
            PluginDir:            pluginPath,
            CodexHooksPath:       CodexPaths.UserHooksJson,
            CursorHooksPath:      CursorPaths.UserHooksJson(),
            CopilotHooksPath:     CopilotPaths.KcapHooksJson(),
            GeminiSettingsPath:   GeminiPaths.SettingsJson(),
            AgentsSkillsDir:      AgentsPaths.UserSkillsDir,
            LegacyCodexSkillsDir: Path.Combine(CodexPaths.Home(), "skills"),
            KiroHooksPath:        KiroPaths.KcapAgentJson(),
            PiExtensionPath:      PiPaths.KcapExtension(),
            OpenCodeExtensionPath: OpenCodePaths.KcapPlugin(),
            AntigravityHooksPath: AntigravityPaths.GlobalHooksJson(),
            CodexConfigTomlPath:  Path.Combine(CodexPaths.Home(), "config.toml"),
            CursorMcpPath:        CursorPaths.UserMcpJson(),
            CopilotMcpPath:       CopilotPaths.McpConfigJson(),
            CopilotInstructionsPath: CopilotPaths.InstructionsMd(),
            GeminiInstructionsPath: GeminiPaths.GeminiMd(),
            AntigravityMcpPath:       AntigravityPaths.McpConfigJson(),
            AntigravityInstructionsPath: AntigravityPaths.InstructionsMd(),
            AntigravitySkillsDir:     AntigravityPaths.SkillsDir(),
            OpenCodeMcpPath:      OpenCodePaths.McpConfigJson(),
            OpenCodeInstructionsPath: OpenCodePaths.AgentsMd(),
            KiroMcpPath:          KiroPaths.SettingsMcpJson(),
            KiroSkillsDir:        KiroPaths.SkillsDir(),
            PiMcpExtensionPath:   PiPaths.KcapMcpExtension(),
            PiAgentsMdPath:       PiPaths.AgentsMd());

        var stepInstallers = new CodingAgentsStep.Installers(
            InstallClaudePlugin:    InstallPlugin,
            InstallCodexHooks:      PluginCommand.InstallCodexHooks,
            InstallCursorHooks:     PluginCommand.InstallCursorHooks,
            InstallCopilotHooks:    PluginCommand.InstallCopilotHooks,
            InstallGeminiHooks:     PluginCommand.InstallGeminiHooks,
            CapacitorOnPath:        () => AgentDetector.IsInstalled("kcap"),
            InstallAgentSkills:     AgentsSkillsInstaller.Install,
            CleanLegacyCodexSkills: legacyDir => AgentsSkillsInstaller.CleanLegacyCodexSkills(legacyDir).RemovedAny,
            InstallKiroHooks:       PluginCommand.InstallKiroHooks,
            InstallPiExtension:     PiExtensionInstaller.Install,
            InstallOpenCodeExtension: OpenCodeExtensionInstaller.Install,
            InstallAntigravityHooks:  PluginCommand.InstallAntigravityHooks,
            EnableCodexNetworkAccess: () => CodexConfigToml.EnableNetworkAccess(codexAllowDomains),
            RegisterCodexMcp:         () => CodexConfigToml.RegisterKcapMcpServers(),
            // every non-Claude JSON harness registers the ForCursor subset — kcap-workitems
            // is a Claude Code plugin-only tool (its session-id default rides the Claude hook env).
            RegisterCursorMcp:        () => JsonMcpConfigWriter.Register(
                CursorPaths.UserMcpJson(), KcapMcpServers.ForCursor, McpConfigShape.Standard, cwd: null, new McpMarker("cursor")),
            RegisterCopilotMcp:       () => JsonMcpConfigWriter.Register(
                CopilotPaths.McpConfigJson(), KcapMcpServers.ForCursor, McpConfigShape.Copilot, cwd: null, new McpMarker("copilot")),
            InstallCopilotInstructions: () => AgentInstructionsWriter.Write(
                CopilotPaths.InstructionsMd(), KcapAgentInstructions.Body),
            // Skills are already current when the on-disk marker matches this build AND
            // every owned kcap-* folder is present; used to skip the prompt + re-copy
            // (mirrors PluginCommand's postinstall fast path). A missing/stale marker — or a
            // deleted skill folder — reads as "not current" → prompt + install (self-heals).
            AgentSkillsCurrent:       AgentsSkillsInstaller.IsCurrent,
            RegisterOpenCodeMcp:      () => JsonMcpConfigWriter.Register(
                OpenCodePaths.McpConfigJson(), KcapMcpServers.ForCursor, McpConfigShape.OpenCode, cwd: null, new McpMarker("opencode")),
            InstallOpenCodeInstructions: () => AgentInstructionsWriter.Write(
                OpenCodePaths.AgentsMd(), KcapAgentInstructions.Body),
            RegisterKiroMcp:          () => JsonMcpConfigWriter.Register(
                KiroPaths.SettingsMcpJson(), KcapMcpServers.ForCursor, McpConfigShape.Standard, cwd: null, new McpMarker("kiro")),
            RegisterGeminiMcp:        () => JsonMcpConfigWriter.Register(
                GeminiPaths.SettingsJson(), KcapMcpServers.ForCursor, McpConfigShape.Gemini, cwd: null, new McpMarker("gemini")),
            InstallGeminiInstructions: () => AgentInstructionsWriter.Write(
                GeminiPaths.GeminiMd(), KcapAgentInstructions.Body),
            RegisterAntigravityMcp:   () => JsonMcpConfigWriter.Register(
                AntigravityPaths.McpConfigJson(), KcapMcpServers.ForCursor, McpConfigShape.Standard, cwd: null, new McpMarker("antigravity")),
            InstallAntigravityInstructions: () => AgentInstructionsWriter.Write(
                AntigravityPaths.InstructionsMd(), KcapAgentInstructions.Body),
            // Pi has no JSON MCP config — the "MCP" is a second extension file (kcap-mcp.ts).
            InstallPiMcp:             PiMcpExtensionInstaller.Install,
            InstallPiInstructions:    () => AgentInstructionsWriter.Write(
                PiPaths.AgentsMd(), KcapAgentInstructions.Body));

        void WriteLine(string line) => AnsiConsole.MarkupLine(line);

        var installResult = await CodingAgentsStep.RunAsync(
            stepOptions, detected, stepPaths, stepInstallers, PromptYesNo, WriteLine);

        // Provider API key handling. kcap scrubs ANTHROPIC_API_KEY / OPENAI_API_KEY
        // from headless agent CLI spawns by default so subscription auth
        // wins. PAYG users with the keys set in their environment can opt back in
        // here; the rest never see this prompt.
        var anthropicSet     = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        var openaiSet        = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var promptApiKey      = (anthropicSet && !skipClaude) || (openaiSet && !skipCodexFlag);
        // Preserve any previous opt-in when no key is in the current env (we just
        // don't have anything to prompt about; the on-disk value is still valid).
        var useProviderApiKey = existing?.UseProviderApiKey ?? false;

        if (promptApiKey) {
            await Console.Out.WriteLineAsync();

            var keys = (anthropicSet, openaiSet) switch {
                (true, true)  => "ANTHROPIC_API_KEY and OPENAI_API_KEY are set",
                (true, false) => "ANTHROPIC_API_KEY is set",
                (false, true) => "OPENAI_API_KEY is set",
                _             => ""
            };

            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(keys)} in your environment.[/]");
            AnsiConsole.MarkupLine("  [dim]By default kcap scrubs these when spawning Claude/Codex for headless calls so your[/]");
            AnsiConsole.MarkupLine("  [dim]subscription login is used. Keep them if you authenticate via API key (PAYG).[/]");

            if (noPrompt) {
                var flagValue = GetArg(args, "--use-provider-api-key");
                if (flagValue is not null) {
                    var parsed = ProviderApiKeyPolicy.TryParseBool(flagValue);
                    if (parsed is null) {
                        await Console.Error.WriteLineAsync(
                            $"  Invalid value for --use-provider-api-key: '{flagValue}'. Must be true/1/yes/on or false/0/no/off.");
                        return 1;
                    }
                    useProviderApiKey = parsed.Value;
                }
                await Console.Out.WriteLineAsync($"  Use provider API key: {useProviderApiKey}");
            } else {
                useProviderApiKey = AnsiConsole.Prompt(
                    new ConfirmationPrompt("  Use these API keys for kcap's headless calls?") { DefaultValue = useProviderApiKey });
            }
        }

        await Console.Out.WriteLineAsync();

        // Step 5: Daemon name + save
        AnsiConsole.Write(new Rule("[yellow]Step 5/6 — Agent Daemon[/]").LeftJustified());

        var    defaultName = Environment.UserName.ToLowerInvariant();
        string daemonName;

        if (noPrompt) {
            daemonName = GetArg(args, "--daemon-name") ?? defaultName;
            await Console.Out.WriteLineAsync($"  Daemon name: {daemonName}");
        } else {
            daemonName = AnsiConsole.Prompt(
                new TextPrompt<string>("Daemon name:")
                    .DefaultValue(defaultName)
                    .ShowDefaultValue());
        }

        await Console.Out.WriteLineAsync();

        // Save config
        var profileConfig  = await AppConfig.LoadProfileConfig();
        var activeName     = profileConfig.ActiveProfile;
        var defaultProfile = profileConfig.Profiles.GetValueOrDefault(activeName) ?? new Profile();

        defaultProfile = defaultProfile with {
            ServerUrl          = serverUrl,
            DefaultVisibility  = defaultVisibility,
            UseProviderApiKey  = useProviderApiKey,
            Daemon             = (defaultProfile.Daemon ?? new DaemonSettings()) with { Name = daemonName }
        };

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) {
            [activeName] = defaultProfile
        };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        // Refresh the in-process resolved state to the exact values just
        // saved, so any same-process work after this point (e.g. the import
        // step) observes this server URL + profile rather than re-resolving
        // CLI/env/repo precedence and possibly landing on something else.
        AppConfig.SetResolvedState(serverUrl, activeName, defaultProfile);

        var finalTokens = await TokenStore.LoadAsync();

        // tell the server this user has finished CLI setup, so the dashboard
        // can flip the new-tenant welcome modal from "Waiting for CLI to register"
        // to "Registered". Best-effort — never block setup completion on this.
        await PingCliSetupAsync(serverUrl);

        await Console.Out.WriteLineAsync();

        // Step 6: Import past sessions
        AnsiConsole.Write(new Rule("[yellow]Step 6/6 — Import past sessions[/]").LeftJustified());

        // detectPullRequest:false — Step 6 only needs (owner, name) to scope the repo import;
        // PR/MR detection would run extra provider probes/subprocesses for nothing here.
        var currentRepoDetected = await RepositoryDetection.DetectRepositoryAsync(
            Environment.CurrentDirectory, detectPullRequest: false);
        (string Owner, string Name)? currentRepo = currentRepoDetected is { Owner: { } o, RepoName: { } n }
            ? (o, n)
            : null;

        // Auth requirements are satisfied when no login is required at all (provider
        // None), or the login this run just did (or already had) produced a usable,
        // non-expired token — not merely "provider != None" (Decision 9).
        // "usable token" = refresh-aware (mirrors the import path's own auth): an expired but
        // refreshable token still counts. The probe is wrapped so a token I/O / refresh failure
        // degrades to an ineligible (best-effort) skip rather than throwing out of setup — the
        // import path's own errors are caught inside RunImportStepAsync, and this eligibility
        // probe (awaited outside that boundary) must be equally non-fatal.
        var authSatisfied = await IsAuthSatisfiedAsync(
            provider, static async () => await TokenStore.GetValidTokensAsync() is not null);

        await RunImportStepAsync(
            currentRepo, authSatisfied, skipImport, noPrompt,
            () => AnsiConsole.Prompt(new ConfirmationPrompt("Import past sessions from this repository?") { DefaultValue = true }),
            serverUrl, activeName, defaultVisibility);

        await Console.Out.WriteLineAsync();

        AnsiConsole.Write(new Rule("[green]Setup complete[/]").LeftJustified());

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Server[/]",     Markup.Escape(serverUrl));
        grid.AddRow("[bold]Visibility[/]", Markup.Escape(defaultVisibility));
        grid.AddRow("[bold]Daemon[/]",     Markup.Escape(daemonName));

        if (useProviderApiKey) {
            grid.AddRow("[bold]Provider API key[/]", "kept in headless spawns");
        }

        if (finalTokens is not null) {
            grid.AddRow("[bold]Auth[/]", Markup.Escape($"{finalTokens.GitHubUsername} ({finalTokens.Provider})"));
        }

        grid.AddRow("[bold]Config[/]", Markup.Escape(AppConfig.GetConfigPath()));

        AnsiConsole.Write(grid);

        // hooks only load at coding-agent session start. The common case is a user
        // running `kcap setup` from inside an already-running session, which won't stream
        // live until it restarts — so tell them, but only when something was actually
        // installed (no point promising recording we never wired up).
        var restartTip = LiveRecordingRestartTip(installResult);
        if (restartTip is not null) AnsiConsole.MarkupLine($"\n{restartTip}");

        // Setup itself is user-scope and works fine outside a repo, but sessions recorded
        // from non-repo directories have no owner/repo/branch/PR enrichment (see
        // RepositoryDetection.DetectRepositoryAsync), which weakens grouping in the UI.
        if (gitRoot is null) {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Tip:[/] you ran setup outside a git working tree ([dim]{Markup.Escape(Environment.CurrentDirectory)}[/]).");
            AnsiConsole.MarkupLine(
                "  Hooks fire from any directory, but sessions recorded outside a repo won't include owner/repo/branch context.");
            AnsiConsole.MarkupLine(
                "  [dim]cd[/] into your project before recording to capture full session context.");
        }

        AnsiConsole.MarkupLine("\n[dim]Optional:[/] start the daemon with [cyan]kcap daemon start -d[/]");
        AnsiConsole.MarkupLine("[dim]Optional:[/] import past sessions with [cyan]kcap import --org[/]");

        // Skipped agent setup means nothing is wired up to answer the prompt — pointing at the
        // tour there would contradict the "no agent detected" warning printed moments earlier.
        // An already-current install still prints: its Installed flags are false too.
        if (!installResult.AgentSetupSkipped) {
            AnsiConsole.Write(
                new Panel($"[bold]{Markup.Escape(GuidedTourCallToAction)}[/]")
                    .BorderColor(Color.Green)
                    .Padding(1, 0));
        }

        return 0;
    }

    /// <summary>
    /// A prompt rather than a slash command, because the invocation differs per vendor. Pinned
    /// against the skill's trigger list by <c>SetupCommandTests</c>.
    /// </summary>
    internal const string GuidedTourCallToAction =
        "Prompt \"Start kcap guided tour\" in your agent for a guided tour of Capacitor";

    /// <summary>
    /// Whether Step 6's import eligibility auth requirement is met: provider <c>None</c> needs no
    /// token; any other provider needs a usable (valid-or-refreshable) token. The token probe is
    /// injected so it's testable, and any exception it throws is treated as "not satisfied" — this
    /// probe is awaited OUTSIDE <see cref="RunImportStepAsync"/>'s try/catch, so it must never
    /// throw out of setup (the optional import failing must not fail <c>kcap setup</c>).
    /// </summary>
    internal static async Task<bool> IsAuthSatisfiedAsync(string provider, Func<Task<bool>> hasUsableToken) {
        if (provider == AuthProvider.None) return true;

        try {
            return await hasUsableToken();
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Step 6 (import past sessions) decision + best-effort execution, extracted from
    /// <see cref="HandleAsync"/> so it's unit-testable without driving the whole wizard: the
    /// eligibility/policy decision goes through <see cref="SetupDecisions.DecideImport"/>, and the
    /// actual import call goes through <see cref="ImportRunnerOverride"/> (the real
    /// <see cref="ImportCommand.HandleImport"/> when null) so tests can intercept the invocation
    /// instead of running a real import. Import is best-effort: a thrown exception or a non-zero
    /// exit code is reported with a warning and swallowed — this method never throws and never
    /// fails setup.
    /// </summary>
    internal static async Task RunImportStepAsync(
            (string Owner, string Name)? currentRepo,
            bool                          authSatisfied,
            bool                          skipImport,
            bool                          noPrompt,
            Func<bool>                    promptYesNo,
            string                        serverUrl,
            string                        activeProfile,
            string                        defaultVisibility) {
        var decision = SetupDecisions.DecideImport(
            currentRepo is not null, authSatisfied, skipImport, noPrompt, promptYesNo);

        if (decision.Outcome == SetupDecisions.ImportOutcome.Skip) {
            if (decision.SkipReason is not null)
                AnsiConsole.MarkupLine($"  [dim]Skipping import — {Markup.Escape(decision.SkipReason)}.[/]");

            return;
        }

        // Run: DecideImport only returns Run when hasCurrentRepo was true, so currentRepo is
        // guaranteed non-null here.
        var invocation = new ImportInvocation(
            BaseUrl:            serverUrl,
            Repo:               currentRepo!.Value,
            DefaultVisibility:  defaultVisibility,
            AutoSkipExclusions: true,
            ForcePrivate:       false,
            ActiveProfile:      activeProfile);

        try {
            var exitCode = await (ImportRunnerOverride ?? DefaultImportRunner)(invocation);

            if (exitCode != 0) {
                AnsiConsole.MarkupLine(
                    "  [yellow]⚠[/] Import of past sessions did not complete. Run [cyan]kcap import[/] manually to retry.");
            }
        } catch (Exception ex) {
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠[/] Import of past sessions failed: {Markup.Escape(ex.Message)}. Run [cyan]kcap import[/] manually to retry.");
        }
    }

    /// <summary>
    /// The arguments Step 6 pins into its embedded <see cref="ImportCommand.HandleImport"/> call.
    /// A record (not a bare argument list) so tests can capture and assert on it via
    /// <see cref="ImportRunnerOverride"/> without running a real import.
    /// </summary>
    internal sealed record ImportInvocation(
        string                       BaseUrl,
        (string Owner, string Name) Repo,
        string?                      DefaultVisibility,
        bool                         AutoSkipExclusions,
        bool                         ForcePrivate,
        string                       ActiveProfile);

    /// <summary>
    /// Test seam: when set, replaces the real <see cref="ImportCommand.HandleImport"/> call made
    /// by <see cref="RunImportStepAsync"/>. Process-global static state — tests must reset it to
    /// null (in a finally block) after use.
    /// </summary>
    internal static Func<ImportInvocation, Task<int>>? ImportRunnerOverride;

    static Task<int> DefaultImportRunner(ImportInvocation inv) =>
        ImportCommand.HandleImport(
            baseUrl:                 inv.BaseUrl,
            filterCwd:               null,
            filterSession:           null,
            minLines:                15,
            generateSummaries:       false,
            sources:                 BuildImportSources(),
            explicitVendorSelection: false,
            since:                   null,
            scope:                   new ImportScope.Repo(inv.Repo.Owner, inv.Repo.Name),
            skipConfirmation:        true,
            forcePrivate:            inv.ForcePrivate,
            activeProfile:           inv.ActiveProfile,
            currentRepo:             inv.Repo,
            needOrgPick:             false,
            storedOrg:               null,
            autoSkipExclusions:      inv.AutoSkipExclusions,
            defaultVisibility:       inv.DefaultVisibility);

    /// <summary>The nine supported import sources — mirrors Program.cs's `kcap import` construction.</summary>
    static IReadOnlyList<IImportSource> BuildImportSources() => new IImportSource[] {
        new ClaudeImportSource(),
        new CodexImportSource(),
        new CursorImportSource(),
        new CopilotImportSource(),
        new GeminiImportSource(),
        new KiroImportSource(),
        new PiImportSource(),
        new OpenCodeImportSource(),
        new AntigravityImportSource(),
    };

    static async Task<(string ServerUrl, string? PreAuthToken, string Provider, bool LoginComplete)?> RunDiscoveryAsync(
            string[] args, bool forceDevice) {
        AnsiConsole.MarkupLine($"  Proxy: [dim]{Markup.Escape(AuthProxyEndpoint.Url)}[/]");

        using var http  = new HttpClient();
        var proxyClient = new AuthProxyClient(http);

        var proxyConfig = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Contacting auth service…",
            async _ => await proxyClient.GetConfigAsync(AuthProxyEndpoint.Url));
        if (proxyConfig is null) {
            AnsiConsole.MarkupLine("  [red]✗[/] Cannot reach the Kurrent auth service. Retry later, or pass --server-url <url>.");
            return null;
        }

        var provider = OAuthLoginFlow.ChooseDiscoveryProvider(args, isInteractive: !HeadlessEnvironment.IsHeadless());

        if (provider == AuthProvider.WorkOS) {
            // Offer inline tenant creation only in an interactive session; headless
            // setup keeps the legacy "ask your admin" dead-end (provisioner is null).
            ITenantProvisioner? provisioner = HeadlessEnvironment.IsHeadless()
                ? null
                : new SpectreTenantProvisioner(new TenantProvisioningClient(new HttpClient()), ProvisioningEndpoint.Url);

            var exit = await WorkOSDiscovery.RunWithLiveAuthAsync(
                AuthProxyEndpoint.Url, proxyConfig, proxyClient, new SpectreTenantPicker(), provisioner);
            if (exit != 0) return null;

            // WorkOSDiscovery saved + activated the picked profile; continue setup against it.
            var cfg    = await AppConfig.LoadProfileConfig();
            var active = cfg.Profiles.GetValueOrDefault(cfg.ActiveProfile);
            if (active?.ServerUrl is null) {
                AnsiConsole.MarkupLine("  [red]✗[/] WorkOS sign-in did not set an active profile.");
                return null;
            }
            return (active.ServerUrl, null, AuthProvider.WorkOS, true);
        }

        if (string.IsNullOrEmpty(proxyConfig.GitHubClientId)) {
            AnsiConsole.MarkupLine("  [red]✗[/] Cannot reach the Kurrent auth service. Retry later, or pass --server-url <url>.");
            return null;
        }

        var ghToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            proxyConfig.GitHubClientId, proxyConfig.GitHubCodeExchangeUrl, forceDevice);
        if (ghToken is null) return null;

        var discovery = new TenantDiscovery(proxyClient, new SpectreTenantPicker());
        var outcome   = await discovery.RunAsync(AuthProxyEndpoint.Url, ghToken);

        if (outcome.ErrorMessage is not null) {
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(outcome.ErrorMessage)}");
            return null;
        }

        var profileCfg = await AppConfig.LoadProfileConfig();
        profileCfg     = TenantDiscovery.MergeProfiles(profileCfg, outcome.Tenants, outcome.Picked!);
        await AppConfig.SaveProfileConfig(profileCfg);

        AnsiConsole.MarkupLine($"  [green]✓[/] Discovered {outcome.Tenants.Length} tenant(s). Active: [cyan]{Markup.Escape(outcome.Picked!.OrgLogin)}[/]");

        return (AppConfig.NormalizeUrl(outcome.Picked.Origin), ghToken, AuthProvider.GitHubApp, false);
    }

    internal static string? ResolvePluginPath(string? overrideDir = null) {
        overrideDir ??= Environment.GetEnvironmentVariable("KCAP_PLUGIN_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir)) {
            return overrideDir;
        }

        var exePath = Environment.ProcessPath;

        if (exePath is null) return null;

        var exeDir = Path.GetDirectoryName(exePath);

        if (exeDir is null) return null;

        // Try: <exe_dir>/../../../../plugin  (npm optional-deps layout)
        // Binary is at <wrapper>/node_modules/@kurrent/<platform-pkg>/bin/kcap
        // Plugin is at <wrapper>/plugin
        var optDepsPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "kcap"));

        if (Directory.Exists(optDepsPluginPath))
            return optDepsPluginPath;

        // Try: <exe_dir>/../../kcap/plugin  (npm flat layout)
        var npmPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kcap", "kcap"));

        if (Directory.Exists(npmPluginPath))
            return npmPluginPath;

        // Try: <exe_dir>/../plugin  (wrapper package direct layout)
        var wrapperPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "kcap"));

        if (Directory.Exists(wrapperPluginPath))
            return wrapperPluginPath;

        // Try: repo root layout (dev mode)
        var repoPlugin = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kcap"));

        return Directory.Exists(repoPlugin) ? repoPlugin : null;
    }

    // best-effort signal to the server that this user has completed CLI setup.
    // Silently swallows network/auth/server errors: the welcome-modal nudge is a UX
    // affordance, not part of the contract of `kcap setup`.
    //
    // Two reliability rules (kcap-cli#113 review):
    //   • Don't use HttpClientExtensions.CreateAuthenticatedClientAsync — its
    //     TokenStore.GetValidTokensAsync refresh path makes HTTP calls that
    //     don't honor a CancellationToken and can block far longer than any
    //     CTS-based timeout. The user just logged in moments ago in this same
    //     command, so a non-expired token is the expected case; if it's
    //     missing or expired we silently skip rather than triggering a refresh.
    //   • Cap the operation with Task.WhenAny(ping, Task.Delay(5s)) so the
    //     wall-clock bound is enforced independently of what HttpClient does
    //     internally. If the delay wins, HttpClient disposal on method-exit
    //     cancels the in-flight POST.
    static async Task PingCliSetupAsync(string serverUrl) {
        // The ping is intentionally silent (see method-doc), which also hides why the
        // dashboard welcome modal never flips when it fails — e.g. a token the server
        // rejects or maps to a different identity. Set KCAP_DEBUG to surface the
        // outcome on stderr without changing the best-effort, non-blocking contract.
        var  debug = Environment.GetEnvironmentVariable("KCAP_DEBUG") is { Length: > 0 };
        void Debug(string message) {
            if (debug) Console.Error.WriteLine($"[kcap] cli-setup ping: {message}");
        }

        try {
            var tokens = await TokenStore.LoadAsync();
            if (tokens is null || tokens.IsExpired) {
                Debug(tokens is null ? "skipped — no stored token" : "skipped — token expired");

                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);

            var version = typeof(SetupCommand).Assembly.GetName().Version?.ToString();
            var payload = new StringContent(
                $$"""{"cliVersion":{{(version is null ? "null" : "\"" + version + "\"")}}}""",
                System.Text.Encoding.UTF8,
                "application/json");

            var pingTask = http.PostAsync($"{serverUrl.TrimEnd('/')}/api/users/me/cli-setup", payload);
            var winner   = await Task.WhenAny(pingTask, Task.Delay(TimeSpan.FromSeconds(5)));

            if (winner == pingTask) {
                // Observe the result so any exception is consumed by the outer
                // catch (instead of surfacing as UnobservedTaskException later).
                using var resp = await pingTask;
                Debug($"{(int)resp.StatusCode} {resp.StatusCode} (provider={tokens.Provider})");
            } else {
                // Wall-clock cap hit. HttpClient.Dispose() at method-exit
                // cancels the in-flight POST; observe the orphan so its
                // cancellation exception doesn't go unhandled.
                Debug("timed out after 5s");
                _ = pingTask.ContinueWith(
                    t => {
                        if (t.IsCompletedSuccessfully) t.Result.Dispose();
                        _ = t.Exception; // mark observed
                    },
                    TaskScheduler.Default);
            }
        } catch (Exception e) {
            // Swallow — see method-doc. KCAP_DEBUG surfaces the reason.
            Debug($"failed — {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// the end-of-setup reminder that live recording only starts on a
    /// <em>new</em> coding-agent session. Claude Code (and the other agents) load hooks
    /// at session start, so a session that was already running when setup installed the
    /// hooks keeps running without them and never streams live. Returns the Spectre-markup
    /// note when at least one agent's hooks were installed, or <c>null</c> when nothing was
    /// wired up (so we don't promise recording that can't happen).
    /// </summary>
    internal static string? LiveRecordingRestartTip(CodingAgentsStep.Result result) {
        if (!result.AnyHooksInstalled) return null;

        // The "how to restart" hint is agent-specific: Claude can resume with
        // --continue; Pi loads the kcap extension at process start; Codex/Cursor/
        // Copilot just need a fresh session. Build it from what was actually
        // installed instead of always naming Claude — the old text read as a
        // Claude instruction even on a Pi/Cursor/Copilot-only setup.
        var how = result.ClaudeInstalled
            ? "Restart your agent (or run [cyan]claude --continue[/])"
            : result.PiExtensionInstalled
                ? "Restart [cyan]pi[/] so the kcap extension loads"
                : "Restart your agent";

        return
            "[yellow]![/] Live recording begins on a [bold]new[/] coding-agent session — hooks only load at session start.\n"
          + $"  {how} to begin streaming; a session that was already\n"
          + "  running when you ran setup isn't being recorded yet.";
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    /// <summary>
    /// Resolves a `kcap setup &lt;tenant&gt;` positional: a bare single label (no scheme/dot/port)
    /// expands to <c>https://{slug}.kcap.ai</c>; anything that already looks like a URL, FQDN, or
    /// host:port is returned unchanged for the normal --server-url path. Self-hosted servers should
    /// pass a full URL.
    /// </summary>
    internal static string ResolveTenantArg(string arg) =>
        arg.Contains("://") || arg.Contains('.') || arg.Contains(':')
        || arg.Equals("localhost", StringComparison.OrdinalIgnoreCase) // bare loopback host, not a kcap.ai slug
            ? arg
            : $"https://{arg}.kcap.ai";

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Registers the kcap plugin in a Claude Code settings.json file by merging
    /// the marketplace source and enabling the plugin. Preserves all existing settings.
    /// </summary>
    internal static bool InstallPlugin(string settingsPath, string marketplacePath) {
        try {
            JsonObject root = [];

            if (File.Exists(settingsPath)) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject obj)
                        root = obj;
                } catch {
                    // Malformed JSON — start fresh
                }
            }

            // Ensure extraKnownMarketplaces.kcap exists with the correct path
            if (root["extraKnownMarketplaces"] is not JsonObject marketplaces) {
                marketplaces                   = [];
                root["extraKnownMarketplaces"] = marketplaces;
            }

            marketplaces["kcap"] = new JsonObject {
                ["source"] = new JsonObject {
                    ["source"] = "directory",
                    ["path"]   = marketplacePath
                }
            };

            // Remove stale marketplace entries from earlier shapes
            marketplaces.Remove("kurrent");
            marketplaces.Remove("kapacitor");

            // Ensure enabledPlugins.kcap@kcap is true
            if (root["enabledPlugins"] is not JsonObject enabled) {
                enabled                = [];
                root["enabledPlugins"] = enabled;
            }

            enabled["kcap@kcap"] = true;

            // Remove stale plugin entries from earlier shapes
            enabled.Remove("kcap@kurrent");
            enabled.Remove("kapacitor@kapacitor");
            enabled.Remove("kapacitor@kurrent");

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));

            ClaudePluginInstaller.WriteMarker(settingsPath);

            return true;
        } catch {
            return false;
        }
    }
}
