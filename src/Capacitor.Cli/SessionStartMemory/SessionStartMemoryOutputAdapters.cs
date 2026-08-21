using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryOutputAdapters {
    /// <param name="workItemsNudge">The static work-items nudge, composed here at the
    /// OUTPUT layer so its presence never touches the lease/disposition of the memory/guidelines
    /// fragment (which was already decided upstream). It is merged marker-first when it is the only
    /// content, so Pi/OpenCode's marker-gated capture still recognises it.</param>
    public static string Render(SessionStartHarness harness, string? fragment, string? workItemsNudge = null) {
        fragment = MergeNudge(fragment, workItemsNudge);
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

    /// <summary>Appends the work-items nudge to the (already marker-first) memory/guidelines
    /// <paramref name="fragment"/>. When the fragment is absent, the nudge stands alone and the shared
    /// <see cref="MemoryIndexEmitter.FragmentMarker"/> is prepended — the same rule
    /// <c>SessionStartCompositeContextProvider.Compose</c> uses for a guidelines-only fragment — so
    /// Pi/OpenCode still capture it. A whitespace/absent nudge leaves the fragment untouched.</summary>
    static string? MergeNudge(string? fragment, string? workItemsNudge) {
        if (string.IsNullOrWhiteSpace(workItemsNudge)) return fragment;
        if (fragment is not null) return fragment + "\n\n" + workItemsNudge;
        return MemoryIndexEmitter.FragmentMarker + "\n" + workItemsNudge;
    }
}
