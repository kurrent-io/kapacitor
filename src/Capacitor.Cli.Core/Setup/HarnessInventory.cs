using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// A machine's coding-agent inventory (surface 3): per vendor, detected + kcap-wired, plus the
/// dismissed vendors. Additive on the wire (an old server ignores it; an old client never sends it).
/// <c>MachineId</c> is <see cref="Core.MachineId.Get"/> — the same id the daemon sends on
/// <c>DaemonConnect</c> — so the server can correlate one machine's reports.
/// </summary>
public sealed record HarnessInventory(
    string MachineId,
    Dictionary<string, HarnessInventoryEntry> Vendors,
    string[] Declined) {

    /// <summary>Computes the inventory from the harnesses this process sees, the offer ledger and
    /// the machine id. Detection and the wiring probe come off the same instances, so the two cannot
    /// resolve one vendor's root differently. No throttle — both carriers (daemon status report,
    /// hook ingest) share this.</summary>
    public static HarnessInventory Evaluate(
            HarnessRegistry harnesses, HarnessOfferLedger ledger, string machineId) {
        var vendors  = new Dictionary<string, HarnessInventoryEntry>(StringComparer.Ordinal);
        var declined = new List<string>();

        foreach (var harness in harnesses) {
            var vendorId = harness.VendorId;

            vendors[vendorId] = new HarnessInventoryEntry(
                Detected: harnesses.Detected(harness.Id),
                Wired: harness.Signals.IsWired);

            if (ledger.Entry(harness.Id) is { Declined: true }) declined.Add(vendorId);
        }

        return new HarnessInventory(machineId, vendors, [.. declined]);
    }

    /// <summary>Production convenience: evaluate over the given harnesses and the default on-disk
    /// offer ledger (read-only — never claims the throttle stamp).</summary>
    public static HarnessInventory EvaluateCurrent(ConfigRoot config, HarnessRegistry harnesses) =>
        Evaluate(harnesses, new HarnessOfferStore(config).Load(), new Core.MachineId(config).Get());
}
