using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// Detects installed coding-agent CLIs by composing a PATH binary probe with each vendor's
/// filesystem install-marker check. See each arm's comment for why that vendor needs the signals
/// it has.
/// </summary>
public static class AgentDetection {
    public static AgentDetectionResult Detect(HarnessPaths p, BinaryProbe binaries) {
        return new(
            // Claude/Codex: PATH probe only — no on-disk install marker is checked today.
            Claude: new(binaries.Finds("claude"), false),
            Codex:  new(binaries.Finds("codex"), false),
            // Cursor: config-dir presence only — no PATH probe exists for it.
            Cursor: new(false, p.Cursor.IsInstalled),
            // Dir presence covers users who launch Copilot through an IDE wrapper; the PATH
            // probe covers fresh installs that haven't run yet (no ~/.copilot until first launch).
            Copilot: new(binaries.Finds("copilot"), p.Copilot.IsInstalled),
            // Dir presence covers IDE-launched Gemini; the PATH probe covers a fresh install
            // that hasn't created ~/.gemini yet.
            Gemini: new(binaries.Finds("gemini"), p.Gemini.IsInstalled),
            // Same dual signal for Kiro: the ~/.kiro tree or the conversation DB covers
            // IDE-launched users; the PATH probe (kiro / kiro-cli) covers fresh CLI installs.
            Kiro: new(binaries.Finds("kiro") || binaries.Finds("kiro-cli"), p.Kiro.IsInstalled),
            // Pi keeps state under ~/.pi/agent (relocatable via PI_CODING_AGENT_DIR); the PATH
            // probe covers fresh installs that haven't created it yet.
            Pi: new(binaries.Finds("pi"), p.Pi.IsInstalled),
            // OpenCode keeps config under ~/.config/opencode + data under
            // ~/.local/share/opencode; the PATH probe covers fresh installs.
            OpenCode: new(binaries.Finds("opencode"), p.OpenCode.IsInstalled),
            // Antigravity is one vendor over two surfaces: the GUI (state under
            // ~/.gemini/antigravity) and the `agy` CLI (state under ~/.gemini/antigravity-cli).
            // IsInstalled covers either root; the PATH probes cover a fresh install that has
            // not created a root yet — and the CLI binary is `agy`, not `antigravity`, so both
            // names must be probed or an agy-only machine goes undetected.
            Antigravity: new(binaries.Finds("antigravity") || binaries.Finds("agy"), p.Antigravity.IsInstalled));
    }
}
