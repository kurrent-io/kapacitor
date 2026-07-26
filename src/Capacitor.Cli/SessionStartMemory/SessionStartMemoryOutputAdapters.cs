using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryOutputAdapters {
    public static string Render(SessionStartHarness harness, string? fragment) {
        if (harness == SessionStartHarness.Claude && fragment is null) return "";
        if (harness is SessionStartHarness.Kiro or SessionStartHarness.Pi or SessionStartHarness.OpenCode)
            return fragment is null ? "" : fragment + "\n";

        object envelope = harness switch {
            SessionStartHarness.Claude => fragment is null
                ? new ClaudeMemoryEnvelope(null!)
                : new ClaudeMemoryEnvelope(new HookMemoryOutput("SessionStart", fragment)),
            SessionStartHarness.Codex => fragment is null
                ? new CodexMemoryEnvelope(true, null!)
                : new CodexMemoryEnvelope(true, new HookMemoryOutput("SessionStart", fragment)),
            SessionStartHarness.Cursor => new CursorMemoryEnvelope(fragment),
            SessionStartHarness.Copilot => new CopilotMemoryEnvelope(fragment),
            SessionStartHarness.Gemini => new GeminiMemoryEnvelope(fragment is null ? null : new HookMemoryOutput("SessionStart", fragment)),
            SessionStartHarness.Antigravity => new AntigravityMemoryEnvelope(fragment is null ? null : [new AntigravityMemoryStep(fragment)]),
            _ => throw new ArgumentOutOfRangeException(nameof(harness))
        };

        var json = envelope switch {
            ClaudeMemoryEnvelope value => fragment is null ? "{}" : JsonSerializer.Serialize(value, SessionStartMemoryJsonContext.Default.ClaudeMemoryEnvelope),
            CodexMemoryEnvelope value => fragment is null ? "{\"continue\":true}" : JsonSerializer.Serialize(value, SessionStartMemoryJsonContext.Default.CodexMemoryEnvelope),
            CursorMemoryEnvelope value => JsonSerializer.Serialize(value, SessionStartMemoryJsonContext.Default.CursorMemoryEnvelope),
            CopilotMemoryEnvelope value => JsonSerializer.Serialize(value, SessionStartMemoryJsonContext.Default.CopilotMemoryEnvelope),
            GeminiMemoryEnvelope value => JsonSerializer.Serialize(value, SessionStartMemoryJsonContext.Default.GeminiMemoryEnvelope),
            AntigravityMemoryEnvelope value => JsonSerializer.Serialize(value, SessionStartMemoryJsonContext.Default.AntigravityMemoryEnvelope),
            _ => throw new InvalidOperationException()
        };
        return json + "\n";
    }
}
