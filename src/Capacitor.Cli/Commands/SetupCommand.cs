using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Kiro;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Harness.Pi;
using Spectre.Console;
using Spectre.Console.Rendering;
using Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Commands;

/// <summary>Setup's step-scoped rendering of façade output: every non-flush line is two-space indented, and setup still owns the guidance tail.</summary>
sealed class SetupAuthProgress(IAuthProgress inner) : IAuthProgress {
    internal const string UnreachableGuidance = "  Retry later, or pass --server-url <url>.";

    public void Notice(string message) => inner.Notice(Indent(message));

    public void Error(string message) => inner.Error(Indent(message));

    public void BrowserOpening(string url) => inner.BrowserOpening(url);

    public void DeviceCode(string code, string verificationUri, string? provider, bool prefilled) => inner.DeviceCode(code, verificationUri, provider, prefilled);

    public void PollTick() => inner.PollTick();

    /// <summary>Mapped off <see cref="AuthFailureReason"/>, never off the rendered text.</summary>
    public void ReportFailure(AuthResult result) {
        if (result is AuthResult.Failed { Reason: AuthFailureReason.Unreachable }) inner.Error(UnreachableGuidance);
    }

    // Blank separators and already-indented copy (the device-flow numbered list) pass through as-is.
    internal static string Indent(string message) =>
        string.IsNullOrWhiteSpace(message) || message.StartsWith(' ') ? message : $"  {message}";
}

/// <summary>The browser leg's rendering, at setup's two-space indent. The URL is printed whether
/// or not a browser opened: a machine with no browser of its own has a human at a different one.</summary>
sealed class SpectreFirstRunFlowProgress : IFirstRunFlowProgress {
    bool   _waiting;
    string? _url;
    int    _ticks;

