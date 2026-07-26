using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Instructions;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure logic for the "coding agents" step of <c>kcap setup</c>. All I/O
/// (filesystem, console, prompts) flows through injected delegates so tests
/// can drive every branch without touching ~/.claude, ~/.codex, or AnsiConsole.
/// </summary>
internal static class CodingAgentsStep {
    // New-vendor fields are appended with defaults so existing (named-arg) call
    // sites and the broad CodingAgentsStep test suite compile unchanged. Gemini,
    // Kiro, and Pi were all added after the original four vendors.
    internal record Options(bool SkipClaude, bool SkipCodex, bool SkipCursor, bool SkipCopilot, bool NoPrompt, bool SkipGemini = false, bool SkipKiro = false, bool SkipPi = false, bool SkipOpenCode = false, bool SkipCodexNetworkAccess = false, bool SkipAntigravity = false, bool SkipCursorMcp = false, bool SkipCopilotMcp = false, bool SkipCopilotInstructions = false, bool SkipGeminiMcp = false, bool SkipGeminiInstructions = false, bool SkipAntigravityMcp = false, bool SkipAntigravityInstructions = false, bool SkipAntigravitySkills = false, bool SkipOpenCodeMcp = false, bool SkipOpenCodeInstructions = false, bool SkipKiroMcp = false, bool SkipKiroSkills = false, bool SkipPiMcp = false, bool SkipPiInstructions = false,
            // Single unified install-consent decision (replaces the nine per-vendor prompts —
            // see SetupDecisions.DecideInstallAgents). Defaults true so the pre-existing test
            // suite (which never set this) stays source- and behavior-compatible; SetupCommand
            // always sets it explicitly from the single detected-agents prompt.
            bool InstallAgents = true);

    internal record DetectedAgents(bool Claude, bool Codex, bool Cursor, bool Copilot, bool Gemini = false, bool Kiro = false, bool Pi = false, bool OpenCode = false, bool Antigravity = false);

    internal record Paths(
            string  ClaudeSettingsPath,
            string  ClaudeScopeLabel,
            string? PluginDir,
            string  CodexHooksPath,
            string  CursorHooksPath,
            string  CopilotHooksPath,
            string  GeminiSettingsPath,
            string  AgentsSkillsDir,
            string  LegacyCodexSkillsDir,
            string  KiroHooksPath = "",
            string  PiExtensionPath = "",
            string  OpenCodeExtensionPath = "",
            string  CodexConfigTomlPath = "",
            string  AntigravityHooksPath = "",
            string  CursorMcpPath = "",
            string  CopilotMcpPath = "",
            string  CopilotInstructionsPath = "",
            string  GeminiInstructionsPath = "",
            string  AntigravityMcpPath = "",
            string  AntigravityInstructionsPath = "",
            string  AntigravitySkillsDir = "",
            string  OpenCodeMcpPath = "",
            string  OpenCodeInstructionsPath = "",
            string  KiroMcpPath = "",
            string  KiroSkillsDir = "",
            string  PiMcpExtensionPath = "",
            string  PiAgentsMdPath = ""
        );

    internal record Installers(
            Func<string /*settingsPath*/, string /*pluginDir*/, bool> InstallClaudePlugin,
            Func<string /*hooksPath*/, bool>                          InstallCodexHooks,
            Func<string /*hooksPath*/, bool>                          InstallCursorHooks,
            Func<string /*hooksPath*/, bool>                          InstallCopilotHooks,
            Func<string /*settingsPath*/, bool>                       InstallGeminiHooks,
            Func<bool>                                                CapacitorOnPath,
            Func<string /*srcDir*/, string /*dstDir*/, bool>          InstallAgentSkills,
            Func<string /*legacyDir*/, bool>                          CleanLegacyCodexSkills,
            Func<string /*agentJsonPath*/, bool>?                     InstallKiroHooks = null,
            Func<string /*extensionPath*/, bool>?                     InstallPiExtension = null,
            Func<string /*pluginPath*/, bool>?                        InstallOpenCodeExtension = null,
            Func<CodexConfigToml.Change>?                            EnableCodexNetworkAccess = null,
            Func<CodexConfigToml.Change>?                            RegisterCodexMcp = null,
            Func<string /*hooksPath*/, bool>?                        InstallAntigravityHooks = null,
            Func<JsonMcpConfigWriter.Change>?                        RegisterCursorMcp = null,
            Func<JsonMcpConfigWriter.Change>?                        RegisterCopilotMcp = null,
            Func<AgentInstructionsWriter.Change>?                    InstallCopilotInstructions = null,
            Func<string /*skillsDir*/, bool>?                        AgentSkillsCurrent = null,
            Func<JsonMcpConfigWriter.Change>?                        RegisterGeminiMcp = null,
            Func<AgentInstructionsWriter.Change>?                    InstallGeminiInstructions = null,
            Func<JsonMcpConfigWriter.Change>?                        RegisterAntigravityMcp = null,
            Func<AgentInstructionsWriter.Change>?                    InstallAntigravityInstructions = null,
            Func<JsonMcpConfigWriter.Change>?                        RegisterOpenCodeMcp = null,
            Func<AgentInstructionsWriter.Change>?                    InstallOpenCodeInstructions = null,
            Func<JsonMcpConfigWriter.Change>?                        RegisterKiroMcp = null,
            Func<string /*mcpExtensionPath*/, bool>?                 InstallPiMcp = null,
            Func<AgentInstructionsWriter.Change>?                    InstallPiInstructions = null
        );

