namespace Capacitor.Cli.Core.Policy;

using System.Text.Json;

sealed record PolicyJournalFileV1(
    [property: System.Text.Json.Serialization.JsonPropertyName("pending_asks")] List<PolicyJournalAskV1> PendingAsks,
    [property: System.Text.Json.Serialization.JsonPropertyName("by_call_id")] List<PolicyJournalCallV1> ByCallId,
    [property: System.Text.Json.Serialization.JsonPropertyName("pass_through_count")] long PassThroughCount);
sealed record PolicyJournalAskV1(
    [property: System.Text.Json.Serialization.JsonPropertyName("input_hash")] string InputHash);
sealed record PolicyJournalCallV1(
    [property: System.Text.Json.Serialization.JsonPropertyName("call_id")] string CallId,
    [property: System.Text.Json.Serialization.JsonPropertyName("outcome")] string Outcome,
    [property: System.Text.Json.Serialization.JsonPropertyName("input_hash")] string InputHash);

public readonly record struct PolicyJournalConsume(bool PendingAsk, string? ExactOutcome, bool Ambiguous);

/// <summary>
/// Per-session decision journal shared by hook processes. With a vendor call id, terminal
/// decisions correlate exactly; without one, only asks journal (FIFO per input hash) so a
/// stale entry can cost at most one extra human prompt and can never weaken an outcome.
/// </summary>
public sealed class PolicyDecisionJournal(ConfigRoot config) {
    string PathFor(string sessionKey) => config.Path("policy", "journal", $"{PolicySnapshotStore.Sanitize(sessionKey)}.json");

    public void RecordAsk(string sessionKey, string? callId, string inputHash) => Mutate(sessionKey, f =>
        callId is { Length: > 0 }
            ? f with { ByCallId = Upsert(f.ByCallId, callId, "ask", inputHash) }
            : f with { PendingAsks = [.. f.PendingAsks, new(inputHash)] });

    public void RecordTerminal(string sessionKey, string callId, string outcome, string inputHash) {
        // Empty call id means no correlation is possible — never journal a terminal under the
        // ask-only fallback, or it would sit in ByCallId unreachable by any Consume(callId: null).
        if (callId is not { Length: > 0 }) return;
        Mutate(sessionKey, f => f with { ByCallId = Upsert(f.ByCallId, callId, outcome, inputHash) });
    }

    // A later call for the same id replaces the earlier one rather than shadowing it: an ask
    // recorded before its own terminal decision must never outrank that decision on Consume.
    static List<PolicyJournalCallV1> Upsert(List<PolicyJournalCallV1> list, string callId, string outcome, string inputHash) =>
        [.. list.Where(e => e.CallId != callId), new(callId, outcome, inputHash)];

    public PolicyJournalConsume Consume(string sessionKey, string? callId, string inputHash) {
        PolicyJournalConsume result = default;
        Mutate(sessionKey, f => {
            if (callId is { Length: > 0 } && f.ByCallId.FirstOrDefault(e => e.CallId == callId) is { } exact) {
                result = new(exact.Outcome == "ask", exact.Outcome, Ambiguous: false);
                return f with { ByCallId = [.. f.ByCallId.Where(e => e.CallId != callId)] };
            }
            var head = f.PendingAsks.FirstOrDefault(e => e.InputHash == inputHash);
            if (head is null) return f;
            result = new(PendingAsk: true, ExactOutcome: null, Ambiguous: true);
            var remaining = new List<PolicyJournalAskV1>(f.PendingAsks);
            remaining.Remove(head);
            return f with { PendingAsks = remaining };
        });
        return result;
    }

    public void IncrementPassThrough(string sessionKey) =>
        Mutate(sessionKey, f => f with { PassThroughCount = f.PassThroughCount + 1 });

    public long TakePassThroughCount(string sessionKey) {
        long count = 0;
        Mutate(sessionKey, f => { count = f.PassThroughCount; return f with { PassThroughCount = 0 }; });
        return count;
    }

    public void ClearTurn(string sessionKey) =>
        Mutate(sessionKey, f => f with { PendingAsks = [], ByCallId = [] });

    void Mutate(string sessionKey, Func<PolicyJournalFileV1, PolicyJournalFileV1> transform) {
        try {
            var path = PathFor(sessionKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Well under a hook's 5s ceiling: parallel hooks contend for this file, and a wait that
            // could eat the whole budget would cost the decision rather than just the journal entry.
            // A timeout lands in the catch below and fails open.
            using var _ = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(2));
            var current = Read(path);
            var next = transform(current);
            if (ReferenceEquals(next, current)) return;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, PolicyJsonContext.Default.PolicyJournalFileV1));
            File.Move(tmp, path, overwrite: true);
        }
        // Bare: also absorbs a foreign-owned mutex on Windows and any other lock/IO surprise —
        // a journal failure must never break a hook.
        catch { }
    }

    static PolicyJournalFileV1 Read(string path) {
        try {
            if (File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), PolicyJsonContext.Default.PolicyJournalFileV1) is { } f)
                // A parsable-but-incomplete file (e.g. missing pending_asks) deserializes its
                // record properties to null; normalize before any caller enumerates them.
                return f with { PendingAsks = f.PendingAsks ?? [], ByCallId = f.ByCallId ?? [] };
        }
        catch (JsonException) { }
        return new([], [], 0);
    }
}