    public void Opening(string setupUrl) {
        _waiting = true;
        _url     = setupUrl;

        AnsiConsole.MarkupLine(SetupAuthProgress.Indent("Opening your browser to finish setup."));
        AnsiConsole.MarkupLine(SetupAuthProgress.Indent($"[dim]If it didn't open:[/]  {Markup.Escape(setupUrl)}"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(SetupAuthProgress.Indent("[dim]Press any key to carry on here instead.[/]"));
        AnsiConsole.WriteLine();
        AnsiConsole.Markup(SetupAuthProgress.Indent("Waiting…"));
    }

    public void PollTick() {
        // Every thirty ticks (~a minute) the URL is reprinted: the dots would otherwise scroll the
        // one line a machine with no browser of its own needs to read off a standard terminal.
        if (++_ticks % 30 == 0 && _url is { } url) {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(SetupAuthProgress.Indent($"[dim]If it didn't open:[/]  {Markup.Escape(url)}"));
            AnsiConsole.Markup(SetupAuthProgress.Indent("Waiting…"));
        }

        AnsiConsole.Write(".");
    }

    public void WaitEnded() {
        if (!_waiting) return;

        _waiting = false;
        AnsiConsole.WriteLine();
    }
}

public sealed class SetupCommand(ConfigRoot config, ProfileContext profiles, IBrowserLauncher browser) {
    public async Task<int> HandleAsync(string[] args) {
        // Before ANY funnel event and before the flag parsing below chooses a lane. cli_setup_started
        // fires further down, and the device-code, headless and no-browser lanes never construct a
        // LoopbackBrowser at all — the key is a per-run correlation id, not a browser artifact, and
        // only the redirect is browser-specific. No-ops when telemetry is disabled.
        SetupJoin.Mint();

        var serverUrlArg     = GetArg(args, "--server-url");

        // `kcap setup <tenant>`: a leading positional arg (bare slug or full URL) is treated as the
        // server, equivalent to --server-url. A bare single label expands to {slug}.kcap.ai.
        if (serverUrlArg is null && args.Length > 1 && !args[1].StartsWith('-'))
            serverUrlArg = ServerInput.ResolveTenantArg(args[1]);
        var noPrompt         = args.Contains("--no-prompt");
        // Also when there is no keyboard: a redirected stdin cannot press the escape-hatch key, so a
        // loopback wait there can only end in the listener timeout. Not a headless guess - an
        // interactive SSH session has a keyboard and keeps the browser.
        var forceDevice      = OAuthLoginFlow.DeviceRouteRequired(args.Contains("--device"), ConsoleKeyWatcher.Instance.CanWatch);
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

        var profile = await AppConfig.LoadProfileConfig(config);

        SetupFunnel.Started(
            hasExistingProfile: AppConfig.HasConfiguredProfile(profile),
            serverUrlProvided:  serverUrlArg is not null,
            noPrompt:           noPrompt);
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
        var activeProfile  = profile.ActiveName;
        var existing       = profile.Profiles.GetValueOrDefault(activeProfile);
        var existingTokens = await new TokenStore(config).LoadAsync(activeProfile);

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
        string  provider;
        bool    loginComplete = false; // Discovery authenticates inline; skip the Step-2 login.

        if (serverUrlArg is not null) {
            var resolved = await ResolveServerAndProviderAsync(serverUrlArg);
            if (resolved is null) return 1;

            (serverUrl, provider) = resolved.Value;
        } else if (noPrompt) {
            await Console.Error.WriteLineAsync("  --server-url is required with --no-prompt");
            return 1;
        } else {
            var discovered = await RunDiscoveryAsync(args, forceDevice);
            if (discovered is null) return 1;
            (serverUrl, provider, loginComplete) = discovered.Value;

            // Discovery activates the tenant you picked, so the profile captured before it ran is
            // now stale. Step 2 must save the token under the profile setup will actually
            // configure, or the token lands on the old profile and the new one has none.
            var afterDiscovery = await AppConfig.LoadProfileConfig(config);
            activeProfile = string.IsNullOrWhiteSpace(afterDiscovery.ActiveProfile)
                ? "default"
                : afterDiscovery.ActiveProfile;
        }

        await Console.Out.WriteLineAsync();

        // Step 2: Login
        AnsiConsole.Write(new Rule("[yellow]Step 2/6 — Login[/]").LeftJustified());

        var loginStepResult = await RunLoginStepAsync(loginComplete, provider, serverUrl, forceDevice, activeProfile);
        if (loginStepResult != 0) return loginStepResult;

        // The browser leg, where the tenant serves one. Unnumbered because it is not a step: the
        // steps below run either way, and on every tenant that has not turned the flow on this
        // returns without a word. It has to sit after login — both routes are authenticated.
        var browserAgents = await RunBrowserFlowStepAsync(serverUrl, provider, noPrompt);

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
            var visibilityPrompt = new SelectionPrompt<string>()
                .Title("Which of your sessions should be readable by other users in the same Kurrent Capacitor account by default?")
                .AddChoices(AppConfig.ValidVisibilities)
                .UseConverter(v => v switch {
                    "private"    => "All private — only you can see your sessions",
                    "project"    => "Project repos public to fellow project members, others private",
                    "org_public" => "Org repos public, others private (default)",
                    "public"     => "All public — others can see all your sessions",
                    _            => v
                });

            // Start the cursor on the option we label "(default)" rather than the first choice.
            visibilityPrompt.DefaultValue = "org_public";

            defaultVisibility = AnsiConsole.Prompt(visibilityPrompt);

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        }

        await Console.Out.WriteLineAsync();

        // Step 4: Coding agents
        AnsiConsole.Write(new Rule("[yellow]Step 4/6 — Coding agents[/]").LeftJustified());
        await Console.Out.WriteLineAsync("  Capacitor records sessions by installing hooks into your coding agent CLIs.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();
        // Composed once in Core so the probe set is testable without touching the real
        // environment — see Capacitor.Cli.Core.Setup.AgentDetection for the per-vendor
        // rationale (dual PATH + install-marker signals, Cursor's marker-only exception, etc).
        var r          = AgentDetection.Detect(AgentDetection.FromEnvironment());
        var detected   = new CodingAgentsStep.DetectedAgents(
            Claude:      r.Claude.Detected,
            Codex:       r.Codex.Detected,
            Cursor:      r.Cursor.Detected,
            Copilot:     r.Copilot.Detected,
            Gemini:      r.Gemini.Detected,
            Kiro:        r.Kiro.Detected,
            Pi:          r.Pi.Detected,
            OpenCode:    r.OpenCode.Detected,
            Antigravity: r.Antigravity.Detected);

        bool PromptYesNo(string text) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(text) { DefaultValue = true });

        var detectedSummary = SetupDecisions.DetectedAgentsSummary(detected);

        if (detectedSummary is not null)
            await Console.Out.WriteLineAsync($"  Detected coding agents: {detectedSummary}");

        bool installAgents;

        if (browserAgents is { } answered) {
            // Asked and answered in the browser minutes ago, so this step applies rather than
            // re-asks — the flow settles its Agents step on the decision being recorded, not on the
            // install finishing, which is what leaves the work here.
            foreach (var line in BrowserAgentsSummary(answered)) AnsiConsole.MarkupLine(line);

            // Nothing understood is not consent. A decline asks for nothing, and an answer whose every
            // entry named a vendor this build has never heard of asks for nothing this build can do —
            // and the step's own writes are gated on this, not on the per-vendor skips.
            installAgents = answered.Choices.Count > 0;
        } else {
            // The single install-consent decision, replacing the nine per-vendor prompts. Made
            // BEFORE CodingAgentsStep.Options is constructed, so it uses the LOCAL `noPrompt` (there
            // is no `options` object yet). NoPrompt alone would not imply InstallAgents, so this must
            // be set explicitly here or `--no-prompt` would silently stop installing agents.
            installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt, PromptYesNo);
        }

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

        stepOptions = SetupDecisions.WithBrowserAnswer(stepOptions, browserAgents);

        // allowlist the Capacitor server(s) Codex skills need to reach. A single
        // **.kcap.ai wildcard covers every SaaS tenant (current + future) and the auth
        // proxy; self-hosted servers are added as exact hosts. Derived from the active
        // server URL plus every configured profile so switching profiles still works.
        var profilesForDomains = await AppConfig.LoadProfileConfig(config);

        // Every profile's server, EXCEPT on the browser path. The Agents screen discloses this on the
        // Codex row — "also opens Codex's sandbox network to your server" — and that sentence is about
        // one server, the one they are setting up. Consent to reach it is not consent to reach every
        // tenant this machine has ever been pointed at.
        var codexAllowDomains = CodexConfigToml.BuildAllowDomains(
            browserAgents is null
                ? new[] { serverUrl }.Concat(profilesForDomains.Profiles.Values.Select(p => p.ServerUrl))
                : [serverUrl]);

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
            CapacitorOnPath:        () => AgentDetection.BinaryOnPath("kcap"),
            InstallAgentSkills:     AgentsSkillsInstaller.Install,
            CleanLegacyCodexSkills: legacyDir => AgentsSkillsInstaller.CleanLegacyCodexSkills(legacyDir).RemovedAny,
            InstallKiroHooks:       PluginCommand.InstallKiroHooks,
            InstallPiExtension:     PiExtensionInstaller.Install,
            InstallOpenCodeExtension: OpenCodeExtensionInstaller.Install,
            InstallAntigravityHooks:  PluginCommand.InstallAntigravityHooks,
            EnableCodexNetworkAccess: () => CodexConfigToml.EnableNetworkAccess(codexAllowDomains),
            RegisterCodexMcp:         () => CodexConfigToml.RegisterKcapMcpServers(),
            // every non-Claude JSON harness registers the ForCursor subset — the full set,
            // kcap-workitems included (see KcapMcpServers.ForCursor).
            RegisterCursorMcp:        () => HarnessMcpProjections.Cursor.Register(CursorPaths.UserMcpJson()),
            RegisterCopilotMcp:       () => HarnessMcpProjections.Copilot.Register(CopilotPaths.McpConfigJson()),
            InstallCopilotInstructions: () => AgentInstructionsWriter.Write(
                CopilotPaths.InstructionsMd(), KcapAgentInstructions.Body),
            // Skills are already current when the on-disk marker matches this build AND
            // every owned kcap-* folder is present; used to skip the prompt + re-copy
            // (mirrors PluginCommand's postinstall fast path). A missing/stale marker — or a
            // deleted skill folder — reads as "not current" → prompt + install (self-heals).
            AgentSkillsCurrent:       AgentsSkillsInstaller.IsCurrent,
            RegisterOpenCodeMcp:      () => HarnessMcpProjections.OpenCode.Register(OpenCodePaths.McpConfigJson()),
            InstallOpenCodeInstructions: () => AgentInstructionsWriter.Write(
                OpenCodePaths.AgentsMd(), KcapAgentInstructions.Body),
            RegisterKiroMcp:          () => HarnessMcpProjections.Kiro.Register(KiroPaths.SettingsMcpJson()),
            RegisterGeminiMcp:        () => HarnessMcpProjections.Gemini.Register(GeminiPaths.SettingsJson()),
            InstallGeminiInstructions: () => AgentInstructionsWriter.Write(
                GeminiPaths.GeminiMd(), KcapAgentInstructions.Body),
            RegisterAntigravityMcp:   () => HarnessMcpProjections.Antigravity.Register(AntigravityPaths.McpConfigJson()),
            InstallAntigravityInstructions: () => AgentInstructionsWriter.Write(
                AntigravityPaths.InstructionsMd(), KcapAgentInstructions.Body),
            // Pi has no JSON MCP config — the "MCP" is a second extension file (kcap-mcp.ts).
            InstallPiMcp:             PiMcpExtensionInstaller.Install,
            InstallPiInstructions:    () => AgentInstructionsWriter.Write(
                PiPaths.AgentsMd(), KcapAgentInstructions.Body));

