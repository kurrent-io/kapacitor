namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex writes its injected preludes as user messages; the chat shows none of them.
public sealed class CodexChatRules : IChatDisplayRules {
    public static readonly CodexChatRules Instance = new();

    static readonly string[] InjectedPreludes = [
        "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>", "<permissions instructions>",
    ];

    CodexChatRules() { }

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) =>
        envelope.Kind == AcpEventKind.UserMessage && IsInjectedPrelude(envelope.Text ?? "") ? null : envelope;

    static bool IsInjectedPrelude(string text) {
        var trimmed = text.TrimStart();
        foreach (var prelude in InjectedPreludes)
            if (trimmed.StartsWith(prelude, StringComparison.Ordinal)) return true;
        return false;
    }
}
