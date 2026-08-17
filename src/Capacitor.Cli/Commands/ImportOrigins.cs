// src/cli/src/Capacitor.Cli/Commands/ImportOrigins.cs
namespace Capacitor.Cli.Commands;

/// <summary>
/// Durable lifecycle-origin marker sent on import session-start/session-end hook payloads.
/// The server distinguishes an import lifecycle event from a live one by this field — NOT by
/// `source` (import posts `source:"Startup"`/`"startup"` and the server hardcodes
/// <c>$source="&lt;vendor&gt;-live"</c>) nor by `reason` (several vendors reuse "historical-import").
/// </summary>
internal static class ImportOrigins {
    public const string Historical = "historical-import";
}