        void WriteLine(string line) => AnsiConsole.MarkupLine(line);

        var installResult = await CodingAgentsStep.RunAsync(
            stepOptions, detected, stepPaths, stepInstallers, PromptYesNo, WriteLine);

        // Record that setup offered these detected agents, so the new-harness nudge doesn't later
        // re-offer a vendor the user just saw at the Step 4 prompt (whether they said yes or no).
        // A vendor skipped by its own --skip-<vendor> flag was not meaningfully offered, so it is
        // left unstamped and can still nudge later. Never writes/overwrites a dismissal.
        var offeredNow = new List<string>();
        void OfferedIf(bool wasDetected, bool skipped, string id) { if (wasDetected && !skipped) offeredNow.Add(id); }
        OfferedIf(detected.Claude,      skipClaude,          "claude");
        OfferedIf(detected.Codex,       skipCodexFlag,       "codex");
        OfferedIf(detected.Cursor,      skipCursorFlag,      "cursor");
        OfferedIf(detected.Copilot,     skipCopilotFlag,     "copilot");
        OfferedIf(detected.Gemini,      skipGeminiFlag,      "gemini");
        OfferedIf(detected.Kiro,        skipKiroFlag,        "kiro");
        OfferedIf(detected.Pi,          skipPiFlag,          "pi");
        OfferedIf(detected.OpenCode,    skipOpenCodeFlag,    "opencode");
        OfferedIf(detected.Antigravity, skipAntigravityFlag, "antigravity");
        new HarnessOfferStore(config).StampOffered(offeredNow, DateTimeOffset.UtcNow);

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
        var activeName     = "default";
        var defaultProfile = new Profile();

        await ConfigMutator.MutateAsync(config, c => {
            activeName     = c.ActiveName;
            defaultProfile = c.Profiles.GetValueOrDefault(activeName) ?? new Profile();

            defaultProfile = defaultProfile with {
                ServerUrl          = serverUrl,
                DefaultVisibility  = defaultVisibility,
                UseProviderApiKey  = useProviderApiKey,
                Daemon             = (defaultProfile.Daemon ?? new DaemonSettings()) with { Name = daemonName }
            };

            return c with {
                Profiles = new Dictionary<string, Profile>(c.Profiles) { [activeName] = defaultProfile }
            };
        });

