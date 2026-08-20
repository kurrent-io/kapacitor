using System.Text.Json;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// Reads and writes the <see cref="HarnessOfferLedger"/> and owns the shared 6-hour evaluation
/// throttle stamp. Both files live under <see cref="PathHelpers.ConfigPath"/> and therefore inherit
/// the <c>KCAP_CONFIG_DIR</c> override like all kcap config. Load is corrupt-tolerant (→ empty
/// ledger); Save is atomic (temp + rename) so a reader never observes a partial file.
/// </summary>
public sealed class HarnessOfferStore {
    readonly string _ledgerPath;
    readonly string _stampPath;

    public HarnessOfferStore(string ledgerPath, string stampPath) {
        _ledgerPath = ledgerPath;
        _stampPath  = stampPath;
    }

    /// <summary>Production instance rooted at <c>~/.config/kcap</c> (honours <c>KCAP_CONFIG_DIR</c>).</summary>
    public static HarnessOfferStore Default() =>
        new(PathHelpers.ConfigPath("harness-offers-v1.json"), PathHelpers.ConfigPath("harness-offers.last-check"));

    /// <summary>Missing or corrupt file → empty ledger; never throws.</summary>
    public HarnessOfferLedger Load() {
        try {
            if (!File.Exists(_ledgerPath)) return new HarnessOfferLedger();
            return JsonSerializer.Deserialize(File.ReadAllText(_ledgerPath), HarnessOfferLedgerJsonContext.Default.HarnessOfferLedger)
                   ?? new HarnessOfferLedger();
        } catch {
            return new HarnessOfferLedger();
        }
    }

    /// <summary>Atomic temp-write + rename. Returns false (never throws) on any I/O failure.</summary>
    public bool Save(HarnessOfferLedger ledger) {
        try {
            var dir = Path.GetDirectoryName(_ledgerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _ledgerPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, HarnessOfferLedgerJsonContext.Default.HarnessOfferLedger));
            File.Move(tmp, _ledgerPath, overwrite: true);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Read-modify-write convenience; returns the mutated ledger (persisted best-effort).</summary>
    public HarnessOfferLedger Update(Func<HarnessOfferLedger, HarnessOfferLedger> mutate) {
        var next = mutate(Load());
        Save(next);
        return next;
    }

    /// <summary>
    /// Records that these vendors were offered now (updating <c>last_offered</c>, seeding
    /// <c>first_seen</c> once). Never writes a dismissal and never overwrites an existing one — a
    /// vendor the user explicitly dismissed stays dismissed even if setup offers it again.
    /// </summary>
    public void StampOffered(IEnumerable<string> vendorIds, DateTimeOffset now) =>
        Update(l => {
            var vendors = new Dictionary<string, HarnessOfferEntry>(l.Vendors);
            foreach (var id in vendorIds) {
                var prior = l.Entry(id);
                if (prior is { Declined: true }) continue; // never revive an explicit dismissal
                vendors[id] = new HarnessOfferEntry {
                    FirstSeen   = prior?.FirstSeen ?? now,
                    LastOffered = now,
                    Declined    = false,
                };
            }
            return l with { Vendors = vendors };
        });

    /// <summary>
    /// Cross-process evaluation throttle shared by surfaces 1 and 2: returns true (and stamps the
    /// attempt) only when the last recorded check is older than <paramref name="throttle"/>. Every
    /// hook is a fresh AOT process, so the guard must be on disk — mtime is the clock. Fail-open: any
    /// stamp I/O error returns true so a hiccup never permanently suppresses the check.
    /// </summary>
    public bool TryClaimCheck(TimeSpan throttle) {
        try {
            if (File.Exists(_stampPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_stampPath) < throttle)
                return false;

            var dir = Path.GetDirectoryName(_stampPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_stampPath, ""); // touch — mtime is the throttle clock
            return true;
        } catch {
            return true;
        }
    }
}
