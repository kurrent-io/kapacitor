namespace Capacitor.Cli.Core.Setup;

/// <summary>Every supported vendor's detection outcome, in catalog order.</summary>
public sealed record AgentDetectionResult(
    DetectedAgent Claude, DetectedAgent Codex, DetectedAgent Cursor, DetectedAgent Copilot,
    DetectedAgent Gemini, DetectedAgent Kiro, DetectedAgent Pi, DetectedAgent OpenCode,
    DetectedAgent Antigravity);
