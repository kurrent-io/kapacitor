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
            ? f with { ByCallId = [.. f.ByCallId, new(callId, "ask", inputHash)] }
            : f with { PendingAsks = [.. f.PendingAsks, new(inputHash)] });

    public void RecordTerminal(string sessionKey, string callId, string outcome, string inputHash) =>
        Mutate(sessionKey, f => f with { ByCallId = [.. f.ByCallId, new(callId, outcome, inputHash)] });

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
            using var _ = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5));
            var current = Read(path);
            var next = transform(current);
            if (ReferenceEquals(next, current)) return;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, PolicyJsonContext.Default.PolicyJournalFileV1));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    static PolicyJournalFileV1 Read(string path) {
        try {
            if (File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), PolicyJsonContext.Default.PolicyJournalFileV1) is { } f)
                return f;
        }
        catch (JsonException) { }
        return new([], [], 0);
    }
}