    internal record Result(
            bool ClaudeInstalled,
            bool CodexHooksInstalled,
            bool AgentSkillsInstalled,
            bool CursorHooksInstalled,
            bool CopilotHooksInstalled,
            bool GeminiHooksInstalled = false,
            bool KiroHooksInstalled = false,
            bool PiExtensionInstalled = false,
            bool OpenCodeExtensionInstalled = false,
            bool CodexNetworkAccessApplied = false,
            bool CodexMcpRegistered = false,
            bool AntigravityHooksInstalled = false,
            bool CursorMcpRegistered = false,
            bool CopilotMcpRegistered = false,
            bool CopilotInstructionsInstalled = false,
            bool GeminiMcpRegistered = false,
            bool GeminiInstructionsInstalled = false,
            bool AntigravityMcpRegistered = false,
            bool AntigravityInstructionsInstalled = false,
            bool AntigravitySkillsInstalled = false,
            bool OpenCodeMcpRegistered = false,
            bool OpenCodeInstructionsInstalled = false,
            bool KiroMcpRegistered = false,
            bool KiroSkillsInstalled = false,
            bool PiMcpInstalled = false,
            bool PiInstructionsInstalled = false
        ) {
        /// <summary>
        /// True when at least one agent's hooks were installed — i.e. there's a
        /// session that will start streaming on the agent's next launch. Skills-only
        /// installs don't count: skills add recall/commands, not the SessionStart hook
        /// that records transcripts. Co-located with the record so the set stays in sync
        /// as agents are added (consumers like SetupCommand's restart tip key off this).
        /// </summary>
        internal bool AnyHooksInstalled =>
            ClaudeInstalled || CodexHooksInstalled || CursorHooksInstalled || CopilotHooksInstalled || GeminiHooksInstalled || KiroHooksInstalled || PiExtensionInstalled || OpenCodeExtensionInstalled || AntigravityHooksInstalled;
    }

    /// <summary>
    /// Drives the agent-detection branches and dispatches to the installer
    /// delegates. Subsequent tasks fill in Claude, Codex hooks, Codex skills,
    /// Cursor hooks, and neither-detected behaviour.
    /// </summary>
    internal static Task<Result> RunAsync(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        // Ordered early-returns — BEFORE any Handle*/HandleAgentSkills/selected work — so a
        // decline (or a no-agents machine) guarantees zero artifact mutations. See
        // SetupDecisions.DecideInstallAgents for how SetupCommand derives InstallAgents.
        if (detected is { Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: false, Kiro: false, Pi: false, OpenCode: false, Antigravity: false }) {
            writeLine("  [yellow]⚠ No supported agent CLI detected.[/] Install Claude Code, Codex CLI, Cursor, Copilot CLI, Gemini CLI, Kiro CLI, Pi, OpenCode, or Antigravity to start capturing sessions.");

            return Task.FromResult(new Result(false, false, false, false, false));
        }

        if (!options.InstallAgents) {
            writeLine("  [dim]· Skipping kcap agent setup[/]");

            return Task.FromResult(new Result(false, false, false, false, false));
        }

        var claudeInstalled       = HandleClaude(options, detected, paths, installers, writeLine);
        var codexHooksInstalled   = HandleCodexHooks(options, detected, paths, installers, writeLine);
        var codexNetworkApplied   = HandleCodexNetworkAccess(options, paths, installers, prompt, writeLine, codexHooksInstalled);
        var codexMcpRegistered    = HandleCodexMcp(paths, installers, writeLine, codexHooksInstalled);
        var cursorHooksInstalled  = HandleCursorHooks(options, detected, paths, installers, writeLine);
        var cursorMcpRegistered   = HandleCursorMcp(options, paths, installers, writeLine, cursorHooksInstalled);
        var copilotHooksInstalled = HandleCopilotHooks(options, detected, paths, installers, writeLine);
        var copilotMcpRegistered  = HandleCopilotMcp(options, paths, installers, writeLine, copilotHooksInstalled);
        var copilotInstructionsInstalled = HandleCopilotInstructions(options, paths, installers, writeLine, copilotHooksInstalled);
        var geminiHooksInstalled  = HandleGeminiHooks(options, detected, paths, installers, writeLine, out var geminiSelected);
        var geminiMcpRegistered   = HandleGeminiMcp(options, paths, installers, writeLine, geminiHooksInstalled);
        // Instructions live in the independent ~/.gemini/GEMINI.md — gate them on the user having
        // SELECTED Gemini (not on hook-write success), so a malformed settings.json that fails the
        // shared hooks/MCP write doesn't also block healing GEMINI.md.
        var geminiInstructionsInstalled = HandleGeminiInstructions(options, paths, installers, writeLine, geminiSelected);
        var kiroHooksInstalled    = HandleKiroHooks(options, detected, paths, installers, writeLine, out var kiroSelected);
        var kiroMcpRegistered     = HandleKiroMcp(options, paths, installers, writeLine, kiroSelected);
        // Kiro's skills live in ~/.kiro/skills (Kiro doesn't read ~/.agents/skills) and steer it toward
        // the kcap MCP tools — gate on the user having SELECTED Kiro, like the MCP registration.
        var kiroSkillsInstalled   = HandleKiroSkills(options, paths, installers, writeLine, kiroSelected);
        var piExtensionInstalled  = HandlePiExtension(options, detected, paths, installers, writeLine, out var piSelected);
        // Pi has no JSON MCP config: the "MCP" is a second extension file (kcap-mcp.ts) and the
        // instructions live in the independent ~/.pi/agent/AGENTS.md — gate both on the user having
        // SELECTED Pi (not on the ingest-write result) so a failed ingest write doesn't block them.
        var piMcpInstalled        = HandlePiMcp(options, paths, installers, writeLine, piSelected);
        var piInstructionsInstalled = HandlePiInstructions(options, paths, installers, writeLine, piSelected);
        var openCodeExtensionInstalled = HandleOpenCodeExtension(options, detected, paths, installers, writeLine);
        var openCodeMcpRegistered      = HandleOpenCodeMcp(options, paths, installers, writeLine, openCodeExtensionInstalled);
        var openCodeInstructionsInstalled = HandleOpenCodeInstructions(options, paths, installers, writeLine, openCodeExtensionInstalled);
        var antigravityHooksInstalled  = HandleAntigravityHooks(options, detected, paths, installers, writeLine, out var antigravitySelected);
        // Antigravity's MCP (own mcp_config.json), instructions (shared GEMINI.md) and skills
        // (~/.gemini/skills) live in files SEPARATE from its hooks.json, so gate them on the user
        // having SELECTED Antigravity (opted-in + kcap on PATH), not on the hook-write succeeding.
        var antigravityMcpRegistered   = HandleAntigravityMcp(options, paths, installers, writeLine, antigravitySelected);
        var antigravityInstructionsInstalled = HandleAntigravityInstructions(options, paths, installers, writeLine, antigravitySelected);
        var antigravitySkillsInstalled = HandleAntigravitySkills(options, paths, installers, writeLine, antigravitySelected);

        // the shared ~/.agents/skills/ install is decoupled from Codex: run it
        // once when any non-Claude agent is detected, independent of that agent's hook
        // install. Placed last so it follows the per-agent steps.
        var agentSkillsInstalled  = HandleAgentSkills(detected, paths, installers, writeLine);

        return Task.FromResult(
            new Result(
                claudeInstalled,
                codexHooksInstalled,
                agentSkillsInstalled,
                cursorHooksInstalled,
                copilotHooksInstalled,
                geminiHooksInstalled,
                kiroHooksInstalled,
                piExtensionInstalled,
                openCodeExtensionInstalled,
                codexNetworkApplied,
                codexMcpRegistered,
                antigravityHooksInstalled,
                CursorMcpRegistered: cursorMcpRegistered,
                CopilotMcpRegistered: copilotMcpRegistered,
                CopilotInstructionsInstalled: copilotInstructionsInstalled,
                GeminiMcpRegistered: geminiMcpRegistered,
                GeminiInstructionsInstalled: geminiInstructionsInstalled,
                AntigravityMcpRegistered: antigravityMcpRegistered,
                AntigravityInstructionsInstalled: antigravityInstructionsInstalled,
                AntigravitySkillsInstalled: antigravitySkillsInstalled,
                OpenCodeMcpRegistered: openCodeMcpRegistered,
                OpenCodeInstructionsInstalled: openCodeInstructionsInstalled,
                KiroMcpRegistered: kiroMcpRegistered,
                KiroSkillsInstalled: kiroSkillsInstalled,
                PiMcpInstalled: piMcpInstalled,
                PiInstructionsInstalled: piInstructionsInstalled
            )
        );
    }

