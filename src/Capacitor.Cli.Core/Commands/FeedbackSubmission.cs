using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Request body for the tenant's <c>POST /api/feedback</c> — see the server's
/// <c>FeedbackRequestDto</c>. <see cref="ClientRequestId"/> is a fresh <see cref="Guid"/> per
/// invocation: it is the idempotency key the server's <c>FeedbackIdempotencyStore</c> dedupes a
/// retried request on, so a transport-level retry of the SAME submission must reuse it — which is
/// why it is minted once by the caller and threaded through, never regenerated per attempt.</summary>
public sealed record FeedbackSubmitRequest(
        [property: JsonPropertyName("category")]           string                 Category,
        [property: JsonPropertyName("message")]             string                 Message,
        [property: JsonPropertyName("client_request_id")]   Guid                   ClientRequestId,
        [property: JsonPropertyName("context")]             FeedbackSubmitContext  Context
    );

/// <summary>Client-supplied context carried alongside a feedback submission. <see cref="Source"/>
/// is always <c>"cli"</c> for this caller — it is how the server (and any future dashboard) tells a
/// CLI-filed report apart from one filed through the web widget.</summary>
public sealed record FeedbackSubmitContext(
        [property: JsonPropertyName("source")]         string  Source,
        [property: JsonPropertyName("client_version")] string? ClientVersion,
        [property: JsonPropertyName("os")]              string? Os
    );

/// <summary>The server's 200 response — the reporter's email, resolved server-side from their
/// persisted profile (never trusted from the client).</summary>
public sealed record FeedbackSubmitResponse {
    [JsonPropertyName("reporter_email")] public string ReporterEmail { get; init; } = "";
}
