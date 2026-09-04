using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Models.Transcripts;

/// One transcript line in, canonical events out. Stateful only through the context the caller
/// passes; a projection never mutates an event it has returned.
public interface ITranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}

/// The one registration site: a vendor's projection lives under Harness/&lt;Vendor&gt;/ and is named
/// here, nowhere else.
public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeTranscriptEvents.Instance,
        _ => null,
    };
}
