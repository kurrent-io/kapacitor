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

    /// <summary>Missing or corrupt file → empty ledger; never throws. A syntactically valid file
    /// with a null <c>vendors</c> member is normalized to an empty dictionary so callers never
    /// dereference null (the corrupt-to-empty contract).</summary>
    public HarnessOfferLedger Load() {
        try {
            if (!File.Exists(_ledgerPath)) return new HarnessOfferLedger();
            var ledger = JsonSerializer.Deserialize(SharedFileText.ReadAllText(_ledgerPath), HarnessOfferLedgerJsonContext.Default.HarnessOfferLedger)
                         ?? new HarnessOfferLedger();
            return ledger.Vendors is null ? ledger with { Vendors = new() } : ledger;
        } catch {
            return new HarnessOfferLedger();
        }
    }

    /// <summary>Atomic write. The temp file carries a per-process-unique suffix so two concurrent
    /// writers never collide on the same temp path before their renames. Returns false (never
    /// throws) on any I/O failure.</summary>
    public bool Save(HarnessOfferLedger ledger) {
        try {
            var dir = Path.GetDirectoryName(_ledgerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = $"{_ledgerPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, HarnessOfferLedgerJsonContext.Default.HarnessOfferLedger));
            File.Move(tmp, _ledgerPath, overwrite: true);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Read-modify-write, serialized across processes by <see cref="ConfigFileLock"/> so a
    /// concurrent hook/setup/command can't overwrite another's change (in particular, lose a
    /// dismissal). Never throws. If the lock can't be acquired within <paramref name="lockTimeout"/>
    /// (default 5s) it does NOT mutate — a lockless write could reintroduce the lost-dismissal race
    /// — and returns false. Returns whether the change was PERSISTED: management commands surface a
    /// false as an honest failure; the hook path ignores it (best-effort, re-nudges next window).
    /// </summary>
    public bool Update(Func<HarnessOfferLedger, HarnessOfferLedger> mutate, TimeSpan? lockTimeout = null) {
        IDisposable lease;
        try { lease = ConfigFileLock.Acquire(_ledgerPath, lockTimeout ?? TimeSpan.FromSeconds(5)); }
        catch { return false; } // timeout or foreign-owned mutex → skip rather than risk a lockless overwrite
        using (lease) {
            return Save(mutate(Load()));
        }
    }

    /// <summary>Non-blocking lock try for the hook/best-effort path: a SessionStart hook must not
    /// spend ANY of its (already-tight) exit budget waiting on the ledger mutex, so on contention it
    /// simply skips stamping — a missed stamp just re-nudges the vendor once more next window.</summary>
    static readonly TimeSpan HookLockTimeout = TimeSpan.Zero;

    /// <summary>
    /// Records that these vendors were offered now (updating <c>last_offered</c>, seeding
    /// <c>first_seen</c> once). Never writes a dismissal and never overwrites an existing one — a
    /// vendor the user explicitly dismissed stays dismissed even if setup offers it again.
    /// Best-effort (hook path): short lock wait, persistence result ignored.
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
        }, HookLockTimeout);

    /// <summary>
    /// Cross-process evaluation throttle shared by surfaces 1 and 2: returns true (and stamps the
    /// attempt) only when the last recorded check is older than <paramref name="throttle"/>. Every
    /// hook is a fresh AOT process, so the guard must be on disk — mtime is the clock. Fail-CLOSED:
    /// a stamp I/O error suppresses (returns false), because the same failure would also block the
    /// per-vendor ledger stamp and cause a nudge on every hook — see the catch below.
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
            // Fail-CLOSED: if we can't persist the throttle stamp, the per-vendor last_offered
            // ledger almost certainly can't persist either, so returning true would re-run
            // detection AND re-emit the same nudge on every hook — nudge spam. Suppress instead.
            return false;
        }
    }
}
