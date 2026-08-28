using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

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

    /// <summary>Keyed by vendor id, the spelling this file has always carried. A row for a vendor
    /// this build does not know — written by a newer one — is kept rather than dropped, so a
    /// downgrade does not silently discard a dismissal.</summary>
    [JsonPropertyName("vendors")]
    public Dictionary<string, HarnessOfferEntry> Vendors { get; init; } = new();

    public HarnessOfferEntry? Entry(HarnessId harness) => Vendors.GetValueOrDefault(harness.VendorId);

    /// <summary>This ledger with <paramref name="harnesses"/> marked dismissed, each keeping the
    /// stamps it already had.</summary>
    public HarnessOfferLedger WithDismissed(IEnumerable<HarnessId> harnesses, DateTimeOffset now) {
        var vendors = new Dictionary<string, HarnessOfferEntry>(Vendors);

        foreach (var harness in harnesses) {
            var prior = Entry(harness);

            vendors[harness.VendorId] = new HarnessOfferEntry {
                FirstSeen   = prior?.FirstSeen ?? now,
                LastOffered = prior?.LastOffered,
                Declined    = true,
            };
        }

        return this with { Vendors = vendors };
    }

    /// <summary>This ledger with every trace of <paramref name="vendorIds"/> gone, so each is
    /// treated as freshly seen and offered again. Takes the spellings a user typed.</summary>
    public HarnessOfferLedger Without(IEnumerable<string> vendorIds) {
        var vendors = new Dictionary<string, HarnessOfferEntry>(Vendors);

        foreach (var vendorId in vendorIds) vendors.Remove(vendorId);

        return this with { Vendors = vendors };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HarnessOfferLedger))]
internal partial class HarnessOfferLedgerJsonContext : JsonSerializerContext;
