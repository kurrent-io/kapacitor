using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core;

/// A vendor's say over how its stored events read in the chat: drop one, or rewrite the envelope.
public interface IChatDisplayRules {
    AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope);
}

/// The chat's view of a transcript: the leaf projection, the envelope mapping, one vendor's rules.
public sealed class TranscriptChatProjection(ITranscriptProjection projection, IChatDisplayRules rules) {
    public TranscriptContext CreateContext(string sessionId, string? agentId) => projection.CreateContext(sessionId, agentId);

    public IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var result = projection.Project(line, lineNumber, receivedAt, context);
        if (result.Events.Count == 0) return [];
        var shown = new List<AcpEventEnvelope>(result.Events.Count);
        foreach (var evt in result.Events)
            foreach (var envelope in TranscriptEnvelopes.From(evt))
                if (rules.Filter(evt, envelope) is { } kept) shown.Add(kept);
        return shown;
    }
}

/// The one registration site in Core: a vendor's chat rules live under Harness/&lt;Vendor&gt;/ and
/// are paired with the leaf's projection here, nowhere else.
public static class TranscriptChat {
    public static TranscriptChatProjection? For(string vendor) =>
        TranscriptProjection.For(vendor) is not { } projection ? null
        : vendor.ToLowerInvariant() switch {
            "claude" => new TranscriptChatProjection(projection, ClaudeChatRules.Instance),
            "codex"  => new TranscriptChatProjection(projection, CodexChatRules.Instance),
            _        => null,
        };
}
