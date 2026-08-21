using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// One vendor's row in the offer ledger: when it was first seen detected-but-unwired, when we last
/// nudged the user about it, and whether the user permanently dismissed it. A missing entry means
/// "never seen"; <see cref="LastOffered"/> null means "never offered".
/// </summary>
public sealed record HarnessOfferEntry {
    [JsonPropertyName("first_seen")]
    public DateTimeOffset? FirstSeen { get; init; }

    [JsonPropertyName("last_offered")]
    public DateTimeOffset? LastOffered { get; init; }

    [JsonPropertyName("declined")]
    public bool Declined { get; init; }
}

/// <summary>
/// Per-machine record of which detected-but-unwired harnesses we have offered to set up, so the
/// nudge surfaces ask once per re-offer floor and stay silent after an explicit dismissal.
/// Persisted to <c>~/.config/kcap/harness-offers-v1.json</c>; a missing or corrupt file reads as
/// empty (worst case: one repeat offer — never a crash, never a blocked hook).
/// </summary>
public sealed record HarnessOfferLedger {
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("vendors")]
    public Dictionary<string, HarnessOfferEntry> Vendors { get; init; } = new();

    public HarnessOfferEntry? Entry(string vendorId) =>
        Vendors.TryGetValue(vendorId, out var e) ? e : null;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HarnessOfferLedger))]
internal partial class HarnessOfferLedgerJsonContext : JsonSerializerContext;
