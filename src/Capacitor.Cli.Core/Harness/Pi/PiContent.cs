using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// Content predicates for Pi <c>message</c> envelopes, mirroring the server
/// <c>PiTranscriptNormalizer</c> emit condition. A user/assistant message only
/// produces a canonical event — and so only counts as import-relevant
/// (<c>PiImportSource.IsImportRelevantLine</c>) or toward the watcher
/// title-event threshold (<c>WatchCommand.IsEvent</c>) — when it actually
/// carries content. Shared so those two never drift from the normalizer (or
/// each other): a role check alone over-counts empty/unsupported envelopes that
/// the normalizer skips, which would re-classify a complete session as Partial
/// forever / trip the title threshold on lines that emit nothing.
/// </summary>
public static class PiContent {
    /// <summary>User message has content: a non-empty string, or a text block with non-empty text.</summary>
    public static bool HasUserContent(JsonElement msg) {
        if (msg.Str("content") is { Length: > 0 }) return true;

        if (msg.Arr("content") is { } content) {
            foreach (var block in content.EnumerateArray()) {
                if (block.Str("type") == "text" && block.Str("text") is { Length: > 0 }) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Assistant message has at least one renderable part — matching
    /// <c>NormalizeAssistant</c>: a non-empty <c>thinking</c> or <c>text</c>
    /// block, or a <c>toolCall</c> with both <c>id</c> and <c>name</c>. An array
    /// of only unsupported blocks (e.g. images) emits nothing, so it is NOT relevant.
    /// </summary>
    public static bool HasAssistantContent(JsonElement msg) {
        if (msg.Arr("content") is not { } content) return false;

        foreach (var block in content.EnumerateArray()) {
            if (block.Str("type") == "thinking" && block.Str("thinking") is { Length: > 0 }) return true;
            if (block.Str("type") == "text"     && block.Str("text") is { Length: > 0 }) return true;
            if (block.Str("type") == "toolCall"
             && block.Str("id") is { Length: > 0 } && block.Str("name") is { Length: > 0 }) return true;
        }

        return false;
    }
}
