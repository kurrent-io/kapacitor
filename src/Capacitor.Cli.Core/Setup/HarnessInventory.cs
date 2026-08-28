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

    /// <summary>Pure: computes the inventory from an injected detection snapshot + offer ledger +
    /// machine id, over every <see cref="HarnessCatalog"/> vendor. No I/O, no throttle — both
    /// carriers (daemon status report, hook ingest) share this.</summary>
    public static HarnessInventory Evaluate(
            HarnessPaths paths, BinaryProbe binaries, HarnessOfferLedger ledger, string machineId) =>
        Evaluate(AgentDetection.Detect(paths, binaries), paths.IsWired, ledger, machineId);

    /// <summary>Injectable core (detection result + wired-probe + ledger already resolved), so the
    /// vendor→entry mapping is unit-testable without touching the filesystem or PATH.</summary>
    public static HarnessInventory Evaluate(
            AgentDetectionResult detected, Func<string, bool> isWired, HarnessOfferLedger ledger, string machineId) {
        var vendors = new Dictionary<string, HarnessInventoryEntry>(StringComparer.Ordinal);
        var declined = new List<string>();

        foreach (var h in HarnessCatalog.All) {
            vendors[h.VendorId] = new HarnessInventoryEntry(
                Detected: h.Select(detected).Detected,
                Wired: isWired(h.VendorId));
            if (ledger.Entry(h.VendorId) is { Declined: true }) declined.Add(h.VendorId);
        }

        return new HarnessInventory(machineId, vendors, [.. declined]);
    }

    /// <summary>Production convenience: evaluate from the current process environment, the default
    /// on-disk offer ledger (read-only — never claims the throttle stamp), and this machine's id.</summary>
    public static HarnessInventory EvaluateCurrent(ConfigRoot config, UserHome home) =>
        Evaluate(HarnessPaths.FromEnvironment(home), BinaryProbe.FromEnvironment(),
                 new HarnessOfferStore(config).Load(), new Core.MachineId(config).Get());
}
