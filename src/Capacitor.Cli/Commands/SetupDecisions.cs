using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure decision helpers for <c>kcap setup</c>'s Step 4 (harnesses). Kept separate from
/// <see cref="CodingAgentsStep"/> so <c>SetupCommand</c>'s consent logic — building the
/// detected-agent summary and deciding whether to install at all — is unit-testable without
/// touching any installer delegate, filesystem, or console.
/// </summary>
internal static class SetupDecisions {
    /// <summary>
    /// Human-readable, comma-joined list of detected harnesses (in a stable, user-facing
    /// order), or null when none are detected. Kiro is annotated because installing for it
    /// makes a material change (clones the user's default agent and sets kcap as default).
    /// </summary>
    public static string? DetectedAgentsSummary(CodingAgentsStep.DetectedAgents d) {
        var names = new List<string>();

        if (d.Claude)      names.Add("Claude Code");
        if (d.Codex)       names.Add("Codex");
        if (d.Cursor)      names.Add("Cursor");
        if (d.Copilot)     names.Add("Copilot");
        if (d.Gemini)      names.Add("Gemini");
        if (d.Kiro)        names.Add("Kiro (installing sets kcap as your default Kiro agent)");
        if (d.Pi)          names.Add("Pi");
        if (d.OpenCode)    names.Add("OpenCode");
        if (d.Antigravity) names.Add("Antigravity");

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>
    /// The single unified install-consent decision that replaces the nine per-vendor prompts.
    /// No agent detected → false, no prompt (CodingAgentsStep.RunAsync's own no-agents
    /// early-return owns the warning). Otherwise <paramref name="noPrompt"/> short-circuits to
    /// true — preserving today's unattended `kcap setup --no-prompt` behaviour — else the
    /// caller's yes/no prompt decides.
    /// </summary>
    public static bool DecideInstallAgents(CodingAgentsStep.DetectedAgents d, bool noPrompt, Func<string, bool> promptYesNo) {
        if (DetectedAgentsSummary(d) is null) return false;

        return noPrompt || promptYesNo("Install kcap for these agents (hooks, skills, instructions, MCP)?");
    }

    /// <summary>
    /// Folds the browser's Agents answer into options already built from this invocation's flags.
    ///
    /// <para><b>The flag still wins.</b> It is an instruction for this run — a script's opt-out, or a
    /// user who typed it — and a browser answer minutes old does not override one. Everything the
    /// answer did not turn on is skipped, since a harness left off is absent from it rather than
    /// present-and-false. A null answer returns the options untouched, which is what an ordinary
    /// terminal setup has always done.</para>
    ///
    /// <para>One function rather than sixteen call sites so the mapping is pinnable: a harness added
    /// to the registry and forgotten here would otherwise round-trip through the screen, be reported
    /// back as chosen, and install nothing.</para>
    /// </summary>
    public static CodingAgentsStep.Options WithBrowserAnswer(
            CodingAgentsStep.Options options, FirstRunAgentsAnswer? answer) {
        if (answer is null) return options;

        bool Skip(HarnessId harness, bool flag) => flag || !answer.Records(harness);

        bool Tools(HarnessId harness, bool blockedByFlag) => blockedByFlag || !answer.Tools(harness);

        return options with {
            SkipClaude      = Skip(HarnessId.Claude, options.SkipClaude),
            SkipCodex       = Skip(HarnessId.Codex, options.SkipCodex),
            SkipCursor      = Skip(HarnessId.Cursor, options.SkipCursor),
            SkipCopilot     = Skip(HarnessId.Copilot, options.SkipCopilot),
            SkipGemini      = Skip(HarnessId.Gemini, options.SkipGemini),
            SkipKiro        = Skip(HarnessId.Kiro, options.SkipKiro),
            SkipPi          = Skip(HarnessId.Pi, options.SkipPi),
            SkipOpenCode    = Skip(HarnessId.OpenCode, options.SkipOpenCode),
            SkipAntigravity = Skip(HarnessId.Antigravity, options.SkipAntigravity),

            // The hooks flag counts against TOOLS as well. `--skip-<vendor>-hooks` is a whole-vendor
            // opt-out, and the browser's separate tools axis must not re-enable the half of it the
            // caller never mentioned: a script that excluded a vendor gets no writes for it.
            SkipCursorMcp      = Tools(HarnessId.Cursor, options.SkipCursor || options.SkipCursorMcp),
            SkipCopilotMcp     = Tools(HarnessId.Copilot, options.SkipCopilot || options.SkipCopilotMcp),
            SkipGeminiMcp      = Tools(HarnessId.Gemini, options.SkipGemini || options.SkipGeminiMcp),
            SkipPiMcp          = Tools(HarnessId.Pi, options.SkipPi || options.SkipPiMcp),
            SkipOpenCodeMcp    = Tools(HarnessId.OpenCode, options.SkipOpenCode || options.SkipOpenCodeMcp),
            SkipAntigravityMcp = Tools(HarnessId.Antigravity, options.SkipAntigravity || options.SkipAntigravityMcp),

            // Kiro is the exception, and stays one. `--skip-kiro-hooks` opts out of only the invasive
            // agent clone; the terminal path registers Kiro's MCP under it and says so. Carrying it
            // across here would make the same flag mean two things depending on whether a browser
            // answered, and drop tools that browser selected.
            SkipKiroMcp        = Tools(HarnessId.Kiro, options.SkipKiroMcp),

            // The browser answered every prompt this step would raise, so it must not raise one.
            NoPrompt = true,

            // The screen asked "record" and "tools" as two questions, so declining the first here
            // does NOT decline the second — unlike --skip-<vendor>-hooks, which has always meant
            // "leave this vendor alone" and must keep meaning it for the scripts that pass it.
            ToolsIndependentOfCapture = true
        };
    }

    /// <summary>Whether Step 6 (import past sessions) ran, or was skipped and why.</summary>
    public enum ImportOutcome { Skip, Run }

    /// <summary><paramref name="SkipReason"/> is null when the user simply declined the prompt.</summary>
    public record ImportDecision(ImportOutcome Outcome, string? SkipReason);

    /// <summary>
    /// The eligibility + policy decision for Step 6 (import past sessions from the current
    /// repository). Guard order: no resolvable current repo (no origin remote) → skip; auth
    /// requirements unsatisfied → skip; <c>--skip-import</c> → skip; <c>--no-prompt</c> → run
    /// without prompting (mirrors the Step-4 unified-install auto-yes under unattended setup);
    /// otherwise the caller's interactive yes/no prompt decides.
    /// </summary>
    public static ImportDecision DecideImport(
            bool hasCurrentRepo, bool authSatisfied, bool skipImport, bool noPrompt, Func<bool> promptYesNo) {
        if (!hasCurrentRepo) return new ImportDecision(ImportOutcome.Skip, "no origin remote — skipping import");
        if (!authSatisfied)  return new ImportDecision(ImportOutcome.Skip, "not authenticated — skipping import");
        if (skipImport)       return new ImportDecision(ImportOutcome.Skip, "--skip-import");
        if (noPrompt)         return new ImportDecision(ImportOutcome.Run, null);

        return promptYesNo()
            ? new ImportDecision(ImportOutcome.Run, null)
            : new ImportDecision(ImportOutcome.Skip, null);
    }
}