        // The exact values just saved, for the same-process work below (the import step). Built
        // rather than re-resolved: CLI/env/repo precedence could land on something else than what
        // this run just wrote.
        var saved = new ProfileContext(
            new(serverUrl, activeName, defaultProfile, null), await AppConfig.LoadProfileConfig(config));

        var finalTokens = await new TokenStore(config).LoadAsync(activeName);

        // tell the server this user has finished CLI setup, so the dashboard
        // can flip the new-tenant welcome modal from "Waiting for CLI to register"
        // to "Registered". Best-effort — never block setup completion on this.
        await PingCliSetupAsync(serverUrl, activeName, provider);

        await Console.Out.WriteLineAsync();

        // Step 6: Import past sessions
        AnsiConsole.Write(new Rule("[yellow]Step 6/6 — Import past sessions[/]").LeftJustified());

        // detectPullRequest:false — Step 6 only needs (owner, name) to scope the repo import;
        // PR/MR detection would run extra provider probes/subprocesses for nothing here.
        var currentRepoDetected = await RepositoryDetection.DetectRepositoryAsync(
            config, Environment.CurrentDirectory, detectPullRequest: false);
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
        // Server-scoped: the import step is only actually authorized if the token both refreshes
        // and belongs to the server we just configured.
        var authSatisfied = await IsAuthSatisfiedAsync(
            provider, async () => (await new TokenStore(config).GetValidTokensForServerAsync(activeName, serverUrl)).Tokens is not null);

        await RunImportStepAsync(
            currentRepo, authSatisfied, skipImport, noPrompt,
            () => AnsiConsole.Prompt(new ConfirmationPrompt("Import past sessions from this repository?") { DefaultValue = true }),
            saved,
            defaultVisibility);

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

        grid.AddRow("[bold]Config[/]", Markup.Escape(AppConfig.GetConfigPath(config)));

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

        WriteNextSteps(ShouldOfferGuidedTour(detectedSummary is not null, claudeSettingsPath, stepPaths));

        // Same fields as Result.AnyHooksInstalled, counted instead of OR'd: CodingAgentsStep
        // doesn't surface a count directly, so this sums the per-vendor hook-install outcomes
        // already tracked locally in installResult — vendor names themselves are never sent
        // (see SetupFunnel.Succeeded).
        var agentsConfigured = new[] {
            installResult.ClaudeInstalled, installResult.CodexHooksInstalled, installResult.CursorHooksInstalled,
            installResult.CopilotHooksInstalled, installResult.GeminiHooksInstalled, installResult.KiroHooksInstalled,
            installResult.PiExtensionInstalled, installResult.OpenCodeExtensionInstalled, installResult.AntigravityHooksInstalled,
        }.Count(installed => installed);

        SetupFunnel.Succeeded(agentsConfigured);

