using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;
using static Capacitor.Cli.Commands.CodingAgentsStep;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class CodingAgentsStepTests {
    [Test]
    public async Task Claude_detected_and_accepted_calls_installer_with_settings_path() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var paths    = TestPaths();
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsTrue();
        await Assert.That(calls.ClaudeArgs).IsEqualTo((paths.ClaudeSettingsPath, paths.PluginDir!));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code plugin installed") && l.Contains("user:"));
    }

    [Test]
    public async Task Claude_installs_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — SetupCommand's single InstallAgents decision is the
        // only gate now, so Claude installs regardless of what a (no longer consulted) prompt
        // delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => false,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsTrue();
        await Assert.That(calls.ClaudeCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code plugin installed"));
    }

    [Test]
    public async Task Claude_not_detected_skips_prompt_and_emits_skip_line() {
        var sink        = new Sink();
        var calls       = new InstallerCalls();
        var promptCount = 0;
        var options     = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        // Codex: true keeps at least one agent detected so this exercises Claude's own
        // "not detected" skip line rather than the distinct all-agents-absent early return.
        var detected    = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => {
                promptCount++;

                return true;
            },
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(promptCount).IsEqualTo(0);
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code not found"));
    }

    [Test]
    public async Task Claude_installer_failure_emits_warning_and_returns_false() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { ClaudeReturns = false };
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not update Claude settings"));
    }

    [Test]
    public async Task Codex_detected_and_accepted_installs_hooks_and_prints_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(calls.CodexHooksArg).IsEqualTo("/fake/.codex/hooks.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_installs_and_shows_trust_hint_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — Codex hooks install (and the trust hint prints)
        // regardless of what a (no longer consulted) prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => false,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(calls.CodexHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_not_detected_skips_prompt_and_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        // Claude: true keeps at least one agent detected so this exercises Codex's own
        // "not detected" skip line rather than the distinct all-agents-absent early return.
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex CLI not found"));
    }

    [Test]
    public async Task Codex_hooks_installer_failure_emits_warning_and_skips_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Codex hooks"));
        await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_skills_installed_when_hooks_succeed_and_plugin_dir_present() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsArgs).IsEqualTo((Path.Combine("/fake/plugin", "skills"), "/fake/.agents/skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills installed"));
    }

    [Test]
    public async Task Agent_skills_failure_still_keeps_codex_trust_hint() {
        // Skills install and Codex hooks are independent steps — a skills-copy
        // failure must not suppress the Codex trust hint.
        var sink     = new Sink();
        var calls    = new InstallerCalls { AgentSkillsReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills could not be copied"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    // ── Agent skills decoupled from Codex ──────────────────────────

    [Test]
    public async Task Agent_skills_installed_when_only_cursor_detected() {
        // The bug: a Cursor-only machine (no Codex) got hooks but never the
        // shared ~/.agents/skills/ skills, because the install was gated on Codex.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(calls.AgentSkillsArgs).IsEqualTo((Path.Combine("/fake/plugin", "skills"), "/fake/.agents/skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills installed"));
    }

    [Test]
    public async Task Agent_skills_installed_when_only_copilot_detected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_installed_when_only_gemini_detected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipGemini: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_still_installed_when_codex_hooks_fail() {
        // Decoupling means a Codex hooks-write failure no longer suppresses the
        // skills copy — Codex is still detected and reads ~/.agents/skills/.
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_install_even_if_prompt_would_decline() {
        // The standalone "Install kcap agent skills?" prompt is gone — folded into the single
        // InstallAgents decision — so skills install regardless of what a (no longer consulted)
        // prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => !t.Contains("agent skills", StringComparison.OrdinalIgnoreCase),
            writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills installed"));
    }

    [Test]
    public async Task Agent_skills_skipped_when_already_current() {
        // Idempotent: a marker matching this build means no prompt and no re-copy.
        var sink     = new Sink();
        var promptCount = 0;
        var calls    = new InstallerCalls { AgentSkillsCurrentReturns = true };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => {
                if (t.Contains("agent skills", StringComparison.OrdinalIgnoreCase)) promptCount++;

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(promptCount).IsEqualTo(0);
        await Assert.That(sink.Lines).Contains(l => l.Contains("already up to date"));
    }

    [Test]
    public async Task Agent_skills_not_installed_when_only_claude_detected() {
        // Claude gets skills via the bundled plugin, not ~/.agents/skills/ — a
        // Claude-only machine must not trigger the shared skills install.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
    }

    [Test]
    public async Task Agent_skills_installed_without_prompting_across_multiple_detected_agents() {
        // The standalone "Install kcap agent skills?" prompt is gone — a single install covers
        // every skills-capable agent, with no prompt call at all (not one per agent).
        var sink        = new Sink();
        var promptCount = 0;
        var calls       = new InstallerCalls();
        var options     = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: false, NoPrompt: false, SkipGemini: false);
        var detected    = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: true, Gemini: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => {
                if (t.Contains("agent skills", StringComparison.OrdinalIgnoreCase)) promptCount++;

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(promptCount).IsEqualTo(0);
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_installed_without_prompt_in_no_prompt_mode() {
        var sink     = new Sink();
        var promptCount = 0;
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => {
                promptCount++;

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(promptCount).IsEqualTo(0);
    }

    [Test]
    public async Task Legacy_codex_cleanup_not_invoked_when_codex_not_detected() {
        // The ~/.codex/skills legacy sweep stays Codex-specific even though the
        // skills copy is now shared.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(calls.LegacyCleanupCalled).IsFalse();
    }

    [Test]
    public async Task Legacy_codex_cleanup_runs_when_skills_current_and_codex_detected() {
        // Codex detected + shared skills already current → the new skills ARE in place,
        // so the stale ~/.codex/skills legacy folders should still be swept even though
        // we short-circuit the (re)install.
        var sink     = new Sink();
        var calls    = new InstallerCalls { AgentSkillsCurrentReturns = true };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(calls.LegacyCleanupCalled).IsTrue();
        await Assert.That(calls.LegacyCleanupArg).IsEqualTo("/fake/.codex/skills");
    }

    [Test]
    public async Task Codex_hooks_no_longer_prompt_for_install_or_skills() {
        // Per-vendor consent prompts are gone (folded into SetupCommand's single InstallAgents
        // decision) — Codex hooks and the shared agent-skills install must proceed without ever
        // calling `prompt` for either. (Codex's SEPARATE network-access prompt still fires once
        // hooks install — that one intentionally remains.)
        var sink     = new Sink();
        var prompts  = new List<string>();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipCodexNetworkAccess: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => {
                prompts.Add(t);

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(prompts).IsEmpty();
        await Assert.That(calls.CodexHooksCalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    // ── Codex sandbox network access ────────────────────────────────

    [Test]
    public async Task Codex_network_access_enabled_when_hooks_installed_and_accepted() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexNetworkAccessApplied).IsTrue();
        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("network access enabled"));
    }

    [Test]
    public async Task Codex_network_access_declined_is_not_applied() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        // Accept the hooks prompt, decline only the network prompt (which is the
        // one asking to "reach your Capacitor" server).
        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: text => !text.Contains("reach your Capacitor"), writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(calls.EnableCodexNetworkCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("network access not changed"));
    }

    [Test]
    public async Task Codex_network_access_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipCodexNetworkAccess: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(calls.EnableCodexNetworkCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("--skip-codex-network-access"));
    }

    [Test]
    public async Task Codex_network_access_unchanged_when_already_enabled() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { EnableCodexNetworkReturns = CodexConfigToml.Change.Unchanged };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("already allows network access"));
    }

    [Test]
    public async Task Codex_network_access_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { EnableCodexNetworkReturns = CodexConfigToml.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not update") && l.Contains("config.toml"));
    }

    [Test]
    public async Task Codex_mcp_registered_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterCodexMcpCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("MCP servers registered"));
    }

    [Test]
    public async Task Codex_mcp_unchanged_when_already_registered() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterCodexMcpReturns = CodexConfigToml.Change.Unchanged };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterCodexMcpCalled).IsTrue();
        await Assert.That(result.CodexMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("already registered"));
    }

    [Test]
    public async Task Codex_mcp_not_registered_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.RegisterCodexMcpCalled).IsFalse();
        await Assert.That(result.CodexMcpRegistered).IsFalse();
    }

    [Test]
    public async Task Codex_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterCodexMcpReturns = CodexConfigToml.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterCodexMcpCalled).IsTrue();
        await Assert.That(result.CodexMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register Codex MCP"));
    }

    [Test]
    public async Task Cursor_mcp_registered_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterCursorMcpCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("MCP servers registered"));
    }

    [Test]
    public async Task Cursor_mcp_not_registered_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CursorHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsFalse();
        await Assert.That(calls.RegisterCursorMcpCalled).IsFalse();
        await Assert.That(result.CursorMcpRegistered).IsFalse();
    }

    [Test]
    public async Task Cursor_mcp_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true, SkipCursorMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsTrue();
        await Assert.That(result.CursorMcpRegistered).IsFalse();
        await Assert.That(calls.RegisterCursorMcpCalled).IsFalse();
    }

    [Test]
    public async Task Cursor_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterCursorMcpReturns = JsonMcpConfigWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterCursorMcpCalled).IsTrue();
        await Assert.That(result.CursorMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register Cursor MCP"));
    }

    [Test]
    public async Task Copilot_mcp_registered_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterCopilotMcpCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("MCP servers registered"));
    }

    [Test]
    public async Task Copilot_mcp_not_registered_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CopilotHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsFalse();
        await Assert.That(calls.RegisterCopilotMcpCalled).IsFalse();
        await Assert.That(result.CopilotMcpRegistered).IsFalse();
    }

    [Test]
    public async Task Copilot_mcp_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true, SkipCopilotMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsTrue();
        await Assert.That(result.CopilotMcpRegistered).IsFalse();
        await Assert.That(calls.RegisterCopilotMcpCalled).IsFalse();
    }

    [Test]
    public async Task Copilot_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterCopilotMcpReturns = JsonMcpConfigWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterCopilotMcpCalled).IsTrue();
        await Assert.That(result.CopilotMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register Copilot MCP"));
    }

    [Test]
    public async Task Copilot_instructions_installed_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotInstructionsInstalled).IsTrue();
        await Assert.That(calls.InstallCopilotInstructionsCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot instructions installed"));
    }

    [Test]
    public async Task Copilot_instructions_not_installed_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CopilotHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsFalse();
        await Assert.That(calls.InstallCopilotInstructionsCalled).IsFalse();
        await Assert.That(result.CopilotInstructionsInstalled).IsFalse();
    }

    [Test]
    public async Task Copilot_instructions_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true, SkipCopilotInstructions: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsTrue();
        await Assert.That(result.CopilotInstructionsInstalled).IsFalse();
        await Assert.That(calls.InstallCopilotInstructionsCalled).IsFalse();
    }

    [Test]
    public async Task Copilot_instructions_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { InstallCopilotInstructionsReturns = AgentInstructionsWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.InstallCopilotInstructionsCalled).IsTrue();
        await Assert.That(result.CopilotInstructionsInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Copilot instructions"));
    }

    static (Options, DetectedAgents) AntigravityOnly(bool skipMcp = false, bool skipInstr = false, bool skipSkills = false) => (
        new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true,
            SkipAntigravityMcp: skipMcp, SkipAntigravityInstructions: skipInstr, SkipAntigravitySkills: skipSkills),
        new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Antigravity: true));

    [Test]
    public async Task Antigravity_mcp_and_instructions_registered_when_selected() {
        var sink = new Sink();
        var calls = new InstallerCalls();
        var (options, detected) = AntigravityOnly();

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterAntigravityMcpCalled).IsTrue();
        await Assert.That(result.AntigravityMcpRegistered).IsTrue();
        await Assert.That(calls.InstallAntigravityInstructionsCalled).IsTrue();
        await Assert.That(result.AntigravityInstructionsInstalled).IsTrue();
    }

    [Test]
    public async Task Antigravity_skills_go_to_gemini_dir_not_agents_skills() {
        var sink = new Sink();
        var calls = new InstallerCalls();
        var (options, detected) = AntigravityOnly();

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        var dsts = calls.AgentSkillsInstalls.Select(x => x.Dst).ToArray();
        await Assert.That(dsts).Contains("/fake/.gemini/skills");        // Antigravity skills → ~/.gemini/skills
        await Assert.That(dsts).DoesNotContain("/fake/.agents/skills");  // NOT the agent-agnostic dir it can't read
        await Assert.That(result.AntigravitySkillsInstalled).IsTrue();
    }

    [Test]
    public async Task Antigravity_mcp_instructions_skills_skipped_by_flags() {
        var sink = new Sink();
        var calls = new InstallerCalls();
        var (options, detected) = AntigravityOnly(skipMcp: true, skipInstr: true, skipSkills: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterAntigravityMcpCalled).IsFalse();
        await Assert.That(calls.InstallAntigravityInstructionsCalled).IsFalse();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).DoesNotContain("/fake/.gemini/skills");
    }

    [Test]
    public async Task Antigravity_installs_independent_files_even_when_hook_write_fails() {
        // Antigravity opted-in + kcap on PATH but the hooks.json write fails → MCP (own file),
        // instructions (GEMINI.md), and skills (~/.gemini/skills) are all separate, so they still install.
        var sink = new Sink();
        var calls = new InstallerCalls { AntigravityHooksReturns = false };
        var (options, detected) = AntigravityOnly();

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AntigravityHooksInstalled).IsFalse();
        await Assert.That(calls.RegisterAntigravityMcpCalled).IsTrue();
        await Assert.That(calls.InstallAntigravityInstructionsCalled).IsTrue();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).Contains("/fake/.gemini/skills");
    }

    [Test]
    public async Task Antigravity_nothing_installed_when_not_selected() {
        // kcap not on PATH → not selected → no MCP/instructions/skills.
        var sink = new Sink();
        var calls = new InstallerCalls { CapacitorOnPathReturns = false };
        var (options, detected) = AntigravityOnly();

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterAntigravityMcpCalled).IsFalse();
        await Assert.That(calls.InstallAntigravityInstructionsCalled).IsFalse();
        await Assert.That(calls.AgentSkillsInstalls).IsEmpty();
    }

    [Test]
    public async Task Antigravity_mcp_not_fired_for_gemini_only_machine() {
        // Non-conflation: Antigravity + Gemini CLI share ~/.gemini but use different files.
        // A Gemini-only machine (Antigravity NOT detected) must NOT register Antigravity's MCP into
        // ~/.gemini/config/mcp_config.json, nor write ~/.gemini/skills.
        var sink = new Sink();
        var calls = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true, Antigravity: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterAntigravityMcpCalled).IsFalse();
        await Assert.That(calls.InstallAntigravityInstructionsCalled).IsFalse();
        await Assert.That(result.AntigravityMcpRegistered).IsFalse();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).DoesNotContain("/fake/.gemini/skills");
    }

    [Test]
    public async Task Gemini_mcp_registered_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.GeminiMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterGeminiMcpCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Gemini MCP servers registered"));
    }

    [Test]
    public async Task Gemini_mcp_not_registered_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { GeminiHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.GeminiHooksInstalled).IsFalse();
        await Assert.That(calls.RegisterGeminiMcpCalled).IsFalse();
        await Assert.That(result.GeminiMcpRegistered).IsFalse();
    }

    [Test]
    public async Task Gemini_mcp_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipGeminiMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.GeminiHooksInstalled).IsTrue();
        await Assert.That(result.GeminiMcpRegistered).IsFalse();
        await Assert.That(calls.RegisterGeminiMcpCalled).IsFalse();
    }

    [Test]
    public async Task Gemini_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterGeminiMcpReturns = JsonMcpConfigWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterGeminiMcpCalled).IsTrue();
        await Assert.That(result.GeminiMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register Gemini MCP"));
    }

    [Test]
    public async Task Gemini_instructions_installed_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.GeminiInstructionsInstalled).IsTrue();
        await Assert.That(calls.InstallGeminiInstructionsCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Gemini instructions installed"));
    }

    [Test]
    public async Task Gemini_instructions_installed_even_when_hook_write_fails() {
        // The user SELECTED Gemini (detected + opted-in + kcap on PATH) but the shared settings.json
        // hook write failed (e.g. malformed JSON). GEMINI.md is a separate file, so it still installs.
        var sink     = new Sink();
        var calls    = new InstallerCalls { GeminiHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.GeminiHooksInstalled).IsFalse();               // hook write failed
        await Assert.That(calls.InstallGeminiInstructionsCalled).IsTrue();      // but GEMINI.md still installs
        await Assert.That(result.GeminiInstructionsInstalled).IsTrue();
    }

    [Test]
    public async Task Gemini_instructions_not_installed_when_not_selected() {
        // kcap not on PATH → Gemini integration NOT selected → GEMINI.md must NOT install either.
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.GeminiHooksInstalled).IsFalse();
        await Assert.That(calls.InstallGeminiInstructionsCalled).IsFalse();
        await Assert.That(result.GeminiInstructionsInstalled).IsFalse();
    }

    [Test]
    public async Task Gemini_instructions_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipGeminiInstructions: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.GeminiHooksInstalled).IsTrue();
        await Assert.That(result.GeminiInstructionsInstalled).IsFalse();
        await Assert.That(calls.InstallGeminiInstructionsCalled).IsFalse();
    }

    [Test]
    public async Task Gemini_instructions_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { InstallGeminiInstructionsReturns = AgentInstructionsWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.InstallGeminiInstructionsCalled).IsTrue();
        await Assert.That(result.GeminiInstructionsInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Gemini instructions"));
    }

    // ── Pi MCP-bridge extension + AGENTS.md steering ──────────────
    // Pi has no JSON mcpServers config: the "MCP" is a second extension file
    // (kcap-mcp.ts) and the instructions live in ~/.pi/agent/AGENTS.md — both
    // gated on the user having SELECTED Pi, independent of the ingest write.

    [Test]
    public async Task Pi_mcp_installed_when_selected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiMcpInstalled).IsTrue();
        await Assert.That(calls.InstallPiMcpCalled).IsTrue();
        await Assert.That(calls.InstallPiMcpArg).IsEqualTo("/fake/.pi/agent/extensions/kcap-mcp.ts");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi MCP extension installed"));
    }

    [Test]
    public async Task Pi_mcp_installed_even_when_ingest_write_fails() {
        // The user SELECTED Pi (detected + opted-in + kcap on PATH) but the ingest
        // extension write failed. kcap-mcp.ts is a separate file, so it still installs.
        var sink     = new Sink();
        var calls    = new InstallerCalls { PiExtensionReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsFalse();  // ingest write failed
        await Assert.That(calls.InstallPiMcpCalled).IsTrue();      // but kcap-mcp.ts still installs
        await Assert.That(result.PiMcpInstalled).IsTrue();
    }

    [Test]
    public async Task Pi_mcp_not_installed_when_not_selected() {
        // kcap not on PATH → Pi integration NOT selected → kcap-mcp.ts must NOT install.
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.InstallPiMcpCalled).IsFalse();
        await Assert.That(result.PiMcpInstalled).IsFalse();
    }

    [Test]
    public async Task Pi_mcp_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipPiMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsTrue();   // ingest still installs
        await Assert.That(result.PiMcpInstalled).IsFalse();
        await Assert.That(calls.InstallPiMcpCalled).IsFalse();
    }

    [Test]
    public async Task Pi_mcp_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { InstallPiMcpReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.InstallPiMcpCalled).IsTrue();
        await Assert.That(result.PiMcpInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write the Pi MCP extension"));
    }

    [Test]
    public async Task Pi_instructions_installed_when_selected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiInstructionsInstalled).IsTrue();
        await Assert.That(calls.InstallPiInstructionsCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi instructions installed"));
    }

    [Test]
    public async Task Pi_instructions_installed_even_when_ingest_write_fails() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { PiExtensionReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsFalse();          // ingest write failed
        await Assert.That(calls.InstallPiInstructionsCalled).IsTrue();     // but AGENTS.md still installs
        await Assert.That(result.PiInstructionsInstalled).IsTrue();
    }

    [Test]
    public async Task Pi_instructions_not_installed_when_not_selected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.InstallPiInstructionsCalled).IsFalse();
        await Assert.That(result.PiInstructionsInstalled).IsFalse();
    }

    [Test]
    public async Task Pi_instructions_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipPiInstructions: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsTrue();
        await Assert.That(result.PiInstructionsInstalled).IsFalse();
        await Assert.That(calls.InstallPiInstructionsCalled).IsFalse();
    }

    [Test]
    public async Task Pi_instructions_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { InstallPiInstructionsReturns = AgentInstructionsWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.InstallPiInstructionsCalled).IsTrue();
        await Assert.That(result.PiInstructionsInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Pi instructions"));
    }

    [Test]
    public async Task Codex_network_access_not_attempted_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.EnableCodexNetworkCalled).IsFalse();
    }

    [Test]
    public async Task Codex_network_access_auto_applied_in_no_prompt() {
        var sink         = new Sink();
        var calls        = new InstallerCalls();
        var options      = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected     = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);
        var promptCalled = false;

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => { promptCalled = true; return false; }, writeLine: sink.Write);

        await Assert.That(result.CodexNetworkAccessApplied).IsTrue();
        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(promptCalled).IsFalse();
    }

    [Test]
    public async Task Plugin_dir_null_skips_claude_install_but_keeps_codex_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: true, Cursor: false, Copilot: false);
        var paths    = TestPaths() with { PluginDir = null };

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(calls.CodexHooksCalled).IsTrue();
        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Plugin directory not found"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills could not be installed (plugin directory not found)"));
    }

    [Test]
    public async Task Neither_detected_emits_warning_and_no_installer_calls() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: false, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("No supported agent CLI detected"));
    }

    [Test]
    public async Task Project_scope_claude_path_is_passed_through_to_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        // Caller (SetupCommand) selects the project-scope path AND the matching label.
        // The step's contract is to honour both faithfully.
        var paths = TestPaths() with {
            ClaudeSettingsPath = "/repo/.claude/settings.local.json",
            ClaudeScopeLabel = "project"
        };

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsTrue();
        await Assert.That(calls.ClaudeArgs).IsEqualTo(("/repo/.claude/settings.local.json", paths.PluginDir!));
        await Assert.That(sink.Lines).Contains(l => l.Contains("project:") && l.Contains("/repo/.claude/settings.local.json"));
    }

    [Test]
    public async Task Legacy_cleanup_invoked_after_successful_codex_skills_install() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(calls.LegacyCleanupCalled).IsTrue();
        await Assert.That(calls.LegacyCleanupArg).IsEqualTo("/fake/.codex/skills");
    }

    [Test]
    public async Task Legacy_cleanup_not_invoked_when_codex_skills_install_fails() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { AgentSkillsReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(calls.LegacyCleanupCalled).IsFalse();
    }

    [Test]
    public async Task Claude_path_with_markup_special_chars_does_not_break_output() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        // [ and ] are legal in paths; if not escaped, Spectre.MarkupLine throws.
        var paths = TestPaths() with {
            ClaudeSettingsPath = "/weird[path]/.claude/settings.json"
        };

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsTrue();
        // The path must appear in the sink, escaped so Spectre treats [ and ] as literals.
        // Markup.Escape("[path]") returns "[[path]]" — Spectre escapes brackets by doubling them.
        await Assert.That(sink.Lines).Contains(l => l.Contains("[[path]]"));
    }

    [Test]
    public async Task Cursor_detected_and_accepted_installs_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsTrue();
        await Assert.That(calls.CursorHooksArg).IsEqualTo("/fake/.cursor/hooks.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor hooks installed"));
    }

    [Test]
    public async Task Cursor_installs_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — Cursor hooks install regardless of what a (no longer
        // consulted) prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsTrue();
        await Assert.That(calls.CursorHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor hooks installed"));
    }

    [Test]
    public async Task Cursor_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        // Claude: true keeps at least one agent detected so this exercises Cursor's own
        // "not detected" skip line rather than the distinct all-agents-absent early return.
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CursorHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor not detected"));
    }

    [Test]
    public async Task Cursor_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CursorHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor hooks skipped by flag"));
    }

    [Test]
    public async Task Cursor_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CursorHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsFalse();
        await Assert.That(calls.CursorHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Cursor hooks"));
    }

    [Test]
    public async Task Cursor_kcap_not_on_path_aborts_install_without_writing_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.CursorHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task Copilot_detected_and_accepted_installs_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsTrue();
        await Assert.That(calls.CopilotHooksArg).IsEqualTo("/fake/.copilot/hooks/kcap.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot hooks installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running copilot session"));
    }

    [Test]
    public async Task Copilot_installs_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — Copilot hooks install regardless of what a (no longer
        // consulted) prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsTrue();
        await Assert.That(calls.CopilotHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot hooks installed"));
    }

    [Test]
    public async Task Copilot_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        // Claude: true keeps at least one agent detected so this exercises Copilot's own
        // "not detected" skip line rather than the distinct all-agents-absent early return.
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CopilotHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot CLI not detected"));
    }

    [Test]
    public async Task Copilot_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CopilotHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot CLI hooks skipped by flag"));
    }

    [Test]
    public async Task Copilot_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CopilotHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsFalse();
        await Assert.That(calls.CopilotHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Copilot hooks"));
    }

    [Test]
    public async Task Copilot_kcap_not_on_path_aborts_install_without_writing_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.CopilotHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task Kiro_detected_and_accepted_installs_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsTrue();
        await Assert.That(calls.KiroHooksArg).IsEqualTo("/fake/.kiro/agents/kcap.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Kiro hooks installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running kiro session"));
    }

    [Test]
    public async Task Kiro_installs_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — Kiro hooks install regardless of what a (no longer
        // consulted) prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsTrue();
        await Assert.That(calls.KiroHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Kiro hooks installed"));
    }

    [Test]
    public async Task Kiro_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        // Claude: true keeps at least one agent detected so this exercises Kiro's own
        // "not detected" skip line rather than the distinct all-agents-absent early return.
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false, Kiro: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.KiroHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Kiro CLI not detected"));
    }

    [Test]
    public async Task Kiro_kcap_not_on_path_aborts_install_without_writing_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.KiroHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    // ── Kiro MCP ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Kiro_mcp_registered_when_selected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterKiroMcpCalled).IsTrue();
    }

    [Test]
    public async Task Kiro_mcp_registered_even_when_agent_clone_fails() {
        // Independence: the agent clone needs kiro-cli and can fail, but the MCP file is a separate
        // settings/mcp.json merge — it must still register when Kiro is opted-in with kcap on PATH.
        var sink     = new Sink();
        var calls    = new InstallerCalls { KiroHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();       // clone failed
        await Assert.That(calls.RegisterKiroMcpCalled).IsTrue();      // MCP still registered
        await Assert.That(result.KiroMcpRegistered).IsTrue();
    }

    [Test]
    public async Task Kiro_mcp_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false, SkipKiroMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsTrue();
        await Assert.That(result.KiroMcpRegistered).IsFalse();
        await Assert.That(calls.RegisterKiroMcpCalled).IsFalse();
    }

    // ── Kiro skills (steer Kiro toward the kcap MCP tools) ─────────────────────

    [Test]
    public async Task Kiro_skills_go_to_kiro_dir_not_agents_skills() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        var dsts = calls.AgentSkillsInstalls.Select(x => x.Dst).ToArray();
        await Assert.That(dsts).Contains("/fake/.kiro/skills");          // Kiro skills → ~/.kiro/skills
        await Assert.That(dsts).DoesNotContain("/fake/.agents/skills");  // NOT the agent-agnostic dir Kiro can't read
        await Assert.That(result.KiroSkillsInstalled).IsTrue();
    }

    [Test]
    public async Task Kiro_skills_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false, SkipKiroSkills: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).DoesNotContain("/fake/.kiro/skills");
    }

    [Test]
    public async Task Kiro_skills_installed_even_when_agent_clone_fails() {
        // The agent clone can fail (needs kiro-cli), but ~/.kiro/skills is independent — it must
        // still install when Kiro is opted-in with kcap on PATH.
        var sink     = new Sink();
        var calls    = new InstallerCalls { KiroHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();          // clone failed
        await Assert.That(result.KiroSkillsInstalled).IsTrue();          // skills still installed
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).Contains("/fake/.kiro/skills");
    }

    [Test]
    public async Task Kiro_skills_not_installed_when_not_selected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).DoesNotContain("/fake/.kiro/skills");
    }

    [Test]
    public async Task Kiro_only_machine_does_not_install_agents_skills() {
        // A Kiro-only machine must NOT get ~/.agents/skills (Kiro can't read it) — only ~/.kiro/skills.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        var dsts = calls.AgentSkillsInstalls.Select(x => x.Dst).ToArray();
        await Assert.That(dsts).Contains("/fake/.kiro/skills");
        await Assert.That(dsts).DoesNotContain("/fake/.agents/skills");
        await Assert.That(result.AgentSkillsInstalled).IsFalse();   // ~/.agents/skills not touched for a Kiro-only box
    }

    [Test]
    public async Task Kiro_mcp_not_registered_when_kcap_not_on_path() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterKiroMcpCalled).IsFalse();     // not selected → no MCP
        await Assert.That(result.KiroMcpRegistered).IsFalse();
    }

    [Test]
    public async Task Kiro_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterKiroMcpReturns = JsonMcpConfigWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterKiroMcpCalled).IsTrue();
        await Assert.That(result.KiroMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register Kiro MCP"));
    }

    [Test]
    public async Task Kiro_mcp_registered_when_hooks_skipped_by_flag() {
        // --skip-kiro-hooks opts out of only the invasive agent clone; the independent MCP
        // registration must STILL happen (gated only by --skip-kiro-mcp).
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();     // hooks skipped by flag
        await Assert.That(calls.KiroHooksCalled).IsFalse();
        await Assert.That(calls.RegisterKiroMcpCalled).IsTrue();     // MCP still registered
        await Assert.That(result.KiroMcpRegistered).IsTrue();
    }

    [Test]
    public async Task Kiro_both_skip_flags_short_circuit_before_path_check() {
        // With BOTH --skip-kiro-hooks and --skip-kiro-mcp nothing is written, so the PATH precheck
        // must NOT gate the message: even when kcap isn't on PATH the user sees the accurate
        // "skipped by flags (hooks + MCP)" line, not a misleading "not on PATH" warning.
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: true, SkipKiroMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CapacitorOnPathCalled).IsFalse();    // short-circuited before the PATH check
        await Assert.That(result.KiroHooksInstalled).IsFalse();
        await Assert.That(result.KiroMcpRegistered).IsFalse();
        await Assert.That(calls.RegisterKiroMcpCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("skipped by flags (hooks + MCP)"));
        await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("not on PATH"));
    }

    [Test]
    public async Task Kiro_mcp_registered_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — Kiro MCP registers (alongside hooks) regardless of
        // what a (no longer consulted) prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsTrue();
        await Assert.That(calls.RegisterKiroMcpCalled).IsTrue();
        await Assert.That(result.KiroMcpRegistered).IsTrue();
    }

    [Test]
    public async Task Kiro_both_skip_flags_skip_hooks_and_mcp_with_accurate_message() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: true, SkipKiroMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.KiroHooksCalled).IsFalse();
        await Assert.That(calls.RegisterKiroMcpCalled).IsFalse();    // both flags → nothing registered
        await Assert.That(result.KiroMcpRegistered).IsFalse();
        // The skip message must NOT claim MCP was still registered when it was also skipped.
        await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("MCP still registered"));
    }

    [Test]
    public async Task Pi_detected_and_accepted_installs_extension() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsTrue();
        await Assert.That(calls.PiExtensionArg).IsEqualTo("/fake/.pi/agent/extensions/kcap.ts");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi extension installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running pi session"));
    }

    [Test]
    public async Task Pi_installs_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — the Pi extension installs regardless of what a (no
        // longer consulted) prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsTrue();
        await Assert.That(calls.PiExtensionCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi extension installed"));
    }

    [Test]
    public async Task Pi_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        // Claude: true keeps at least one agent detected so this exercises Pi's own
        // "not detected" skip line rather than the distinct all-agents-absent early return.
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false, Pi: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.PiExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi not detected"));
    }

    [Test]
    public async Task Pi_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.PiExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi extension skipped by flag"));
    }

    [Test]
    public async Task Pi_no_prompt_installs_without_prompting() {
        // --no-prompt: a detected, non-skipped Pi installs without asking.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var promptCalled = false;

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => { promptCalled = true; return false; }, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsTrue();
        await Assert.That(calls.PiExtensionCalled).IsTrue();
        await Assert.That(promptCalled).IsFalse();
    }

    [Test]
    public async Task Pi_kcap_not_on_path_aborts_install_without_writing_extension() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.PiExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task Pi_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { PiExtensionReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsFalse();
        await Assert.That(calls.PiExtensionCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write the Pi extension"));
    }

    // ── OpenCode (SST): a plugin file, like Pi ──────────────────────

    [Test]
    public async Task OpenCode_detected_and_accepted_installs_plugin() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsTrue();
        await Assert.That(calls.OpenCodeExtensionArg).IsEqualTo("/fake/.config/opencode/plugins/kcap.ts");
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode plugin installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running opencode session"));
    }

    [Test]
    public async Task OpenCode_installs_even_if_prompt_would_decline() {
        // The per-vendor prompt is gone — the OpenCode plugin installs regardless of what a (no
        // longer consulted) prompt delegate would return.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsTrue();
        await Assert.That(calls.OpenCodeExtensionCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode plugin installed"));
    }

    [Test]
    public async Task OpenCode_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        // Claude: true keeps at least one agent detected so this exercises OpenCode's own
        // "not detected" skip line rather than the distinct all-agents-absent early return.
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false, OpenCode: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.OpenCodeExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode not detected"));
    }

    [Test]
    public async Task OpenCode_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.OpenCodeExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode plugin skipped by flag"));
    }

    [Test]
    public async Task OpenCode_kcap_not_on_path_aborts_install_without_writing_plugin() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.OpenCodeExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task OpenCode_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { OpenCodeExtensionReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsFalse();
        await Assert.That(calls.OpenCodeExtensionCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write the OpenCode plugin"));
    }

    // ── OpenCode MCP + instructions ──────────────────────────────────────────

    [Test]
    public async Task OpenCode_mcp_and_instructions_registered_when_plugin_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterOpenCodeMcpCalled).IsTrue();
        await Assert.That(result.OpenCodeInstructionsInstalled).IsTrue();
        await Assert.That(calls.InstallOpenCodeInstructionsCalled).IsTrue();
    }

    [Test]
    public async Task OpenCode_mcp_and_instructions_not_registered_when_plugin_fails() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { OpenCodeExtensionReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsFalse();
        await Assert.That(calls.RegisterOpenCodeMcpCalled).IsFalse();
        await Assert.That(calls.InstallOpenCodeInstructionsCalled).IsFalse();
    }

    [Test]
    public async Task OpenCode_mcp_and_instructions_skipped_by_flags() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipOpenCode: false, SkipOpenCodeMcp: true, SkipOpenCodeInstructions: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsTrue();  // plugin still installs
        await Assert.That(calls.RegisterOpenCodeMcpCalled).IsFalse();
        await Assert.That(calls.InstallOpenCodeInstructionsCalled).IsFalse();
    }

    [Test]
    public async Task OpenCode_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterOpenCodeMcpReturns = JsonMcpConfigWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterOpenCodeMcpCalled).IsTrue();
        await Assert.That(result.OpenCodeMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register OpenCode MCP"));
    }

    // ── InstallAgents gate (single unified consent decision) ───────────────────

    [Test]
    public async Task RunAsync_InstallAgentsFalse_InstallsNothing() {
        // A "no" to the single detected-agents prompt (InstallAgents = false) must mutate
        // NOTHING on disk, even with every agent detected and no --skip-* flags set — every
        // installer delegate below throws if invoked, proving none of them are reached.
        var sink     = new Sink();
        var detected = new DetectedAgents(Claude: true, Codex: true, Cursor: true, Copilot: true, Gemini: true, Kiro: true, Pi: true, OpenCode: true, Antigravity: true);
        var options  = new Options(SkipClaude: false, SkipCodex: false, SkipCursor: false, SkipCopilot: false, NoPrompt: false, InstallAgents: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            ThrowingInstallers(),
            prompt: _ => throw new InvalidOperationException("must not prompt"),
            writeLine: sink.Write
        );

        await Assert.That(result).IsEqualTo(new Result(false, false, false, false, false));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Skipping coding-agent setup"));
    }

    [Test]
    public async Task RunAsync_NoAgentsDetected_EmitsWarningAndNeverPrompts() {
        var sink         = new Sink();
        var calls        = new InstallerCalls();
        var detected     = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);
        var options      = new Options(SkipClaude: false, SkipCodex: false, SkipCursor: false, SkipCopilot: false, NoPrompt: false);
        var promptCalled = false;

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => { promptCalled = true; return true; },
            writeLine: sink.Write);

        await Assert.That(result).IsEqualTo(new Result(false, false, false, false, false));
        await Assert.That(promptCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("No supported agent CLI detected"));
    }

    // ── Kiro flag-coupling truth table (InstallAgents=true, Kiro detected, kcap on PATH) ──
    // See spec "Per-vendor truth table" / "Kiro flag coupling" — the SkipKiro+SkipKiroMcp
    // short-circuit must keep suppressing Kiro skills too, via `selected=false`.

    [Test]
    public async Task Kiro_truth_table_skip_hooks_only_keeps_mcp_and_skills() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();
        await Assert.That(result.KiroMcpRegistered).IsTrue();
        await Assert.That(result.KiroSkillsInstalled).IsTrue();
    }

    [Test]
    public async Task Kiro_truth_table_skip_hooks_and_mcp_installs_nothing() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: true, SkipKiroMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();
        await Assert.That(result.KiroMcpRegistered).IsFalse();
        await Assert.That(result.KiroSkillsInstalled).IsFalse();
    }

    [Test]
    public async Task Kiro_truth_table_skip_hooks_and_skills_keeps_mcp_only() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipKiro: true, SkipKiroSkills: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroMcpRegistered).IsTrue();
        await Assert.That(result.KiroSkillsInstalled).IsFalse();
    }

    // ── Shared ~/.agents/skills eligibility ─────────────────────────────────────
    // Kiro and Antigravity read their OWN skills dirs, not ~/.agents/skills — so their
    // presence alone (with nothing else detected) must never trigger this shared install.

    [Test]
    public async Task Shared_skills_not_installed_when_only_kiro_detected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).DoesNotContain("/fake/.agents/skills");
    }

    [Test]
    public async Task Shared_skills_not_installed_when_only_antigravity_detected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Antigravity: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).DoesNotContain("/fake/.agents/skills");
    }

    [Test]
    public async Task Shared_skills_installed_when_only_codex_detected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(), prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsInstalls.Select(x => x.Dst)).Contains("/fake/.agents/skills");
    }

    static Paths TestPaths() => new(
        ClaudeSettingsPath:   "/fake/.claude/settings.json",
        ClaudeScopeLabel:     "user",
        PluginDir:            "/fake/plugin",
        CodexHooksPath:       "/fake/.codex/hooks.json",
        CursorHooksPath:      "/fake/.cursor/hooks.json",
        CopilotHooksPath:     "/fake/.copilot/hooks/kcap.json",
        GeminiSettingsPath:   "/fake/.gemini/settings.json",
        AgentsSkillsDir:      "/fake/.agents/skills",
        LegacyCodexSkillsDir: "/fake/.codex/skills",
        KiroHooksPath:        "/fake/.kiro/agents/kcap.json",
        PiExtensionPath:      "/fake/.pi/agent/extensions/kcap.ts",
        OpenCodeExtensionPath: "/fake/.config/opencode/plugins/kcap.ts",
        CodexConfigTomlPath:  "/fake/.codex/config.toml",
        CursorMcpPath:        "/fake/.cursor/mcp.json",
        CopilotMcpPath:       "/fake/.copilot/mcp-config.json",
        CopilotInstructionsPath: "/fake/.copilot/copilot-instructions.md",
        GeminiInstructionsPath: "/fake/.gemini/GEMINI.md",
        AntigravityMcpPath:       "/fake/.gemini/config/mcp_config.json",
        AntigravityInstructionsPath: "/fake/.gemini/GEMINI.md",
        AntigravitySkillsDir:     "/fake/.gemini/skills",
        OpenCodeMcpPath:      "/fake/.config/opencode/opencode.json",
        OpenCodeInstructionsPath: "/fake/.config/opencode/AGENTS.md",
        KiroMcpPath:          "/fake/.kiro/settings/mcp.json",
        KiroSkillsDir:        "/fake/.kiro/skills",
        PiMcpExtensionPath:   "/fake/.pi/agent/extensions/kcap-mcp.ts",
        PiAgentsMdPath:       "/fake/.pi/agent/AGENTS.md"
    );

    // Every delegate throws — used to prove InstallAgents=false short-circuits RunAsync
    // before any installer is ever invoked.
    static Installers ThrowingInstallers() => new(
        InstallClaudePlugin:    (_, _) => throw new InvalidOperationException("must not install Claude plugin"),
        InstallCodexHooks:      _ => throw new InvalidOperationException("must not install Codex hooks"),
        InstallCursorHooks:     _ => throw new InvalidOperationException("must not install Cursor hooks"),
        InstallCopilotHooks:    _ => throw new InvalidOperationException("must not install Copilot hooks"),
        InstallGeminiHooks:     _ => throw new InvalidOperationException("must not install Gemini hooks"),
        CapacitorOnPath:        () => throw new InvalidOperationException("must not probe kcap on PATH"),
        InstallAgentSkills:     (_, _) => throw new InvalidOperationException("must not install agent skills"),
        CleanLegacyCodexSkills: _ => throw new InvalidOperationException("must not sweep legacy Codex skills"),
        InstallKiroHooks:       _ => throw new InvalidOperationException("must not install Kiro hooks"),
        InstallPiExtension:     _ => throw new InvalidOperationException("must not install Pi extension"),
        InstallOpenCodeExtension: _ => throw new InvalidOperationException("must not install OpenCode extension"),
        EnableCodexNetworkAccess: () => throw new InvalidOperationException("must not enable Codex network access"),
        RegisterCodexMcp:         () => throw new InvalidOperationException("must not register Codex MCP"),
        InstallAntigravityHooks:  _ => throw new InvalidOperationException("must not install Antigravity hooks"),
        RegisterCursorMcp:        () => throw new InvalidOperationException("must not register Cursor MCP"),
        RegisterCopilotMcp:       () => throw new InvalidOperationException("must not register Copilot MCP"),
        InstallCopilotInstructions: () => throw new InvalidOperationException("must not install Copilot instructions"),
        AgentSkillsCurrent:       _ => throw new InvalidOperationException("must not check agent skills marker"),
        RegisterGeminiMcp:        () => throw new InvalidOperationException("must not register Gemini MCP"),
        InstallGeminiInstructions: () => throw new InvalidOperationException("must not install Gemini instructions"),
        RegisterAntigravityMcp:   () => throw new InvalidOperationException("must not register Antigravity MCP"),
        InstallAntigravityInstructions: () => throw new InvalidOperationException("must not install Antigravity instructions"),
        RegisterOpenCodeMcp:      () => throw new InvalidOperationException("must not register OpenCode MCP"),
        InstallOpenCodeInstructions: () => throw new InvalidOperationException("must not install OpenCode instructions"),
        RegisterKiroMcp:          () => throw new InvalidOperationException("must not register Kiro MCP"),
        InstallPiMcp:             _ => throw new InvalidOperationException("must not install Pi MCP"),
        InstallPiInstructions:    () => throw new InvalidOperationException("must not install Pi instructions")
    );

    sealed class Sink {
        public List<string> Lines { get; } = [];
        public void Write(string s) => Lines.Add(s);
    }

    sealed class InstallerCalls {
        public bool                                 ClaudeCalled  { get; private set; }
        public (string Settings, string PluginDir)? ClaudeArgs    { get; private set; }
        public bool                                 ClaudeReturns { get; set; } = true;

        public bool    CodexHooksCalled  { get; private set; }
        public string? CodexHooksArg     { get; private set; }
        public bool    CodexHooksReturns { get; set; } = true;

        public bool    CursorHooksCalled  { get; private set; }
        public string? CursorHooksArg     { get; private set; }
        public bool    CursorHooksReturns { get; set; } = true;

        public bool    CopilotHooksCalled  { get; private set; }
        public string? CopilotHooksArg     { get; private set; }
        public bool    CopilotHooksReturns { get; set; } = true;

        public bool    AntigravityHooksCalled  { get; private set; }
        public string? AntigravityHooksArg     { get; private set; }
        public bool    AntigravityHooksReturns { get; set; } = true;

        public bool    GeminiHooksCalled  { get; private set; }
        public string? GeminiHooksArg     { get; private set; }
        public bool    GeminiHooksReturns { get; set; } = true;

        public bool    KiroHooksCalled  { get; private set; }
        public string? KiroHooksArg     { get; private set; }
        public bool    KiroHooksReturns { get; set; } = true;

        public bool    PiExtensionCalled  { get; private set; }
        public string? PiExtensionArg     { get; private set; }
        public bool    PiExtensionReturns { get; set; } = true;

        public bool    OpenCodeExtensionCalled  { get; private set; }
        public string? OpenCodeExtensionArg     { get; private set; }
        public bool    OpenCodeExtensionReturns { get; set; } = true;

        public bool CapacitorOnPathCalled  { get; private set; }
        public bool CapacitorOnPathReturns { get; set; } = true;

        public bool                      AgentSkillsCalled  { get; private set; }
        public (string Src, string Dst)? AgentSkillsArgs    { get; private set; }
        public bool                      AgentSkillsReturns { get; set; } = true;

        public bool    AgentSkillsCurrentCalled  { get; private set; }
        public string? AgentSkillsCurrentArg     { get; private set; }
        public bool    AgentSkillsCurrentReturns { get; set; } // default false → not current → install

        public bool    LegacyCleanupCalled  { get; private set; }
        public string? LegacyCleanupArg     { get; private set; }
        public bool    LegacyCleanupReturns { get; set; } = true;

        public bool                   EnableCodexNetworkCalled  { get; private set; }
        public CodexConfigToml.Change EnableCodexNetworkReturns { get; set; } = CodexConfigToml.Change.Updated;

        public bool                   RegisterCodexMcpCalled  { get; private set; }
        public CodexConfigToml.Change RegisterCodexMcpReturns { get; set; } = CodexConfigToml.Change.Updated;

        public bool                     RegisterCursorMcpCalled  { get; private set; }
        public JsonMcpConfigWriter.Change RegisterCursorMcpReturns { get; set; } = JsonMcpConfigWriter.Change.Updated;

        public bool                     RegisterCopilotMcpCalled  { get; private set; }
        public JsonMcpConfigWriter.Change RegisterCopilotMcpReturns { get; set; } = JsonMcpConfigWriter.Change.Updated;

        public bool                          InstallCopilotInstructionsCalled  { get; private set; }
        public AgentInstructionsWriter.Change InstallCopilotInstructionsReturns { get; set; } = AgentInstructionsWriter.Change.Updated;

        public bool                     RegisterGeminiMcpCalled  { get; private set; }
        public JsonMcpConfigWriter.Change RegisterGeminiMcpReturns { get; set; } = JsonMcpConfigWriter.Change.Updated;

        public bool                          InstallGeminiInstructionsCalled  { get; private set; }
        public AgentInstructionsWriter.Change InstallGeminiInstructionsReturns { get; set; } = AgentInstructionsWriter.Change.Updated;

        public bool                     RegisterAntigravityMcpCalled  { get; private set; }
        public JsonMcpConfigWriter.Change RegisterAntigravityMcpReturns { get; set; } = JsonMcpConfigWriter.Change.Updated;

        public bool                          InstallAntigravityInstructionsCalled  { get; private set; }
        public AgentInstructionsWriter.Change InstallAntigravityInstructionsReturns { get; set; } = AgentInstructionsWriter.Change.Updated;

        // Every InstallAgentSkills(src,dst) call (the single AgentSkillsArgs only keeps the last) — so
        // tests can assert WHICH skills dirs got written (e.g. ~/.gemini/skills vs ~/.agents/skills).
        public List<(string Src, string Dst)> AgentSkillsInstalls { get; } = [];

        public bool                     RegisterOpenCodeMcpCalled  { get; private set; }
        public JsonMcpConfigWriter.Change RegisterOpenCodeMcpReturns { get; set; } = JsonMcpConfigWriter.Change.Updated;

        public bool                          InstallOpenCodeInstructionsCalled  { get; private set; }
        public AgentInstructionsWriter.Change InstallOpenCodeInstructionsReturns { get; set; } = AgentInstructionsWriter.Change.Updated;

        public bool                     RegisterKiroMcpCalled  { get; private set; }
        public JsonMcpConfigWriter.Change RegisterKiroMcpReturns { get; set; } = JsonMcpConfigWriter.Change.Updated;

        public bool    InstallPiMcpCalled  { get; private set; }
        public string? InstallPiMcpArg     { get; private set; }
        public bool    InstallPiMcpReturns { get; set; } = true;

        public bool                          InstallPiInstructionsCalled  { get; private set; }
        public AgentInstructionsWriter.Change InstallPiInstructionsReturns { get; set; } = AgentInstructionsWriter.Change.Updated;

        public Installers AsInstallers() => new(
            InstallClaudePlugin: (s, p) => {
                ClaudeCalled = true;
                ClaudeArgs   = (s, p);

                return ClaudeReturns;
            },
            InstallCodexHooks: h => {
                CodexHooksCalled = true;
                CodexHooksArg    = h;

                return CodexHooksReturns;
            },
            InstallCursorHooks: h => {
                CursorHooksCalled = true;
                CursorHooksArg    = h;

                return CursorHooksReturns;
            },
            InstallCopilotHooks: h => {
                CopilotHooksCalled = true;
                CopilotHooksArg    = h;

                return CopilotHooksReturns;
            },
            InstallGeminiHooks: h => {
                GeminiHooksCalled = true;
                GeminiHooksArg    = h;

                return GeminiHooksReturns;
            },
            CapacitorOnPath: () => {
                CapacitorOnPathCalled = true;
                return CapacitorOnPathReturns;
            },
            InstallKiroHooks: h => {
                KiroHooksCalled = true;
                KiroHooksArg    = h;

                return KiroHooksReturns;
            },
            InstallPiExtension: p => {
                PiExtensionCalled = true;
                PiExtensionArg    = p;

                return PiExtensionReturns;
            },
            InstallOpenCodeExtension: p => {
                OpenCodeExtensionCalled = true;
                OpenCodeExtensionArg    = p;

                return OpenCodeExtensionReturns;
            },
            InstallAntigravityHooks: h => {
                AntigravityHooksCalled = true;
                AntigravityHooksArg    = h;

                return AntigravityHooksReturns;
            },
            InstallAgentSkills: (s, d) => {
                AgentSkillsCalled = true;
                AgentSkillsArgs   = (s, d);
                AgentSkillsInstalls.Add((s, d));

                return AgentSkillsReturns;
            },
            CleanLegacyCodexSkills: d => {
                LegacyCleanupCalled = true;
                LegacyCleanupArg    = d;

                return LegacyCleanupReturns;
            },
            EnableCodexNetworkAccess: () => {
                EnableCodexNetworkCalled = true;

                return EnableCodexNetworkReturns;
            },
            RegisterCodexMcp: () => {
                RegisterCodexMcpCalled = true;

                return RegisterCodexMcpReturns;
            },
            RegisterCursorMcp: () => {
                RegisterCursorMcpCalled = true;

                return RegisterCursorMcpReturns;
            },
            RegisterCopilotMcp: () => {
                RegisterCopilotMcpCalled = true;

                return RegisterCopilotMcpReturns;
            },
            InstallCopilotInstructions: () => {
                InstallCopilotInstructionsCalled = true;

                return InstallCopilotInstructionsReturns;
            },
            RegisterOpenCodeMcp: () => {
                RegisterOpenCodeMcpCalled = true;

                return RegisterOpenCodeMcpReturns;
            },
            InstallOpenCodeInstructions: () => {
                InstallOpenCodeInstructionsCalled = true;

                return InstallOpenCodeInstructionsReturns;
            },
            RegisterKiroMcp: () => {
                RegisterKiroMcpCalled = true;

                return RegisterKiroMcpReturns;
            },
            AgentSkillsCurrent: dir => {
                AgentSkillsCurrentCalled = true;
                AgentSkillsCurrentArg    = dir;

                return AgentSkillsCurrentReturns;
            },
            RegisterGeminiMcp: () => {
                RegisterGeminiMcpCalled = true;

                return RegisterGeminiMcpReturns;
            },
            InstallGeminiInstructions: () => {
                InstallGeminiInstructionsCalled = true;

                return InstallGeminiInstructionsReturns;
            },
            RegisterAntigravityMcp: () => {
                RegisterAntigravityMcpCalled = true;

                return RegisterAntigravityMcpReturns;
            },
            InstallAntigravityInstructions: () => {
                InstallAntigravityInstructionsCalled = true;

                return InstallAntigravityInstructionsReturns;
            },
            InstallPiMcp: p => {
                InstallPiMcpCalled = true;
                InstallPiMcpArg    = p;

                return InstallPiMcpReturns;
            },
            InstallPiInstructions: () => {
                InstallPiInstructionsCalled = true;

                return InstallPiInstructionsReturns;
            }
        );
    }
}
