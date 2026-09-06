using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;

namespace Capacitor.Models.Transcripts;

/// One canonical event a projection derived from a transcript line. The payload is complete as
/// returned: a caller adds metadata around it, never fields inside it.
public sealed record CanonicalEvent(
        string          EventType,
        object          Payload,
        Guid            EventId,
        DateTimeOffset  Timestamp,
        string?         RecordTimestamp     = null,
        string?         CausedBy            = null,
        TokenUsage?     Usage               = null,
        bool            UsageIsEcho         = false,
        long?           CacheCreationTokens = null,
        IReadOnlyList<TranscriptAttachment>? Attachments = null
    );

/// A whole extension block for one slug, to shallow-merge over what the target already holds.
public sealed record EventAmendment(Guid TargetEventId, string Slug, Struct Extension);

/// Usage to stamp onto earlier events of the same response cluster; the caller decides which
/// targets it still holds. Never persisted as itself.
public sealed record UsageApplied(TokenUsage Usage, Guid AnchorEventId, IReadOnlyList<UsageTarget> Targets);

public sealed record UsageTarget(Guid EventId, string EventType, string? ToolName, bool IsEcho);

public sealed record TranscriptAttachment(Guid Id, string FileName, string ContentType, byte[] Data);

/// What one line projects to. Rejected is set for a line that is not a JSON object (or is
/// unusable in a way the vendor names); both lists are then empty and no context state moved.
public sealed record ProjectionResult(
        IReadOnlyList<CanonicalEvent> Events,
        IReadOnlyList<EventAmendment> Amendments,
        string?                       Rejected = null
    ) {
    public static readonly ProjectionResult Empty = new([], []);

    public static ProjectionResult Of(IReadOnlyList<CanonicalEvent> events) => events.Count == 0 ? Empty : new(events, []);

    public static ProjectionResult Reject(string reason) => new([], [], reason);
}