    static bool HandleKiroHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine,
            out bool           selected
        ) {
        selected = false;

        if (!detected.Kiro) {
            writeLine("  [dim]· Kiro CLI not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Kiro CLI detected");

        // Both flags → nothing will be written (no agent clone, no MCP), so short-circuit with the
        // accurate message BEFORE the PATH precheck — that check only matters when we'd write a
        // config that invokes `kcap`.
        if (options.SkipKiro && options.SkipKiroMcp) {
            writeLine("  [dim]· Kiro CLI skipped by flags (hooks + MCP)[/]");

            return false;
        }

        // kcap must be on PATH for BOTH the agent hooks (`kcap hook`) and the MCP servers (`kcap mcp`) —
        // same precheck as the Cursor/Copilot branches. No kcap → neither hooks nor MCP.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Kiro integration skipped — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        // --skip-kiro-hooks (alone) opts out of ONLY the invasive agent clone + default-agent flip. The
        // independent, non-invasive MCP registration (~/.kiro/settings/mcp.json) still applies —
        // gated separately by --skip-kiro-mcp — so MCP stays eligible under --skip-kiro-hooks.
        if (options.SkipKiro) {
            writeLine("  [dim]· Kiro CLI hooks skipped by flag — MCP still registered (use --skip-kiro-mcp to skip that too)[/]");
            selected = true;   // MCP eligibility; HandleKiroMcp still honours --skip-kiro-mcp

            return false;
        }

        // Opted into Kiro (InstallAgents true, not skipped) + kcap on PATH → hooks + MCP. MCP still
        // registers even if the clone below fails.
        selected = true;

        var ok = installers.InstallKiroHooks?.Invoke(paths.KiroHooksPath) ?? false;

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not set up the Kiro agent hooks (needs kiro-cli on PATH to clone your default agent; the clone or default-agent update may have failed).");

            return false;
        }

        writeLine($"  [green]✓[/] Kiro hooks installed ({Markup.Escape(paths.KiroHooksPath)})");
        writeLine("  [dim]  Note: Kiro loads agent hooks at startup — restart any running kiro session to pick them up.[/]");

        return true;
    }

    /// <summary>Registers the kcap MCP servers in <c>~/.kiro/settings/mcp.json</c>, gated on
    /// <paramref name="kiroSelected"/> (independent of the agent-clone outcome) + <see cref="Options.SkipKiroMcp"/>.</summary>
    static bool HandleKiroMcp(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           kiroSelected
        ) {
        if (installers.RegisterKiroMcp is null || !kiroSelected || options.SkipKiroMcp) return false;

        var configPath = Markup.Escape(paths.KiroMcpPath);

        switch (installers.RegisterKiroMcp()) {
            case JsonMcpConfigWriter.Change.Updated:
                writeLine($"  [green]✓[/] Kiro MCP servers registered ([dim]{configPath}[/])");

                return true;
            case JsonMcpConfigWriter.Change.Unchanged:
                writeLine("  [dim]· Kiro MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register Kiro MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
    }

    /// <summary>
    /// Installs kcap's skills into <c>~/.kiro/skills</c> (Kiro reads these via the agent's
    /// <c>skill:///~/.kiro/skills/*/SKILL.md</c> resources — NOT the agent-agnostic
    /// <c>~/.agents/skills</c>), steering Kiro toward the kcap MCP tools. Gated on the user having
    /// SELECTED Kiro (same trigger as <see cref="HandleKiroMcp"/>) and on <see cref="Options.SkipKiroSkills"/>.
    /// Mirrors <see cref="HandleAntigravitySkills"/>.
    /// </summary>
    static bool HandleKiroSkills(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           kiroSelected
        ) {
        if (!kiroSelected || options.SkipKiroSkills) return false;

        var dst = paths.KiroSkillsDir;

        if (installers.AgentSkillsCurrent?.Invoke(dst) == true) {
            writeLine("  [dim]· Kiro skills already up to date — no change needed[/]");

            return false;
        }

        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Kiro skills could not be installed (plugin directory not found).");

            return false;
        }

        var src = Path.Combine(paths.PluginDir, "skills");

        if (!installers.InstallAgentSkills(src, dst)) {
            writeLine($"  [yellow]⚠[/] Kiro skills could not be copied to {Markup.Escape(dst)}");

            return false;
        }

        writeLine($"  [green]✓[/] Kiro skills installed ([dim]{Markup.Escape(dst)}[/])");

        return true;
    }

    static bool HandleCopilotHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine
        ) {
        if (!detected.Copilot) {
            writeLine("  [dim]· Copilot CLI not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Copilot CLI detected");

        if (options.SkipCopilot) {
            writeLine("  [dim]· Copilot CLI hooks skipped by flag[/]");

            return false;
        }

        // kcap.json writes the bare "kcap hook --copilot" command and relies
        // on Copilot finding it on PATH — same precheck as the Cursor branch.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Copilot hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallCopilotHooks(paths.CopilotHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Copilot hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Copilot hooks installed ({Markup.Escape(paths.CopilotHooksPath)})");
        writeLine("  [dim]  Note: Copilot loads hook config at startup — restart any running copilot session to pick them up.[/]");

        return true;
    }

    static bool HandleGeminiHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine,
            out bool           selected
        ) {
        // `selected` = the user opted into Gemini (detected + not skipped, InstallAgents true) AND
        // kcap is on PATH — i.e. a full Gemini integration was requested. It stays true even when the
        // hook WRITE fails (malformed shared settings.json), so the independent ~/.gemini/GEMINI.md can
        // still be healed; the bool return still reflects only actual hook-write success.
        selected = false;

        if (!detected.Gemini) {
            writeLine("  [dim]· Gemini CLI not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Gemini CLI detected");

        if (options.SkipGemini) {
            writeLine("  [dim]· Gemini CLI hooks skipped by flag[/]");

            return false;
        }

        // settings.json writes the bare "kcap hook --gemini" command and relies
        // on Gemini finding it on PATH — same precheck as the Cursor/Copilot branch.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Gemini hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        selected = true;  // opted in + kcap on PATH → GEMINI.md may install even if the hook write fails

        var ok = installers.InstallGeminiHooks(paths.GeminiSettingsPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not install Gemini hooks — ensure settings.json is valid JSON (left untouched).");

            return false;
        }

        writeLine($"  [green]✓[/] Gemini hooks installed ({Markup.Escape(paths.GeminiSettingsPath)})");
        writeLine("  [dim]  Note: Gemini loads hook config at startup — restart any running gemini session to pick them up.[/]");

        return true;
    }

    static bool HandlePiExtension(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine,
            out bool           selected
        ) {
        // `selected` = the user opted into Pi (detected + not skipped, InstallAgents true) AND
        // kcap is on PATH — i.e. a full Pi integration was requested. It stays true even when the
        // ingest-extension WRITE fails, so the independent MCP-bridge extension + ~/.pi/agent/AGENTS.md
        // can still be installed; the bool return still reflects only actual ingest-write success.
        selected = false;

        if (!detected.Pi) {
            writeLine("  [dim]· Pi not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Pi detected");

        if (options.SkipPi) {
            writeLine("  [dim]· Pi extension skipped by flag[/]");

            return false;
        }

        if (installers.InstallPiExtension is null) return false;

        // Pi has no shell hooks — the extensions (kcap.ts / kcap-mcp.ts) shell out to
        // the bare "kcap" command, so pi must find kcap on PATH (same precheck as the
        // Cursor/Copilot branches).
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Pi extension not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        selected = true;  // opted in + kcap on PATH → MCP bridge + AGENTS.md may install even if the ingest write fails

        var ok = installers.InstallPiExtension(paths.PiExtensionPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write the Pi extension file.");

            return false;
        }

        writeLine($"  [green]✓[/] Pi extension installed ({Markup.Escape(paths.PiExtensionPath)})");
        writeLine("  [dim]  Note: Pi loads extensions at startup — restart any running pi session to pick it up.[/]");

        return true;
    }

    /// <summary>
    /// Installs the kcap MCP-bridge extension (<c>~/.pi/agent/extensions/kcap-mcp.ts</c>). Pi has no
    /// JSON <c>mcpServers</c> config, so the "MCP" for Pi is this extension file — spawning the
    /// <c>kcap mcp &lt;name&gt;</c> servers and registering their tools. Gated on the user having
    /// SELECTED Pi (<paramref name="piSelected"/>, not on the ingest-write result — it's a separate
    /// file) and on <see cref="Options.SkipPiMcp"/>.
    /// </summary>
    static bool HandlePiMcp(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           piSelected
        ) {
        if (installers.InstallPiMcp is null || !piSelected || options.SkipPiMcp) return false;

        var path = Markup.Escape(paths.PiMcpExtensionPath);

        if (installers.InstallPiMcp(paths.PiMcpExtensionPath)) {
            writeLine($"  [green]✓[/] Pi MCP extension installed ([dim]{path}[/])");

            return true;
        }

        writeLine($"  [yellow]⚠[/] Could not write the Pi MCP extension ({path}).");

        return false;
    }

    /// <summary>
    /// Installs kcap's agent-instructions block into Pi's global <c>~/.pi/agent/AGENTS.md</c> so Pi's
    /// model is steered toward the kcap MCP tools. Gated on the user having SELECTED Pi (same trigger
    /// as <see cref="HandlePiMcp"/>) and on <see cref="Options.SkipPiInstructions"/>. Non-destructive:
    /// only kcap's marker-delimited block is written.
    /// </summary>
    static bool HandlePiInstructions(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           piSelected
        ) {
        if (installers.InstallPiInstructions is null || !piSelected || options.SkipPiInstructions) return false;

        var path = Markup.Escape(paths.PiAgentsMdPath);

        switch (installers.InstallPiInstructions()) {
            case AgentInstructionsWriter.Change.Updated:
                writeLine($"  [green]✓[/] Pi instructions installed ([dim]{path}[/])");

                return true;
            case AgentInstructionsWriter.Change.Unchanged:
                writeLine("  [dim]· Pi instructions already up to date — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not write Pi instructions to {path}.");

                return false;
        }
    }

    static bool HandleOpenCodeExtension(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine
        ) {
        if (!detected.OpenCode) {
            writeLine("  [dim]· OpenCode not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] OpenCode detected");

        if (options.SkipOpenCode) {
            writeLine("  [dim]· OpenCode plugin skipped by flag[/]");

            return false;
        }

        if (installers.InstallOpenCodeExtension is null) return false;

        // OpenCode has no shell hooks — the plugin (kcap.ts) shells out to the bare
        // "kcap hook --opencode" command, so OpenCode must find kcap on PATH (same
        // precheck as the Pi/Cursor/Copilot branches).
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] OpenCode plugin not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallOpenCodeExtension(paths.OpenCodeExtensionPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write the OpenCode plugin file.");

            return false;
        }

        writeLine($"  [green]✓[/] OpenCode plugin installed ({Markup.Escape(paths.OpenCodeExtensionPath)})");
        writeLine("  [dim]  Note: OpenCode loads plugins at startup — restart any running opencode session to pick it up.[/]");

        return true;
    }

    static bool HandleAntigravityHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine,
            out bool           selected
        ) {
        // `selected` = Antigravity opted-in (detected + not skipped, InstallAgents true) AND kcap on
        // PATH; stays true even if the hooks.json write fails, so the SEPARATE mcp_config.json / shared
        // GEMINI.md / ~/.gemini/skills still install. The bool return reflects only hook-write success.
        selected = false;

        if (!detected.Antigravity) {
            writeLine("  [dim]· Antigravity not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Antigravity detected");

        if (options.SkipAntigravity) {
            writeLine("  [dim]· Antigravity hooks skipped by flag[/]");

            return false;
        }

        if (installers.InstallAntigravityHooks is null) return false;

        // Antigravity's hooks.json runs the bare "kcap hook --antigravity" command, so
        // Antigravity must find kcap on PATH (same precheck as the OpenCode/Pi branches).
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Antigravity hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        selected = true;  // opted in + kcap on PATH → MCP/instructions/skills install even if the hook write fails

        var ok = installers.InstallAntigravityHooks(paths.AntigravityHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write the Antigravity hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Antigravity hooks installed ({Markup.Escape(paths.AntigravityHooksPath)})");
        writeLine("  [dim]  Note: Antigravity loads hooks at startup — restart it to pick them up.[/]");

        return true;
    }

    /// <summary>
    /// Registers the kcap MCP servers in Antigravity's own <c>~/.gemini/config/mcp_config.json</c>
    /// (Standard shape). Gated on the user having SELECTED Antigravity + on
    /// <see cref="Options.SkipAntigravityMcp"/>. No prompt: registration is non-destructive.
    /// </summary>
    static bool HandleAntigravityMcp(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           antigravitySelected
        ) {
        if (installers.RegisterAntigravityMcp is null || !antigravitySelected || options.SkipAntigravityMcp) return false;

        var configPath = Markup.Escape(paths.AntigravityMcpPath);

        switch (installers.RegisterAntigravityMcp()) {
            case JsonMcpConfigWriter.Change.Updated:
                writeLine($"  [green]✓[/] Antigravity MCP servers registered ([dim]{configPath}[/])");

                return true;
            case JsonMcpConfigWriter.Change.Unchanged:
                writeLine("  [dim]· Antigravity MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register Antigravity MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
    }

    /// <summary>
    /// Installs kcap's steering block into the shared <c>~/.gemini/GEMINI.md</c> (read by both
    /// Antigravity and the Gemini CLI). Gated on SELECTED + <see cref="Options.SkipAntigravityInstructions"/>.
    /// </summary>
    static bool HandleAntigravityInstructions(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           antigravitySelected
        ) {
        if (installers.InstallAntigravityInstructions is null || !antigravitySelected || options.SkipAntigravityInstructions) return false;

        var path = Markup.Escape(paths.AntigravityInstructionsPath);

        switch (installers.InstallAntigravityInstructions()) {
            case AgentInstructionsWriter.Change.Updated:
                writeLine($"  [green]✓[/] Antigravity instructions installed ([dim]{path}[/])");

                return true;
            case AgentInstructionsWriter.Change.Unchanged:
                writeLine("  [dim]· Antigravity instructions already up to date — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not write Antigravity instructions to {path}.");

                return false;
        }
    }

    /// <summary>
    /// Installs the kcap skills into <c>~/.gemini/skills</c> — where Antigravity reads them (it does
    /// NOT read the agent-agnostic <c>~/.agents/skills</c>). Reuses the shared skills installer +
    /// marker (targeting the Antigravity skills dir). Gated on SELECTED + <see cref="Options.SkipAntigravitySkills"/>.
    /// </summary>
    static bool HandleAntigravitySkills(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           antigravitySelected
        ) {
        if (!antigravitySelected || options.SkipAntigravitySkills) return false;

        var dst = paths.AntigravitySkillsDir;

        if (installers.AgentSkillsCurrent?.Invoke(dst) == true) {
            writeLine("  [dim]· Antigravity skills already up to date — no change needed[/]");

            return false;
        }

        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Antigravity skills could not be installed (plugin directory not found).");

            return false;
        }

        var src = Path.Combine(paths.PluginDir, "skills");

        if (!installers.InstallAgentSkills(src, dst)) {
            writeLine($"  [yellow]⚠[/] Antigravity skills could not be copied to {Markup.Escape(dst)}");

            return false;
        }

        writeLine($"  [green]✓[/] Antigravity skills installed ([dim]{Markup.Escape(dst)}[/])");

        return true;
    }

    /// <summary>
    /// installs the agent-agnostic kcap skills to <c>~/.agents/skills/</c>,
    /// decoupled from Codex. Every non-Claude coding agent reads (or may read) the
    /// cross-agent skills tree, so install once when any is detected — independent of
    /// whether that agent's hooks were installed. Claude is excluded: it gets skills
    /// through the bundled plugin, not this directory. Idempotent: skips the copy when the
    /// on-disk skills already match this build. The legacy
    /// <c>~/.codex/skills</c> sweep stays Codex-specific.
    /// </summary>
    static bool HandleAgentSkills(
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine
        ) {
        // Antigravity AND Kiro are intentionally EXCLUDED here: they read their own skills dirs
        // (~/.gemini/skills via HandleAntigravitySkills, ~/.kiro/skills via HandleKiroSkills), NOT the
        // agent-agnostic ~/.agents/skills — so their presence alone must not trigger a ~/.agents/skills
        // install they can't see.
        var anyNonClaudeDetected =
            detected.Codex || detected.Cursor || detected.Copilot || detected.Gemini
         || detected.Pi    || detected.OpenCode;

        // Nothing that reads ~/.agents/skills/ is present (Claude-only or nothing) — the
        // Claude plugin install handles Claude's skills, so there's nothing to do here.
        if (!anyNonClaudeDetected) return false;

        // Idempotent: a marker matching this build means the on-disk skills are already
        // current — no re-copy (mirrors PluginCommand's npm-postinstall fast path).
        // Checked before the plugin-dir probe: current skills need no source.
        if (installers.AgentSkillsCurrent?.Invoke(paths.AgentsSkillsDir) == true) {
            writeLine("  [dim]· Agent skills already up to date — no change needed[/]");

            // The shared skills are in place, so still sweep any stale legacy
            // ~/.codex/skills (Codex-only) — a Cursor-first install could have made the
            // marker current without ever running this Codex-specific cleanup.
            SweepLegacyCodexSkills(detected, paths, installers);

            return false;
        }

        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Agent skills could not be installed (plugin directory not found).");

            return false;
        }

        var src = Path.Combine(paths.PluginDir, "skills");
        var ok  = installers.InstallAgentSkills(src, paths.AgentsSkillsDir);

        if (!ok) {
            writeLine($"  [yellow]⚠[/] Agent skills could not be copied to {Markup.Escape(paths.AgentsSkillsDir)}");

            return false;
        }

        writeLine($"  [green]✓[/] Agent skills installed (user: {Markup.Escape(paths.AgentsSkillsDir)})");
        writeLine("    [dim]kcap-recap, kcap-errors, kcap-hide, kcap-disable, kcap-validate-plan, review-flows[/]");

        SweepLegacyCodexSkills(detected, paths, installers);

        return true;
    }

    /// <summary>
    /// Codex-specific: removes the legacy <c>~/.codex/skills/kcap-*</c> folders
    /// left by pre-migration installer versions (Codex reads <c>~/.agents/skills</c> now).
    /// Runs only when Codex is detected <em>and</em> the shared skills are in place this
    /// run (freshly installed or already current) — never on the declined / copy-failed /
    /// no-plugin paths, so we don't strip a Codex user's skills with no replacement.
    /// </summary>
    static void SweepLegacyCodexSkills(DetectedAgents detected, Paths paths, Installers installers) {
        if (detected.Codex) installers.CleanLegacyCodexSkills(paths.LegacyCodexSkillsDir);
    }

    static bool HandleCodexHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine
        ) {
        if (!detected.Codex) {
            writeLine("  [dim]· Codex CLI not found on PATH — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Codex CLI detected");

        if (options.SkipCodex) {
            writeLine("  [dim]· Codex CLI hooks skipped by flag[/]");

            return false;
        }

        var ok = installers.InstallCodexHooks(paths.CodexHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Codex hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Codex hooks installed (user: {Markup.Escape(paths.CodexHooksPath)})");
        writeLine("  [dim]  Next: Codex will prompt to trust the kcap hooks on its next launch —[/]");
        writeLine("  [dim]  accept once to trust them all (or run /hooks inside Codex to trust them individually).[/]");

        return true;
    }

    /// <summary>
    /// Codex runs the agent's shell tool in a network-blocked sandbox, so kcap
    /// skills (which shell out to <c>kcap …</c>) can't reach the Capacitor server. After
    /// Codex hooks install, offer to enable sandbox network access for the configured
    /// server(s) via <see cref="Installers.EnableCodexNetworkAccess"/>. Gated on hooks
    /// actually installing — there's nothing to fix if the Codex integration is off.
    /// </summary>
    static bool HandleCodexNetworkAccess(
            Options            options,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine,
            bool               codexHooksInstalled
        ) {
        if (!codexHooksInstalled || installers.EnableCodexNetworkAccess is null) return false;

        if (options.SkipCodexNetworkAccess) {
            writeLine("  [dim]· Codex sandbox network access left unchanged (--skip-codex-network-access)[/]");

            return false;
        }

        writeLine("  [dim]  kcap skills (recap, errors, …) run in Codex's sandbox, which blocks network by default.[/]");

        var shouldApply = options.NoPrompt
            || prompt("Allow Codex's sandbox to reach your Capacitor server(s) so kcap skills work?");

        if (!shouldApply) {
            writeLine("  [dim]· Codex sandbox network access not changed — kcap skills may prompt for escalation (see README)[/]");

            return false;
        }

        var configPath = Markup.Escape(paths.CodexConfigTomlPath);

        switch (installers.EnableCodexNetworkAccess()) {
            case CodexConfigToml.Change.Updated:
                writeLine($"  [green]✓[/] Codex sandbox network access enabled for kcap ([dim]{configPath}[/])");

                return true;
            case CodexConfigToml.Change.Unchanged:
                writeLine("  [dim]· Codex sandbox already allows network access — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not update {configPath} — enable Codex sandbox network access manually (see README).");

                return false;
        }
    }

    /// <summary>
    /// Registers the kcap MCP servers (<see cref="KcapMcpServers.ForCodex"/>) in
    /// <c>~/.codex/config.toml</c> via <see cref="Installers.RegisterCodexMcp"/> so Codex
    /// CLI picks them up with no manual TOML edit. Gated on Codex hooks installing — the
    /// same "full Codex integration" trigger as skills. No prompt: registration is
    /// non-destructive (only adds missing kcap servers) and mirrors how the Claude plugin
    /// auto-registers its MCP servers. <c>kcap-flows</c> stays Claude-only.
    /// </summary>
    static bool HandleCodexMcp(
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           codexHooksInstalled
        ) {
        if (!codexHooksInstalled || installers.RegisterCodexMcp is null) return false;

        var configPath = Markup.Escape(paths.CodexConfigTomlPath);

        switch (installers.RegisterCodexMcp()) {
            case CodexConfigToml.Change.Updated:
                writeLine($"  [green]✓[/] Codex MCP servers registered: {string.Join(", ", KcapMcpServers.ForCodex.Select(s => s.Name))} ([dim]{configPath}[/])");

                return true;
            case CodexConfigToml.Change.Unchanged:
                writeLine("  [dim]· Codex MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register Codex MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
    }

    static bool HandleCursorHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine
        ) {
        if (!detected.Cursor) {
            writeLine("  [dim]· Cursor not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Cursor detected");

        if (options.SkipCursor) {
            writeLine("  [dim]· Cursor hooks skipped by flag[/]");

            return false;
        }

        // hooks.json writes the bare "kcap hook --cursor" command and
        // relies on Cursor finding it on PATH. If kcap isn't on PATH
        // we'd write a config Cursor can't execute — surface a setup error
        // instead. Mirror of PluginCommand.InstallCursor's precheck.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Cursor hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallCursorHooks(paths.CursorHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Cursor hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Cursor hooks installed ({Markup.Escape(paths.CursorHooksPath)})");

        return true;
    }

    /// <summary>
    /// Registers the kcap MCP servers in <c>~/.cursor/mcp.json</c> via
    /// <see cref="Installers.RegisterCursorMcp"/> so Cursor picks them up with no manual
    /// JSON edit. Gated on Cursor hooks installing — the same "full Cursor integration"
    /// trigger used by <see cref="HandleCodexMcp"/> — and on <see cref="Options.SkipCursorMcp"/>.
    /// No prompt: registration is non-destructive (only adds missing kcap servers) and
    /// mirrors how the Claude plugin auto-registers its MCP servers.
    /// </summary>
    static bool HandleCursorMcp(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           cursorHooksInstalled
        ) {
        if (installers.RegisterCursorMcp is null || !cursorHooksInstalled || options.SkipCursorMcp) return false;

        var configPath = Markup.Escape(paths.CursorMcpPath);

        switch (installers.RegisterCursorMcp()) {
            case JsonMcpConfigWriter.Change.Updated:
                writeLine($"  [green]✓[/] Cursor MCP servers registered ([dim]{configPath}[/])");

                return true;
            case JsonMcpConfigWriter.Change.Unchanged:
                writeLine("  [dim]· Cursor MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register Cursor MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
    }

    /// <summary>
    /// Registers the kcap MCP servers in <c>~/.copilot/mcp-config.json</c> via
    /// <see cref="Installers.RegisterCopilotMcp"/> so Copilot picks them up with no manual
    /// JSON edit. Gated on Copilot hooks installing — the same "full Copilot integration"
    /// trigger used by <see cref="HandleCursorMcp"/> — and on <see cref="Options.SkipCopilotMcp"/>.
    /// No prompt: registration is non-destructive (only adds missing kcap servers).
    /// </summary>
    static bool HandleCopilotMcp(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           copilotHooksInstalled
        ) {
        if (installers.RegisterCopilotMcp is null || !copilotHooksInstalled || options.SkipCopilotMcp) return false;

        var configPath = Markup.Escape(paths.CopilotMcpPath);

        switch (installers.RegisterCopilotMcp()) {
            case JsonMcpConfigWriter.Change.Updated:
                writeLine($"  [green]✓[/] Copilot MCP servers registered ([dim]{configPath}[/])");

                return true;
            case JsonMcpConfigWriter.Change.Unchanged:
                writeLine("  [dim]· Copilot MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register Copilot MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
    }

    /// <summary>
    /// Installs kcap's agent-instructions block into <c>~/.copilot/copilot-instructions.md</c> via
    /// <see cref="Installers.InstallCopilotInstructions"/> so Copilot's model is steered toward the
    /// kcap MCP tools. Gated on Copilot hooks installing — same "full Copilot integration" trigger
    /// as <see cref="HandleCopilotMcp"/> — and on <see cref="Options.SkipCopilotInstructions"/>.
    /// Non-destructive: only kcap's marker-delimited block is written.
    /// </summary>
    static bool HandleCopilotInstructions(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           copilotHooksInstalled
        ) {
        if (installers.InstallCopilotInstructions is null || !copilotHooksInstalled || options.SkipCopilotInstructions) return false;

        var path = Markup.Escape(paths.CopilotInstructionsPath);

        switch (installers.InstallCopilotInstructions()) {
            case AgentInstructionsWriter.Change.Updated:
                writeLine($"  [green]✓[/] Copilot instructions installed ([dim]{path}[/])");

                return true;
            case AgentInstructionsWriter.Change.Unchanged:
                writeLine("  [dim]· Copilot instructions already up to date — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not write Copilot instructions to {path}.");

                return false;
        }
    }

    /// <summary>
    /// Registers the kcap MCP servers into Gemini's shared <c>~/.gemini/settings.json</c>
    /// (<c>mcpServers</c> block) via <see cref="Installers.RegisterGeminiMcp"/> so Gemini picks them
    /// up with no manual JSON edit. Gated on Gemini hooks installing — the same "full Gemini
    /// integration" trigger used by the Cursor/Copilot MCP handlers — and on
    /// <see cref="Options.SkipGeminiMcp"/>. No prompt: registration is non-destructive (only adds
    /// missing kcap servers, preserving the user's other settings).
    /// </summary>
    static bool HandleGeminiMcp(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           geminiHooksInstalled
        ) {
        if (installers.RegisterGeminiMcp is null || !geminiHooksInstalled || options.SkipGeminiMcp) return false;

        // MCP servers live in the same settings.json as the hooks.
        var configPath = Markup.Escape(paths.GeminiSettingsPath);

        switch (installers.RegisterGeminiMcp()) {
            case JsonMcpConfigWriter.Change.Updated:
                writeLine($"  [green]✓[/] Gemini MCP servers registered ([dim]{configPath}[/])");

                return true;
            case JsonMcpConfigWriter.Change.Unchanged:
                writeLine("  [dim]· Gemini MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register Gemini MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
    }

    /// <summary>
    /// Installs kcap's agent-instructions block into Gemini's global <c>~/.gemini/GEMINI.md</c> via
    /// <see cref="Installers.InstallGeminiInstructions"/> so Gemini's model is steered toward the
    /// kcap MCP tools. Gated on Gemini hooks installing — same "full Gemini integration" trigger as
    /// <see cref="HandleGeminiMcp"/> — and on <see cref="Options.SkipGeminiInstructions"/>.
    /// Non-destructive: only kcap's marker-delimited block is written.
    /// </summary>
    static bool HandleGeminiInstructions(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           geminiSelected
        ) {
        if (installers.InstallGeminiInstructions is null || !geminiSelected || options.SkipGeminiInstructions) return false;

        var path = Markup.Escape(paths.GeminiInstructionsPath);

        switch (installers.InstallGeminiInstructions()) {
            case AgentInstructionsWriter.Change.Updated:
                writeLine($"  [green]✓[/] Gemini instructions installed ([dim]{path}[/])");

                return true;
            case AgentInstructionsWriter.Change.Unchanged:
                writeLine("  [dim]· Gemini instructions already up to date — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not write Gemini instructions to {path}.");

                return false;
        }
    }

    /// <summary>
    /// Registers the kcap MCP servers in OpenCode's <c>~/.config/opencode/opencode.json</c> via
    /// <see cref="Installers.RegisterOpenCodeMcp"/>. Gated on the OpenCode plugin installing — the
    /// same "full OpenCode integration" trigger as the plugin step — and on
    /// <see cref="Options.SkipOpenCodeMcp"/>. Non-destructive + idempotent.
    /// </summary>
    static bool HandleOpenCodeMcp(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           openCodeExtensionInstalled
        ) {
        if (installers.RegisterOpenCodeMcp is null || !openCodeExtensionInstalled || options.SkipOpenCodeMcp) return false;

        var configPath = Markup.Escape(paths.OpenCodeMcpPath);

        switch (installers.RegisterOpenCodeMcp()) {
            case JsonMcpConfigWriter.Change.Updated:
                writeLine($"  [green]✓[/] OpenCode MCP servers registered ([dim]{configPath}[/])");

                return true;
            case JsonMcpConfigWriter.Change.Unchanged:
                writeLine("  [dim]· OpenCode MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register OpenCode MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
    }

    /// <summary>
    /// Installs kcap's agent-instructions block into OpenCode's <c>~/.config/opencode/AGENTS.md</c>
    /// via <see cref="Installers.InstallOpenCodeInstructions"/>. Gated on the OpenCode plugin
    /// installing and on <see cref="Options.SkipOpenCodeInstructions"/>. Non-destructive: only
    /// kcap's marker-delimited block is written.
    /// </summary>
    static bool HandleOpenCodeInstructions(
            Options        options,
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           openCodeExtensionInstalled
        ) {
        if (installers.InstallOpenCodeInstructions is null || !openCodeExtensionInstalled || options.SkipOpenCodeInstructions) return false;

        var path = Markup.Escape(paths.OpenCodeInstructionsPath);

        switch (installers.InstallOpenCodeInstructions()) {
            case AgentInstructionsWriter.Change.Updated:
                writeLine($"  [green]✓[/] OpenCode instructions installed ([dim]{path}[/])");

                return true;
            case AgentInstructionsWriter.Change.Unchanged:
                writeLine("  [dim]· OpenCode instructions already up to date — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not write OpenCode instructions to {path}.");

                return false;
        }
    }

    static bool HandleClaude(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Action<string>     writeLine
        ) {
        if (!detected.Claude) {
            writeLine("  [dim]· Claude Code not found on PATH — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Claude Code detected");

        if (options.SkipClaude) {
            writeLine("  [dim]· Claude Code plugin skipped by flag[/]");

            return false;
        }

        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Plugin directory not found. Re-install kcap via npm:");
            writeLine("    [cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallClaudePlugin(paths.ClaudeSettingsPath, paths.PluginDir);

        if (!ok) {
            writeLine($"  [yellow]⚠[/] Could not update Claude settings file. Install manually inside Claude Code: [cyan]/plugin install {Markup.Escape(paths.PluginDir)}[/]");

            return false;
        }

        writeLine($"  [green]✓[/] Claude Code plugin installed ({Markup.Escape(paths.ClaudeScopeLabel)}: {Markup.Escape(paths.ClaudeSettingsPath)})");

        return true;
    }
}