        return 0;
    }

    /// <summary>The closing "Next steps" box: a question per item, its answer indented beneath.</summary>
    static void WriteNextSteps(bool offerGuidedTour) {
        var rows = new List<IRenderable>();

        // Padder, not a "  " prefix: these lines wrap, and a prefix indents only the first of them.
        foreach (var (question, answer) in NextStepItems(offerGuidedTour)) {
            if (rows.Count > 0) rows.Add(Text.Empty);

            rows.Add(new Markup($"[bold]{Markup.Escape(question)}[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Padder(new Markup(answer), new Padding(2, 0, 0, 0)));
        }

        AnsiConsole.Write(
            new Panel(new Rows(rows))
                .Header("[bold green] Next steps [/]")
                .BorderColor(Color.Green)
                .Padding(1, 0));
    }

    /// <summary>The box's (question, answer-markup) pairs, split from the write so copy is testable.</summary>
    internal static List<(string Question, string Answer)> NextStepItems(bool offerGuidedTour) {
        var items = new List<(string, string)> {
            (ServerSetupQuestion,
             $"{Markup.Escape(ServerSetupAction)}\n[cyan]{Markup.Escape(ServerSetupDocsUrl)}[/]"),
        };

        if (offerGuidedTour) {
            // Markup-safe: the quoted prompt has no [ or ], so escaping leaves it a plain substring.
            items.Add((GuidedTourQuestion,
                       Markup.Escape(GuidedTourAction)
                             .Replace(GuidedTourPromptQuoted,
                                      $"[cyan]{GuidedTourPromptQuoted}[/]", StringComparison.Ordinal)));
        }

        return items;
    }

    /// <summary>
    /// Whether to point the user at the guided tour. Both halves are required: an agent to type
    /// the prompt into, and the skill actually on disk for one of them. Skill presence is read
    /// from the filesystem rather than the install result, because the installers report false
    /// when work was skipped as already-current — a wired-up machine re-running setup, which
    /// must still get the CTA.
    /// </summary>
    internal static bool ShouldOfferGuidedTour(
            bool anyAgentDetected, string claudeSettingsPath, CodingAgentsStep.Paths paths) =>
        anyAgentDetected
     && (ClaudeCarriesGuidedTour(claudeSettingsPath, paths.PluginDir)
      || AgentsSkillsInstaller.HasSkill(paths.AgentsSkillsDir,      GuidedTourSkillName)
      || AgentsSkillsInstaller.HasSkill(paths.KiroSkillsDir,        GuidedTourSkillName)
      || AgentsSkillsInstaller.HasSkill(paths.AntigravitySkillsDir, GuidedTourSkillName));

    /// <summary>
    /// The plugin is registered AND the directory Claude loads it from ships the skill. The
    /// registered marketplace path in settings is the artifact that matters — <paramref
    /// name="pluginDir"/> is only where THIS build would install from, and after an upgrade the
    /// two can differ. Falls back to it when nothing is registered; false when neither resolves,
    /// because an unverifiable skill must not be advertised.
    /// </summary>
    static bool ClaudeCarriesGuidedTour(string claudeSettingsPath, string? pluginDir) {
        if (!ClaudePluginInstaller.IsInstalled(claudeSettingsPath)) return false;

        var dir = ClaudePluginInstaller.RegisteredMarketplacePath(claudeSettingsPath) ?? pluginDir;

        return dir is not null
            && File.Exists(Path.Combine(dir, "skills", GuidedTourSkillName, "SKILL.md"));
    }

    /// <summary>Source folder name under <c>kcap/skills/</c>; <c>kcap-</c>-prefixed once installed.</summary>
    internal const string GuidedTourSkillName = "guided-tour";

    internal const string GuidedTourQuestion = "New to Capacitor?";

    /// <summary>
    /// A prompt, not <c>/kcap:guided-tour</c>: this box prints for every vendor and only Claude Code
    /// has slash commands. Must stay a verbatim trigger in the skill's frontmatter description
    /// (pinned by <c>SetupCommandTests</c>) or it fires nothing.
    /// </summary>
    internal const string GuidedTourPrompt = "Start kcap guided tour";

    /// <summary>Quoted as well as coloured — colour is lost to <c>NO_COLOR</c> and redirected stdout.</summary>
    internal const string GuidedTourPromptQuoted = $"\"{GuidedTourPrompt}\"";

    internal const string GuidedTourAction =
        $"Prompt {GuidedTourPromptQuoted} in your coding agent to see what Capacitor can do for you";

    internal const string GuidedTourCallToAction = $"{GuidedTourQuestion} {GuidedTourAction}";

    /// <summary>
    /// Server setup lives in the dashboard, so this can only be pointed at. Always printed: who owns
    /// the server is not knowable here, so the reader self-selects on the question. Says "server",
    /// never "workspace" — that word means the local tree everywhere else in this CLI.
    /// </summary>
    internal const string ServerSetupQuestion = "Did you create this Capacitor server?";

    internal const string ServerSetupAction = "Complete server setup with instructions here:";

    internal const string ServerSetupDocsUrl =
        "https://capacitor.kurrent.io/docs/getting-started/setup-server/";

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
    internal async Task RunImportStepAsync(
            (string Owner, string Name)? currentRepo,
            bool                          authSatisfied,
            bool                          skipImport,
            bool                          noPrompt,
            Func<bool>                    promptYesNo,
            ProfileContext                profiles,
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
            Repo:               currentRepo!.Value,
            DefaultVisibility:  defaultVisibility,
            AutoSkipExclusions: true,
            ForcePrivate:       false,
            Profiles:           profiles);

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
        (string Owner, string Name) Repo,
        string?                      DefaultVisibility,
        bool                         AutoSkipExclusions,
        bool                         ForcePrivate,
        ProfileContext               Profiles);

    /// <summary>
    /// Test seam: when set, replaces the real <see cref="ImportCommand.HandleImport"/> call made
    /// by <see cref="RunImportStepAsync"/>. Process-global static state — tests must reset it to
    /// null (in a finally block) after use.
    /// </summary>
    internal static Func<ImportInvocation, Task<int>>? ImportRunnerOverride;

    Task<int> DefaultImportRunner(ImportInvocation inv) =>
        new ImportCommand(config, inv.Profiles).HandleImport(
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
            currentRepo:             inv.Repo,
            needOrgPick:             false,
            storedOrg:               null,
            autoSkipExclusions:      inv.AutoSkipExclusions,
            defaultVisibility:       inv.DefaultVisibility);

    /// <summary>The nine supported import sources — mirrors Program.cs's `kcap import` construction.</summary>
    IReadOnlyList<IImportSource> BuildImportSources() => [
        new ClaudeImportSource(config),
        new CodexImportSource(config),
        new CursorImportSource(config),
        new CopilotImportSource(config),
        new GeminiImportSource(),
        new KiroImportSource(config),
        new PiImportSource(config),
        new OpenCodeImportSource(),
        new AntigravityImportSource()
    ];

    /// <summary>
    /// Normalizes a user-supplied server (a full URL, or a bare slug already expanded by
    /// <see cref="ServerInput.ResolveTenantArg"/>), probes it, and reads the auth provider from the server's
    /// own <c>/auth/config</c>. Returns null after printing the reason. Shared by
    /// `kcap setup &lt;tenant&gt;` / --server-url and by the zero-tenant "I already have a
    /// workspace" path, so provider selection has exactly one implementation.
    /// </summary>
    async Task<(string ServerUrl, string Provider)?> ResolveServerAndProviderAsync(string serverArg) {
        var normalized = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Checking server…",
            async _ => await ServerUrlNormalizer.NormalizeAsync(
                serverArg, skipProbe: false, CancellationToken.None));

        if (!normalized.Reachable) {
            AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(normalized.Warning ?? serverArg)}");
            AnsiConsole.MarkupLine("  [dim]Check the URL is correct and the server is running.[/]");
            return null;
        }

        var serverUrl = normalized.Url;
        await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");

        // Reachable, but with an informational warning (e.g. https→http downgrade).
        if (normalized.Warning is not null)
            AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(normalized.Warning)}");

        try {
            var provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl, config, profiles);
            AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{Markup.Escape(provider)}[/]");

            return (serverUrl, provider);
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    /// <summary>Test seam: overrides façade construction for Step 1/2. Reset to null in a finally block.</summary>
    internal static Func<ITenantProvisioner?, OnboardingFacade>? FacadeOverride;

    internal static readonly SetupAuthProgress StepProgress = new(ConsoleAuthProgress.Instance);

    OnboardingFacade NewFacade(ITenantProvisioner? provisioner) =>
        FacadeOverride?.Invoke(provisioner)
            ?? new OnboardingFacade(config, StepProgress, browser, new SpectreTenantPicker(), provisioner,
                beforeCommit: null) {
                KeyWatcher = ConsoleKeyWatcher.Instance
            };

    /// <summary>
    /// Step 2 (Login) as a standalone step: a discovery-completed sign-in just reports what
    /// discovery already published; everything else — including a <c>None</c> provider, which needs
    /// no interactive login but still needs its auth_provider stamp written inside the façade's
    /// commit boundary — goes through the façade, adopting the server onto the active profile,
    /// since setup's whole job is configuring that profile for the chosen server.
    /// </summary>
    internal async Task<int> RunLoginStepAsync(
            bool loginComplete, string provider, string serverUrl, bool forceDevice, string activeProfile) {
        if (loginComplete) {
            var cfgAfter = await AppConfig.LoadProfileConfig(config);
            var tokens   = await new TokenStore(config).LoadAsync(cfgAfter.ActiveProfile);
            AnsiConsole.MarkupLine($"  [green]✓[/] Logged in as [cyan]{Markup.Escape(tokens?.GitHubUsername ?? "?")}[/]");

            return 0;
        }

        var result = await NewFacade(provisioner: null)
            .LoginAsync(serverUrl, forceDevice, activeProfile, CancellationToken.None, adoptServer: true);

        if (result is not AuthResult.Committed) {
            await Console.Error.WriteLineAsync("  Login failed.");

            return 1;
        }

        if (provider == AuthProvider.None) {
            // The façade's ConsoleAuthProgress already printed the "no authentication configured" notice.
            return 0;
        }

        var loggedInTokens = await new TokenStore(config).LoadAsync(activeProfile);
        await Console.Out.WriteLineAsync($"  ✓ Logged in as {loggedInTokens?.GitHubUsername}");

        return 0;
    }

    /// <summary>Per request, not per leg: the poll below runs for as long as a human takes.</summary>
    static readonly TimeSpan BrowserFlowHttpTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long the login-shell probe may hold up the create. Two attempts at its own 5s
    /// timeout, plus room to spawn them; past that the machine reports "not probed", which is not the
    /// same as "not found" and draws no alarm on the screen.</summary>
    static readonly TimeSpan LoginShellProbeBudget = TimeSpan.FromSeconds(12);

    /// <summary>Creates the first-run flow, opens the browser on it, and polls it as itself, returning
    /// the Agents decision for Step 4 to apply. <b>Nothing is configured here</b> — what crosses is
    /// vendor keys and booleans, and the install runs through the same one place the terminal prompt
    /// does. Every outcome leaves setup running: sign-in has already happened, so nothing in this leg
    /// can strand a machine.</summary>
    async Task<FirstRunAgentsAnswer?> RunBrowserFlowStepAsync(string serverUrl, string provider, bool noPrompt) {
        // --no-prompt is a scripted run and this waits on a human. None has no identity for a flow to
        // be owned by, and its routes are authenticated. Headless is deliberately NOT a skip: a
        // machine with no browser of its own is exactly the one whose user is sitting at another, and
        // the URL is printed for them to carry across — the device path keeps the screens rather than
        // designing that population out of them.
        if (noPrompt || provider == AuthProvider.None) return null;

        await Console.Out.WriteLineAsync();

        FirstRunFlowResult result;

        // Through the ONE authenticated-client choke point, so the bearer refreshes and a 401 is
        // recovered rather than ending a wait that can outlive a short-lived WorkOS token. The try
        // keeps the leg's "no reachable failure crashes setup" promise whole.
        try {
            var (http, authStatus) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
                config, profiles, serverUrl, autoRetryUnauthorized: true);

            using (http) {
                http.Timeout = BrowserFlowHttpTimeout;

                // Ok runs the leg. NoAuthRequired is the None-provider skip again — silent, for the
                // same reason. The rest get one line: the factory's quiet variant prints nothing, and
                // expired / not authenticated / wrong server all share one remedy.
                if (authStatus is not AuthStatus.Ok) {
                    if (authStatus != AuthStatus.NoAuthRequired)
                        AnsiConsole.MarkupLine(
                            "  [dim]Skipped browser setup: the stored token is not usable. Run 'kcap login' to re-authenticate.[/]");

                    return null;
                }

                // Said out loud because it is the one part of this leg that takes noticeable time: the
                // login-shell probe spawns a shell, and a slow profile would otherwise be dead air
                // ahead of the only line that explains what is happening.
                AnsiConsole.MarkupLine("  [dim]Checking this machine for coding agents…[/]");

                var report = FirstRunMachineReport.EvaluateCurrent(
                    config, Environment.MachineName, await LoginShellFindsCliAsync());

                result = await new BrowserFirstRunFlow(
                        new FirstRunFlowClient(http), new SpectreFirstRunFlowProgress(), browser)
                    .RunAsync(serverUrl, report, CancellationToken.None);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            AnsiConsole.MarkupLine($"  [yellow]![/] Could not start browser setup: {Markup.Escape(ex.Message)}");

            return null;
        }

        // Silent: this tenant does not serve the flow, which is every tenant that has not turned it
        // on. Announcing it would report our rollout as though it were the user's problem.
        if (result is FirstRunFlowResult.Unavailable) return null;

        AnsiConsole.MarkupLine(BrowserFlowOutcome(result));

        // Not after a keypress: that outcome's own line already says where setup went, and saying it
        // twice would be the only thing this added.
        if (result is not (FirstRunFlowResult.Finished or FirstRunFlowResult.Dismissed))
            AnsiConsole.MarkupLine("  [dim]Carrying on here.[/]");

        return FirstRunFlowOutcomes.Agents(result);
    }

    /// <summary>Whether the login shell resolves the CLI — see <see cref="ILoginShellProbe"/> for why
    /// that differs from this process's PATH. Bounded because it spawns a shell: a probe that did not
    /// finish reports unknown rather than as a hazard, since only an explicit false draws the alarm.</summary>
    static async Task<bool?> LoginShellFindsCliAsync() {
        try {
            using var cts = new CancellationTokenSource(LoginShellProbeBudget);

            return await new LoginShellProbe(new ProcessRunner(), Environment.GetEnvironmentVariable)
                .KcapOnPathAsync(cts.Token);
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>What Step 4 says instead of prompting, when the browser already answered it. Split
    /// from the write so the copy is testable.</summary>
    internal static IReadOnlyList<string> BrowserAgentsSummary(FirstRunAgentsAnswer answer) {
        var lines = new List<string>();

        if (answer.IsDecline)
            lines.Add("  [dim]· You chose not to set any up in the browser — nothing to install.[/]");
        else if (answer.Labels.ToList() is { Count: > 0 } labels)
            lines.Add($"  [dim]Chosen in the browser: {Markup.Escape(string.Join(", ", labels))}[/]");

        if (answer.Unrecognised > 0)
            lines.Add(
                $"  [yellow]![/] {answer.Unrecognised} of your choices name an agent this version of kcap "
              + "does not know. Run 'kcap update' and setup again to finish them.");

        return lines;
    }

    /// <summary>The leg's one line about how it ended, split from the write so the copy is testable.
    /// <see cref="FirstRunFlowResult.Unavailable"/> has none — it is not reported at all.</summary>
    internal static string BrowserFlowOutcome(FirstRunFlowResult result) => result switch {
        FirstRunFlowResult.Finished => "  [green]✓[/] Browser setup finished.",

        FirstRunFlowResult.Expired =>
            "  [yellow]![/] That setup link expired before the browser finished with it.",

        FirstRunFlowResult.Abandoned => "  [yellow]![/] The browser didn't finish setup.",

        // Not a warning. The user chose this, and dressing a choice up as something gone wrong is how
        // a CLI teaches people to read past its warnings.
        FirstRunFlowResult.Dismissed => "  [dim]Left the browser to it.[/]",

        // The server asks for ten minutes, which is not a wait an interactive setup can offer, so the
        // number is reported rather than slept through.
        FirstRunFlowResult.RateLimited limited =>
            $"  [yellow]![/] Too many setup links created recently. Browser setup is available again in {Math.Max(1, (int)Math.Ceiling(limited.RetryAfter.TotalMinutes))} min.",

        FirstRunFlowResult.Failed failed => $"  [yellow]![/] {Markup.Escape(failed.Message)}",

        _ => "  [yellow]![/] Browser setup did not finish."
    };

    internal async Task<(string ServerUrl, string Provider, bool LoginComplete)?> RunDiscoveryAsync(
            string[] args, bool forceDevice) {
        var chosen   = OAuthLoginFlow.ChooseDiscoveryProvider(args);
        var headless = HeadlessEnvironment.IsHeadless();

        AnsiConsole.MarkupLine($"  Proxy: [dim]{Markup.Escape(AuthProxyEndpoint.Url)}[/]");

        // WorkOS no longer follows headlessness: its ladder opens the browser either way, and only an
        // explicit --device takes the device grant. GitHub's exchange URL is not known until the proxy
        // answers inside DiscoverAsync, so its label stays the environment guess it has always been.
        var signinMode = chosen == AuthProvider.WorkOS
            ? OAuthLoginFlow.ChooseWorkOSFlow(forceDevice) == WorkOSFlow.Device ? "device" : "browser"
            : forceDevice || headless ? "device" : "browser";
        SetupFunnel.SigninOpened(signinMode, chosen);

        // Armed for every WorkOS session, headless included: that path has a device grant now, so
        // a zero-workspace headless user now completes a sign-in and would otherwise hold a live
        // credential with nowhere to spend it. GitHub never provisions.
        var provisioner = chosen == AuthProvider.WorkOS
            ? new SpectreTenantProvisioner(new TenantProvisioningClient(new HttpClient()), ProvisioningEndpoint.Url)
            : null;

        var result = await NewFacade(provisioner).DiscoverAsync(chosen, forceDevice, CancellationToken.None);

        // WorkOS's own signin_completed/tenant_none fire from inside Core — only GitHub is derived here.
        if (chosen == AuthProvider.GitHubApp) {
            switch (result) {
                case AuthResult.Failed { Reason: AuthFailureReason.SigninDenied }:
                    SetupFunnel.SigninFailed("github_token_denied");

                    break;
                // Other and NoTenantsFound only occur once AcquireGitHubTokenAsync already succeeded.
                case AuthResult.Committed:
                case AuthResult.Failed { Reason: AuthFailureReason.Other }:
                    SetupFunnel.SigninCompleted(AuthProvider.GitHubApp);

                    break;
                case AuthResult.Failed { Reason: AuthFailureReason.NoTenantsFound }:
                    SetupFunnel.SigninCompleted(AuthProvider.GitHubApp);
                    SetupFunnel.TenantNone(AuthProvider.GitHubApp);

                    break;
            }
        }

        switch (result) {
            case AuthResult.Committed committed: {
                var cfg    = await AppConfig.LoadProfileConfig(config);
                var active = cfg.Profiles.GetValueOrDefault(cfg.ActiveProfile);

                if (active?.ServerUrl is null) {
                    AnsiConsole.MarkupLine("  [red]✗[/] Sign-in did not set an active profile.");

                    return null;
                }

                // WorkOS already reported "Logged in as … → …" via the façade; GitHub gets its own line.
                if (committed.Provider != AuthProvider.WorkOS) {
                    AnsiConsole.MarkupLine(
                        $"  [green]✓[/] Discovered {committed.Published.Count} tenant(s). Active: [cyan]{Markup.Escape(committed.ActiveProfile)}[/]");
                }

                return (active.ServerUrl, committed.Provider, true);
            }
            case AuthResult.Retarget retarget: {
                // Origin first, then slug expansion: a pasted "acme.kcap.ai/sessions" must lose its
                // path before ResolveTenantArg decides it already looks like a host.
                var retargeted = await ResolveServerAndProviderAsync(
                    ServerInput.ResolveTenantArg(ServerInput.ToServerOrigin(retarget.ServerInput)));

                return retargeted is null ? null : (retargeted.Value.ServerUrl, retargeted.Value.Provider, false);
            }
            default:
                // Failed/Cancelled — already rendered through the façade's progress sink, bar the tail setup owns.
                StepProgress.ReportFailure(result);

                return null;
        }
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
    /// <summary>
    /// The cli-setup ping body, hand-built on purpose. A typed DTO here would inherit
    /// CapacitorJsonContext's global SnakeCaseLower policy and serialise <c>cli_version</c>,
    /// silently breaking an endpoint that works today — the mirror image of why
    /// <c>ProvisionRequest.JoinId</c> needs an explicit attribute. So both names stay literal
    /// camelCase, and <c>joinId</c> is omitted entirely rather than sent as null when telemetry is
    /// off. Both inputs are ours — an assembly version and 32 hex chars — so neither can carry a
    /// quote that would break the literal.
    /// </summary>
    internal static string CliSetupPingBody(string? version, string? joinId) {
        var versionJson = version is null ? "null" : $"\"{version}\"";
        var joinJson    = joinId is null ? "" : $",\"joinId\":\"{joinId}\"";

        return $$"""{"cliVersion":{{versionJson}}{{joinJson}}}""";
    }

    async Task PingCliSetupAsync(string serverUrl, string profile, string provider) {
        // The ping is intentionally silent (see method-doc), which also hides why the
        // dashboard welcome modal never flips when it fails — e.g. a token the server
        // rejects or maps to a different identity. Set KCAP_DEBUG to surface the
        // outcome on stderr without changing the best-effort, non-blocking contract.
        var  debug = Environment.GetEnvironmentVariable("KCAP_DEBUG") is { Length: > 0 };
        void Debug(string message) {
            if (debug) Console.Error.WriteLine($"[kcap] cli-setup ping: {message}");
        }

        // A None-provider server neither needs nor should receive a bearer. Setup performs no login
        // on that path, so any token still in the profile belongs to whatever server it pointed at
        // before — sending it here would disclose it to an unrelated host.
        if (provider == AuthProvider.None) {
            Debug("skipped — server requires no authentication");

            return;
        }

        try {
            var tokens = await new TokenStore(config).LoadAsync(profile);
            if (tokens is null || tokens.IsExpired) {
                Debug(tokens is null ? "skipped — no stored token" : "skipped — token expired");

                return;
            }

            // Re-running setup to point an authenticated profile at a different server would
            // otherwise send the previous server's bearer to the new one. Checked inline rather
            // than through the resolving accessor to keep this path refresh-free and time-bound.
            if (tokens.ServerUrl is not null && !ServerIdentity.SameServer(tokens.ServerUrl, serverUrl)) {
                Debug($"skipped — stored token belongs to {tokens.ServerUrl}");

                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);

            var version = typeof(SetupCommand).Assembly.GetName().Version?.ToString();
            var payload = new StringContent(
                CliSetupPingBody(version, SetupJoin.Current),
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
