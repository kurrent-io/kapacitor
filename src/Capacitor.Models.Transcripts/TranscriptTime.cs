using System.Globalization;

namespace Capacitor.Models.Transcripts;

public static class TranscriptTime {
    /// The record's own instant when it parses, else the receive time; the raw string rides along
    /// whenever the record had one, parseable or not, because metadata keeps it verbatim.
    public static (DateTimeOffset At, string? Record) Resolve(string? raw, DateTimeOffset receivedAt) =>
        raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? (parsed, raw)
            : (receivedAt, raw);
}
